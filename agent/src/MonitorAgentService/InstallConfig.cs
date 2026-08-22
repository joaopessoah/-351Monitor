using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using M351.Agent.Core.Logging;

namespace MonitorAgentService;

/// <summary>
/// Config gravada pelo MSI no diretorio de dados (%ProgramData%\M351\MonitorAgent\install.json).
/// E o canal pelo qual o instalador entrega SERVERURL/PROXYURL e, no caso de golden image
/// (NOENROLL=1), a enrollment key pendente para o enroll no PRIMEIRO BOOT real (Secao 6.6).
///
/// O arquivo NAO guarda segredo de longo prazo: a enrollment key (ek_...) so fica aqui ate o
/// primeiro boot consumi-la; o device_token resultante e cifrado com DPAPI na fila (queue.db).
/// O MSI grava com ACL SYSTEM + Administrators (igual ao restante do diretorio de dados).
/// </summary>
public sealed record InstallConfig
{
    /// <summary>Base URL do servidor (ex.: https://api.produto.com.br) — vira State.ServerUrl.</summary>
    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }

    /// <summary>
    /// Proxy opcional (PROXYURL). Consumido por AgentRuntime no ponto unico do HttpClient
    /// (enroll/batch/update); ausente = proxy de sistema (WinHTTP) ou conexao direta (F4.3).
    /// </summary>
    [JsonPropertyName("proxy_url")]
    public string? ProxyUrl { get; init; }

    /// <summary>
    /// Enrollment key pendente (golden image / NOENROLL): o servico consome no primeiro boot
    /// e zera o campo. Em instalacao normal o MSI ja faz enroll e NAO grava esta chave.
    /// </summary>
    [JsonPropertyName("pending_enroll_key")]
    public string? PendingEnrollKey { get; init; }

    /// <summary>
    /// Log em nivel Debug (Secao 6.3): UNICO nivel onde titulo/usuario podem aparecer. Desligado por
    /// padrao; quando true, o agente registra um aviso de que detalhe sensivel sera gravado em log.
    /// </summary>
    [JsonPropertyName("verbose_debug")]
    public bool VerboseDebug { get; init; }

    /// <summary>
    /// Verificacao Authenticode REAL do MSI do auto-update (Secao 6.7). Default FALSE: o
    /// certificado de code signing ainda nao foi comprado
    /// (docs/runbooks/comprar-certificado-codesigning.md) e o MSI nao-assinado precisa instalar em
    /// dev/teste. A versao empacotada COM o certificado grava true aqui, e a partir dai um MSI sem
    /// assinatura confiavel e descartado sem instalar.
    /// </summary>
    [JsonPropertyName("verify_authenticode")]
    public bool VerifyAuthenticode { get; init; }

    /// <summary>
    /// CN esperado no Subject do certificado do signatario (ex.: a razao social da empresa). Opcional:
    /// com verify_authenticode ligado e este campo preenchido, um MSI assinado por OUTRO titular
    /// (ainda que por certificado valido) tambem e recusado. Ignorado se verify_authenticode=false.
    /// </summary>
    [JsonPropertyName("expected_signer_cn")]
    public string? ExpectedSignerCn { get; init; }

    private const string FileName = "install.json";

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>Le install.json se existir; nunca lanca (best-effort, instalacao pode ter pulado).</summary>
    public static InstallConfig? TryLoad(string dataDirectory, ILogSink log)
    {
        var path = PathFor(dataDirectory);
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, InstallConfigJsonContext.Default.InstallConfig);
        }
        catch (Exception ex)
        {
            log.Warn($"install.json ilegivel ({path}): {ex.Message}");
            return null;
        }
    }

    /// <summary>Reescreve install.json (best-effort) — usado para zerar a key apos consumir.</summary>
    public void Save(string dataDirectory, ILogSink log)
    {
        var path = PathFor(dataDirectory);
        try
        {
            var json = JsonSerializer.Serialize(this, InstallConfigJsonContext.Default.InstallConfig);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            log.Warn($"Falha ao gravar install.json ({path}): {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(InstallConfig))]
[SupportedOSPlatform("windows")]
public partial class InstallConfigJsonContext : JsonSerializerContext
{
}
