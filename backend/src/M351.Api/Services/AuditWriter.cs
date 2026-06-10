using System.Net;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;

namespace M351.Api.Services;

/// <summary>Grava trilha em audit_log (append-only). O SaveChanges é do chamador (mesma transação).</summary>
public class AuditWriter(M351DbContext db)
{
    public void Add(
        Guid tenantId,
        string action,
        Guid? actorUserId = null,
        IPAddress? actorIp = null,
        string? targetType = null,
        Guid? targetId = null,
        string? detailJson = null)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Uuid7.NewUuid7(),
            TenantId = tenantId,
            ActorUserId = actorUserId,
            ActorIp = actorIp,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detailJson,
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }
}
