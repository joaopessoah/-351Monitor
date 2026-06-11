using System.Text.Json;

namespace M351.Api.Contracts;

public record MeUserResponse(Guid Id, string Email, string DisplayName, string Role);

/// <summary>
/// business_hours é o jsonb CRU da org ({"days":[1..5],"start":"08:00","end":"18:00"})
/// ou null — o portal usa como default da janela "Horário de trabalho" (Seção 8.5).
/// </summary>
public record MeOrganizationResponse(Guid Id, string Name, string Slug, string Timezone, JsonElement? BusinessHours);

/// <summary>Resposta de GET /api/v1/me (Seção 7.4: perfil + papel + org).</summary>
public record MeResponse(MeUserResponse User, MeOrganizationResponse Organization);
