using M351.Infrastructure.Digest;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job WeeklyDigest (F5): roda DE HORA EM HORA e envia o resumo semanal das orgs cuja hora
/// local é segunda 08h (multi-fuso: cada org no seu horário). A lógica vive em
/// WeeklyDigestService (Infrastructure) — invocável pelos testes; a idempotência é por
/// organizations.last_weekly_digest_at (reinício do worker na mesma janela não reenvia).
/// </summary>
[DisallowConcurrentExecution]
public sealed class WeeklyDigestJob(WeeklyDigestService service, ILogger<WeeklyDigestJob> logger) : IJob
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
            logger.LogError(ex, "Ciclo do digest semanal falhou.");
        }
    }
}
