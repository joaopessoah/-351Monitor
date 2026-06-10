using System.Net;
using M351.Api.Auth;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Services;

public enum LoginOutcome
{
    Success,
    MfaRequired,
    MfaSetupRequired,
    InvalidCredentials,
    Locked,
}

public record TokenPair(string AccessToken, int ExpiresIn, string RefreshToken, TimeSpan RefreshLifetime);

public record LoginFlowResult(LoginOutcome Outcome, TokenPair? Tokens = null, string? MfaToken = null, DateTimeOffset? LockedUntil = null);

public enum InviteAcceptOutcome
{
    Success,
    MfaSetupRequired,
    InvalidToken,
    Expired,
    WeakPassword,
}

public record InviteAcceptResult(InviteAcceptOutcome Outcome, TokenPair? Tokens = null, string? MfaToken = null);

/// <summary>
/// Regras de autenticação da Seção 7.5: Argon2id, lockout 10→15 min (N22), MFA TOTP
/// obrigatória para Owner/Admin, refresh opaco 30 dias com rotação single-use,
/// convite 7 dias single-use. Logins e aceites de convite são auditados.
/// </summary>
public class AuthFlowService(
    M351DbContext db,
    IPasswordHasher passwordHasher,
    IMfaService mfaService,
    JwtTokenService jwtTokenService,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    public const int MaxFailedAttempts = 10;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public const int MinPasswordLength = 12;

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<LoginFlowResult> LoginAsync(
        string email, string password, IPAddress? ip, string? userAgent, CancellationToken ct)
    {
        // e-mail é único por (tenant, email); o login não conhece o tenant → busca global explícita
        var candidates = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Email == email && u.Status == UserStatus.Active && u.PasswordHash != null)
            .ToListAsync(ct);

        User? matched = null;
        foreach (var candidate in candidates)
        {
            ResetExpiredLockout(candidate);
            if (passwordHasher.Verify(password, candidate.PasswordHash!))
            {
                matched = candidate;
                break;
            }
        }

        if (matched is null)
        {
            foreach (var candidate in candidates.Where(c => c.LockedUntil is null || c.LockedUntil <= Now))
            {
                candidate.FailedLoginCount++;
                if (candidate.FailedLoginCount >= MaxFailedAttempts)
                {
                    candidate.LockedUntil = Now.Add(LockoutDuration);
                }
            }

            await db.SaveChangesAsync(ct);
            return new LoginFlowResult(LoginOutcome.InvalidCredentials);
        }

        if (matched.LockedUntil is { } lockedUntil && lockedUntil > Now)
        {
            await db.SaveChangesAsync(ct);
            return new LoginFlowResult(LoginOutcome.Locked, LockedUntil: lockedUntil);
        }

        if (matched.MfaEnabled)
        {
            await db.SaveChangesAsync(ct);
            return new LoginFlowResult(LoginOutcome.MfaRequired, MfaToken: jwtTokenService.CreateMfaToken(matched));
        }

        if (matched.Role.RequiresMfa())
        {
            // Owner/Admin sem MFA configurada: não emite tokens plenos — força o setup
            await db.SaveChangesAsync(ct);
            return new LoginFlowResult(LoginOutcome.MfaSetupRequired, MfaToken: jwtTokenService.CreateMfaToken(matched));
        }

        var tokens = await IssueTokensAsync(matched, ip, userAgent, "password", ct);
        return new LoginFlowResult(LoginOutcome.Success, Tokens: tokens);
    }

    /// <summary>Valida TOTP (login em duas etapas ou conclusão de setup) e emite tokens plenos.</summary>
    public async Task<TokenPair?> VerifyMfaAsync(
        Guid userId, string code, IPAddress? ip, string? userAgent, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct);
        if (user?.MfaSecretEnc is null)
        {
            return null;
        }

        if (!mfaService.VerifyCode(user.MfaSecretEnc, code))
        {
            return null;
        }

        if (!user.MfaEnabled)
        {
            user.MfaEnabled = true; // conclusão do setup
        }

        return await IssueTokensAsync(user, ip, userAgent, "password+totp", ct);
    }

    /// <summary>Rotação single-use: o refresh usado é revogado e um novo é emitido. Reuso → null (negado).</summary>
    public async Task<TokenPair?> RefreshAsync(
        string rawRefreshToken, IPAddress? ip, string? userAgent, CancellationToken ct)
    {
        var hash = TokenGenerator.Sha256(rawRefreshToken);
        var stored = await db.RefreshTokens.IgnoreQueryFilters()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null || !stored.IsActive(Now) || stored.User is null || stored.User.Status != UserStatus.Active)
        {
            return null;
        }

        stored.RevokedAt = Now;

        var newRaw = TokenGenerator.NewOpaqueToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Uuid7.NewUuid7(),
            TenantId = stored.TenantId,
            UserId = stored.UserId,
            TokenHash = TokenGenerator.Sha256(newRaw),
            ExpiresAt = Now.Add(jwtTokenService.RefreshTokenLifetime),
            UserAgent = userAgent,
            Ip = ip,
        });

        await db.SaveChangesAsync(ct);

        return new TokenPair(
            jwtTokenService.CreateAccessToken(stored.User),
            jwtTokenService.AccessTokenSeconds,
            newRaw,
            jwtTokenService.RefreshTokenLifetime);
    }

    public async Task LogoutAsync(Guid userId, string? rawRefreshToken, CancellationToken ct)
    {
        if (rawRefreshToken is null)
        {
            return;
        }

        var hash = TokenGenerator.Sha256(rawRefreshToken);
        var stored = await db.RefreshTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);

        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = Now;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<InviteAcceptResult> AcceptInviteAsync(
        string token, string password, string? displayName, IPAddress? ip, string? userAgent, CancellationToken ct)
    {
        var hash = TokenGenerator.Sha256(token);
        var invitation = await db.Invitations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);

        if (invitation is null)
        {
            return new InviteAcceptResult(InviteAcceptOutcome.InvalidToken);
        }

        if (invitation.AcceptedAt is not null || invitation.ExpiresAt <= Now)
        {
            return new InviteAcceptResult(InviteAcceptOutcome.Expired);
        }

        if (password.Length < MinPasswordLength)
        {
            return new InviteAcceptResult(InviteAcceptOutcome.WeakPassword);
        }

        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == invitation.TenantId && u.Email == invitation.Email, ct);

        if (user is null || user.Status == UserStatus.Disabled)
        {
            return new InviteAcceptResult(InviteAcceptOutcome.InvalidToken);
        }

        user.PasswordHash = passwordHasher.Hash(password);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            user.DisplayName = displayName.Trim();
        }

        user.Status = UserStatus.Active;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        invitation.AcceptedAt = Now;

        audit.Add(invitation.TenantId, AuditActions.InviteAccept, user.Id, ip,
            targetType: "invitation", targetId: invitation.Id,
            detailJson: $$"""{"role":"{{user.Role.ToDbValue()}}"}""");

        if (user.Role.RequiresMfa())
        {
            // papel exige MFA: salvar estado e devolver token temporário para o setup
            await db.SaveChangesAsync(ct);
            return new InviteAcceptResult(InviteAcceptOutcome.MfaSetupRequired,
                MfaToken: jwtTokenService.CreateMfaToken(user));
        }

        var tokens = await IssueTokensAsync(user, ip, userAgent, "invite", ct);
        return new InviteAcceptResult(InviteAcceptOutcome.Success, Tokens: tokens);
    }

    private async Task<TokenPair> IssueTokensAsync(
        User user, IPAddress? ip, string? userAgent, string method, CancellationToken ct)
    {
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = Now;

        var rawRefresh = TokenGenerator.NewOpaqueToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Uuid7.NewUuid7(),
            TenantId = user.TenantId,
            UserId = user.Id,
            TokenHash = TokenGenerator.Sha256(rawRefresh),
            ExpiresAt = Now.Add(jwtTokenService.RefreshTokenLifetime),
            UserAgent = userAgent,
            Ip = ip,
        });

        audit.Add(user.TenantId, AuditActions.Login, user.Id, ip,
            targetType: "user", targetId: user.Id,
            detailJson: $$"""{"method":"{{method}}"}""");

        await db.SaveChangesAsync(ct);

        return new TokenPair(
            jwtTokenService.CreateAccessToken(user),
            jwtTokenService.AccessTokenSeconds,
            rawRefresh,
            jwtTokenService.RefreshTokenLifetime);
    }

    private void ResetExpiredLockout(User user)
    {
        if (user.LockedUntil is { } lockedUntil && lockedUntil <= Now)
        {
            user.LockedUntil = null;
            user.FailedLoginCount = 0;
        }
    }
}
