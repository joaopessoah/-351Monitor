using System.Net;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.RateLimiting;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Email;
using M351.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M351.Api.Controllers;

[Route("api/v1/auth")]
public class AuthController(
    AuthFlowService authFlow,
    M351DbContext db,
    IMfaService mfaService,
    IEmailSender emailSender,
    IOptions<PortalOptions> portalOptions,
    TimeProvider timeProvider) : ApiControllerBase
{
    private IPAddress? ClientIp => HttpContext.Connection.RemoteIpAddress;
    private string? ClientUserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Informe e-mail e senha.");
        }

        var result = await authFlow.LoginAsync(request.Email.Trim(), request.Password, ClientIp, ClientUserAgent, ct);

        switch (result.Outcome)
        {
            case LoginOutcome.Success:
                RefreshCookie.Set(Response, result.Tokens!.RefreshToken, result.Tokens.RefreshLifetime);
                return Ok(new AuthResponse("ok", result.Tokens.AccessToken, "Bearer", result.Tokens.ExpiresIn));

            case LoginOutcome.MfaRequired:
                return Ok(new AuthResponse("mfa_required", MfaToken: result.MfaToken));

            case LoginOutcome.MfaSetupRequired:
                return Ok(new AuthResponse("mfa_setup_required", MfaToken: result.MfaToken));

            case LoginOutcome.Locked:
                // mensagem genérica + código para o portal exibir o cooldown (N22)
                return ProblemResponse(StatusCodes.Status401Unauthorized,
                    "E-mail ou senha inválidos.",
                    "Conta temporariamente bloqueada por excesso de tentativas. Tente novamente em alguns minutos.",
                    code: "account_locked");

            default:
                return ProblemResponse(StatusCodes.Status401Unauthorized, "E-mail ou senha inválidos.");
        }
    }

    [HttpPost("mfa/setup")]
    [Authorize(Policy = AuthConstants.PolicyMfaToken)]
    public async Task<IActionResult> MfaSetup(CancellationToken ct)
    {
        var userId = CurrentUser.UserId(User);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct);
        if (user is null)
        {
            return ProblemResponse(StatusCodes.Status401Unauthorized, "Sessão inválida.");
        }

        if (user.MfaEnabled)
        {
            return ProblemResponse(StatusCodes.Status409Conflict, "MFA já está configurada para este usuário.");
        }

        var (secretBase32, secretEncrypted) = mfaService.GenerateSecret();
        user.MfaSecretEnc = secretEncrypted;
        await db.SaveChangesAsync(ct);

        return Ok(new MfaSetupResponse(secretBase32, mfaService.BuildOtpAuthUri(secretBase32, user.Email)));
    }

    [HttpPost("mfa/verify")]
    [Authorize(Policy = AuthConstants.PolicyMfaToken)]
    public async Task<IActionResult> MfaVerify([FromBody] MfaVerifyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Informe o código de verificação.");
        }

        var tokens = await authFlow.VerifyMfaAsync(CurrentUser.UserId(User), request.Code, ClientIp, ClientUserAgent, ct);
        if (tokens is null)
        {
            return ProblemResponse(StatusCodes.Status401Unauthorized, "Código de verificação inválido.");
        }

        RefreshCookie.Set(Response, tokens.RefreshToken, tokens.RefreshLifetime);
        return Ok(new AuthResponse("ok", tokens.AccessToken, "Bearer", tokens.ExpiresIn));
    }

    /// <summary>
    /// Seção 7.5: (re)gera os 10 recovery codes do próprio usuário — exibidos UMA única vez,
    /// só o hash é persistido; os anteriores são invalidados. Exige MFA já habilitada.
    /// </summary>
    [HttpPost("mfa/recovery-codes")]
    [Authorize]
    public async Task<IActionResult> GenerateRecoveryCodes(CancellationToken ct)
    {
        var codes = await authFlow.GenerateRecoveryCodesAsync(CurrentUser.UserId(User), ClientIp, ct);
        if (codes.Count == 0)
        {
            return ProblemResponse(StatusCodes.Status409Conflict,
                "MFA não está habilitada para este usuário.");
        }

        return Ok(new RecoveryCodesResponse(codes));
    }

    /// <summary>
    /// Seção 7.4: token 1 h; resposta SEMPRE genérica (202) para não revelar existência de
    /// conta. Rate limit por IP (mesma janela fixa do enroll) contra enumeração/abuso de e-mail.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingPolicies.Enroll)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return Accepted(); // genérico até para entrada inválida
        }

        var entries = await authFlow.CreatePasswordResetTokensAsync(request.Email.Trim(), ct);
        foreach (var entry in entries)
        {
            var link = $"{portalOptions.Value.BaseUrl.TrimEnd('/')}/redefinir-senha/{entry.RawToken}";
            await emailSender.SendAsync(new EmailMessage(
                entry.User.Email,
                "Redefinição de senha no +351 Monitor",
                $"""
                Olá,

                Recebemos um pedido para redefinir a senha da sua conta na organização {entry.OrganizationName} no +351 Monitor.

                Para escolher uma nova senha, abra o link abaixo (válido por 60 minutos):

                {link}

                Se você não pediu a redefinição, ignore este e-mail. Sua senha atual continua valendo.
                """), ct);
        }

        return Accepted();
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingPolicies.Enroll)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrEmpty(request.Password))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Informe o token e a nova senha.");
        }

        var outcome = await authFlow.ResetPasswordAsync(request.Token, request.Password, ClientIp, ct);
        return outcome switch
        {
            PasswordResetOutcome.Success => NoContent(),
            PasswordResetOutcome.WeakPassword => ProblemResponse(StatusCodes.Status400BadRequest,
                $"A senha deve ter no mínimo {AuthFlowService.MinPasswordLength} caracteres.",
                code: "weak_password"),
            _ => ProblemResponse(StatusCodes.Status400BadRequest,
                "Link inválido ou expirado. Solicite uma nova recuperação de senha.",
                code: "reset_invalid"),
        };
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var raw = RefreshCookie.Get(Request);
        if (raw is null)
        {
            return ProblemResponse(StatusCodes.Status401Unauthorized, "Sessão expirada. Faça login novamente.");
        }

        var tokens = await authFlow.RefreshAsync(raw, ClientIp, ClientUserAgent, ct);
        if (tokens is null)
        {
            RefreshCookie.Delete(Response);
            return ProblemResponse(StatusCodes.Status401Unauthorized, "Sessão expirada. Faça login novamente.");
        }

        RefreshCookie.Set(Response, tokens.RefreshToken, tokens.RefreshLifetime);
        return Ok(new AuthResponse("ok", tokens.AccessToken, "Bearer", tokens.ExpiresIn));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await authFlow.LogoutAsync(CurrentUser.UserId(User), RefreshCookie.Get(Request), ct);
        RefreshCookie.Delete(Response);
        return NoContent();
    }

    /// <summary>
    /// Preview público do convite para a tela /convite/:token (Seção 8.2:
    /// "Você foi convidado(a) para {org} como {papel}"). 404 inexistente; 410 expirado/usado.
    /// </summary>
    [HttpGet("invite/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> InvitePreview(string token, CancellationToken ct)
    {
        var hash = TokenGenerator.Sha256(token);

        // endpoint anônimo: não há tenant no contexto → busca explícita sem filtro global
        var invitation = await db.Invitations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);

        if (invitation is null)
        {
            return ProblemResponse(StatusCodes.Status404NotFound, "Convite não encontrado.");
        }

        if (invitation.AcceptedAt is not null || invitation.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return ProblemResponse(StatusCodes.Status410Gone,
                "Este convite expirou ou já foi utilizado.", code: "invite_expired");
        }

        var org = await db.Organizations.IgnoreQueryFilters()
            .FirstAsync(o => o.Id == invitation.TenantId, ct);

        return Ok(new InvitePreviewResponse(
            invitation.Email,
            invitation.Role.ToDbValue(),
            org.Name,
            invitation.Role.RequiresMfa()));
    }

    [HttpPost("invite/accept")]
    [AllowAnonymous]
    public async Task<IActionResult> AcceptInvite([FromBody] InviteAcceptRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrEmpty(request.Password))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Informe o token do convite e a nova senha.");
        }

        var result = await authFlow.AcceptInviteAsync(
            request.Token, request.Password, request.DisplayName, ClientIp, ClientUserAgent, ct);

        switch (result.Outcome)
        {
            case InviteAcceptOutcome.Success:
                RefreshCookie.Set(Response, result.Tokens!.RefreshToken, result.Tokens.RefreshLifetime);
                return Ok(new AuthResponse("ok", result.Tokens.AccessToken, "Bearer", result.Tokens.ExpiresIn));

            case InviteAcceptOutcome.MfaSetupRequired:
                return Ok(new AuthResponse("mfa_setup_required", MfaToken: result.MfaToken));

            case InviteAcceptOutcome.Expired:
                return ProblemResponse(StatusCodes.Status410Gone,
                    "Este convite expirou ou já foi utilizado.", code: "invite_expired");

            case InviteAcceptOutcome.WeakPassword:
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    $"A senha deve ter no mínimo {AuthFlowService.MinPasswordLength} caracteres.",
                    code: "weak_password");

            default:
                return ProblemResponse(StatusCodes.Status404NotFound, "Convite não encontrado.");
        }
    }
}
