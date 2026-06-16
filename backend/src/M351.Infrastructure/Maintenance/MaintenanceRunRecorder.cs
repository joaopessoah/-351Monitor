using System.Text.Json;
using M351.Domain;
using Npgsql;

namespace M351.Infrastructure.Maintenance;

/// <summary>
/// Grava uma linha em maintenance_runs (trilha GLOBAL das execucoes de retencao/purga — F4.6).
/// Compartilhado pelos tres servicos (PartitionMaintenance/RetentionPurge/Housekeeping) para a
/// gravacao ser identica em todos. A linha e gravada SEMPRE — inclusive em falha, com a causa
/// em detail e status='error' — porque a F4.8 (Transparencia) le "data da ultima purga" daqui e
/// uma falha silenciosa enganaria a tela.
///
/// detail e um jsonb com as contagens da execucao (particoes criadas/dropadas, linhas deletadas);
/// a forma exata e responsabilidade de cada servico (passa um dicionario serializavel).
/// </summary>
public static class MaintenanceRunRecorder
{
    public const string PartitionMaintenance = "PartitionMaintenance";
    public const string RetentionPurge = "RetentionPurge";
    public const string Housekeeping = "Housekeeping";

    public const string StatusOk = "ok";
    public const string StatusError = "error";

    /// <summary>
    /// Insere a execucao. started_at/finished_at delimitam a janela de parede; detail carrega as
    /// contagens (e, em erro, a causa). Conexao propria e auto-commit — a trilha sobrevive mesmo
    /// que o job tenha abortado uma transacao no meio.
    /// </summary>
    public static async Task RecordAsync(
        NpgsqlDataSource dataSource, string jobName, DateTimeOffset startedAt, DateTimeOffset finishedAt,
        string status, object detail, CancellationToken ct = default)
    {
        var detailJson = JsonSerializer.Serialize(detail);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO maintenance_runs (id, job_name, started_at, finished_at, status, detail)
            VALUES (@id, @job, @started, @finished, @status, @detail::jsonb)
            """, connection);
        command.Parameters.AddWithValue("id", Uuid7.NewUuid7());
        command.Parameters.AddWithValue("job", jobName);
        command.Parameters.AddWithValue("started", startedAt);
        command.Parameters.AddWithValue("finished", finishedAt);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("detail", detailJson);
        await command.ExecuteNonQueryAsync(ct);
    }
}
