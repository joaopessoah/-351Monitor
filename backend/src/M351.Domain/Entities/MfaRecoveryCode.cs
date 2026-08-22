namespace M351.Domain.Entities;

/// <summary>
/// Tabela mfa_recovery_codes — códigos de recuperação de MFA (Seção 7.5: "10 recovery codes
/// hasheados"). Single-use; regenerar invalida todos os anteriores. Aceitos no lugar do TOTP
/// apenas quando a MFA já está habilitada (nunca concluem o setup).
/// </summary>
public class MfaRecoveryCode : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 do código normalizado (maiúsculas, sem separadores).</summary>
    public required byte[] CodeHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
