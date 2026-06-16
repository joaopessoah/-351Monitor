using System.Security.Cryptography;

namespace M351.Agent.Core.Update;

/// <summary>
/// Verificacao de integridade do MSI baixado (Secao 6.7): SHA-256 do arquivo == sha256 do
/// manifesto. Comparacao case-insensitive e tolerante a espacos. Se nao bate, o MSI e descartado
/// e NAO instalado.
/// </summary>
public static class Sha256Verifier
{
    /// <summary>Hex (lowercase, 64 chars) do conteudo de um stream.</summary>
    public static string ComputeHex(Stream content)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Hex (lowercase) de um arquivo, lido em streaming (nunca todo em memoria).</summary>
    public static string ComputeFileHex(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ComputeHex(fs);
    }

    /// <summary>true se o hex calculado bate com o esperado (ambos normalizados).</summary>
    public static bool Matches(string actualHex, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        var expected = expectedHex.Trim();
        return string.Equals(actualHex.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}
