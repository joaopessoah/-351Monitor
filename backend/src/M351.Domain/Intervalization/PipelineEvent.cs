namespace M351.Domain.Intervalization;

/// <summary>
/// Evento de entrada da máquina de estados (Seção 7.3). O chamador entrega os eventos
/// JÁ ordenados por (occurred_at, seq) e com occurred_at JÁ corrigido por clock_offset_ms
/// — o motor é puro e não conhece banco, fuso nem device.
/// </summary>
public sealed record PipelineEvent
{
    public required long Seq { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string EventType { get; init; }

    /// <summary>SID do usuário da sessão (lane); null em eventos de máquina.</summary>
    public string? WindowsSid { get; init; }
    public string? ProcessName { get; init; }
    public string? WindowTitle { get; init; }

    /// <summary>IDLE_START: o fechamento retroativo (N5) usa este campo, NUNCA o occurred_at.</summary>
    public DateTimeOffset? LastInputAt { get; init; }

    /// <summary>HEARTBEAT: active | idle | locked | no_session.</summary>
    public string? HeartbeatState { get; init; }

    /// <summary>EVENTS_DROPPED: início do trecho descartado.</summary>
    public DateTimeOffset? OldestDroppedAt { get; init; }
}

/// <summary>Tipos de evento que o pipeline conhece (Seção 5.3). Tipos desconhecidos são neutros.</summary>
public static class PipelineEventTypes
{
    public const string Heartbeat = "HEARTBEAT";
    public const string ActiveWindowChanged = "ACTIVE_WINDOW_CHANGED";
    public const string IdleStart = "IDLE_START";
    public const string IdleEnd = "IDLE_END";
    public const string Lock = "LOCK";
    public const string Unlock = "UNLOCK";
    public const string SessionStart = "SESSION_START";
    public const string SessionEnd = "SESSION_END";
    public const string AgentStart = "AGENT_START";
    public const string AgentStop = "AGENT_STOP";
    public const string SystemSuspend = "SYSTEM_SUSPEND";
    public const string SystemResume = "SYSTEM_RESUME";
    public const string TimeChanged = "TIME_CHANGED";
    public const string EventsDropped = "EVENTS_DROPPED";
}

/// <summary>Enum único de estado (Seção 7.3): pipeline, timeline e presença falam a mesma língua.</summary>
public static class IntervalStates
{
    public const string Active = "active";
    public const string Idle = "idle";
    public const string Locked = "locked";
    public const string OffClean = "off_clean";
    public const string NoData = "no_data";
}

/// <summary>Intervalo produzido pelo motor. WindowsSid null = intervalo de máquina (off_clean/no_data).</summary>
public sealed record BuiltInterval
{
    public string? WindowsSid { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required string State { get; init; }
    public string? ProcessName { get; init; }
    public string? WindowTitle { get; init; }
    public bool DataIncomplete { get; init; }

    public TimeSpan Duration => EndedAt - StartedAt;
}

/// <summary>
/// Estado-semente de uma lane na borda da janela de reprocessamento (derivado do último
/// intervalo sobrevivente ao DELETE) — e também o "rabo aberto" devolvido ao final do build.
/// </summary>
public sealed record LaneSeed(
    string? WindowsSid,
    string State,
    DateTimeOffset Since,
    string? ProcessName,
    string? WindowTitle);
