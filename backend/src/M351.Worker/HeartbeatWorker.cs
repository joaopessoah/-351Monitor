namespace M351.Worker;

/// <summary>
/// Prova de vida do worker no log. Era um stub da F0 que anunciava a intervalização como
/// futura (mensagem obsoleta desde a F2 e ruído a cada 30 s); agora registra a partida uma
/// vez e um pulso ESPAÇADO, que serve para ler o Seq e para o operador confirmar que o
/// processo está de pé sem depender de rede externa.
///
/// A liveness de verdade não é este log: é o DeadManSwitchJob (ping externo a cada 5 min no
/// healthchecks.io, que alerta quando o ping SOME) mais o /readyz da API, que checa a idade
/// da última execução de job com sucesso. Um log só prova vida para quem está olhando.
/// </summary>
public class HeartbeatWorker(ILogger<HeartbeatWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("M351.Worker iniciado");

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                logger.LogInformation("Worker ativo em {Timestamp:O}", DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException)
        {
            // desligamento normal
        }

        logger.LogInformation("M351.Worker finalizando");
    }
}
