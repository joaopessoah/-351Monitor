using System.Diagnostics;
using M351.Agent.Core.Logging;

namespace M351.Agent.Core.Update;

/// <summary>
/// Aplica um update ja decidido (Secao 6.7): baixa o MSI, verifica SHA-256 + assinatura
/// Authenticode (real, atras da flag verify_authenticode do install.json — ver VerifyAuthenticode
/// abaixo), grava a sentinela .update e dispara msiexec /i /qn. O MSI para o
/// servico (ResolveStopReason ve .update -> AGENT_STOP{reason:update}), instala a versao nova
/// e reinicia (OnStart ve .update -> AGENT_START{start_reason:update}).
///
/// As acoes de efeito colateral (gravar sentinela, rodar msiexec) sao injetadas para os testes:
/// NUNCA baixar/instalar MSI real nem rodar msiexec em teste.
/// </summary>
public sealed class UpdateInstaller
{
    private readonly UpdateClient _client;
    private readonly ILogSink _log;
    private readonly string _updatesDir;
    private readonly Action _writeUpdateSentinel;
    private readonly Action _clearUpdateSentinel;
    private readonly Func<string, CancellationToken, Task<bool>> _runInstaller;
    private readonly bool _verifyAuthenticode;
    private readonly string? _expectedSignerCn;

    public UpdateInstaller(
        UpdateClient client,
        ILogSink log,
        string updatesDir,
        Action writeUpdateSentinel,
        Func<string, CancellationToken, Task<bool>>? runInstaller = null,
        Action? clearUpdateSentinel = null,
        bool verifyAuthenticode = false,
        string? expectedSignerCn = null)
    {
        _client = client;
        _log = log;
        _updatesDir = updatesDir;
        _writeUpdateSentinel = writeUpdateSentinel;
        _clearUpdateSentinel = clearUpdateSentinel ?? (() => { });
        _runInstaller = runInstaller ?? DefaultRunMsiexecAsync;
        _verifyAuthenticode = verifyAuthenticode;
        _expectedSignerCn = expectedSignerCn;
    }

    /// <summary>
    /// Fluxo completo. Retorna true se o msiexec foi disparado (a partir dai o MSI conduz o stop/start).
    /// false em qualquer falha de download/verificacao (nada e instalado; tenta no proximo ciclo).
    /// </summary>
    public async Task<bool> ApplyAsync(UpdateManifest manifest, CancellationToken ct)
    {
        var fileName = SafeFileName(manifest.Url, manifest.Version);
        var destPath = Path.Combine(_updatesDir, fileName);

        _log.Info($"Auto-update: baixando {manifest.Version} de {manifest.Url} para {destPath}…");
        if (!await _client.DownloadAsync(manifest.Url, destPath, ct))
        {
            _log.Warn("Auto-update: download falhou — update adiado para o proximo ciclo.");
            return false;
        }

        string actualHex;
        try
        {
            actualHex = Sha256Verifier.ComputeFileHex(destPath);
        }
        catch (Exception ex)
        {
            _log.Error("Auto-update: falha ao calcular SHA-256 do MSI baixado.", ex);
            TryDelete(destPath);
            return false;
        }

        if (!Sha256Verifier.Matches(actualHex, manifest.Sha256))
        {
            _log.Error($"Auto-update: SHA-256 nao confere (esperado {manifest.Sha256}, obtido {actualHex}). MSI descartado — NAO instalado.");
            TryDelete(destPath);
            return false;
        }
        _log.Info("Auto-update: SHA-256 do MSI confere.");

        if (!VerifyAuthenticode(destPath))
        {
            _log.Error("Auto-update: assinatura Authenticode invalida — MSI descartado.");
            TryDelete(destPath);
            return false;
        }

        // Gravamos a sentinela ANTES do msiexec (o MSI pode parar o servico antes de ApplyAsync
        // retornar). Se o msiexec NAO subir, removemos a sentinela abaixo para nao deixa-la orfa.
        _log.Info("Auto-update: gravando sentinela .update e iniciando msiexec /i /qn…");
        try
        {
            _writeUpdateSentinel();
        }
        catch (Exception ex)
        {
            _log.Error("Auto-update: falha ao gravar a sentinela .update — abortando para nao mascarar o start_reason.", ex);
            return false;
        }

        var started = await _runInstaller(destPath, ct);
        if (!started)
        {
            // msiexec nao subiu: o servico NAO vai descer. Se a sentinela ficar viva, um stop/start
            // NORMAL nas proximas ~6h (ate o proximo ciclo) seria rotulado update indevidamente
            // (ResolveStopReason ve .update -> AGENT_STOP{update}; OnStart consome -> start_reason update).
            // Removemos a sentinela orfa para nao mislabel a telemetria; o update e retentado no proximo ciclo.
            _log.Error("Auto-update: msiexec nao pode ser iniciado — removendo a sentinela .update para nao rotular um stop/start normal como update.");
            try { _clearUpdateSentinel(); }
            catch (Exception ex) { _log.Warn($"Auto-update: falha ao remover a sentinela .update orfa: {ex.Message}"); }
        }
        return started;
    }

