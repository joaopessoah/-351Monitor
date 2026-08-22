using M351.Infrastructure.AccountHealth;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Job AccountHealth (F5): segunda 09h America/Sao_Paulo, uma hora DEPOIS do digest semanal
/// dos clientes, para o CS já começar a semana com a lista de contas em risco na caixa de
/// entrada. A lógica vive em AccountHealthService (Infrastructure), invocável pelos testes;
/// o Worker só agenda. O job SÓ É REGISTRADO com Cs:AlertEmail preenchido (ver Program.cs):
/// antes do início do piloto a lista é vazia por definição e ligar isso só gera ruído.
/// O serviço já loga e grava maintenance_runs em falha, o catch aqui blinda o worker.
/// </summary>
[DisallowConcurrentExecution]
public sealed class AccountHealthJob(AccountHealthService service, ILogger<AccountHealthJob> logger) : IJob
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
            logger.LogError(ex, "Ciclo do score de saúde de conta falhou.");
        }
    }
}
