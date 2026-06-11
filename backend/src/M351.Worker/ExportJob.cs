using M351.Infrastructure.Exports;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job ExportWorker (Seção 7.6): a cada 15 s drena a fila de export_jobs chamando
/// ExportService.RunOnceAsync até a fila esvaziar dentro do ciclo (cada chamada claima e
/// processa NO MÁXIMO um job — o SKIP LOCKED do claim garante exclusão entre instâncias).
/// DisallowConcurrentExecution evita ciclos sobrepostos nesta instância. A lógica vive em
/// ExportService (Infrastructure) — invocável também pelos testes de integração.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ExportJob(ExportService service, ILogger<ExportJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var processed = 0;
            while (await service.RunOnceAsync(context.CancellationToken) > 0)
            {
                processed++;
            }

            if (processed > 0)
                logger.LogInformation("Exports: {Jobs} job(s) processado(s) neste ciclo.", processed);
        }
        catch (OperationCanceledException)
        {
            // shutdown do host (o job interrompido foi devolvido à fila pelo serviço)
        }
        catch (Exception ex)
        {
            // o ciclo seguinte tenta de novo; job claimado que falhou já está 'failed'
            logger.LogError(ex, "Ciclo de exports falhou.");
        }
    }
}
