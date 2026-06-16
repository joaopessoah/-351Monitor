using M351.Agent.Core.Update;
using Xunit;

namespace M351.Agent.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)]   // numerico, NAO lexicografico (string quebraria aqui)
    [InlineData("1.9.0", "1.10.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.1.0", "1.1", 0)]      // patch ausente == 0
    [InlineData("1.1", "1.0.9", 1)]
    [InlineData("1.0.10", "1.0.9", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    public void Compara_por_componente_numerico(string a, string b, int expectedSign)
    {
        Assert.True(SemVer.TryParse(a, out var va));
        Assert.True(SemVer.TryParse(b, out var vb));
        Assert.Equal(expectedSign, Math.Sign(va.CompareTo(vb)));
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]       // prefixo v
    [InlineData("  1.2.3  ", 1, 2, 3)]    // espacos
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("1", 1, 0, 0)]
    [InlineData("1.2.3-beta", 1, 2, 3)]   // pre-release ignorado no MVP
    [InlineData("1.2.3+build7", 1, 2, 3)] // build-metadata ignorado
    public void Parse_tolerante(string input, int major, int minor, int patch)
    {
        Assert.True(SemVer.TryParse(input, out var v));
        Assert.Equal(new SemVer(major, minor, patch), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.x.0")]
    [InlineData("1.2.3.4")]   // 4 componentes
    [InlineData("-1.0.0")]    // negativo
    public void Parse_invalido_retorna_false(string? input)
    {
        Assert.False(SemVer.TryParse(input, out _));
    }

    [Fact]
    public void Operadores_de_ordem()
    {
        SemVer.TryParse("1.10.0", out var maior);
        SemVer.TryParse("1.9.0", out var menor);
        SemVer.TryParse("1.10.0", out var igual);
        Assert.True(maior > menor);
        Assert.True(menor < maior);
        Assert.True(maior >= igual);
        Assert.True(maior <= igual);
        Assert.True(maior == igual);
        Assert.True(maior != menor);
    }
}
