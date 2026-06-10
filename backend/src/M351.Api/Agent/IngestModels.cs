using System.Collections.Frozen;
using System.Text.Json;

namespace M351.Api.Agent;

/// <summary>Tipos canônicos de evento do MVP (Seção 5.3 — exatamente 17).</summary>
public static class EventTypes
{
    public const string AgentStart = "AGENT_START";
    public const string AgentStop = "AGENT_STOP";
    public const string SessionStart = "SESSION_START";
    public const string SessionEnd = "SESSION_END";
    public const string Lock = "LOCK";
    public const string Unlock = "UNLOCK";
    public const string ActiveWindowChanged = "ACTIVE_WINDOW_CHANGED";
    public const string IdleStart = "IDLE_START";
    public const string IdleEnd = "IDLE_END";
    public const string Heartbeat = "HEARTBEAT";
    public const string SystemSuspend = "SYSTEM_SUSPEND";
    public const string SystemResume = "SYSTEM_RESUME";
    public const string TimeChanged = "TIME_CHANGED";
    public const string EventsDropped = "EVENTS_DROPPED";
    public const string AgentTamper = "AGENT_TAMPER";
    public const string NoticeAck = "NOTICE_ACK";
    public const string PolicyApplied = "POLICY_APPLIED";

    public static readonly FrozenSet<string> Known = new[]
    {
        AgentStart, AgentStop, SessionStart, SessionEnd, Lock, Unlock, ActiveWindowChanged,
        IdleStart, IdleEnd, Heartbeat, SystemSuspend, SystemResume, TimeChanged,
        EventsDropped, AgentTamper, NoticeAck, PolicyApplied,
    }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>Motivos canônicos de rejeição (Seções 5.5/5.6).</summary>
public static class RejectReasons
{
    public const string TimestampTooOld = "timestamp_too_old";
    public const string TimestampInFuture = "timestamp_in_future";
    public const string BatchTooLarge = "batch_too_large";
    public const string InvalidEvent = "invalid_event";
}

/// <summary>Lote já desserializado (envelope da Seção 5.4); events ainda crus (JsonElement).</summary>
public sealed record IngestBatch(
    string? BatchId,
    string? AgentVersion,
    DateTimeOffset? SentAt,
    int? ConfigVersion,
    IReadOnlyList<JsonElement> Events);

/// <summary>Evento validado do envelope comum (Seção 5.2) + colunas extraídas de data (Seção 7.1).</summary>
public sealed record ParsedEvent(
    Guid EventId,
    long Seq,
    string Type,
    DateTimeOffset OccurredAt,
    int? TzOffsetMin,
    long? MonoMs,
    Guid? BootId,
    int? SessionId,
    string? WindowsSid,
    string? WindowsUser,
    string PayloadJson,
    string? ProcessName,
    string? WindowTitle,
    DateTimeOffset? LastInputAt,
    string? HeartbeatState,
    int? AppliedConfigVersion);
