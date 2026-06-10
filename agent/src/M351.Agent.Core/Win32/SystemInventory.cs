using System.Runtime.Versioning;
using M351.Agent.Core.Contracts;

namespace M351.Agent.Core.Win32;

/// <summary>
/// Inventário do AGENT_START (Seção 5.3): hostname, SO, monitores, is_vm, join_type.
/// Coletado no enrollment e a cada AGENT_START (Seção 6.2).
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemInventory
{
    public static AgentStartData BuildAgentStartData(string bootId, string startReason, long uptimeMs) => new()
    {
        AgentVersion = AgentVersionInfo.Current,
        OsVersion = DescribeOs(),
        OsBuild = Environment.OSVersion.Version.Build.ToString(),
        Hostname = Environment.MachineName,
        BootId = bootId,
        UptimeMs = uptimeMs,
        StartReason = startReason,
        Monitors = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SmCMonitors), 1),
        IsVm = DetectVm(),
        JoinType = DetectJoinType()
    };

    /// <summary>Ex.: "Windows 11 Pro 23H2 (22631)".</summary>
    public static string DescribeOs()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var displayVersion = key?.GetValue("DisplayVersion") as string;
            var build = Environment.OSVersion.Version.Build;

            // o registro reporta "Windows 10" mesmo no 11 (quirk conhecido); corrigir pelo build
            if (build >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal))
                product = string.Concat("Windows 11", product.AsSpan("Windows 10".Length));

            return displayVersion is null ? $"{product} ({build})" : $"{product} {displayVersion} ({build})";
        }
        catch (Exception)
        {
            return Environment.OSVersion.VersionString;
        }
    }

    private static bool DetectVm()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get())
            {
                var text = $"{obj["Manufacturer"]} {obj["Model"]}";
                if (text.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("vmware", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("kvm", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("qemu", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("hyper-v", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception) { /* WMI indisponível: assume físico */ }
        return false;
    }

    /// <summary>ad | aad | workgroup (aad por heurística de registro — best-effort no MVP).</summary>
    private static string DetectJoinType()
    {
        try
        {
            var rc = NativeMethods.NetGetJoinInformation(null, out var buffer, out var status);
            if (buffer != IntPtr.Zero) _ = NativeMethods.NetApiBufferFree(buffer);
            if (rc == 0 && status == NativeMethods.NetSetupDomainName) return "ad";
        }
        catch (Exception) { /* segue para a heurística aad */ }

        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
            if (key is not null && key.SubKeyCount > 0) return "aad";
        }
        catch (Exception) { /* sem acesso: workgroup */ }

        return "workgroup";
    }
}
