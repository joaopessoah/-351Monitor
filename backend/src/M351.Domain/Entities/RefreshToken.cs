using System.Net;

namespace M351.Domain.Entities;

/// <summary>Tabela refresh_tokens — refresh opaco 30 dias, hash SHA-256, revogável (sem famílias no MVP).</summary>
public class RefreshToken : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? UserAgent { get; set; }
    public IPAddress? Ip { get; set; }

    public User? User { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
