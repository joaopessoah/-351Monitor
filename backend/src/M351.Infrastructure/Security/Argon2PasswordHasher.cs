using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace M351.Infrastructure.Security;

/// <summary>
/// Argon2id no formato PHC: $argon2id$v=19$m=...,t=...,p=...$salt$hash (base64 sem padding).
/// Os parâmetros são lidos do PRÓPRIO hash na verificação — mudar a config não invalida hashes antigos.
/// </summary>
public class Argon2PasswordHasher(IOptions<PasswordHashingOptions> options) : IPasswordHasher
{
    private readonly PasswordHashingOptions _options = options.Value;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(_options.SaltBytes);
        var hash = Compute(password, salt, _options.MemoryKb, _options.Iterations, _options.Parallelism, _options.HashBytes);

        return string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={_options.MemoryKb},t={_options.Iterations},p={_options.Parallelism}${B64(salt)}${B64(hash)}");
    }

    public bool Verify(string password, string encodedHash)
    {
        try
        {
            var parts = encodedHash.Split('$', StringSplitOptions.RemoveEmptyEntries);
            // [argon2id, v=19, m=..,t=..,p=.., salt, hash]
            if (parts.Length != 5 || parts[0] != "argon2id")
            {
                return false;
            }

            int memoryKb = 0, iterations = 0, parallelism = 0;
            foreach (var kv in parts[2].Split(','))
            {
                var pair = kv.Split('=');
                switch (pair[0])
                {
                    case "m": memoryKb = int.Parse(pair[1], CultureInfo.InvariantCulture); break;
                    case "t": iterations = int.Parse(pair[1], CultureInfo.InvariantCulture); break;
                    case "p": parallelism = int.Parse(pair[1], CultureInfo.InvariantCulture); break;
                }
            }

            var salt = FromB64(parts[3]);
            var expected = FromB64(parts[4]);
            var actual = Compute(password, salt, memoryKb, iterations, parallelism, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int hashBytes)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(hashBytes);
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    private static byte[] FromB64(string value)
    {
        var padded = (value.Length % 4) switch
        {
            2 => value + "==",
            3 => value + "=",
            _ => value,
        };
        return Convert.FromBase64String(padded);
    }
}
