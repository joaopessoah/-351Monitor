using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Controllers;

[Route("api/v1/devices")]
[Authorize] // Viewer+ (policy default exige token de acesso pleno)
public class DevicesController(M351DbContext db, AuditWriter audit) : ApiControllerBase
{
    private const int MaxPageSize = 100;

    /// <summary>
    /// Lista paginada de devices do tenant com filtros da F2 (Seção 7.4):
    /// ?status (active|paused|archived|revoked), ?tag (match em tags[]), ?q (hostname/nome).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 50,
        [FromQuery] string? status = null, [FromQuery] string? tag = null, [FromQuery] string? q = null,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        IQueryable<Device> filtered = db.Devices;
        if (!string.IsNullOrWhiteSpace(status)) filtered = filtered.Where(d => d.Status == status);
        if (!string.IsNullOrWhiteSpace(tag)) filtered = filtered.Where(d => d.Tags != null && d.Tags.Contains(tag));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = $"%{q.Trim()}%";
            filtered = filtered.Where(d =>
                EF.Functions.ILike(d.Hostname, needle) ||
                (d.DisplayName != null && EF.Functions.ILike(d.DisplayName, needle)));
        }

        var query = filtered.OrderBy(d => d.Hostname);
        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new DeviceResponse(
                d.Id, d.Hostname, d.DisplayName, d.OsType, d.OsVersion, d.AgentVersion,
                d.Status, d.Tags, d.LastSeenAt, d.TzOffsetMin, d.ClockOffsetMs))
            .ToListAsync(ct);

        return Ok(new PagedResponse<DeviceResponse>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var device = await db.Devices
            .Where(d => d.Id == id)
            .Select(d => new DeviceResponse(
                d.Id, d.Hostname, d.DisplayName, d.OsType, d.OsVersion, d.AgentVersion,
                d.Status, d.Tags, d.LastSeenAt, d.TzOffsetMin, d.ClockOffsetMs))
            .FirstOrDefaultAsync(ct);

        return device is null ? NotFoundProblem() : Ok(device);
    }

    /// <summary>
    /// Revogação de device (Seção 7.4 — POST /devices/{id}/revoke, Admin+): status=revoked,
    /// token vigente invalidado (hash substituído → próximo batch 401) e UNENROLL enfileirado
    /// para entrega no próximo ack (o agente re-enrolla após o 401 e recebe o comando).
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public Task<IActionResult> Revoke(Guid id, CancellationToken ct) => RevokeCoreAsync(id, ct);

    /// <summary>Alias DELETE do fluxo de revogação (mesmo efeito do POST /revoke).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => RevokeCoreAsync(id, ct);

    private async Task<IActionResult> RevokeCoreAsync(Guid id, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return NotFoundProblem(); // inexistente OU de outro tenant — nunca 403
        }

        if (device.Status != "revoked")
        {
            device.Status = "revoked";
            // invalida o token vigente: hash substituído por um valor nunca emitido
            device.TokenHash = TokenGenerator.Sha256(TokenGenerator.NewOpaqueToken());

            db.DeviceCommands.Add(new DeviceCommand
            {
                Id = Uuid7.NewUuid7(),
                TenantId = device.TenantId,
                DeviceId = device.Id,
                Type = DeviceCommand.TypeUnenroll,
            });

            audit.Add(Auth.CurrentUser.TenantId(User), AuditActions.RevokeDevice, Auth.CurrentUser.UserId(User),
                HttpContext.Connection.RemoteIpAddress, targetType: "device", targetId: device.Id);

            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }
}
