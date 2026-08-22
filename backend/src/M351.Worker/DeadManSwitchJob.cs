using Quartz;

namespace M351.Worker;

/// <summary>
/// Dead-man switch do worker (quem monitora o monitor): a cada 5 minutos faz um GET na
/// URL de DeadMan:WorkerUrl (env DeadMan__WorkerUrl, um check do healthchecks.io). Se o
/// worker travar ou morrer, o ping some e o healthchecks.io alerta pela AUSÊNCIA, por
/// isso a falha do ping em si é apenas logada (jamais pode derrubar os jobs de verdade).
/// Sem URL configurada o job nem é registrado (ver o bloco AddQuartz do Program.cs).
/// </summary>
[DisallowConcurrentExecution]
public sealed class DeadManSwitchJob(IConfiguration configuration, ILogger<DeadManSwitchJob> logger) : IJob
{
    // HttpClient estático: 1 GET a cada 5 min não justifica factory; timeout curto (10 s)
    // para o ping nunca segurar a thread do scheduler.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task Execute(IJobExecutionContext context)
    {
        var url = configuration["DeadMan:WorkerUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            return; // defensivo: o job só é agendado com a URL definida
        }

        try
        {
            using var response = await Http.GetAsync(url, context.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Ping do dead-man switch respondeu HTTP {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // shutdown do host
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Ping do dead-man switch falhou (o healthchecks.io alertará pela ausência de ping).");
        }
    }
}
