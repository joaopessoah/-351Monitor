using M351.Agent.Core.Logging;

namespace MonitorAgentService;

/// <summary>
/// Sentinela de auto-update (Secao 6.7), analoga a .uninstall (UninstallFlag) mas gravada pelo
/// PROPRIO agente antes de disparar msiexec /i /qn — nao pelo MSI. O fluxo:
///
///   1. UpdateInstaller grava .update (Write) e roda msiexec.
///   2. O MSI para o servico  -> ResolveStopReason ve .update -> AGENT_STOP{reason:"update"}
///                               (precede service_stop; NAO consome — o start ainda precisa ver).
///   3. O MSI instala a versao nova e reinicia o servico.
///   4. OnStart ve .update -> start_reason "update" (precede crash_recovery) e CONSOME a sentinela.
///
/// Mora em %ProgramData%\M351\MonitorAgent\ (DATAFOLDER), preservado pelo major-upgrade
/// (Permanent/NeverOverwrite — Package.wxs), exatamente como a fila e a identidade.
/// </summary>
public static class UpdateFlag
{
    private const string FileName = ".update";

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static bool IsSet(string dataDirectory)
    {
        try { return File.Exists(PathFor(dataDirectory)); }
        catch (Exception) { return false; }
    }

    /// <summary>Grava a sentinela (carimbo de tempo como conteudo; best-effort lanca para o caller decidir).</summary>
    public static void Write(string dataDirectory, ILogSink log)
    {
        var path = PathFor(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        log.Info("Sentinela .update gravada — o proximo stop/start sera atribuido ao update.");
    }

    /// <summary>Consome a sentinela (best-effort): retorna true se estava setada e a remove.</summary>
    public static bool Consume(string dataDirectory, ILogSink log)
    {
        var path = PathFor(dataDirectory);
        try
        {
            if (!File.Exists(path)) return false;
            try { File.Delete(path); }
            catch (Exception ex) { log.Warn($"Falha ao remover sentinela de update: {ex.Message}"); }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
