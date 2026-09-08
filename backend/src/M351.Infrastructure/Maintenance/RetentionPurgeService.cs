using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Maintenance;

/// <summary>
/// Job RetentionPurge (Secao 7.6; tabela 7.2 N12; Secao 9.6). Roda 1x/dia (02:30
/// America/Sao_Paulo, agendado pelo Worker). DELETE (nao DROP) das tabelas de agregado diario
/// — daily_device_summaries, daily_app_usage e hourly_activity (F6) — com summary_date alem de
/// 24 meses. Sao as UNICAS
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
    public sealed record RetentionPurgeResult(
        bool LockAcquired, int SummariesDeleted, int AppUsageDeleted, int HourlyActivityDeleted = 0);

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
                    return new RetentionPurgeResult(LockAcquired: false, 0, 0, 0);
                }
            }

            var summariesDeleted = await DeleteAsync(conn, tx, "daily_device_summaries", cutoff, ct);
            var appUsageDeleted = await DeleteAsync(conn, tx, "daily_app_usage", cutoff, ct);
            // F6: hourly_activity e um agregado diario como os outros dois (mesma retencao N12)
            var hourlyDeleted = await DeleteAsync(conn, tx, "hourly_activity", cutoff, ct);
            // F9: o agregado MENSAL segue a MESMA retencao dos diarios, e nao uma mais longa. A
            // pagina publica de transparencia promete "Agregados: 24 meses" (RetencoesPublic);
            // um mensal sobrevivente ao corte transformaria a promessa em mentira. A coluna de
            // data e month_start, dai o parametro.
            var monthlyDeleted = await DeleteAsync(conn, tx, "monthly_summaries", cutoff, ct, "month_start");
            // F6: alerta de gestao JA RESOLVIDO e historico, e segue a mesma retencao. O alerta
            // VIVO (resolved_at NULL) nunca e purgado, por mais antigo que seja o first_seen_at:
            // enquanto a condicao persiste, a linha e estado corrente, nao historico.
            var alertsDeleted = await DeleteResolvedAlertsAsync(conn, tx, cutoff, ct);
            detail["daily_device_summaries_deleted"] = summariesDeleted;
            detail["daily_app_usage_deleted"] = appUsageDeleted;
            detail["hourly_activity_deleted"] = hourlyDeleted;
            detail["monthly_summaries_deleted"] = monthlyDeleted;
            detail["management_alerts_deleted"] = alertsDeleted;
            detail["cutoff"] = cutoff.ToString("yyyy-MM-dd");

            await tx.CommitAsync(ct);

            await MaintenanceRunRecorder.RecordAsync(
                dataSource, MaintenanceRunRecorder.RetentionPurge, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusOk, detail, ct);

            logger?.LogInformation(
                "RetentionPurge: summaries={Summaries} / app_usage={AppUsage} linha(s) deletada(s) (corte {Cutoff:yyyy-MM-dd}).",
                summariesDeleted, appUsageDeleted, cutoff);

            return new RetentionPurgeResult(LockAcquired: true, summariesDeleted, appUsageDeleted, hourlyDeleted);
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

    /// <summary>
    /// DELETE por data alem do corte. dateColumn e sempre um literal do proprio codigo (nunca
    /// entrada de usuario), entao a interpolacao no SQL nao abre injecao: o mensal so difere dos
    /// diarios por chamar a coluna de month_start em vez de summary_date.
    /// </summary>
    private static async Task<int> DeleteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table, DateOnly cutoff, CancellationToken ct,
        string dateColumn = "summary_date")
    {
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {table} WHERE {dateColumn} < @cutoff", conn, tx);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Alerta de gestao RESOLVIDO alem do corte. Chave propria (nao tem summary_date), e o
    /// filtro por resolved_at e deliberado: alerta vivo e estado corrente, nao historico.
    /// </summary>
    private static async Task<int> DeleteResolvedAlertsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, DateOnly cutoff, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM management_alerts
            WHERE resolved_at IS NOT NULL
              AND resolved_at < (@cutoff::date)::timestamptz
            """, conn, tx);
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
