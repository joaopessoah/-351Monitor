using M351.Agent.Core.Logging;

namespace MonitorAgentService;

/// <summary>
/// Sentinela de desinstalacao. O MSI grava este arquivo no diretorio de dados ANTES de parar
/// o servico no uninstall; o ShutdownCore do servico le a sentinela para distinguir um stop de
/// desinstalacao (AGENT_STOP{reason:"uninstall"}) de um stop normal (service_stop) — Secao 6.6.
///
/// A sentinela mora em %ProgramData% (DATAFOLDER), que o MSI NAO apaga nem no uninstall: o
/// DataDirComponent e Permanent=yes/NeverOverwrite=yes (Package.wxs:254) para preservar fila e
/// identidade. A unica limpeza da sentinela e o best-effort de Consume no fluxo de stop (ResolveStopReason).
/// Para nao herdar uma sentinela orfa num reinstall futuro (caso um stop ocorra sem passar pelo
/// OnStop, ex.: servico ja parado/crash), Consume tambem e chamado uma vez no OnStart fora de
/// fluxo de uninstall (ver AgentWindowsService), descartando qualquer sentinela remanescente.
/// </summary>
public static class UninstallFlag
{
    private const string FileName = ".uninstall";

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static bool IsSet(string dataDirectory)
    {
        try { return File.Exists(PathFor(dataDirectory)); }
        catch (Exception) { return false; }
    }

    /// <summary>Consome a sentinela (best-effort): retorna true se estava setada e a remove.</summary>
    public static bool Consume(string dataDirectory, ILogSink log)
    {
        var path = PathFor(dataDirectory);
        try
        {
            if (!File.Exists(path)) return false;
            try { File.Delete(path); }
            catch (Exception ex) { log.Warn($"Falha ao remover sentinela de uninstall: {ex.Message}"); }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
