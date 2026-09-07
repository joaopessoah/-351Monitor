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
    int? GoalWorkRelatedPct,
    /// <summary>
    /// F6, decisão 1 — vocabulário dos rótulos de classificação da organização:
    /// "produtividade" (Produtivo / Neutro / Improdutivo / Sem classificação, default) ou
    /// "trabalho" (Relacionado ao trabalho / Neutro / Não relacionado ao trabalho / Não
    /// categorizado). Vem no /me porque TODA tela que mostra classificação precisa do rótulo
    /// certo antes do primeiro render, e o /me já é a query de sessão do portal.
    /// </summary>
    string ClassificationVocabulary,
    /// <summary>
    /// F6, decisão 5 — alertas de escopo PESSOA ligados pela organização. Vem no /me porque a
    /// tela de alertas precisa saber, antes do primeiro render, se as duas regras individuais
    /// estão sequer sendo avaliadas.
    /// </summary>
    bool PersonAlertsEnabled);

/// <summary>
/// GET/PATCH /me/email-prefs (F5): preferências de e-mail do próprio usuário. Sem linha no
/// banco valem os defaults (digest e alertas de frota ligados, jornada semanal desligada).
/// </summary>
public record EmailPrefsResponse(bool WeeklyDigest, bool FleetAlerts, bool JornadaWeekly);

/// <summary>Resposta de GET /api/v1/me (Seção 7.4: perfil + papel + org).</summary>
public record MeResponse(MeUserResponse User, MeOrganizationResponse Organization);
