using System.Net;

namespace M351.Domain.Entities;

public static class AuditActions
{
    public const string Login = "login";
    public const string InviteAccept = "invite_accept";
    public const string UpdateUserRole = "update_user_role";
    public const string RevokeKey = "revoke_key";
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
