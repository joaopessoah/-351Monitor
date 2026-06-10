using System.Security.Cryptography;
using System.Text;
using M351.Agent.Core.Fingerprint;
using Xunit;

namespace M351.Agent.Tests;

public class FingerprintTests
{
    private sealed class FakeSource(string? guid, string? serial) : IFingerprintSource
    {
        public int Calls { get; private set; }
        public string? GetMachineGuid() { Calls++; return guid; }
        public string? GetBiosSerial() => serial;
    }

    [Fact]
    public void Fingerprint_e_SHA256_hex_lowercase_de_guid_mais_serial()
    {
        var source = new FakeSource("4c4c4544-0042-3010-8051-b4c04f4d3732", "BR12345");
        var fingerprint = MachineFingerprint.Compute(source);

        var expected = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("4c4c4544-0042-3010-8051-b4c04f4d3732BR12345"))).ToLowerInvariant();

        Assert.Equal(expected, fingerprint);
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint, fingerprint.ToLowerInvariant());
    }

    [Fact]
    public void Fingerprint_e_estavel_entre_chamadas()
    {
        var source = new FakeSource("guid-fixo", "serial-fixo");
        Assert.Equal(MachineFingerprint.Compute(source), MachineFingerprint.Compute(source));
    }

    [Fact]
    public void Serial_diferente_gera_fingerprint_diferente()
    {
        Assert.NotEqual(
            MachineFingerprint.Compute(new FakeSource("guid", "serial-1")),
            MachineFingerprint.Compute(new FakeSource("guid", "serial-2")));
    }

    [Fact]
    public void Fontes_nulas_nao_quebram_e_geram_valor_estavel()
    {
        var fingerprint = MachineFingerprint.Compute(new FakeSource(null, null));
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint, MachineFingerprint.Compute(new FakeSource(null, null)));
    }
}
