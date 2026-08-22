namespace M351.Api.Contracts;

/// <summary>
/// GET/PATCH /organization/agent-config (F5, spec §7.4/§8.7): a config de coleta do tenant
/// vista pelo portal. heartbeat_sec e active_window_poll_sec são read-only (constantes do
/// protocolo N1/N2); os demais são editáveis, com FULL restrito ao backoffice (kit LGPD item 3).
/// </summary>
public record AgentConfigAdminResponse(
    int ConfigVersion,
    int HeartbeatSec,
    int ActiveWindowPollSec,
    int IdleThresholdSec,
    string WindowTitlePolicy,
    string[] MaskedPatterns,
    string[] IgnoredProcesses,
    CollectionWindowDto CollectionWindow,
    DateTimeOffset UpdatedAt);
