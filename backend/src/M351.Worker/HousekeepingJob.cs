using M351.Infrastructure.Maintenance;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job Housekeeping (Secao 7.6): diario 03:00 America/Sao_Paulo. Expira invitations vencidos,
/// refresh_tokens mortos e export_jobs vencidos (so apos o ExportService ter varrido o arquivo).
/// A logica vive em HousekeepingService (Infrastructure) — invocavel pelos testes; o Worker so
/// agenda. DisallowConcurrentExecution evita ciclos sobrepostos nesta instancia; a exclusao entre
/// instancias e o pg_try_advisory_xact_lock('housekeeping') do servico. O servico ja loga e grava
/// maintenance_runs em falha — o catch aqui blinda o worker.
/// </summary>
[DisallowConcurrentExecution]
public sealed class HousekeepingJob(HousekeepingService service, ILogger<HousekeepingJob> logger) : IJob
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
            logger.LogError(ex, "Ciclo de Housekeeping falhou.");
        }
    }
}
