using System.Runtime.InteropServices;
using System.Text;

namespace M351.Agent.Core.Win32;

/// <summary>
/// P/Invoke direto (DllImport) — sem dependência externa de interop.
/// SOMENTE as APIs da lista fechada de coleta (Seções 6.2 e 9.1). Por princípio arquitetural
/// (Princípio 2), JAMAIS adicionar aqui: hooks de teclado, captura de tela, clipboard,
/// leitura de conteúdo, injeção — o código não existe.
/// </summary>
internal static partial class NativeMethods
{
    internal const int ProcessQueryLimitedInformation = 0x1000;
    internal const int SmCMonitors = 80; // GetSystemMetrics(SM_CMONITORS)

    // ------------------------------------------------------------- user32

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    internal delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc callback, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    // ------------------------------------------------------------- kernel32

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

    /// <summary>AUMID do app UWP real (janela hospedada pelo ApplicationFrameHost — Seção 6.2).</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength, char[]? applicationUserModelId);

    // ------------------------------------------------------------- netapi32 (join_type do AGENT_START)

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out int joinStatus);

    [DllImport("netapi32.dll")]
    internal static extern int NetApiBufferFree(IntPtr buffer);

    internal const int NetSetupDomainName = 3;
}
