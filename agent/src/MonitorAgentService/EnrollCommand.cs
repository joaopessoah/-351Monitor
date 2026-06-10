using System.Runtime.Versioning;
using M351.Agent.Core.Logging;

namespace MonitorAgentService;

/// <summary>--enroll &lt;ek_…&gt; --server &lt;url&gt;: registra o device (Seção 5.7) e sai.</summary>
[SupportedOSPlatform("windows")]
public static class EnrollCommand
{
    public static int Run(string enrollmentKey, string? serverUrl)
    {
        var log = new ConsoleLogSink();
        using var runtime = AgentRuntime.Create(log);

        serverUrl ??= runtime.State.ServerUrl;
        if (serverUrl is null)
        {
            log.Error("Informe o servidor: --enroll <ek_...> --server <url>");
            return 2;
        }

        log.Info($"Registrando este dispositivo em {serverUrl} …");
        var ok = runtime.Enrollment.EnrollAsync(serverUrl, enrollmentKey, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!ok)
        {
            log.Error("Falha no enrollment — verifique a chave (ek_...), a URL do servidor e a rede.");
            return 1;
        }

        log.Info($"Enrollment concluído. device_id={runtime.State.DeviceId}");
        log.Info("O token do device foi cifrado com DPAPI (escopo máquina) na fila local.");
        return 0;
    }
}
