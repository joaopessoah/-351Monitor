using System.Text.Json;
using M351.Agent.Core.Contracts;

namespace M351.Agent.Core.Events;

/// <summary>
/// Monta envelopes canônicos (Seção 5.2): event_id UUIDv7 local, occurred_at UTC ISO-8601,
/// tz_offset_min local, mono_ms via Environment.TickCount64, boot_id por boot.
/// O `seq` é atribuído pela fila SQLite no enqueue (AUTOINCREMENT).
/// </summary>
public sealed class EventFactory
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<long> _monoMs;

    public string BootId { get; }

    public EventFactory(string bootId, Func<DateTimeOffset>? utcNow = null, Func<long>? monoMs = null)
    {
        BootId = bootId;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _monoMs = monoMs ?? (() => Environment.TickCount64);
    }

    public AgentEvent Create(string type, object? payload = null,
        int? sessionId = null, string? windowsSid = null, string? windowsUser = null)
    {
        var now = _utcNow();
        return new AgentEvent
        {
            EventId = Uuid7.NewUuid7(now).ToString(),
            Seq = 0, // atribuído pela fila (AUTOINCREMENT) no enqueue
            Type = type,
            OccurredAt = Iso.Format(now),
            TzOffsetMin = (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes,
            MonoMs = _monoMs(),
            BootId = BootId,
            SessionId = sessionId,
            WindowsSid = windowsSid,
            WindowsUser = windowsUser,
            Data = ToElement(payload)
        };
    }

    public static JsonElement ToElement(object? payload)
    {
        if (payload is null) return EmptyObject;
        if (payload is JsonElement el) return el;
        return JsonSerializer.SerializeToElement(payload, payload.GetType(), AgentJsonContext.Default);
    }
}
