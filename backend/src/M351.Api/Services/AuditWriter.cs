using System.Net;
using Dapper;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using Npgsql;

namespace M351.Api.Services;

/// <summary>
/// Grava trilha em audit_log (append-only). Dois caminhos, mesma linha:
/// - <see cref="Add"/> via EF — o SaveChanges é do chamador (mesma transação do DbContext);
/// - <see cref="AddInTransactionAsync"/> via INSERT cru — para mutações Dapper/Npgsql: a
///   trilha commita (ou faz rollback) JUNTO com a mudança, nunca a mudança sem a trilha.
/// </summary>
public class AuditWriter(M351DbContext db)
{
    /// <summary>
    /// INSERT em audit_log na MESMA NpgsqlTransaction da mutação (chame ANTES do Commit).
    /// actor_ip fica NULL — mesmo comportamento dos writes EF atuais, que não o preenchem.
    /// </summary>
    public static Task AddInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid tenantId,
        string action,
        Guid? actorUserId = null,
        string? targetType = null,
        Guid? targetId = null,
        string? detailJson = null,
        CancellationToken ct = default)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_log (id, tenant_id, actor_user_id, action, target_type, target_id, detail, occurred_at)
            VALUES (@Id, @TenantId, @ActorUserId, @Action, @TargetType, @TargetId, @Detail::jsonb, @OccurredAt)
            """,
            new
            {
                Id = Uuid7.NewUuid7(),
                TenantId = tenantId,
                ActorUserId = actorUserId,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Detail = detailJson,
                OccurredAt = DateTimeOffset.UtcNow,
            },
            transaction: tx, cancellationToken: ct));
    }

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
