using M351.Infrastructure.Maintenance;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job RetentionPurge (Secao 7.6): diario 02:30 America/Sao_Paulo. DELETE dos agregados diarios
/// alem de 24 meses (N12). A logica vive em RetentionPurgeService (Infrastructure) — invocavel
/// pelos testes; o Worker so agenda. DisallowConcurrentExecution evita ciclos sobrepostos nesta
/// instancia; a exclusao entre instancias e o pg_try_advisory_xact_lock('retention_purge') do
/// servico. O servico ja loga e grava maintenance_runs em falha — o catch aqui blinda o worker.
/// </summary>
[DisallowConcurrentExecution]
public sealed class RetentionPurgeJob(RetentionPurgeService service, ILogger<RetentionPurgeJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await service.RunOnceAsync(context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // shutdown do host
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ciclo de RetentionPurge falhou.");
        }
    }
}
