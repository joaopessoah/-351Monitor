using System.Security.Cryptography;

namespace M351.Infrastructure.Security;

/// <summary>
/// Enrollment key (Seção 5.7): `ek_` + 12 chars aleatórios base62.
/// Armazenada como SHA-256 + prefixo visível (ex.: `ek_4Qz8`).
/// </summary>
public static class EnrollmentKeyGenerator
{
    public const string Prefix = "ek_";
    public const int RandomLength = 12;
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string NewKey()
    {
        Span<char> chars = stackalloc char[RandomLength];
        for (var i = 0; i < RandomLength; i++)
        {
            chars[i] = Base62[RandomNumberGenerator.GetInt32(Base62.Length)];
        }

        return Prefix + new string(chars);
    }

    /// <summary>Prefixo visível no portal: 'ek_' + 4 primeiros chars do segredo (ex.: 'ek_4Qz8').</summary>
    public static string VisiblePrefix(string fullKey) => fullKey[..(Prefix.Length + 4)];
}
