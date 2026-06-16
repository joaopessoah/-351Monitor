using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Maintenance;

/// <summary>
/// Job RetentionPurge (Secao 7.6; tabela 7.2 N12; Secao 9.6). Roda 1x/dia (02:30
/// America/Sao_Paulo, agendado pelo Worker). DELETE (nao DROP) das tabelas de agregado diario
/// — daily_device_summaries e daily_app_usage — com summary_date alem de 24 meses. Sao as UNICAS
/// nao-particionadas das quatro retencoes (as tres particionadas N10/N11/N13 sao do
/// PartitionMaintenance — este job NAO toca nelas).
///
/// Corte = primeiro dia do mes corrente menos 24 meses (alinhado ao corte mensal das outras
/// retencoes): summary_date &lt; corte. Como summary_date e DATE (dia local da org ja resolvido na
/// agregacao), o corte e em DATE puro — sem matematica de fuso aqui.
///
/// Advisory lock proprio (hashtext('retention_purge') — chave distinta da intervalizacao
/// hashtext(device_id::text), da agregacao 'dailyagg:'+id e do PartitionMaintenance) para que
/// multiplas instancias do worker nao purguem em paralelo. pg_try_advisory_xact_lock: se outra
/// instancia ja esta purgando, esta sai sem bloquear (o ciclo diario da outra cobre o dia).
/// O DELETE por data nao colide com a agregacao concorrente: a agregacao so reescreve dias
/// RECENTES (dirty_days), nunca um summary_date de 24 meses atras.
/// </summary>
public sealed class RetentionPurgeService(NpgsqlDataSource dataSource, ILogger<RetentionPurgeService>? logger = null)
{
    /// <summary>Retencao N12 dos agregados diarios: 24 meses.</summary>
    public const int SummariesRetentionMonths = 24;

    /// <summary>
    /// Linhas deletadas por tabela (e se o lock foi adquirido) — permite aos testes asseridarem o
    /// ciclo sem reler maintenance_runs (tabela global compartilhada entre testes).
    /// </summary>
    public sealed record RetentionPurgeResult(bool LockAcquired, int SummariesDeleted, int AppUsageDeleted);

    /// <summary>Um ciclo: deleta os agregados diarios alem de 24 meses. Grava maintenance_runs.</summary>
    public async Task<RetentionPurgeResult> RunOnceAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var detail = new Dictionary<string, object>();
        // corte em DATE: 1o dia do mes corrente − 24 meses
        var firstOfMonth = new DateOnly(startedAt.Year, startedAt.Month, 1);
        var cutoff = firstOfMonth.AddMonths(-SummariesRetentionMonths);

        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // exclusao mutua entre instancias, escopada a transacao; se outra ja purga, sai limpo
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_try_advisory_xact_lock(hashtext('retention_purge'))", conn, tx))
            {
                var acquired = (bool)(await lockCommand.ExecuteScalarAsync(ct))!;
                if (!acquired)
                {
                    logger?.LogInformation("RetentionPurge: outra instancia ja purgando; ciclo pulado.");
                    await tx.RollbackAsync(ct);
                    return new RetentionPurgeResult(LockAcquired: false, 0, 0);
                }
            }

            var summariesDeleted = await DeleteAsync(conn, tx, "daily_device_summaries", cutoff, ct);
            var appUsageDeleted = await DeleteAsync(conn, tx, "daily_app_usage", cutoff, ct);
            detail["daily_device_summaries_deleted"] = summariesDeleted;
            detail["daily_app_usage_deleted"] = appUsageDeleted;
            detail["cutoff"] = cutoff.ToString("yyyy-MM-dd");

            await tx.CommitAsync(ct);

            await MaintenanceRunRecorder.RecordAsync(
                dataSource, MaintenanceRunRecorder.RetentionPurge, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusOk, detail, ct);

            logger?.LogInformation(
                "RetentionPurge: summaries={Summaries} / app_usage={AppUsage} linha(s) deletada(s) (corte {Cutoff:yyyy-MM-dd}).",
                summariesDeleted, appUsageDeleted, cutoff);

            return new RetentionPurgeResult(LockAcquired: true, summariesDeleted, appUsageDeleted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "RetentionPurge falhou.");
            detail["error"] = ex.Message;
            await SafeRecordErrorAsync(startedAt, detail);
            return new RetentionPurgeResult(LockAcquired: true, 0, 0);
        }
    }

    private static async Task<int> DeleteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table, DateOnly cutoff, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {table} WHERE summary_date < @cutoff", conn, tx);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task SafeRecordErrorAsync(DateTimeOffset startedAt, object detail)
    {
        try
        {
            await MaintenanceRunRecorder.RecordAsync(
                dataSource, MaintenanceRunRecorder.RetentionPurge, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusError, detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Falha ao gravar maintenance_runs (status=error) de RetentionPurge.");
        }
    }
}
