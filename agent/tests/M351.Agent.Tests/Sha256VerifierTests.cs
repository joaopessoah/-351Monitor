using System.Security.Cryptography;
using System.Text;
using M351.Agent.Core.Update;
using Xunit;

namespace M351.Agent.Tests;

public class Sha256VerifierTests
{
    [Fact]
    public void Hash_de_conteudo_conhecido_bate_com_o_esperado()
    {
        var bytes = Encoding.UTF8.GetBytes("MonitorAgent-1.1.0.msi conteudo de teste");
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var stream = new MemoryStream(bytes);
        var actual = Sha256Verifier.ComputeHex(stream);

        Assert.Equal(expected, actual);
        Assert.True(Sha256Verifier.Matches(actual, expected));
    }

    [Fact]
    public void Match_e_case_insensitive_e_tolerante_a_espacos()
    {
        var hex = new string('a', 64);
        Assert.True(Sha256Verifier.Matches(hex, hex.ToUpperInvariant()));
        Assert.True(Sha256Verifier.Matches(hex, $"  {hex}  "));
    }

    [Fact]
    public void Mismatch_rejeita()
    {
        var hex = new string('a', 64);
        var outro = new string('b', 64);
        Assert.False(Sha256Verifier.Matches(hex, outro));
    }

    [Fact]
    public void Esperado_nulo_ou_vazio_rejeita()
    {
        var hex = new string('a', 64);
        Assert.False(Sha256Verifier.Matches(hex, null));
        Assert.False(Sha256Verifier.Matches(hex, ""));
    }

    [Fact]
    public void Hash_de_arquivo_em_streaming()
    {
        var path = Path.Combine(Path.GetTempPath(), $"m351-msi-{Guid.NewGuid():N}.bin");
        var bytes = Encoding.UTF8.GetBytes("binario do msi");
        File.WriteAllBytes(path, bytes);
        try
        {
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expected, Sha256Verifier.ComputeFileHex(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
