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

public enum PasswordResetOutcome
{
    Success,
    InvalidToken,
    WeakPassword,
}

/// <summary>Pedido de recuperação aceito: dados para o controller compor o e-mail do link.</summary>
public record PasswordResetRequestEntry(User User, string OrganizationName, string RawToken);

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

    /// <summary>Seção 7.4: token de recuperação de senha vale 1 hora.</summary>
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    /// <summary>Seção 7.5: "10 recovery codes hasheados".</summary>
    public const int RecoveryCodeCount = 10;

    /// <summary>Alfabeto sem caracteres ambíguos (sem I, L, O, U, 0, 1) para os recovery codes.</summary>
    private const string RecoveryCodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

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

    /// <summary>
    /// Valida TOTP (login em duas etapas ou conclusão de setup) e emite tokens plenos.
    /// Com MFA JÁ habilitada, aceita também um recovery code não usado no lugar do TOTP
    /// (Seção 7.5) — o código é consumido no ato; recovery code nunca conclui o setup.
    /// </summary>
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
            if (!user.MfaEnabled)
            {
                return null;
            }

            var normalized = NormalizeRecoveryCode(code);
            if (normalized.Length < 8)
            {
                return null;
            }

            var hash = TokenGenerator.Sha256(normalized);
            var recovery = await db.MfaRecoveryCodes.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.UserId == user.Id && c.UsedAt == null && c.CodeHash == hash, ct);
            if (recovery is null)
            {
                return null;
            }

            recovery.UsedAt = Now;
            return await IssueTokensAsync(user, ip, userAgent, "password+recovery_code", ct);
        }

        if (!user.MfaEnabled)
        {
            user.MfaEnabled = true; // conclusão do setup
        }

        return await IssueTokensAsync(user, ip, userAgent, "password+totp", ct);
    }

    /// <summary>
    /// (Re)gera os recovery codes do usuário (Seção 7.5): invalida todos os anteriores e
    /// retorna os 10 novos EM CLARO — exibidos uma única vez, só o hash é persistido.
    /// Lista vazia = usuário sem MFA habilitada (o controller converte em 409).
    /// </summary>
    public async Task<IReadOnlyList<string>> GenerateRecoveryCodesAsync(
        Guid userId, IPAddress? ip, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct);
        if (user is null || !user.MfaEnabled)
        {
            return [];
        }

        var previous = await db.MfaRecoveryCodes.IgnoreQueryFilters()
            .Where(c => c.UserId == userId).ToListAsync(ct);
        db.MfaRecoveryCodes.RemoveRange(previous);

        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var code = NewRecoveryCode();
            codes.Add(code);
            db.MfaRecoveryCodes.Add(new MfaRecoveryCode
            {
                Id = Uuid7.NewUuid7(),
                TenantId = user.TenantId,
                UserId = userId,
                CodeHash = TokenGenerator.Sha256(NormalizeRecoveryCode(code)),
                CreatedAt = Now,
            });
        }

        audit.Add(user.TenantId, AuditActions.MfaRecoveryCodes, userId, ip,
            targetType: "user", targetId: userId,
            detailJson: $$"""{"count":{{RecoveryCodeCount}}}""");

        await db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>
    /// Cria tokens de recuperação de senha para TODAS as contas ativas com esse e-mail
    /// (e-mail é único por tenant, pode existir em mais de uma organização — um e-mail por
    /// conta, com o nome da org no corpo). Tokens anteriores não usados são invalidados.
    /// Convites pendentes (sem senha) ficam de fora: o caminho deles é o link de convite.
    /// </summary>
    public async Task<IReadOnlyList<PasswordResetRequestEntry>> CreatePasswordResetTokensAsync(
        string email, CancellationToken ct)
    {
        var users = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Email == email && u.Status == UserStatus.Active && u.PasswordHash != null)
            .ToListAsync(ct);
        if (users.Count == 0)
        {
            return [];
        }

        var tenantIds = users.Select(u => u.TenantId).ToList();
        var orgNames = await db.Organizations.IgnoreQueryFilters()
            .Where(o => tenantIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, ct);

        var entries = new List<PasswordResetRequestEntry>(users.Count);
        foreach (var user in users)
        {
            var stale = await db.PasswordResetTokens.IgnoreQueryFilters()
                .Where(t => t.UserId == user.Id && t.UsedAt == null).ToListAsync(ct);
            db.PasswordResetTokens.RemoveRange(stale);

            var raw = TokenGenerator.NewOpaqueToken();
            db.PasswordResetTokens.Add(new PasswordResetToken
            {
                Id = Uuid7.NewUuid7(),
                TenantId = user.TenantId,
                UserId = user.Id,
                TokenHash = TokenGenerator.Sha256(raw),
                ExpiresAt = Now.Add(PasswordResetLifetime),
            });

            entries.Add(new PasswordResetRequestEntry(user, orgNames[user.TenantId], raw));
        }

        await db.SaveChangesAsync(ct);
        return entries;
    }

    /// <summary>
    /// Redefine a senha via token do e-mail: single-use, 1 h, revoga TODAS as sessões do
    /// usuário e zera o lockout. Auditado como password_reset sob o tenant do usuário.
    /// </summary>
    public async Task<PasswordResetOutcome> ResetPasswordAsync(
        string token, string newPassword, IPAddress? ip, CancellationToken ct)
    {
        var hash = TokenGenerator.Sha256(token);
        var stored = await db.PasswordResetTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null || stored.UsedAt is not null || stored.ExpiresAt <= Now)
        {
            return PasswordResetOutcome.InvalidToken;
        }

        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == stored.UserId && u.Status == UserStatus.Active, ct);
        if (user is null)
        {
            return PasswordResetOutcome.InvalidToken;
        }

        if (newPassword.Length < MinPasswordLength)
        {
            return PasswordResetOutcome.WeakPassword;
        }

        user.PasswordHash = passwordHasher.Hash(newPassword);
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        stored.UsedAt = Now;

        var sessions = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct);
        foreach (var session in sessions)
        {
            session.RevokedAt = Now;
        }

        audit.Add(user.TenantId, AuditActions.PasswordReset, user.Id, ip,
            targetType: "user", targetId: user.Id);

        await db.SaveChangesAsync(ct);
        return PasswordResetOutcome.Success;
    }

    private static string NewRecoveryCode()
    {
        var chars = System.Security.Cryptography.RandomNumberGenerator
            .GetItems<char>(RecoveryCodeAlphabet, 10);
        return $"{new string(chars[..5])}-{new string(chars[5..])}";
    }

    private static string NormalizeRecoveryCode(string code) =>
        code.Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

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
