namespace M351.Worker;

/// <summary>
/// Stub da F0: o worker só prova vida (heartbeat no log). O pipeline de intervalização,
/// agregação diária e jobs de retenção/partição (Seção 7.6) chegam na F2+.
/// </summary>
public class HeartbeatWorker(ILogger<HeartbeatWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("M351.Worker iniciado (stub F0 — pipeline de intervalização chega na F2)");

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                logger.LogInformation("Worker ativo (heartbeat) em {Timestamp:O}", DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException)
        {
            // desligamento normal
        }

        logger.LogInformation("M351.Worker finalizando");
    }
}
