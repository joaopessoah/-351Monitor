using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OtpNet;

namespace M351.Infrastructure.Security;

public interface IMfaService
{
    /// <summary>Gera um segredo TOTP novo (20 bytes). Retorna (segredo base32, segredo cifrado p/ persistir).</summary>
    (string SecretBase32, byte[] SecretEncrypted) GenerateSecret();

    /// <summary>Monta a URI otpauth:// para apps autenticadores.</summary>
    string BuildOtpAuthUri(string secretBase32, string accountEmail);

    /// <summary>Valida um código TOTP contra o segredo cifrado persistido (janela ±1 passo de 30 s).</summary>
    bool VerifyCode(byte[] secretEncrypted, string code);
}

/// <summary>TOTP (Otp.NET) com segredo cifrado em AES-GCM (nonce 12B + tag 16B + ciphertext).</summary>
public class MfaService : IMfaService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;
    private readonly string _issuer;

    public MfaService(IOptions<MfaOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.EncryptionKey);
        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Mfa:EncryptionKey deve ser uma chave AES-256 (32 bytes em base64).");
        }

        _issuer = options.Value.Issuer;
    }

    public (string SecretBase32, byte[] SecretEncrypted) GenerateSecret()
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        return (Base32Encoding.ToString(secret), Encrypt(secret));
    }

    public string BuildOtpAuthUri(string secretBase32, string accountEmail)
    {
        var issuer = Uri.EscapeDataString(_issuer);
        var account = Uri.EscapeDataString(accountEmail);
        return $"otpauth://totp/{issuer}:{account}?secret={secretBase32}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }

    public bool VerifyCode(byte[] secretEncrypted, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var secret = Decrypt(secretEncrypted);
        var totp = new Totp(secret);
        return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(previous: 1, future: 1));
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        ciphertext.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    private byte[] Decrypt(byte[] payload)
    {
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var ciphertext = payload.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
