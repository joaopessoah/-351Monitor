using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using M351.Agent.Core.Collectors;

namespace M351.Agent.Core.Win32;

/// <summary>
/// Ociosidade por sessão (Seção 6.2): GetLastInputInfo comparado ao tick corrente.
/// Coleta APENAS o fato da ociosidade — JAMAIS o conteúdo do input (Princípio 2).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32IdleTimeQuery : IIdleTimeQuery
{
    public long GetIdleMilliseconds()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };
        if (!NativeMethods.GetLastInputInfo(ref info)) return 0;

        // dwTime é o tick de 32 bits; a subtração em uint trata o wrap-around corretamente
        var now = unchecked((uint)Environment.TickCount64);
        return unchecked(now - info.dwTime);
    }
}
