using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace M351.Agent.Core.Fingerprint;

/// <summary>Fontes do fingerprint (mockáveis em teste).</summary>
public interface IFingerprintSource
{
    /// <summary>MachineGuid de HKLM\SOFTWARE\Microsoft\Cryptography.</summary>
    string? GetMachineGuid();

    /// <summary>Serial do BIOS via WMI Win32_BIOS.</summary>
    string? GetBiosSerial();
}

/// <summary>
/// machine_fingerprint = SHA-256 hex de (MachineGuid + serial do BIOS) — Seção 5.7.
/// Estável entre reinstalações: o backend faz re-enroll idempotente por (tenant_id, fingerprint).
/// </summary>
public static class MachineFingerprint
{
    public static string Compute(IFingerprintSource source)
    {
        var guid = source.GetMachineGuid() ?? "";
        var serial = source.GetBiosSerial() ?? "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(guid + serial));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsFingerprintSource : IFingerprintSource
{
    public string? GetMachineGuid()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string? GetBiosSerial()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT SerialNumber FROM Win32_BIOS");
            foreach (var obj in searcher.Get())
            {
                var serial = obj["SerialNumber"] as string;
                if (!string.IsNullOrWhiteSpace(serial)) return serial.Trim();
            }
        }
        catch (Exception)
        {
            // WMI indisponível: fingerprint segue só com MachineGuid
        }
        return null;
    }
}
