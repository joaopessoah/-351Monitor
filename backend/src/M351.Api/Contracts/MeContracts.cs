using System.Text.Json;

namespace M351.Api.Contracts;

public record MeUserResponse(Guid Id, string Email, string DisplayName, string Role);

/// <summary>
/// business_hours é o jsonb CRU da org ({"days":[1..5],"start":"08:00","end":"18:00"})
/// ou null — o portal usa como default da janela "Horário de trabalho" (Seção 8.5).
/// plan/device_limit alimentam o medidor de dispositivos do plano; o checklist de
/// primeiros passos (Seção 8.3 passo 4) usa onboarding_checklist_dismissed_at.
/// </summary>
public record MeOrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    string Timezone,
    JsonElement? BusinessHours,
    string Plan,
    int? DeviceLimit,
    DateTimeOffset? OnboardingChecklistDismissedAt,
    int? GoalWeeklyActiveHours,
    int? GoalWorkRelatedPct);

/// <summary>
/// GET/PATCH /me/email-prefs (F5): preferências de e-mail do próprio usuário. Sem linha no
/// banco valem os defaults (digest e alertas de frota ligados, jornada semanal desligada).
/// </summary>
public record EmailPrefsResponse(bool WeeklyDigest, bool FleetAlerts, bool JornadaWeekly);

/// <summary>Resposta de GET /api/v1/me (Seção 7.4: perfil + papel + org).</summary>
public record MeResponse(MeUserResponse User, MeOrganizationResponse Organization);
