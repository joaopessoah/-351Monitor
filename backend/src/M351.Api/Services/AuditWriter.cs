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
///
/// F4.7: actor_ip é preenchido em TODOS os caminhos COM requisição HTTP (filter de leitura,
/// Add EF e as mutações Dapper passam HttpContext.Connection.RemoteIpAddress). Ações sem
/// requisição — CLI/backoffice (publish/rollback de release) — gravam actor_ip NULL por
/// definição (não há IP de cliente). O INSERT cru grava o IP no tipo inet do Postgres
/// (Npgsql mapeia <see cref="IPAddress"/> → inet); null fica NULL.
/// O trigger append-only (migration AuditLogAppendOnlyF4) BARRA UPDATE/DELETE de linha em
/// audit_log independente da role — o DROP de partição da retenção (N13) é DDL e não é
/// afetado.
/// </summary>
public class AuditWriter(M351DbContext db)
{
    /// <summary>
    /// INSERT em audit_log na MESMA NpgsqlTransaction da mutação (chame ANTES do Commit).
    /// actor_ip é gravado quando informado (F4.7 — antes ficava sempre NULL); null = ação
    /// sem IP (CLI/sistema).
    /// </summary>
    public static Task AddInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid tenantId,
        string action,
        Guid? actorUserId = null,
        IPAddress? actorIp = null,
        string? targetType = null,
        Guid? targetId = null,
        string? detailJson = null,
        CancellationToken ct = default)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_log (id, tenant_id, actor_user_id, actor_ip, action, target_type, target_id, detail, occurred_at)
            VALUES (@Id, @TenantId, @ActorUserId, @ActorIp, @Action, @TargetType, @TargetId, @Detail::jsonb, @OccurredAt)
            """,
            new
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
