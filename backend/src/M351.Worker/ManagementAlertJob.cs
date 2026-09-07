using M351.Infrastructure.Alerts;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job ManagementAlert (F6, seção 4 do spec de 07/09/2026): a cada 15 min reavalia as sete
/// regras de gestão sobre os agregados diários e sincroniza <c>management_alerts</c>.
///
/// 15 min é o MESMO passo da agregação diária de propósito: o alerta nasce no ciclo seguinte
/// ao agregado que o justifica, sem janela em que a tela mostre um número novo com um alerta
/// velho. Toda a calibragem (estado, cooldown de 24 h, silêncio fora do horário, opt-in de
/// pessoa, gate do plano Pro) vive no ManagementAlertService, invocável pelos testes; o worker
/// só agenda — mesma divisão do FleetAlertJob.
///
/// DisallowConcurrentExecution corta ciclos sobrepostos NESTA instância; a exclusão entre
/// instâncias é o <c>pg_try_advisory_xact_lock('management_alerts')</c> do serviço.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ManagementAlertJob(
    ManagementAlertService service, ILogger<ManagementAlertJob> logger) : IJob
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
            // o ciclo seguinte reavalia tudo do zero: o estado é derivado, nada se perde
            logger.LogError(ex, "Ciclo de alertas de gestão falhou.");
        }
    }
}
