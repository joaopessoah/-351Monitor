namespace M351.Domain.Entities;

public static class UserStatus
{
    public const string Invited = "invited";
    public const string Active = "active";
    public const string Disabled = "disabled";
}

/// <summary>Tabela users — usuários do PORTAL.</summary>
public class User : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Email { get; set; }

    /// <summary>Argon2id (64 MB, 3 iterações, paralelismo 4); NULL = convite pendente.</summary>
    public string? PasswordHash { get; set; }

    public required string DisplayName { get; set; }
    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>Segredo TOTP cifrado com AES-GCM (chave no secret store).</summary>
    public byte[]? MfaSecretEnc { get; set; }

    public bool MfaEnabled { get; set; }
    public int FailedLoginCount { get; set; }

    /// <summary>Lockout: 10 falhas → 15 min (N22).</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public string Status { get; set; } = UserStatus.Invited;
    public DateTimeOffset? LastLoginAt { get; set; }
}
