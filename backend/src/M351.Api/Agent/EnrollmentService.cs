using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Agent;

/// <summary>
/// POST /api/v1/agent/enroll (Seção 5.7): anônimo + enrollment key no body.
/// Valida a key (SHA-256), respeita device_limit do plano (422), reconcilia re-enroll
/// pela machine_fingerprint (mesmo device_id, token novo) e responde device_id +
/// device_token (opaco dt_, hash no banco) + config inicial + config_version.
/// </summary>
public class EnrollmentService(
    M351DbContext db,
    TenantContext tenantContext,
    AgentConfigService configService,
    TimeProvider clock,
    ILogger<EnrollmentService> logger)
{
    public async Task<IResult> EnrollAsync(EnrollRequest? request, CancellationToken ct)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.EnrollmentKey)
            || string.IsNullOrWhiteSpace(request.Hostname)
            || string.IsNullOrWhiteSpace(request.MachineFingerprint))
        {
            return Problem(StatusCodes.Status400BadRequest,
                "enrollment_key, hostname e machine_fingerprint são obrigatórios.", "invalid_request");
        }

        var now = clock.GetUtcNow();
        var keyHash = TokenGenerator.Sha256(request.EnrollmentKey.Trim());
        var key = await db.EnrollmentKeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash, ct);

        if (key is null || key.RevokedAt is not null || (key.ExpiresAt is { } exp && exp <= now))
        {
            // não distinguir inexistente/revogada/expirada para quem está fora
            logger.LogWarning("Enroll recusado: enrollment key inválida ou revogada (prefixo {Prefix})",
                request.EnrollmentKey.Length >= 7 ? request.EnrollmentKey[..7] : "?");
            return Problem(StatusCodes.Status403Forbidden, "Enrollment key inválida ou revogada.", "enrollment_key_invalid");
        }

        if (key.MaxUses is { } maxUses && key.UseCount >= maxUses)
        {
            return Problem(StatusCodes.Status403Forbidden, "Enrollment key esgotada (limite de usos).", "enrollment_key_exhausted");
        }

        // a partir daqui a requisição tem tenant: escopa filtros globais e o interceptor
        tenantContext.TenantId = key.TenantId;

        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == key.TenantId, ct)
            ?? throw new InvalidOperationException("Enrollment key órfã: organização inexistente.");

        var config = await GetOrCreateConfigAsync(key.TenantId, ct);
        var fingerprint = request.MachineFingerprint.Trim();
        var deviceToken = AuthConstants.DeviceTokenPrefix + TokenGenerator.NewOpaqueToken(32);
        var tokenHash = TokenGenerator.Sha256(deviceToken);

        var device = await db.Devices.FirstOrDefaultAsync(d => d.MachineFingerprint == fingerprint, ct);
        if (device is not null)
        {
            // Detecção barata de CLONE de golden image sem sysprep (F5): mesmo fingerprint
            // (MachineGuid + serial de BIOS idênticos) chegando com hostname DIFERENTE é o
            // sintoma clássico, o re-enroll fundiria máquinas distintas num só device e os
            // dados ficariam incompreensíveis. Não bloqueia (pode ser rename legítimo), mas
            // deixa rastro para o suporte investigar.
            if (!string.Equals(device.Hostname, request.Hostname.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Possível clone de golden image: fingerprint {Fingerprint} re-enrollado com hostname "
                    + "{NovoHostname} (antes {HostnameAntigo}), device {DeviceId} do tenant {TenantId}",
                    fingerprint[..Math.Min(12, fingerprint.Length)], request.Hostname.Trim(),
                    device.Hostname, device.Id, device.TenantId);
            }

            // re-enroll idempotente (Seção 5.7): revoga o token antigo (hash substituído),
            // emite novo, preserva o device e seu histórico
            device.TokenHash = tokenHash;
            device.Hostname = request.Hostname.Trim();
            device.OsVersion = request.OsVersion;
            device.AgentVersion = request.AgentVersion;
            device.EnrollmentKeyId = key.Id;
            device.Status = "active";
            device.ConfigVersion = config.ConfigVersion;
            device.TransparencyToken ??= Uuid7.NewUuid7(); // devices antigos ganham no re-enroll
        }
        else
        {
            // enforcement do device_limit do plano (N24 no trial) — só para device NOVO
            var activeDevices = await db.Devices
                .CountAsync(d => d.Status != "archived" && d.Status != "revoked", ct);
            if (org.DeviceLimit is { } limit && activeDevices >= limit)
            {
                return Problem(StatusCodes.Status422UnprocessableEntity,
                    $"Limite de dispositivos do plano atingido ({limit}).", "device_limit_exceeded");
            }

            device = new Device
            {
                Id = Uuid7.NewUuid7(),
                TenantId = key.TenantId,
                Hostname = request.Hostname.Trim(),
                MachineFingerprint = fingerprint,
                OsVersion = request.OsVersion,
                AgentVersion = request.AgentVersion,
                EnrollmentKeyId = key.Id,
                TokenHash = tokenHash,
                ConfigVersion = config.ConfigVersion,
                TransparencyToken = Uuid7.NewUuid7(),
            };
            db.Devices.Add(device);
            key.UseCount++;
        }

        await db.SaveChangesAsync(ct);

        var response = new EnrollResponse(
            device.Id, deviceToken, config.ConfigVersion, configService.Build(config, org.Slug));

        return Results.Created($"/api/v1/devices/{device.Id}", response);
    }

    private async Task<TenantAgentConfig> GetOrCreateConfigAsync(Guid tenantId, CancellationToken ct)
    {
        var config = await db.TenantAgentConfigs.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        if (config is null)
        {
            config = new TenantAgentConfig { TenantId = tenantId, UpdatedAt = clock.GetUtcNow() };
            db.TenantAgentConfigs.Add(config);
        }

        return config;
    }

    private static IResult Problem(int statusCode, string title, string reason) =>
        Results.Problem(title: title, statusCode: statusCode, extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
