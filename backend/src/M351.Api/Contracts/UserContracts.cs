namespace M351.Api.Contracts;

public record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    string Status,
    bool MfaEnabled,
    DateTimeOffset? LastLoginAt);

public record InviteUserRequest(string? Email, string? Role, string? DisplayName);

public record InviteUserResponse(Guid UserId, Guid InvitationId, DateTimeOffset ExpiresAt);

public record UpdateUserRequest(string? Role);
