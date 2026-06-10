using System.Text.Json;
using System.Text.Json.Serialization;

namespace M351.Agent.Core.Contracts;

/// <summary>Lote de ingestão — Seção 5.4 do spec. `device_id` NÃO vai no body (resolvido do token).</summary>
public sealed class BatchRequest
{
    [JsonPropertyName("batch_id")] public string BatchId { get; set; } = "";
    [JsonPropertyName("agent_version")] public string AgentVersion { get; set; } = "";
    [JsonPropertyName("sent_at")] public string SentAt { get; set; } = "";
    [JsonPropertyName("config_version")] public int ConfigVersion { get; set; }
    [JsonPropertyName("events")] public List<AgentEvent> Events { get; set; } = [];
}

/// <summary>Ack do batch — Seção 5.5 do spec. Único canal de config e comandos no MVP.</summary>
public sealed class AckResponse
{
    [JsonPropertyName("accepted")] public int Accepted { get; set; }
    [JsonPropertyName("duplicates")] public int Duplicates { get; set; }
    [JsonPropertyName("rejected")] public List<RejectedEvent> Rejected { get; set; } = [];
    [JsonPropertyName("server_time")] public string ServerTime { get; set; } = "";
    [JsonPropertyName("config_version")] public int ConfigVersion { get; set; }

    /// <summary>null quando o config_version do agente já está atual.</summary>
    [JsonPropertyName("config")] public AgentConfig? Config { get; set; }

    [JsonPropertyName("commands")] public List<AgentCommand>? Commands { get; set; }
}

public sealed class RejectedEvent
{
    [JsonPropertyName("event_id")] public string EventId { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class AgentCommand
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>MVP: apenas UNENROLL (outros tipos: ignorar — v1.1).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    [JsonPropertyName("payload")] public JsonElement Payload { get; set; }
}

public static class CommandTypes
{
    public const string Unenroll = "UNENROLL";
}
