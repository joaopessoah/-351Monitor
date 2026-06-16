using System.Text.Json;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Privacy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// /api/v1/privacy/* (F4.5, Seções 7.4/8.7/9.3 — DSR: direitos do titular). A fatia mais
/// sensível de LGPD: suporta a controladora a responder ao titular em 15 dias (art. 19) com
/// EXPORT (acesso/portabilidade) e EXCLUSÃO (eliminação, art. 18 V).
///
/// O TITULAR é um device_user (NÃO um usuário de portal): as rotas de subject usam
/// {deviceUserId}. As de device usam {deviceId} (todos os device_users do device).
///
/// Papéis (Seção 7.4): EXPORT subject/device → AdminPlus; EXPORT tenant-full e toda EXCLUSÃO
/// → OwnerOnly (o ato irreversível e o offboarding ficam só com o Owner). Admin que tenta
/// excluir recebe 403; recurso de outro tenant → 404 (nunca 403 — Princípio 4).
///
/// EXPORT: cria um export_job (kind dsr_subject|dsr_device|tenant_full, status queued) — o
/// ExportService (worker) gera o ZIP em 72h; o download é servido pelo ExportsController. A
/// trilha dsr_export é gravada na MESMA transação do INSERT.
///
/// EXCLUSÃO: confirmation (repetir o windows_username do titular / hostname do device) +
/// reason obrigatório; hard delete transacional via DsrService; recibo com contagens; trilha
/// dsr_delete {alvo, reason, receipt} na MESMA transação (a trilha NÃO é apagada — é a
/// evidência). A REGRA de exclusão (DsrService) é defensável mas PRECISA de validação jurídica.
/// </summary>
[Route("api/v1/privacy")]
[Authorize] // refinado por ação ([AdminPlus]/[OwnerOnly]); base impede anônimo
public class PrivacyController(
    NpgsqlDataSource dataSource,
    DsrService dsrService) : ApiControllerBase
{
    /// <summary>reason do DELETE: mínimo de caracteres (a controladora documenta o porquê).</summary>
    public const int MinReasonLength = 8;

    // ============================================================ SUBJECT (device_user)

    // ------------------------------------------------------------ POST /privacy/subjects/{id}/export
    [HttpPost("subjects/{deviceUserId:guid}/export")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> ExportSubject(Guid deviceUserId, CancellationToken ct)
    {
        var tenantId = CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // 404 se o device_user não existe no tenant (cross-tenant é indistinguível de inexistente)
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM device_users WHERE tenant_id = @t AND id = @id",
            new { t = tenantId, id = deviceUserId }, cancellationToken: ct));
        if (exists == 0) return NotFoundProblem();

        return await QueueExportAsync(connection, tenantId, "dsr_subject",
            new Dictionary<string, object?> { ["device_user_id"] = deviceUserId },
            targetType: "device_user", targetId: deviceUserId,
            auditDetail: new Dictionary<string, object?> { ["device_user_id"] = deviceUserId }, ct);
    }

    // ------------------------------------------------------------ DELETE /privacy/subjects/{id}/data
    [HttpDelete("subjects/{deviceUserId:guid}/data")]
    [Authorize(Policy = AuthConstants.PolicyOwnerOnly)] // Admin → 403
    public async Task<IActionResult> DeleteSubject(
        Guid deviceUserId, [FromBody] DsrDeleteRequest? body, CancellationToken ct)
    {
        var tenantId = CurrentUser.TenantId(User);
        var userId = CurrentUser.UserId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // valor de segurança da confirmação dupla: o windows_username do titular
        var windowsUsername = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT windows_username FROM device_users WHERE tenant_id = @t AND id = @id",
            new { t = tenantId, id = deviceUserId }, cancellationToken: ct));
        if (windowsUsername is null) return NotFoundProblem();

        var invalid = ValidateConfirmation(body, expected: windowsUsername);
        if (invalid is not null) return invalid;

        return await ExecuteDeleteAsync(connection, tenantId, userId,
            (conn, tx) => dsrService.DeleteSubjectAsync(conn, tx, tenantId, deviceUserId, ct),
            targetType: "device_user", targetId: deviceUserId,
            auditExtra: new Dictionary<string, object?> { ["device_user_id"] = deviceUserId },
            reason: body!.Reason!, ct);
    }

    // ============================================================ DEVICE (todos os device_users)

    // ------------------------------------------------------------ POST /privacy/devices/{id}/export
    [HttpPost("devices/{deviceId:guid}/export")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> ExportDevice(Guid deviceId, CancellationToken ct)
    {
        var tenantId = CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM devices WHERE tenant_id = @t AND id = @id",
            new { t = tenantId, id = deviceId }, cancellationToken: ct));
        if (exists == 0) return NotFoundProblem();

        return await QueueExportAsync(connection, tenantId, "dsr_device",
            new Dictionary<string, object?> { ["device_id"] = deviceId },
            targetType: "device", targetId: deviceId,
            auditDetail: new Dictionary<string, object?> { ["device_id"] = deviceId }, ct);
    }

    // ------------------------------------------------------------ DELETE /privacy/devices/{id}/data
    [HttpDelete("devices/{deviceId:guid}/data")]
    [Authorize(Policy = AuthConstants.PolicyOwnerOnly)] // Admin → 403
    public async Task<IActionResult> DeleteDevice(
        Guid deviceId, [FromBody] DsrDeleteRequest? body, CancellationToken ct)
    {
        var tenantId = CurrentUser.TenantId(User);
        var userId = CurrentUser.UserId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // valor de segurança da confirmação dupla: o hostname do device
        var hostname = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT hostname FROM devices WHERE tenant_id = @t AND id = @id",
            new { t = tenantId, id = deviceId }, cancellationToken: ct));
        if (hostname is null) return NotFoundProblem();

        var invalid = ValidateConfirmation(body, expected: hostname);
        if (invalid is not null) return invalid;

        return await ExecuteDeleteAsync(connection, tenantId, userId,
            (conn, tx) => dsrService.DeleteDeviceAsync(conn, tx, tenantId, deviceId, ct),
            targetType: "device", targetId: deviceId,
            auditExtra: new Dictionary<string, object?> { ["device_id"] = deviceId },
            reason: body!.Reason!, ct);
    }

    // ============================================================ TENANT (offboarding)

    // ------------------------------------------------------------ POST /privacy/tenant/full-export
    /// <summary>
    /// Acervo completo do tenant (offboarding). Só o FULL-EXPORT é código — a PURGE do tenant
    /// é runbook MANUAL (jamais automatizar exclusão de tenant: risco). OwnerOnly.
    /// </summary>
    [HttpPost("tenant/full-export")]
    [Authorize(Policy = AuthConstants.PolicyOwnerOnly)]
    public async Task<IActionResult> ExportTenantFull(CancellationToken ct)
    {
        var tenantId = CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        return await QueueExportAsync(connection, tenantId, "tenant_full",
            new Dictionary<string, object?>(),
            targetType: "tenant", targetId: tenantId,
            auditDetail: new Dictionary<string, object?> { ["scope"] = "tenant" }, ct);
    }

    // ============================================================ helpers

    /// <summary>
    /// Cria o export_job DSR (status queued) + trilha dsr_export na MESMA transação e devolve
    /// 202 — o worker gera o ZIP. Mesmo formato de resposta do export de relatório.
    /// </summary>
    private async Task<IActionResult> QueueExportAsync(
        NpgsqlConnection connection, Guid tenantId, string kind, Dictionary<string, object?> jobParams,
        string targetType, Guid targetId, Dictionary<string, object?> auditDetail, CancellationToken ct)
    {
        var userId = CurrentUser.UserId(User);
        var jobId = Uuid7.NewUuid7();
        var paramsJson = JsonSerializer.Serialize(jobParams);

        DateTimeOffset createdAt;
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            createdAt = await connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition(
                """
                INSERT INTO export_jobs (id, tenant_id, requested_by, kind, params, status)
                VALUES (@Id, @TenantId, @RequestedBy, @Kind, @Params::jsonb, 'queued')
                RETURNING created_at
                """,
                new { Id = jobId, TenantId = tenantId, RequestedBy = userId, Kind = kind, Params = paramsJson },
                transaction: tx, cancellationToken: ct));

            await AuditWriter.AddInTransactionAsync(
                connection, tx, tenantId, AuditActions.DsrExport,
                actorUserId: userId, targetType: targetType, targetId: targetId,
                detailJson: JsonSerializer.Serialize(new Dictionary<string, object?>(auditDetail) { ["kind"] = kind }),
                ct: ct);

            await tx.CommitAsync(ct);
        }

        return Accepted(new DsrExportResponse(jobId, kind, "queued", createdAt));
    }

    /// <summary>
    /// Hard delete transacional (DsrService) + trilha dsr_delete {alvo, reason, receipt} na
    /// MESMA transação. Ou o titular some por inteiro com a trilha gravada, ou nada muda.
    /// </summary>
    private async Task<IActionResult> ExecuteDeleteAsync(
        NpgsqlConnection connection, Guid tenantId, Guid userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<DsrService.DeleteReceipt>> delete,
        string targetType, Guid targetId, Dictionary<string, object?> auditExtra, string reason, CancellationToken ct)
    {
        DsrService.DeleteReceipt receipt;
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            receipt = await delete(connection, tx);

            var detail = new Dictionary<string, object?>(auditExtra)
            {
                ["reason"] = reason,
                ["receipt"] = new Dictionary<string, object?>
                {
                    ["raw_events_deleted"] = receipt.RawEventsDeleted,
                    ["intervals_deleted"] = receipt.IntervalsDeleted,
                    ["device_users_anonymized"] = receipt.DeviceUsersAnonymized,
                    ["daily_rows_kept"] = receipt.DailyRowsKept,
                },
            };

            await AuditWriter.AddInTransactionAsync(
                connection, tx, tenantId, AuditActions.DsrDelete,
                actorUserId: userId, targetType: targetType, targetId: targetId,
                detailJson: JsonSerializer.Serialize(detail), ct: ct);

            await tx.CommitAsync(ct);
        }

        return Ok(new DsrDeleteResponse(new DsrDeleteReceipt(
            receipt.RawEventsDeleted, receipt.IntervalsDeleted, receipt.DeviceUsersAnonymized,
            receipt.DailyRowsKept, DsrService.ReceiptNote)));
    }

    /// <summary>
    /// Confirmação dupla: confirmation deve bater EXATAMENTE com o valor de segurança esperado
    /// (windows_username do titular ou hostname do device) e reason >= MinReasonLength chars.
    /// Inválido → 400 sem efeito. Ordem 404-antes-de-400 garantida pelo chamador (o lookup do
    /// alvo já retornou 404 quando inexistente/cross-tenant).
    /// </summary>
    private ObjectResult? ValidateConfirmation(DsrDeleteRequest? body, string expected)
    {
        if (body is null)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Corpo obrigatório com confirmation e reason.");
        if (!string.Equals(body.Confirmation, expected, StringComparison.Ordinal))
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Confirmação não confere.",
                detail: "Repita exatamente o identificador do titular/dispositivo para confirmar a exclusão.");
        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < MinReasonLength)
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Motivo obrigatório com pelo menos {MinReasonLength} caracteres.");
        return null;
    }
}
