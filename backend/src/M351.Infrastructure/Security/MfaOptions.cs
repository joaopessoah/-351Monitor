namespace M351.Infrastructure.Security;

public class MfaOptions
{
    public const string SectionName = "Mfa";

    /// <summary>Chave AES-256 (base64, 32 bytes) usada para cifrar o segredo TOTP (mfa_secret_enc).</summary>
    public string EncryptionKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "+351 Monitor";

    /// <summary>
    /// Quando true, Owner/Admin precisam de TOTP: setup no 1º acesso e código a cada login.
    /// Default false (decisão de 16/09/2026): nenhum acesso pede MFA, mesmo para quem já a configurou.
    /// </summary>
    public bool Enforced { get; set; } = false;
}
