using System.Collections.Concurrent;
using Npgsql;

namespace M351.Api.Agent;

/// <summary>
/// Garante partições diárias de raw_events para os dias dos eventos do lote (a janela de
/// aceitação N9 vai até 14 dias no passado — a migration inicial só cobre o mês corrente e o
/// próximo, e o job PartitionMaintenance é F2). CREATE TABLE IF NOT EXISTS idempotente, em
/// auto-commit (fora da transação do lote), com cache em memória por host.
/// </summary>
public sealed class RawEventPartitionManager
{
    private readonly ConcurrentDictionary<DateOnly, byte> _known = new();

    public async Task EnsureDaysAsync(NpgsqlConnection connection, IEnumerable<DateOnly> days, CancellationToken ct)
    {
        foreach (var day in days.Distinct().Where(d => !_known.ContainsKey(d)))
        {
            var from = day.ToString("yyyy-MM-dd");
            var to = day.AddDays(1).ToString("yyyy-MM-dd");
            var name = $"raw_events_{day:yyyyMMdd}";

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE TABLE IF NOT EXISTS {name} PARTITION OF raw_events FOR VALUES FROM ('{from}') TO ('{to}')";

            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DuplicateTable or PostgresErrorCodes.UniqueViolation)
            {
                // corrida benigna entre lotes concorrentes: a partição já existe
            }

            _known.TryAdd(day, 0);
        }
    }

    /// <summary>Dias (UTC) cobertos pelos eventos, expandidos ±1 dia (bordas de fuso da sessão DDL).</summary>
    public static IEnumerable<DateOnly> DaysFor(IEnumerable<DateTimeOffset> timestamps)
    {
        var days = new SortedSet<DateOnly>();
        foreach (var ts in timestamps)
        {
            var day = DateOnly.FromDateTime(ts.UtcDateTime);
            days.Add(day.AddDays(-1));
            days.Add(day);
            days.Add(day.AddDays(1));
        }

        return days;
    }
}
