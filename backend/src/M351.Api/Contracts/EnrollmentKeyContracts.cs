namespace M351.Api.Contracts;

public record EnrollmentKeyResponse(
    Guid Id,
    string KeyPrefix,
    string? Label,
    int? MaxUses,
    int UseCount,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

public record CreateEnrollmentKeyRequest(string? Label, int? MaxUses, DateTimeOffset? ExpiresAt);

/// <summary>O segredo completo (key) é exibido UMA única vez, na criação.</summary>
public record CreateEnrollmentKeyResponse(
    Guid Id,
    string Key,
    string KeyPrefix,
    string? Label,
    int? MaxUses,
    DateTimeOffset? ExpiresAt);
