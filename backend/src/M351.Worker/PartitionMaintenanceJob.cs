using M351.Infrastructure.Maintenance;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job PartitionMaintenance (Secao 7.6): diario 02:00 America/Sao_Paulo. Cria particoes futuras
/// (raw D+3, intervals/audit mes+2) e dropa as expiradas (N10/N11/N13). A logica vive em
/// PartitionMaintenanceService (Infrastructure) — invocavel pelos testes; o Worker so agenda.
/// DisallowConcurrentExecution evita ciclos sobrepostos nesta instancia; a exclusao entre
/// instancias e por DDL idempotente (CREATE IF NOT EXISTS / DROP IF EXISTS). O servico ja engole
/// e loga a propria excecao (grava maintenance_runs com status=error) — o catch aqui e a ultima
/// rede para o ciclo nunca derrubar o worker.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PartitionMaintenanceJob(PartitionMaintenanceService service, ILogger<PartitionMaintenanceJob> logger) : IJob
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
            logger.LogError(ex, "Ciclo de PartitionMaintenance falhou.");
        }
    }
}
