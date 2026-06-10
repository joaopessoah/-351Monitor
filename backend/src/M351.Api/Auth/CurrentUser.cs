using System.Security.Claims;
using M351.Domain;

namespace M351.Api.Auth;

/// <summary>Acesso tipado aos claims do usuário autenticado.</summary>
public static class CurrentUser
{
    public static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(AuthConstants.ClaimSub)
            ?? throw new InvalidOperationException("Token sem claim sub."));

    public static Guid TenantId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(AuthConstants.ClaimOrgId)
            ?? throw new InvalidOperationException("Token sem claim org_id."));

    public static UserRole Role(ClaimsPrincipal principal) =>
        UserRoleExtensions.FromDbValue(principal.FindFirstValue(AuthConstants.ClaimRole)
            ?? throw new InvalidOperationException("Token sem claim role."));
}
