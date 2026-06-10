using System.Text.RegularExpressions;
using M351.Infrastructure.Security;

namespace M351.IntegrationTests.Unit;

/// <summary>Geração da enrollment key (Seção 5.7): `ek_` + 12 chars base62, prefixo visível ek_XXXX.</summary>
public partial class EnrollmentKeyGeneratorTests
{
    [GeneratedRegex("^ek_[0-9A-Za-z]{12}$")]
    private static partial Regex KeyFormat();

    [Fact]
    public void NewKey_TemFormato_ekMais12CharsBase62()
    {
        for (var i = 0; i < 50; i++)
        {
            var key = EnrollmentKeyGenerator.NewKey();
            Assert.Matches(KeyFormat(), key);
            Assert.Equal(15, key.Length); // "ek_" + 12
        }
    }

    [Fact]
    public void VisiblePrefix_EhEkMais4PrimeirosChars()
    {
        var key = "ek_4Qz8kT2mWx9P";
        Assert.Equal("ek_4Qz8", EnrollmentKeyGenerator.VisiblePrefix(key));
    }

    [Fact]
    public void NewKey_NaoRepete()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => EnrollmentKeyGenerator.NewKey()).ToHashSet();
        Assert.Equal(200, keys.Count);
    }
}
