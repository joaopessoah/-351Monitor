namespace M351.Agent.Core.Contracts;

/// <summary>
/// Tabela canônica de tipos de evento do MVP — Seção 5.3 do spec (19 tipos, exatos).
/// APPS_SNAPSHOT foi CORTADO do MVP — não existe aqui de propósito.
/// AGENT_ERROR entrou na F5 (telemetria de falha do próprio agente) e UPDATE_FAILED logo depois
/// (vigilância de rollout): o rollout dos dois é agente-primeiro, pois um ingest antigo IGNORA
/// tipo desconhecido sem rejeitar o lote (regra da Seção 5.3).
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

    /// <summary>
    /// Falha interna do próprio agente (F5): {error_type, stack_hash, count}. JAMAIS a mensagem
    /// crua da exceção, que pode conter caminho, título ou nome de usuário.
    /// </summary>
    public const string AgentError = "AGENT_ERROR";

    /// <summary>
    /// Auto-update que NÃO chegou a instalar: {from_version, to_version, reason}. O `reason` é
    /// CATEGORIZADO (UpdateFailureReasons), jamais a mensagem crua da exceção — mesma regra de
    /// privacidade do AGENT_ERROR. O sucesso não tem tipo próprio: ele já é o
    /// AGENT_START{start_reason:"update"} que o MSI provoca ao reinstalar o serviço.
    /// </summary>
    public const string UpdateFailed = "UPDATE_FAILED";

    public static readonly IReadOnlyList<string> All =
    [
        AgentStart, AgentStop, SessionStart, SessionEnd, Lock, Unlock,
        ActiveWindowChanged, IdleStart, IdleEnd, Heartbeat,
        SystemSuspend, SystemResume, TimeChanged, EventsDropped,
        AgentTamper, NoticeAck, PolicyApplied, AgentError, UpdateFailed
    ];
}
