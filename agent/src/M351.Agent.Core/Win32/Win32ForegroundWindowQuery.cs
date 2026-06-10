using System.Runtime.Versioning;
using System.Text;
using M351.Agent.Core.Collectors;

namespace M351.Agent.Core.Win32;

/// <summary>
/// Coleta da janela ativa (Seção 6.2): GetForegroundWindow → GetWindowThreadProcessId →
/// OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) → QueryFullProcessImageNameW; título via
/// GetWindowTextW (cache do USER — não envia WM_GETTEXT cross-process, não trava com app congelado).
/// Apps UWP: janela do ApplicationFrameHost.exe → child window com PID ≠ frame → AUMID.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ForegroundWindowQuery : IForegroundWindowQuery
{
    public ForegroundSample? GetForegroundWindowInfo()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null; // troca de foco em andamento: sem evento, sem crash

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            var exePath = GetProcessImagePath(pid);
            if (exePath is null) return null; // processo finalizado entre chamadas: ignorar amostra

            var processName = Path.GetFileName(exePath).ToLowerInvariant();
            var title = GetWindowText(hwnd);
            string? appId = null;

            if (processName == "applicationframehost.exe")
            {
                var realPid = FindUwpChildProcess(hwnd, pid);
                if (realPid != 0)
                {
                    var realPath = GetProcessImagePath(realPid);
                    if (realPath is not null)
                    {
                        exePath = realPath;
                        processName = Path.GetFileName(realPath).ToLowerInvariant();
                    }
                    appId = GetAppUserModelId(realPid);
                }
            }

            return new ForegroundSample(processName, exePath, appId, title);
        }
        catch (Exception)
        {
            return null; // robustez: o loop de coleta nunca derruba o agente
        }
    }

    private static string? GetProcessImagePath(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            var buffer = new StringBuilder(size);
            return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string? GetWindowText(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0) return null;
        var buffer = new StringBuilder(length + 1);
        var copied = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString() : null;
    }

    /// <summary>EnumChildWindows na janela frame, localizando o child com PID ≠ frame (Seção 6.2).</summary>
    private static uint FindUwpChildProcess(IntPtr frameHwnd, uint framePid)
    {
        uint found = 0;
        NativeMethods.EnumChildWindows(frameHwnd, (child, lparam) =>
        {
            NativeMethods.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != 0 && childPid != framePid)
            {
                found = childPid;
                return false; // para a enumeração
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string? GetAppUserModelId(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            uint length = 0;
            _ = NativeMethods.GetApplicationUserModelId(handle, ref length, null);
            if (length == 0) return null;
            var buffer = new char[length];
            return NativeMethods.GetApplicationUserModelId(handle, ref length, buffer) == 0
                ? new string(buffer, 0, (int)Math.Max(length - 1, 0))
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
