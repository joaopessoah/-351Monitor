using System.Text.Json.Serialization;

namespace M351.Agent.Core.Contracts;

/// <summary>
/// Protocolo do named pipe \\.\pipe\monitoragent.{sessionId} — JSON delimitado por linha (Seção 6.1).
/// helper → serviço: kinds "event", "update", "drops". serviço → helper: kind "config".
/// O helper NÃO acessa fila nem token — só troca mensagens.
/// </summary>
public sealed class PipeMessage
{
    public const string KindEvent = "event";
    public const string KindUpdate = "update";
    public const string KindDrops = "drops";
    public const string KindConfig = "config";

    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    [JsonPropertyName("event")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentEvent? Event { get; set; }

    // kind=drops (coalescimento N17 reportado pelo helper; o serviço emite EVENTS_DROPPED)
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; set; }

    [JsonPropertyName("oldest_dropped_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OldestDroppedAt { get; set; }

    // kind=config (serviço → helper)
    [JsonPropertyName("config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentConfig? Config { get; set; }

    [JsonPropertyName("config_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConfigVersion { get; set; }

    [JsonPropertyName("device_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; set; }

    /// <summary>boot_id do serviço — o helper carimba os envelopes com ele.</summary>
    [JsonPropertyName("boot_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BootId { get; set; }

    [JsonPropertyName("last_sent_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastSentAt { get; set; }
}
