using System.Text.Json;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Reports;
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
                org.Plan, org.DeviceLimit, org.OnboardingChecklistDismissedAt,
                org.GoalWeeklyActiveHours, org.GoalWorkRelatedPct)));
    }

    /// <summary>
    /// F5 — preferências de e-mail do PRÓPRIO usuário (digest semanal, alertas de frota,
    /// jornada semanal). Linha ausente = defaults (digest e alertas ligados, jornada
    /// desligada), então o GET responde os defaults sem materializar nada.
    /// </summary>
    [HttpGet("email-prefs")]
    public async Task<IActionResult> GetEmailPrefs(CancellationToken ct)
    {
        var userId = CurrentUser.UserId(User);
        var prefs = await db.UserEmailPrefs.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return Ok(ToResponse(prefs));
    }

    /// <summary>
    /// Atualização parcial das próprias preferências (campos ausentes não mudam). Sem
    /// auditoria: é preferência de notificação do próprio usuário, não config de privacidade
    /// nem acesso a dado pessoal de titular. Único gate: jornada_weekly só LIGA no plano Pro
    /// (403 fora dele), porque relatório agendado é feature paga.
    /// </summary>
    [HttpPatch("email-prefs")]
    public async Task<IActionResult> PatchEmailPrefs([FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Corpo inválido: envie um objeto JSON.");
        }

        var userId = CurrentUser.UserId(User);
        var prefs = await db.UserEmailPrefs.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
        {
            prefs = new UserEmailPrefs { UserId = userId, TenantId = CurrentUser.TenantId(User) };
            db.UserEmailPrefs.Add(prefs);
        }

        if (TryGetBool(body, "weekly_digest", out var digest)) prefs.WeeklyDigest = digest;
        if (TryGetBool(body, "fleet_alerts", out var alerts)) prefs.FleetAlerts = alerts;

        if (TryGetBool(body, "jornada_weekly", out var jornada))
        {
            // GATE DE PLANO: relatório agendado por e-mail é exclusivo do Pro
            // (docs/design/05-produto-mvp.md). O gate vive no plano da org, a flag por tenant do
            // backoffice. LIGAR fora do Pro é 403; DESLIGAR é sempre permitido, para um downgrade
            // não deixar ninguém preso a uma assinatura que não consegue cancelar.
            if (jornada && !await db.Organizations.AnyAsync(o => o.Plan == JornadaWeeklyReportService.RequiredPlan, ct))
            {
                return ProblemResponse(StatusCodes.Status403Forbidden,
                    "O relatório de jornada semanal por e-mail é exclusivo do plano Pro.",
                    detail: "Fale com a gente para habilitar no seu plano.");
            }

            prefs.JornadaWeekly = jornada;
        }

        prefs.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToResponse(prefs));
    }

    private static EmailPrefsResponse ToResponse(UserEmailPrefs? prefs) => prefs is null
        ? new EmailPrefsResponse(true, true, false)
        : new EmailPrefsResponse(prefs.WeeklyDigest, prefs.FleetAlerts, prefs.JornadaWeekly);

    private static bool TryGetBool(JsonElement body, string field, out bool value)
    {
        value = false;
        if (!body.TryGetProperty(field, out var el)
            || el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = el.GetBoolean();
        return true;
    }
}
