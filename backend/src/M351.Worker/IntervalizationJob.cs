using M351.Infrastructure.Intervalization;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job Intervalization (Seção 7.6): a cada 60 s consome ingest_cursors sujos e reconstrói
/// activity_intervals. A lógica vive em IntervalizationService (Infrastructure) — invocável
/// também pelos testes de integração. DisallowConcurrentExecution evita ciclos sobrepostos
/// nesta instância; a exclusão por device entre instâncias é o pg_advisory_xact_lock do serviço.
/// </summary>
[DisallowConcurrentExecution]
public sealed class IntervalizationJob(IntervalizationService service, ILogger<IntervalizationJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var processed = await service.RunOnceAsync(context.CancellationToken);
            if (processed > 0)
                logger.LogInformation("Intervalização: {Devices} device(s) processado(s) neste ciclo.", processed);
        }
        catch (OperationCanceledException)
        {
            // shutdown do host
        }
        catch (Exception ex)
        {
            // o ciclo seguinte tenta de novo; cursor sujo nunca se perde
            logger.LogError(ex, "Ciclo de intervalização falhou.");
        }
    }
}
