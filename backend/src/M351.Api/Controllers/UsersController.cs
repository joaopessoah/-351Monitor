using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Email;
using M351.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M351.Api.Controllers;

[Route("api/v1/users")]
[Authorize(Policy = AuthConstants.PolicyAdminPlus)]
public class UsersController(
    M351DbContext db,
    IEmailSender emailSender,
    AuditWriter audit,
    IOptions<PortalOptions> portalOptions) : ApiControllerBase
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var users = await db.Users
            .OrderBy(u => u.Email)
            .Select(u => new UserResponse(u.Id, u.Email, u.DisplayName, u.Role.ToDbValue(), u.Status, u.MfaEnabled, u.LastLoginAt))
            .ToListAsync(ct);

        return Ok(new { items = users });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null
            ? NotFoundProblem()
            : Ok(new UserResponse(user.Id, user.Email, user.DisplayName, user.Role.ToDbValue(), user.Status, user.MfaEnabled, user.LastLoginAt));
    }

    /// <summary>Convida usuário por e-mail (token 7 dias, single-use). Convidar Owner exige Owner.</summary>
    [HttpPost("invitations")]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Informe um e-mail válido.");
        }

        if (!UserRoleExtensions.TryFromDbValue(request.Role, out var role))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Papel inválido. Use owner, admin ou viewer.");
        }

        if (role == UserRole.Owner && CurrentUser.Role(User) != UserRole.Owner)
        {
            return ProblemResponse(StatusCodes.Status403Forbidden, "Apenas um Owner pode convidar outro Owner.");
        }

        var email = request.Email.Trim();
        var exists = await db.Users.AnyAsync(u => u.Email == email, ct);
        if (exists)
        {
            return ProblemResponse(StatusCodes.Status409Conflict, "Já existe um usuário com este e-mail.");
        }

        var tenantId = CurrentUser.TenantId(User);
        var actorId = CurrentUser.UserId(User);

        var user = new User
        {
            Id = Uuid7.NewUuid7(),
            TenantId = tenantId,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email.Split('@')[0] : request.DisplayName.Trim(),
            Role = role,
            Status = UserStatus.Invited,
        };

        var token = TokenGenerator.NewOpaqueToken();
        var invitation = new Invitation
        {
            Id = Uuid7.NewUuid7(),
            TenantId = tenantId,
            Email = email,
            Role = role,
            TokenHash = TokenGenerator.Sha256(token),
            ExpiresAt = DateTimeOffset.UtcNow.Add(InvitationLifetime),
            InvitedBy = actorId,
        };

        db.Users.Add(user);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        var org = await db.Organizations.FirstAsync(ct);
        var link = $"{portalOptions.Value.BaseUrl.TrimEnd('/')}/convite/{token}";
        await emailSender.SendAsync(new EmailMessage(
            email,
            $"Você foi convidado(a) para {org.Name} no +351 Monitor",
            $"""
            Olá,

            Você foi convidado(a) para a organização {org.Name} no +351 Monitor com o papel {role.ToDbValue()}.

            Para criar sua senha e acessar o portal, abra o link abaixo (válido por 7 dias):

            {link}

            Se você não esperava este convite, ignore este e-mail.
            """), ct);

        return CreatedAtAction(nameof(GetById), new { id = user.Id },
            new InviteUserResponse(user.Id, invitation.Id, invitation.ExpiresAt));
    }

    /// <summary>Altera o papel. Mexer em Owner (origem ou destino) exige Owner; sempre ≥ 1 Owner ativo.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFoundProblem();
        }

        if (!UserRoleExtensions.TryFromDbValue(request.Role, out var newRole))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Papel inválido. Use owner, admin ou viewer.");
        }

        var actorRole = CurrentUser.Role(User);
        if ((user.Role == UserRole.Owner || newRole == UserRole.Owner) && actorRole != UserRole.Owner)
        {
            return ProblemResponse(StatusCodes.Status403Forbidden, "Apenas um Owner pode alterar papéis de Owner.");
        }

        if (user.Role == UserRole.Owner && newRole != UserRole.Owner)
        {
            var hasAnotherOwner = await db.Users.AnyAsync(
                u => u.Id != user.Id && u.Role == UserRole.Owner && u.Status == UserStatus.Active, ct);
            if (!hasAnotherOwner)
            {
                return ProblemResponse(StatusCodes.Status409Conflict,
                    "A organização precisa de pelo menos um Owner ativo.");
            }
        }

        var oldRole = user.Role;
        user.Role = newRole;

        audit.Add(CurrentUser.TenantId(User), AuditActions.UpdateUserRole, CurrentUser.UserId(User),
            HttpContext.Connection.RemoteIpAddress, targetType: "user", targetId: user.Id,
            detailJson: $$"""{"from":"{{oldRole.ToDbValue()}}","to":"{{newRole.ToDbValue()}}"}""");

        await db.SaveChangesAsync(ct);
        return Ok(new UserResponse(user.Id, user.Email, user.DisplayName, user.Role.ToDbValue(), user.Status, user.MfaEnabled, user.LastLoginAt));
    }

    /// <summary>Desativa o usuário (status=disabled) e revoga seus refresh tokens.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFoundProblem();
        }

        if (user.Role == UserRole.Owner)
        {
            if (CurrentUser.Role(User) != UserRole.Owner)
            {
                return ProblemResponse(StatusCodes.Status403Forbidden, "Apenas um Owner pode desativar outro Owner.");
            }

            var hasAnotherOwner = await db.Users.AnyAsync(
                u => u.Id != user.Id && u.Role == UserRole.Owner && u.Status == UserStatus.Active, ct);
            if (!hasAnotherOwner)
            {
                return ProblemResponse(StatusCodes.Status409Conflict,
                    "A organização precisa de pelo menos um Owner ativo.");
            }
        }

        user.Status = UserStatus.Disabled;

        var now = DateTimeOffset.UtcNow;
        var tokens = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
