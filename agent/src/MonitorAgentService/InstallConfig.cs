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

    /// <summary>Proxy opcional (PROXYURL). Persistido para consumo futuro (wiring real na F4.3).</summary>
    [JsonPropertyName("proxy_url")]
    public string? ProxyUrl { get; init; }

    /// <summary>
    /// Enrollment key pendente (golden image / NOENROLL): o servico consome no primeiro boot
    /// e zera o campo. Em instalacao normal o MSI ja faz enroll e NAO grava esta chave.
    /// </summary>
    [JsonPropertyName("pending_enroll_key")]
    public string? PendingEnrollKey { get; init; }

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
