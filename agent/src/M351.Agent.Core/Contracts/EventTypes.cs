namespace M351.Agent.Core.Contracts;

/// <summary>
/// Tabela canônica de tipos de evento do MVP — Seção 5.3 do spec (17 tipos, exatos).
/// APPS_SNAPSHOT foi CORTADO do MVP — não existe aqui de propósito.
/// </summary>
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

    public static readonly IReadOnlyList<string> All =
    [
        AgentStart, AgentStop, SessionStart, SessionEnd, Lock, Unlock,
        ActiveWindowChanged, IdleStart, IdleEnd, Heartbeat,
        SystemSuspend, SystemResume, TimeChanged, EventsDropped,
        AgentTamper, NoticeAck, PolicyApplied
    ];
}
