namespace M351.Domain.Entities;

/// <summary>
/// Tabela password_reset_tokens — recuperação de senha (Seção 7.4: token 1 h, resposta sempre
/// genérica). Single-use: usado ou expirado nunca é reutilizável; pedir de novo invalida os
/// anteriores não usados do mesmo usuário.
/// </summary>
public class PasswordResetToken : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 do token do link (o token cru nunca é persistido).</summary>
    public required byte[] TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
