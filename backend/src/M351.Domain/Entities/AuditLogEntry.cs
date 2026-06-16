using System.Net;

namespace M351.Domain.Entities;

public static class AuditActions
{
    public const string Login = "login";
    public const string InviteAccept = "invite_accept";
    public const string UpdateUserRole = "update_user_role";
    public const string RevokeKey = "revoke_key";
    public const string RevokeDevice = "revoke_device";

    /// <summary>Visualização de relatório/dashboard FILTRADO por um titular (device ou device_user).</summary>
    public const string ViewReport = "view_report";

    /// <summary>
    /// Visualização da linha do tempo (F3.4, Seção 7.4): GET /timeline/device (alvo device) e
    /// /timeline/team (dado pessoal de VÁRIAS pessoas — target_type "team", sem alvo individual).
    /// Era gravado como string literal "view_timeline" nos controllers (F3.4); F4.7 promove a
    /// constante e CONSOLIDA a gravação no filter de auditoria de leitura (AuditReadFilter).
    /// </summary>
    public const string ViewTimeline = "view_timeline";

    /// <summary>
    /// Mudança de categorização que dispara a reagregação de 30 dias (F3.3): PATCH de categoria
    /// com troca de classification, DELETE de categoria e PUT de mapeamento app→categoria.
    /// </summary>
    public const string UpdateCategory = "update_category";

    /// <summary>Solicitação de export CSV (F3.5): POST /exports, detail {kind, params}.</summary>
    public const string ExportCsv = "export_csv";

    /// <summary>
    /// PATCH /devices/{id} (F3.7): edição de display_name/tags/status, detail com de→para por
    /// campo alterado. Ação FORA da lista de exemplos da spec (que só ilustra revoke_device,
    /// update_category etc.) — adotada pelo padrão verbo_alvo das demais; decisão documentada.
    /// </summary>
    public const string UpdateDevice = "update_device";

    /// <summary>
    /// CLI backoffice publish-agent-release (F4.2): publicação de um novo release do agente no
    /// canal de auto-update, detail {channel, version, min_version, sha256, file_name}. Ação de
    /// operação GLOBAL (sem tenant) — gravada sob o tenant-sentinela Guid.Empty.
    /// </summary>
    public const string PublishAgentRelease = "publish_agent_release";

    /// <summary>
    /// CLI backoffice rollback-agent-release (F4.2): rollback do canal para uma versão já
    /// publicada (move is_current sem redeploy), detail {channel, from_version, to_version}.
    /// </summary>
    public const string RollbackAgentRelease = "rollback_agent_release";

    /// <summary>
    /// Direito de ACESSO/PORTABILIDADE do titular (F4.5, Seção 9.3): solicitação de pacote DSR
    /// — POST /privacy/subjects/{id}/export, /privacy/devices/{id}/export e
    /// /privacy/tenant/full-export. detail {device_user_id} ou {device_id} ou {scope:"tenant"}
    /// conforme o alvo. Insumo da resposta da controladora em 15 dias (art. 19 LGPD).
    /// </summary>
    public const string DsrExport = "dsr_export";

    /// <summary>
    /// Direito de EXCLUSÃO do titular (F4.5, Seção 9.3): hard delete irreversível dos dados
    /// pessoais identificáveis — DELETE /privacy/subjects/{id}/data e
    /// /privacy/devices/{id}/data. detail {device_user_id|device_id, reason, receipt} — o
    /// motivo e o recibo de contagens ficam na trilha (a própria trilha NÃO é apagada: é a
    /// evidência de que a exclusão ocorreu).
    /// </summary>
    public const string DsrDelete = "dsr_delete";
}

/// <summary>Tabela audit_log — append-only, particionada por mês, retenção 24 meses (N13).</summary>
public class AuditLogEntry : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public IPAddress? ActorIp { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }

    /// <summary>JSON (jsonb) com contexto: período consultado, filtros, de→para de config etc.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
