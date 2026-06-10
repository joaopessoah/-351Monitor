using M351.Api.Auth;
using M351.Infrastructure.Data;

namespace M351.Api.Middleware;

/// <summary>
/// Princípio 4: o tenant vem SEMPRE do JWT, nunca da URL. Após a autenticação, propaga o claim
/// org_id para o TenantContext (scoped) que alimenta o filtro global do EF e o interceptor.
/// </summary>
public class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        var orgClaim = context.User.FindFirst(AuthConstants.ClaimOrgId)?.Value;
        if (Guid.TryParse(orgClaim, out var tenantId))
        {
            tenantContext.TenantId = tenantId;
        }

        await next(context);
    }
}
