using System.Text.Json;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Domain;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Controllers;

/// <summary>GET /api/v1/me (Seção 7.4, papel mínimo Viewer): perfil + papel + org.</summary>
[Route("api/v1/me")]
[Authorize]
public class MeController(M351DbContext db) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = CurrentUser.UserId(User);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return ProblemResponse(StatusCodes.Status401Unauthorized, "Sessão inválida.");
        }

        // filtro global por tenant garante a org do token
        var org = await db.Organizations.FirstAsync(ct);

        // business_hours: jsonb cru da org exposto como objeto (Clone() desprende do doc descartado)
        JsonElement? businessHours = null;
        if (org.BusinessHours is not null)
        {
            using var doc = JsonDocument.Parse(org.BusinessHours);
            businessHours = doc.RootElement.Clone();
        }

        return Ok(new MeResponse(
            new MeUserResponse(user.Id, user.Email, user.DisplayName, user.Role.ToDbValue()),
            new MeOrganizationResponse(
                org.Id, org.Name, org.Slug, org.Timezone, businessHours,
                org.Plan, org.DeviceLimit, org.OnboardingChecklistDismissedAt)));
    }
}
