namespace M351.Api.Contracts;

/// <summary>
/// GET/PATCH /organization/agent-config (F5, spec §7.4/§8.7): a config de coleta do tenant
/// vista pelo portal. heartbeat_sec e active_window_poll_sec são read-only (constantes do
/// protocolo N1/N2); os demais são editáveis, com FULL restrito ao backoffice (kit LGPD item 3).
///
/// Os campos notice_* descrevem o aviso de ciência (Seções 6.5/9.4): `notice_text` é o corpo
/// escrito pela controladora (null = corpo padrão do agente) e `notice_version` reexibe o aviso
/// na frota quando sobe. `notice_default_body`, `notice_fixed_framing` e `notice_max_length` são
/// read-only e existem para o portal montar o PREVIEW do texto final sem duplicar as regras: o
/// enquadramento fixo é concatenado pelo agente e não pode ser editado nem removido pelo tenant.
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
    string? NoticeText,
    int NoticeVersion,
    string NoticeDefaultBody,
    string NoticeFixedFraming,
    int NoticeMaxLength,
    DateTimeOffset UpdatedAt);
