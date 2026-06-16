using System.Runtime.Versioning;
using M351.Agent.Core.Logging;

namespace MonitorAgentService;

/// <summary>
/// --write-install-config: chamado pela custom action DEFERIDA do MSI (elevada/SYSTEM) para
/// materializar %ProgramData%\M351\MonitorAgent\install.json a partir das propriedades publicas
/// do instalador. Mantem a geracao do JSON em C# (escapamento correto) em vez de cmd.exe.
///
///   --write-install-config --data-dir &lt;dir&gt; [--server &lt;url&gt;] [--proxy &lt;url&gt;]
///                          [--enroll-key &lt;ek_...&gt;] [--noenroll 0|1]
///
/// A enrollment key so e gravada como pendente quando NOENROLL=1 (golden image): nesse caso o
/// enroll acontece no PRIMEIRO BOOT real (AgentWindowsService.ApplyInstallConfigAndEnroll),
/// evitando identidade clonada. Em instalacao normal o MSI ja faz --enroll e a key NAO vai ao disco.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallConfigCommand
{
    public static int Run(IReadOnlyDictionary<string, string> args)
    {
        var log = new ConsoleLogSink();

        if (!args.TryGetValue("data-dir", out var dataDir) || string.IsNullOrWhiteSpace(dataDir))
        {
            log.Error("--write-install-config exige --data-dir.");
            return 2;
        }

        try
        {
            Directory.CreateDirectory(dataDir);
        }
        catch (Exception ex)
        {
            log.Error($"Nao foi possivel criar o diretorio de dados {dataDir}.", ex);
            return 1;
        }

        var noEnroll = args.TryGetValue("noenroll", out var ne) && ne is "1" or "true";
        args.TryGetValue("enroll-key", out var enrollKey);

        var cfg = new InstallConfig
        {
            ServerUrl = Blank(args, "server"),
            ProxyUrl = Blank(args, "proxy"),
            // So persiste a key pendente em golden image; instalacao normal nao grava segredo.
            PendingEnrollKey = noEnroll && !string.IsNullOrWhiteSpace(enrollKey) ? enrollKey : null
        };

        cfg.Save(dataDir, log);
        log.Info($"install.json gravado em {InstallConfig.PathFor(dataDir)} (noenroll={noEnroll}).");
        return 0;
    }

    private static string? Blank(IReadOnlyDictionary<string, string> args, string key)
        => args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
