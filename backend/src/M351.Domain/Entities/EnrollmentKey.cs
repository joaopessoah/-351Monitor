namespace M351.Domain.Entities;

/// <summary>Tabela enrollment_keys — chave de instalação `ek_` + 12 chars (base62), SHA-256 + prefixo visível.</summary>
public class EnrollmentKey : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Ex.: 'ek_4Qz8' (visível no portal).</summary>
    public required string KeyPrefix { get; set; }

    /// <summary>SHA-256 da chave completa (ek_ + 12 chars).</summary>
    public required byte[] KeyHash { get; set; }

    public string? Label { get; set; }
    public int? MaxUses { get; set; }
    public int UseCount { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
