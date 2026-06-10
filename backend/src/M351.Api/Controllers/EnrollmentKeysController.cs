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

[Route("api/v1/enrollment-keys")]
[Authorize(Policy = AuthConstants.PolicyAdminPlus)]
public class EnrollmentKeysController(M351DbContext db, AuditWriter audit) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var keys = await db.EnrollmentKeys
            .OrderBy(k => k.Id)
            .Select(k => new EnrollmentKeyResponse(k.Id, k.KeyPrefix, k.Label, k.MaxUses, k.UseCount, k.ExpiresAt, k.RevokedAt))
            .ToListAsync(ct);

        return Ok(new { items = keys });
    }

    /// <summary>Gera `ek_` + 12 chars base62. O segredo completo é retornado UMA única vez.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEnrollmentKeyRequest request, CancellationToken ct)
    {
        if (request.MaxUses is <= 0)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "max_uses deve ser maior que zero.");
        }

        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "expires_at deve estar no futuro.");
        }

        var fullKey = EnrollmentKeyGenerator.NewKey();
        var key = new EnrollmentKey
        {
            Id = Uuid7.NewUuid7(),
            TenantId = CurrentUser.TenantId(User),
            KeyPrefix = EnrollmentKeyGenerator.VisiblePrefix(fullKey),
            KeyHash = TokenGenerator.Sha256(fullKey),
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
            MaxUses = request.MaxUses,
            ExpiresAt = request.ExpiresAt,
        };

        db.EnrollmentKeys.Add(key);
        await db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created,
            new CreateEnrollmentKeyResponse(key.Id, fullKey, key.KeyPrefix, key.Label, key.MaxUses, key.ExpiresAt));
    }

    /// <summary>Revoga a chave: devices já registrados continuam; novas instalações são recusadas.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var key = await db.EnrollmentKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return NotFoundProblem();
        }

        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTimeOffset.UtcNow;
            audit.Add(CurrentUser.TenantId(User), AuditActions.RevokeKey, CurrentUser.UserId(User),
                HttpContext.Connection.RemoteIpAddress, targetType: "enrollment_key", targetId: key.Id);
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }
}
