using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace M351.Agent.Core.Security;

/// <summary>Proteção de segredos em repouso (device_token, enrollment key) — Seção 5.7.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string? Unprotect(string protectedBase64);
}

/// <summary>
/// DPAPI escopo MÁQUINA (CRYPTPROTECT_LOCAL_MACHINE): o serviço (SYSTEM) e o modo console
/// decifram o mesmo blob nesta máquina; o blob é inútil fora dela.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("M351.MonitorAgent.v1");

    public string Protect(string plaintext)
    {
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(blob);
    }

    public string? Unprotect(string protectedBase64)
    {
        try
        {
            var blob = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(blob);
        }
        catch (Exception)
        {
            return null; // blob corrompido ou de outra máquina
        }
    }
}

/// <summary>SOMENTE para testes unitários (sem DPAPI).</summary>
public sealed class PlaintextSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string? Unprotect(string protectedBase64) => protectedBase64;
}
