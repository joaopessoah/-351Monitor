using M351.Api.Contracts;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Controllers;

[Route("api/v1/devices")]
[Authorize] // Viewer+ (policy default exige token de acesso pleno)
public class DevicesController(M351DbContext db) : ApiControllerBase
{
    private const int MaxPageSize = 100;

    /// <summary>Lista paginada de devices do tenant (funcional mesmo vazia — F0).</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Devices.OrderBy(d => d.Hostname);
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
}
