using M351.Infrastructure.Alerts;
using M351.Infrastructure.Billing;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job FleetAlert (F5): a cada 15 min avalia a frota das orgs elegíveis (plano Pro) e envia
/// no máximo UM e-mail por org por ciclo. Toda a calibragem anti-fadiga (cooldown de 24 h por
/// device+tipo, quiet hours pelo horário de trabalho, opt-out por usuário) vive no
/// FleetAlertService, invocável pelos testes; o worker só agenda.
/// </summary>
[DisallowConcurrentExecution]
public sealed class FleetAlertJob(FleetAlertService service, ILogger<FleetAlertJob> logger) : IJob
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
            logger.LogError(ex, "Ciclo de alertas de frota falhou.");
        }
    }
}

/// <summary>
/// Job BillingSnapshot (F5): diário 04:00 America/Sao_Paulo, congela os meses fechados ainda
/// sem snapshot em device_billing_months (no fuso de CADA tenant). Diário e idempotente em vez
/// de mensal: se o worker estiver parado no dia 1, o congelamento acontece no próximo dia.
/// </summary>
[DisallowConcurrentExecution]
public sealed class BillingSnapshotJob(BillingSnapshotService service, ILogger<BillingSnapshotJob> logger) : IJob
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
            logger.LogError(ex, "Congelamento mensal de cobrança falhou.");
        }
    }
}
