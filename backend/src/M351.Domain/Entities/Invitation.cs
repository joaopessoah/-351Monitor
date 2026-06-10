namespace M351.Domain.Entities;

/// <summary>Tabela invitations — convite por e-mail, token 7 dias, single-use.</summary>
public class Invitation : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Email { get; set; }
    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>SHA-256 do token do link.</summary>
    public required byte[] TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? InvitedBy { get; set; }
}
