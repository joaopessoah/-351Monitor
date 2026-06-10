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
