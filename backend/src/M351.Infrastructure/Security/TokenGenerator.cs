using System.Security.Cryptography;
using System.Text;

namespace M351.Infrastructure.Security;

/// <summary>Geração de tokens opacos (refresh, convite) e hashing SHA-256 para armazenamento.</summary>
public static class TokenGenerator
{
    /// <summary>Token opaco URL-safe (base64url, sem padding) com <paramref name="bytes"/> de entropia.</summary>
    public static string NewOpaqueToken(int bytes = 32)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(buffer)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] Sha256(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
