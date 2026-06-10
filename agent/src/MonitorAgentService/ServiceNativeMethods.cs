using System.Runtime.InteropServices;
using System.Text;

namespace MonitorAgentService;

/// <summary>
/// P/Invoke do lado serviço (Seções 6.1/6.2): WTS (sessões), token de usuário e
/// CreateProcessAsUser para lançar 1 helper por sessão interativa.
/// </summary>
internal static class ServiceNativeMethods
{
    internal const int WTSActive = 0;
    internal const int WTSUserName = 5;
    internal const int WTSDomainName = 7;
    internal const int WTSClientProtocolType = 16;
    internal const uint TokenAllAccess = 0xF01FF;
    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version,
        out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass,
        out IntPtr buffer, out int bytesReturned);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
        IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    internal static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessAsUser(IntPtr token, string? applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    internal static string? QuerySessionString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes))
            return null;
        try
        {
            return bytes <= 2 ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    internal static short? QuerySessionProtocol(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTSClientProtocolType, out var buffer, out var bytes))
            return null;
        try
        {
            return bytes >= 2 ? Marshal.ReadInt16(buffer) : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    internal static List<(int SessionId, int State)> EnumerateSessions()
    {
        var result = new List<(int, int)>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var info, out var count)) return result;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<WTS_SESSION_INFO>(info + i * size);
                result.Add((item.SessionId, item.State));
            }
        }
        finally
        {
            WTSFreeMemory(info);
        }
        return result;
    }
}
