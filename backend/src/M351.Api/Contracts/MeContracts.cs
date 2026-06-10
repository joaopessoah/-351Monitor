namespace M351.Api.Contracts;

public record MeUserResponse(Guid Id, string Email, string DisplayName, string Role);

public record MeOrganizationResponse(Guid Id, string Name, string Slug, string Timezone);

/// <summary>Resposta de GET /api/v1/me (Seção 7.4: perfil + papel + org).</summary>
public record MeResponse(MeUserResponse User, MeOrganizationResponse Organization);
