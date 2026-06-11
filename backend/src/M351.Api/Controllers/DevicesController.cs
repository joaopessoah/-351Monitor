using System.Text.Json;
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
    /// F3.7: ?include_archived (default TRUE, preservando o comportamento existente) — false
    /// esconde os archived (toggle "incluir arquivados" da tela Dispositivos, spec linha 954).
    /// O filtro explícito ?status=archived continua funcionando e IGNORA include_archived.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 50,
        [FromQuery] string? status = null, [FromQuery] string? tag = null, [FromQuery] string? q = null,
        [FromQuery(Name = "include_archived")] bool includeArchived = true,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        IQueryable<Device> filtered = db.Devices;
        if (!string.IsNullOrWhiteSpace(status)) filtered = filtered.Where(d => d.Status == status);
        else if (!includeArchived) filtered = filtered.Where(d => d.Status != "archived");
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

    private const int MaxDisplayNameLength = 200;
    private const int MaxTagLength = 100;
    private const int MaxTags = 50;

    /// <summary>
    /// PATCH /api/v1/devices/{id} (Seção 7.4 linha 799 — Admin+): atualização parcial de
    /// display_name, tags e status (active|paused|archived). Campos AUSENTES não mudam;
    /// display_name null limpa o apelido (o portal volta a exibir o hostname). Device revoked
    /// é terminal (só re-enroll revive): qualquer PATCH responde 400. Mutação EF + audit
    /// update_device (detail com de→para por campo alterado) no MESMO SaveChanges, padrão do
    /// Revoke — a mudança jamais persiste sem a trilha. Corpo cru (JsonElement) porque o
    /// contrato distingue "display_name ausente" (não muda) de "display_name: null" (limpa).
    /// </summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Update(Guid id, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Corpo inválido: envie um objeto JSON.");
        }

        // ----- display_name: ausente = não muda; null = limpa; string = renomeia -----
        var hasDisplayName = body.TryGetProperty("display_name", out var displayNameEl);
        string? newDisplayName = null;
        if (hasDisplayName && displayNameEl.ValueKind != JsonValueKind.Null)
        {
            if (displayNameEl.ValueKind != JsonValueKind.String)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest, "display_name deve ser string ou null.");
            }

            newDisplayName = displayNameEl.GetString()!.Trim();
            if (newDisplayName.Length is 0 or > MaxDisplayNameLength)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    $"display_name inválido (1 a {MaxDisplayNameLength} caracteres, ou null para limpar o apelido).");
            }
        }

        // ----- tags: ausente = não muda; lista substitui INTEIRA; null limpa (decisão p/
        // silêncio do contrato — equivale a lista vazia, sem tags) -----
        var hasTags = body.TryGetProperty("tags", out var tagsEl);
        string[]? newTags = null;
        if (hasTags && tagsEl.ValueKind != JsonValueKind.Null)
        {
            if (tagsEl.ValueKind != JsonValueKind.Array)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest, "tags deve ser uma lista de strings.");
            }

            var parsed = new List<string>();
            foreach (var tagEl in tagsEl.EnumerateArray())
            {
                if (tagEl.ValueKind != JsonValueKind.String)
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest, "tags deve ser uma lista de strings.");
                }

                var tag = tagEl.GetString()!.Trim();
                if (tag.Length is 0 or > MaxTagLength)
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest,
                        $"Tag inválida (1 a {MaxTagLength} caracteres).");
                }

                if (!parsed.Contains(tag)) parsed.Add(tag); // dedupe preservando a ordem
            }

            if (parsed.Count > MaxTags)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest, $"Máximo de {MaxTags} tags por device.");
            }

            newTags = [.. parsed];
        }

        // ----- status: só as transições do portal; revogação é fluxo próprio (POST /revoke) -----
        var hasStatus = body.TryGetProperty("status", out var statusEl);
        string? newStatus = null;
        if (hasStatus)
        {
            newStatus = statusEl.ValueKind == JsonValueKind.String ? statusEl.GetString() : null;
            if (newStatus is not ("active" or "paused" or "archived"))
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "status inválido. Use active, paused ou archived (revogação é via POST /devices/{id}/revoke).");
            }
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return NotFoundProblem(); // inexistente OU de outro tenant — nunca 403
        }

        if (device.Status == "revoked")
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Device revogado não pode ser alterado: somente um novo enroll o reativa.");
        }

        // aplica somente o que mudou de fato e registra o de→para por campo (detail do audit)
        var changes = new Dictionary<string, object?>();
        if (hasDisplayName && device.DisplayName != newDisplayName)
        {
            changes["display_name"] = new { from = device.DisplayName, to = newDisplayName };
            device.DisplayName = newDisplayName;
        }

        if (hasTags && !TagsEqual(device.Tags, newTags))
        {
            changes["tags"] = new { from = device.Tags, to = newTags };
            device.Tags = newTags;
        }

        if (hasStatus && device.Status != newStatus)
        {
            changes["status"] = new { from = device.Status, to = newStatus };
            device.Status = newStatus!;
        }

        if (changes.Count > 0)
        {
            audit.Add(Auth.CurrentUser.TenantId(User), AuditActions.UpdateDevice, Auth.CurrentUser.UserId(User),
                HttpContext.Connection.RemoteIpAddress, targetType: "device", targetId: device.Id,
                detailJson: JsonSerializer.Serialize(changes));

            await db.SaveChangesAsync(ct);
        }

        // mesmo shape do GET {id}
        return Ok(new DeviceResponse(
            device.Id, device.Hostname, device.DisplayName, device.OsType, device.OsVersion,
            device.AgentVersion, device.Status, device.Tags, device.LastSeenAt,
            device.TzOffsetMin, device.ClockOffsetMs));
    }

    /// <summary>null e lista vazia são equivalentes (sem tags): troca entre eles não é mudança.</summary>
    private static bool TagsEqual(string[]? a, string[]? b) => (a ?? []).SequenceEqual(b ?? []);

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
