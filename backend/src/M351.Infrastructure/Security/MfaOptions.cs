namespace M351.Infrastructure.Security;

public class MfaOptions
{
    public const string SectionName = "Mfa";

    /// <summary>Chave AES-256 (base64, 32 bytes) usada para cifrar o segredo TOTP (mfa_secret_enc).</summary>
    public string EncryptionKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "+351 Monitor";
}
