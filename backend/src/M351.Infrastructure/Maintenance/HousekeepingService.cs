using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Maintenance;

/// <summary>
/// Job Housekeeping (Secao 7.6 linha 838). Roda 1x/dia (03:00 America/Sao_Paulo, agendado pelo
/// Worker). Expira o "lixo operacional" de auth/exports — linhas que perderam a validade e nao
/// precisam mais existir:
///  - invitations vencidos: expires_at &lt; now E ainda nao aceitos (accepted_at IS NULL). Convite
///    aceito vira historico de quem entrou e fica;
///  - refresh_tokens vencidos OU ja revogados: expires_at &lt; now OU revoked_at IS NOT NULL —
///    token morto nunca mais autentica;
///  - export_jobs vencidos cujo ARQUIVO JA FOI VARRIDO: expires_at &lt; now E file_path IS NULL.
///
/// COORDENACAO COM O ExportService (proibicao da fatia — nao conflitar com o sweep de arquivos):
/// o ExportService.RunOnceAsync (a cada 15s) varre o ARQUIVO de jobs vencidos e ZERA file_path
/// (SweepExpiredAsync). So entao o Housekeeping apaga a LINHA. A condicao file_path IS NULL
/// garante a ordem: enquanto o arquivo existir (file_path preenchido), a linha permanece — sem
/// orfanar arquivo no disco nem correr com o sweep. Jobs que nunca geraram arquivo (failed/queued
/// vencidos) tambem tem file_path NULL e sao colhidos aqui. A linha so sai depois do arquivo: a
/// trilha de "quem gerou o que, quando" (tela /relatorios/exportacoes) sobrevive ate o arquivo
/// expirar de fato.
///
/// Advisory lock proprio (hashtext('housekeeping') — distinto dos demais jobs) para multi-instancia.
/// Grava maintenance_runs com as contagens.
/// </summary>
public sealed class HousekeepingService(NpgsqlDataSource dataSource, ILogger<HousekeepingService>? logger = null)
{
    /// <summary>Um ciclo: expira invitations/refresh_tokens/export_jobs. Grava maintenance_runs.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var detail = new Dictionary<string, object>();
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_try_advisory_xact_lock(hashtext('housekeeping'))", conn, tx))
            {
                var acquired = (bool)(await lockCommand.ExecuteScalarAsync(ct))!;
                if (!acquired)
                {
                    logger?.LogInformation("Housekeeping: outra instancia ja rodando; ciclo pulado.");
                    await tx.RollbackAsync(ct);
                    return;
                }
            }

            // convites vencidos e nunca aceitos (o aceito e historico de entrada)
            detail["invitations_deleted"] = await ExecAsync(conn, tx,
                "DELETE FROM invitations WHERE expires_at < now() AND accepted_at IS NULL", ct);

            // refresh tokens mortos (vencidos OU revogados)
            detail["refresh_tokens_deleted"] = await ExecAsync(conn, tx,
                "DELETE FROM refresh_tokens WHERE expires_at < now() OR revoked_at IS NOT NULL", ct);

            // export_jobs vencidos cujo arquivo JA FOI varrido pelo ExportService (file_path IS NULL)
            detail["export_jobs_deleted"] = await ExecAsync(conn, tx,
                "DELETE FROM export_jobs WHERE expires_at < now() AND file_path IS NULL", ct);

            await tx.CommitAsync(ct);

            await MaintenanceRunRecorder.RecordAsync(
                dataSource, MaintenanceRunRecorder.Housekeeping, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusOk, detail, ct);

            logger?.LogInformation(
                "Housekeeping: invitations={Inv} / refresh_tokens={Rt} / export_jobs={Ej} linha(s) expirada(s).",
                detail["invitations_deleted"], detail["refresh_tokens_deleted"], detail["export_jobs_deleted"]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Housekeeping falhou.");
            detail["error"] = ex.Message;
            await SafeRecordErrorAsync(startedAt, detail);
        }
    }

    private static async Task<int> ExecAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, conn, tx);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task SafeRecordErrorAsync(DateTimeOffset startedAt, object detail)
    {
        try
        {
            await MaintenanceRunRecorder.RecordAsync(
                dataSource, MaintenanceRunRecorder.Housekeeping, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusError, detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Falha ao gravar maintenance_runs (status=error) de Housekeeping.");
        }
    }
}
