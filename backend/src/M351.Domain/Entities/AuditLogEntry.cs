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
    /// Mudança de categorização que dispara a reagregação de 30 dias (F3.3): PATCH de categoria
    /// com troca de classification, DELETE de categoria e PUT de mapeamento app→categoria.
    /// </summary>
    public const string UpdateCategory = "update_category";

    /// <summary>Solicitação de export CSV (F3.5): POST /exports, detail {kind, params}.</summary>
    public const string ExportCsv = "export_csv";
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
