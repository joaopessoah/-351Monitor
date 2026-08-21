using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace MonitorAgentService;

/// <summary>
/// Resolve o SID de "DOMÍNIO\usuário" via LookupAccountName (advapi32) — Seção 6.2 ("SID via
/// token") tem uma janela: o SESSION_START chega ANTES de EnsureHelper criar o host da sessão,
/// então SessionManager.GetSessionSid ainda retorna null nesse instante (e reordenar não resolve:
/// EnsureHelper é assíncrono e continuaria correndo atrás). Aqui o SID sai do próprio
/// windows_user já consultado no EmitSessionEvent. Best-effort: null quando a conta não resolve
/// (ex.: conta de domínio com DC inacessível) — windows_sid é opcional no envelope (Seção 5.2).
/// </summary>
[SupportedOSPlatform("windows")]
public static class SessionSidResolver
{
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountNameW(string? systemName, string accountName,
        byte[]? sid, ref uint sidSize, char[]? referencedDomain, ref uint domainSize, out int sidUse);

    /// <summary>SID em string ("S-1-5-21-…") de "DOMÍNIO\usuário", ou null (fallback silencioso).</summary>
    public static string? TryResolve(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;
        try
        {
            // Primeira chamada só mede os buffers (falha esperada com ERROR_INSUFFICIENT_BUFFER).
            uint sidSize = 0, domainSize = 0;
            LookupAccountNameW(null, accountName, null, ref sidSize, null, ref domainSize, out _);
            if (sidSize == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;

            var sid = new byte[sidSize];
            var domain = new char[domainSize];
            if (!LookupAccountNameW(null, accountName, sid, ref sidSize, domain, ref domainSize, out _))
                return null;
            return new SecurityIdentifier(sid, 0).Value;
        }
        catch (Exception)
        {
            return null; // best-effort: evento sai sem windows_sid, nunca deixa de sair
        }
    }
}
