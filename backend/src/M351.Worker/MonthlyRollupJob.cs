using M351.Infrastructure.Aggregation;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job MonthlyRollup (F9): de hora em hora, resume daily_device_summaries em monthly_summaries.
/// A lógica vive em MonthlyRollupService (Infrastructure) — invocável também pelos testes de
/// integração, como os demais jobs.
///
/// De hora em hora, e não a cada 15 min como a agregação diária: o mensal não alimenta nenhuma
/// tela de tempo real. Ele existe para trimestre e ano, janelas que ninguém abre esperando ver o
/// minuto anterior. Graças à marca-d'água o ciclo em que nada mudou custa duas consultas.
///
/// DisallowConcurrentExecution evita ciclos sobrepostos nesta instância; entre instâncias, a
/// exclusão é o pg_try_advisory_xact_lock por tenant dentro do serviço.
/// </summary>
[DisallowConcurrentExecution]
public sealed class MonthlyRollupJob(MonthlyRollupService service, ILogger<MonthlyRollupJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var months = await service.RunOnceAsync(context.CancellationToken);
            if (months > 0)
            {
                logger.LogInformation("Rollup mensal: {Months} par(es) (tenant, mês) neste ciclo.", months);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown do host
        }
        catch (Exception ex)
        {
            // o ciclo seguinte tenta de novo: a marca-d'água só avança em transação commitada,
            // então uma falha aqui nunca deixa mês por resumir
            logger.LogError(ex, "Ciclo de rollup mensal falhou.");
        }
    }
}
