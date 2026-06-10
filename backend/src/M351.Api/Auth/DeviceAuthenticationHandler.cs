using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using M351.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;

namespace M351.Api.Auth;

/// <summary>
/// Scheme de autenticação do AGENTE (separado do JWT do portal): Authorization: Bearer dt_...
/// O token é opaco; o lookup é por SHA-256 em devices.token_hash (índice ix_devices_token_hash).
/// Token desconhecido ou revogado (hash substituído na revogação) → 401. O tenant vem SEMPRE
/// do device resolvido — claims org_id + device_id alimentam o escopo de tenant da requisição.
/// </summary>
public class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NpgsqlDataSource dataSource)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        if (!token.StartsWith(AuthConstants.DeviceTokenPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Token de device inválido.");
        }

        var tokenHash = TokenGenerator.Sha256(token);

        await using var connection = await dataSource.OpenConnectionAsync(Context.RequestAborted);
        var device = await connection.QuerySingleOrDefaultAsync<(Guid Id, Guid TenantId, string Status)>(
            new CommandDefinition(
                "SELECT id, tenant_id, status FROM devices WHERE token_hash = @TokenHash",
                new { TokenHash = tokenHash },
                cancellationToken: Context.RequestAborted));

        if (device == default)
        {
            return AuthenticateResult.Fail("Token de device desconhecido ou revogado.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(AuthConstants.ClaimDeviceId, device.Id.ToString()),
            new Claim(AuthConstants.ClaimOrgId, device.TenantId.ToString()),
            new Claim(AuthConstants.ClaimTokenUse, AuthConstants.TokenUseDevice),
        ], Scheme.Name);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>Acesso tipado aos claims do device autenticado.</summary>
public static class CurrentDevice
{
    public static Guid DeviceId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(AuthConstants.ClaimDeviceId)
            ?? throw new InvalidOperationException("Principal sem claim device_id."));

    public static Guid TenantId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(AuthConstants.ClaimOrgId)
            ?? throw new InvalidOperationException("Principal sem claim org_id."));
}