    /// <summary>
    /// Verificacao Authenticode do MSI baixado, ATRAS DE FLAG (install.json verify_authenticode,
    /// default FALSE).
    ///
    /// Desligada (padrao de hoje): o certificado de code signing ainda nao foi comprado
    /// (docs/runbooks/comprar-certificado-codesigning.md) e o MSI nao-assinado precisa instalar em
    /// dev/teste, entao apenas registramos no log que a barreira esta desligada e seguimos com o
    /// SHA-256 do manifesto como unica verificacao. Nao e silencio: sai no log a cada update.
    ///
    /// Ligada (versao empacotada pos-compra): WinVerifyTrust de verdade
    /// (WINTRUST_ACTION_GENERIC_VERIFY_V2) mais a exigencia de que o Subject do certificado do
    /// signatario contenha o CN esperado (expected_signer_cn), para que um MSI assinado por OUTRA
    /// empresa tambem seja recusado. Qualquer recusa descarta o MSI sem instalar.
    /// </summary>
    public bool VerifyAuthenticode(string msiPath)
    {
        var fileName = Path.GetFileName(msiPath);
        if (!_verifyAuthenticode)
        {
            _log.Warn($"Auto-update: verificacao Authenticode DESLIGADA (install.json verify_authenticode=false) " +
                      $"— {fileName} aceito apenas pelo SHA-256 do manifesto. Ligar quando o certificado de code " +
                      $"signing estiver em uso.");
            return true;
        }

        var result = Authenticode.Verify(msiPath, _expectedSignerCn);
        if (!result.Trusted)
        {
            _log.Error($"Auto-update: assinatura de {fileName} RECUSADA — {result.Detail}" +
                       (result.SignerSubject is null ? "" : $" (signatario: {result.SignerSubject})") + ".",
                new InvalidOperationException(result.Detail));
            return false;
        }

        _log.Info($"Auto-update: assinatura de {fileName} confere ({result.SignerSubject}).");
        return true;
    }

    /// <summary>msiexec /i &lt;msi&gt; /qn — produz o major-upgrade que preserva %ProgramData%.</summary>
    private async Task<bool> DefaultRunMsiexecAsync(string msiPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("msiexec.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(msiPath);
            psi.ArgumentList.Add("/qn");

            var proc = Process.Start(psi);
            if (proc is null) return false;

            // Nao bloqueia o ciclo de update esperando: o msiexec vai parar este proprio servico.
            await Task.Yield();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Auto-update: falha ao iniciar msiexec.", ex);
            return false;
        }
    }

    /// <summary>
    /// Nome de arquivo seguro a partir da url; fallback MonitorAgent-{version}.msi. Evita path
    /// traversal a partir de uma url maliciosa do manifesto (so o segmento final, sem separadores).
    /// </summary>
    public static string SafeFileName(string url, string version)
    {
        try
        {
            var candidate = Path.GetFileName(new Uri(url, UriKind.Absolute).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(candidate) &&
                candidate.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                candidate.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        catch (Exception) { /* url relativa/invalida: usa fallback */ }
        return $"MonitorAgent-{version}.msi";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* best-effort */ }
    }
}
