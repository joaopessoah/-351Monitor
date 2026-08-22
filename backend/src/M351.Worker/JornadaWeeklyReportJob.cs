using M351.Infrastructure.Reports;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job da jornada semanal por e-mail (F5): roda de 5 em 5 MINUTOS. O intervalo curto serve às
/// duas etapas do serviço, que decidem sozinhas o que fazer: o enfileiramento só dispara nas
/// orgs cuja hora local é segunda 07h (multi-fuso, sem um trigger por org, e idempotente pelo
/// UNIQUE (user_id, week_start)), e a entrega do link acontece logo depois do ExportService
/// terminar o arquivo, em vez de esperar a hora cheia seguinte. A lógica vive em
/// JornadaWeeklyReportService (Infrastructure), invocável pelos testes.
/// </summary>
[DisallowConcurrentExecution]
public sealed class JornadaWeeklyReportJob(
    JornadaWeeklyReportService service, ILogger<JornadaWeeklyReportJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await service.RunOnceAsync(DateTimeOffset.UtcNow, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // shutdown do host
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ciclo do relatório de jornada semanal falhou.");
        }
    }
}
