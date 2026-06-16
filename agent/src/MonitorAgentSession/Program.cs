using System.Diagnostics;
using System.IO.Compression;
using M351.Agent.Core.Logging;

namespace MonitorAgentSession;

/// <summary>
/// MonitorAgentSession.exe — helper de sessão (token do PRÓPRIO usuário, baixo privilégio).
/// Coletores de sessão + tray de transparência. Lançado pelo serviço com --session {id}.
/// --diag gera ZIP de suporte sem UI (Seção 6.5).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var diag = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session" when i + 1 < args.Length && int.TryParse(args[i + 1], out var sid):
                    sessionId = sid;
                    i++;
                    break;
                case "--diag":
                    diag = true;
                    break;
            }
        }

        if (diag) return WriteDiagnosticsZip();

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(sessionId));
        return 0;
    }

    /// <summary>
    /// ZIP de suporte: logs + contadores. Config sanitizada (sem token, sem títulos). Os *.log sao
    /// redigidos linha a linha pelo LogScrubber ao empacotar (DiagnosticsLogPackager): mesmo as
    /// linhas Debug crus (verbose_debug) saem sem window_title/usuario, pois o ZIP vai ao TI/suporte.
    /// </summary>
    private static int WriteDiagnosticsZip()
    {
        try
        {
            var programData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "M351", "MonitorAgent");
            var target = Path.Combine(Path.GetTempPath(), $"monitoragent-diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
            DiagnosticsLogPackager.AddScrubbedLogs(zip, Path.Combine(programData, "logs"));

            var info = zip.CreateEntry("info.txt");
            using (var writer = new StreamWriter(info.Open()))
            {
                writer.WriteLine($"versao_agente: {M351.Agent.Core.AgentVersionInfo.Current}");
                writer.WriteLine($"gerado_em: {DateTime.Now:O}");
                writer.WriteLine($"maquina: {Environment.MachineName}");
            }

            Console.WriteLine(target);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
