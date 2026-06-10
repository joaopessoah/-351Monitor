using M351.Infrastructure.Aggregation;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job DailyAggregation (Seção 7.6): a cada 15 min consome dirty_days e recomputa
/// daily_device_summaries + daily_app_usage. A lógica vive em DailyAggregationService
/// (Infrastructure) — invocável também pelos testes de integração. DisallowConcurrentExecution
/// evita ciclos sobrepostos nesta instância; a exclusão por device entre instâncias é o
/// pg_advisory_xact_lock('dailyagg:' + device) do serviço.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DailyAggregationJob(DailyAggregationService service, ILogger<DailyAggregationJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var processed = await service.RunOnceAsync(context.CancellationToken);
            if (processed > 0)
                logger.LogInformation("Agregação diária: {Pairs} par(es) (device, dia) processado(s) neste ciclo.", processed);
        }
        catch (OperationCanceledException)
        {
            // shutdown do host
        }
        catch (Exception ex)
        {
            // o ciclo seguinte tenta de novo; dirty_days não consumido nunca se perde
            logger.LogError(ex, "Ciclo de agregação diária falhou.");
        }
    }
}
