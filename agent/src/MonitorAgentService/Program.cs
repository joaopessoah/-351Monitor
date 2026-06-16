using System.Runtime.Versioning;
using System.ServiceProcess;
using M351.Agent.Core.Logging;

namespace MonitorAgentService;

/// <summary>
/// MonitorAgentService.exe — orquestrador do agente (+351 Monitor).
///   (sem args, não interativo)  → serviço Windows (LocalSystem)
///   --console [--run-for N]     → TUDO num processo só na sessão atual (E2E/diagnóstico)
///   --enroll &lt;ek_…&gt; --server &lt;url&gt; → registra o device e sai
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static int Main(string[] args)
    {
        // --write-install-config usa pares --chave valor (inclusive valores opcionais); parser dedicado.
        if (args.Length > 0 && args[0] == "--write-install-config")
            return InstallConfigCommand.Run(ParseKeyValueArgs(args.AsSpan(1)));

        string? enrollKey = null;
        string? serverUrl = null;
        var consoleMode = false;
        int? runForSeconds = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--console":
                    consoleMode = true;
                    break;
                case "--enroll" when i + 1 < args.Length:
                    enrollKey = args[++i];
                    break;
                case "--server" when i + 1 < args.Length:
                    serverUrl = args[++i];
                    break;
                case "--run-for" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds):
                    runForSeconds = seconds;
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"Argumento desconhecido: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        if (enrollKey is not null)
            return EnrollCommand.Run(enrollKey, serverUrl);

        if (consoleMode)
            return ConsoleOrchestrator.RunAsync(runForSeconds).GetAwaiter().GetResult();

        if (!Environment.UserInteractive)
        {
            ServiceBase.Run(new AgentWindowsService());
            return 0;
        }

        PrintUsage();
        return 1;
    }

    /// <summary>Pares "--chave valor" → dicionario (chave sem o "--"). Flags sem valor viram "1".</summary>
    private static Dictionary<string, string> ParseKeyValueArgs(ReadOnlySpan<string> args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = token[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                map[key] = args[++i];
            else
                map[key] = "1";
        }
        return map;
    }

    private static void PrintUsage()
    {
        var log = new ConsoleLogSink();
        log.Info("+351 Monitor — MonitorAgentService (agente Windows)");
        log.Info("Uso:");
        log.Info("  MonitorAgentService.exe --console [--run-for <segundos>]");
        log.Info("      Roda tudo num processo só na sessão atual (coletores in-process),");
        log.Info("      logs no stdout e parada limpa em Ctrl+C.");
        log.Info("  MonitorAgentService.exe --enroll <ek_...> --server <url>");
        log.Info("      Registra este dispositivo no servidor e sai.");
        log.Info("  (sem argumentos, no SCM) roda como serviço Windows.");
    }
}
