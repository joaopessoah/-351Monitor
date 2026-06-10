using System.Text.Json;
using System.Text.Json.Serialization;

namespace M351.Agent.Core.Contracts;

/// <summary>
/// Envelope comum de evento — Seção 5.2 do spec. Nomes de campo EXATOS do contrato canônico.
/// `occurred_at` é string ISO-8601 UTC e é IMUTÁVEL depois de gravado na fila local.
/// </summary>
public sealed class AgentEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = "";

    /// <summary>Sequência monotônica por device — É o AUTOINCREMENT da fila SQLite (Seção 6.4).</summary>
    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("occurred_at")]
    public string OccurredAt { get; set; } = "";

    [JsonPropertyName("tz_offset_min")]
    public int TzOffsetMin { get; set; }

    /// <summary>Environment.TickCount64 (GetTickCount64) no momento do evento.</summary>
    [JsonPropertyName("mono_ms")]
    public long MonoMs { get; set; }

    [JsonPropertyName("boot_id")]
    public string BootId { get; set; } = "";

    [JsonPropertyName("session_id")]
    public int? SessionId { get; set; }

    [JsonPropertyName("windows_sid")]
    public string? WindowsSid { get; set; }

    [JsonPropertyName("windows_user")]
    public string? WindowsUser { get; set; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    public AgentEvent CloneWithData(JsonElement data) => new()
    {
        EventId = EventId,
        Seq = Seq,
        Type = Type,
        OccurredAt = OccurredAt,
        TzOffsetMin = TzOffsetMin,
        MonoMs = MonoMs,
        BootId = BootId,
        SessionId = SessionId,
        WindowsSid = WindowsSid,
        WindowsUser = WindowsUser,
        Data = data
    };
}
