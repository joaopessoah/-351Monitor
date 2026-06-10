using System.Globalization;
using M351.Domain;
using M351.Domain.Intervalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace M351.Infrastructure.Intervalization;

/// <summary>
/// Job de intervalização (Seção 7.3) — consome ingest_cursors sujos e reconstrói
/// activity_intervals por device (delete-and-rebuild idempotente). Vive na Infrastructure
/// (e não no Worker) para ser invocável pelos testes de integração — o Worker apenas agenda.
///
/// Passos por device, numa única transação serializada por pg_advisory_xact_lock:
///  1. captura (dirty_from, updated_at) do cursor; janela R = [date_trunc('hour', dirty_from) − 1h, now];
///  2. estende R.start até cobrir o started_at de qualquer intervalo que cruze a borda
///     (ponto-fixo) — sem isso o DELETE perderia a cabeça de intervalos longos;
///  3. DELETE intervalos com ended_at > R.start e reconstrói dos raw_events ordenados por
///     (occurred_at, seq), com timestamps corrigidos por clock_offset_ms (corrigido = cru + offset;
///     o skew servidor−agente é a EMA de 5 lotes calculada na ingestão — raw fica intacto);
///  4. divide intervalos na meia-noite do fuso da org (source_day exato por dia local);
///  5. resolve app_id no app_catalog (auto-insert não-curado; JAMAIS window_title no catálogo)
///     e device_user_id por (device_id, windows_sid);
///  6. grava intervalos + dirty_days e finaliza o cursor — dirty_from só é zerado se o
///     updated_at não mudou (lote que chegou DURANTE o processamento re-suja o cursor).
///
/// Partições mensais de activity_intervals são garantidas fora da transação (auto-commit,
/// mesmo padrão do RawEventPartitionManager) — a migration só criou mês corrente e próximo.
/// </summary>
public sealed class IntervalizationService(NpgsqlDataSource dataSource, ILogger<IntervalizationService>? logger = null)
{
    private readonly HashSet<string> _knownPartitions = [];

    /// <summary>Um ciclo: processa todos os devices com cursor sujo. Retorna quantos processou.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var dirty = new List<Guid>();
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand(
            "SELECT device_id FROM ingest_cursors WHERE dirty_from IS NOT NULL ORDER BY dirty_from LIMIT 500", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) dirty.Add(reader.GetGuid(0));
        }

        var processed = 0;
        foreach (var deviceId in dirty)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ProcessDeviceAsync(deviceId, ct)) processed++;
            }
            catch (Exception ex)
            {
                // um device com problema não pode parar a varredura dos demais
                logger?.LogError(ex, "Intervalização falhou para o device {DeviceId}", deviceId);
            }
        }
        return processed;
    }

    /// <summary>Processa um device sujo. Retorna false se o cursor já estava limpo.</summary>
    public async Task<bool> ProcessDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        // contexto fora da transação: identidade do device + fuso da org
        Guid tenantId;
        long clockOffsetMs;
        string orgTimezone;
        DateTimeOffset? dirtyPeek;
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        {
            await using var command = new NpgsqlCommand("""
                SELECT d.tenant_id, d.clock_offset_ms, o.timezone, c.dirty_from
                FROM devices d
                JOIN organizations o ON o.id = d.tenant_id
                LEFT JOIN ingest_cursors c ON c.device_id = d.id
                WHERE d.id = @id
                """, connection);
            command.Parameters.AddWithValue("id", deviceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            tenantId = reader.GetGuid(0);
            clockOffsetMs = reader.GetInt64(1);
            orgTimezone = reader.GetString(2);
            dirtyPeek = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
        }
        if (dirtyPeek is null) return false;

        // partições mensais ANTES da transação (DDL idempotente em auto-commit); a extensão
        // da janela só alcança meses de intervalos já existentes (cuja partição já existe)
        await EnsureMonthlyPartitionsAsync(dirtyPeek.Value.AddHours(-2), DateTimeOffset.UtcNow.AddMonths(1), ct);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // exclusão mútua por device, escopada à transação (Seção 7.3 passo 1)
        await ExecAsync(conn, tx, "SELECT pg_advisory_xact_lock(hashtext(@id::text))",
            [("id", deviceId.ToString())], ct);

        // recaptura o cursor JÁ sob o lock (outro processador pode ter limpado)
        DateTimeOffset dirtyFrom;
        DateTimeOffset cursorUpdatedAt;
        await using (var command = new NpgsqlCommand(
            "SELECT dirty_from, updated_at FROM ingest_cursors WHERE device_id = @id", conn, tx))
        {
            command.Parameters.AddWithValue("id", deviceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) return false;
            dirtyFrom = reader.GetFieldValue<DateTimeOffset>(0);
            cursorUpdatedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        var now = DateTimeOffset.UtcNow;
        var windowStart = Truncate(dirtyFrom, TimeSpan.FromHours(1)).AddHours(-1); // R (§7.3 passo 2)

        // ponto-fixo: nenhum intervalo pode cruzar a borda da janela (a cabeça se perderia)
        for (var i = 0; i < 8; i++)
        {
            var crossing = await ScalarAsync<DateTimeOffset?>(conn, tx, """
                SELECT min(started_at) FROM activity_intervals
                WHERE tenant_id = @t AND device_id = @d AND ended_at > @start AND started_at < @start
                """, [("t", tenantId), ("d", deviceId), ("start", windowStart)], ct);
            if (crossing is null || crossing >= windowStart) break;
            windowStart = crossing.Value;
        }

        // eventos da janela, na ordem canônica, com os campos do payload que o motor usa
        var events = new List<PipelineEvent>();
        await using (var command = new NpgsqlCommand("""
            SELECT seq, occurred_at, event_type, windows_sid, process_name, window_title,
                   payload->>'last_input_at', payload->>'state', payload->>'oldest_dropped_at'
            FROM raw_events
            WHERE tenant_id = @t AND device_id = @d AND occurred_at >= @from AND occurred_at <= @to
            ORDER BY occurred_at, seq
            """, conn, tx))
        {
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("d", deviceId);
            command.Parameters.AddWithValue("from", windowStart.AddMilliseconds(-clockOffsetMs)); // janela em tempo CRU
            command.Parameters.AddWithValue("to", now);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var offset = TimeSpan.FromMilliseconds(clockOffsetMs);
            while (await reader.ReadAsync(ct))
            {
                events.Add(new PipelineEvent
                {
                    Seq = reader.GetInt64(0),
                    OccurredAt = reader.GetFieldValue<DateTimeOffset>(1) + offset,
                    EventType = reader.GetString(2),
                    WindowsSid = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ProcessName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    WindowTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                    LastInputAt = ParseIso(reader, 6, offset),
                    HeartbeatState = reader.IsDBNull(7) ? null : reader.GetString(7),
                    OldestDroppedAt = ParseIso(reader, 8, offset),
                });
            }
        }

        // baseline de contiguidade de seq na borda da janela (lacuna-de-seq, §11.2)
        var seqBefore = await ScalarAsync<long?>(conn, tx, """
            SELECT max(seq) FROM raw_events
            WHERE tenant_id = @t AND device_id = @d AND occurred_at < @from
            """, [("t", tenantId), ("d", deviceId), ("from", windowStart.AddMilliseconds(-clockOffsetMs))], ct);

        var result = IntervalizationEngine.Build(events, seeds: null, seqBefore, windowStart);

        // delete-and-rebuild (idempotente)
        await ExecAsync(conn, tx, """
            DELETE FROM activity_intervals
            WHERE tenant_id = @t AND device_id = @d AND ended_at > @start
            """, [("t", tenantId), ("d", deviceId), ("start", windowStart)], ct);

        var rows = await MaterializeAsync(conn, tx, tenantId, deviceId, orgTimezone, result.Intervals, ct);

        // dirty_days: cada dia local (TZ da org) tocado (§7.3 passo 4) — consumo é F3
        var days = rows.Select(r => r.SourceDay).Distinct().ToList();
        foreach (var day in days)
        {
            await ExecAsync(conn, tx, """
                INSERT INTO dirty_days (tenant_id, device_id, day)
                VALUES (@t, @d, @day) ON CONFLICT DO NOTHING
                """, [("t", tenantId), ("d", deviceId), ("day", day)], ct);
        }

        // finaliza o cursor: dirty_from só zera se NADA chegou durante o processamento
        await ExecAsync(conn, tx, """
            UPDATE ingest_cursors SET
              processed_until = @until,
              dirty_from = CASE WHEN updated_at = @captured THEN NULL ELSE dirty_from END,
              updated_at = now()
            WHERE device_id = @d
            """, [("until", now), ("captured", cursorUpdatedAt), ("d", deviceId)], ct);

        await tx.CommitAsync(ct);

        logger?.LogInformation(
            "Intervalização: device {DeviceId} janela {From:o}→{To:o}, {Events} eventos → {Intervals} intervalos, lag {Lag:F0}s",
            deviceId, windowStart, now, events.Count, rows.Count, (now - dirtyFrom).TotalSeconds);
        return true;
    }

    // ------------------------------------------------------------ materialização
    private sealed record IntervalRow(
        Guid Id, Guid? DeviceUserId, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
        string State, Guid? AppId, string? WindowTitle, bool DataIncomplete, DateOnly SourceDay);

    private async Task<List<IntervalRow>> MaterializeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid deviceId,
        string orgTimezone, IReadOnlyList<BuiltInterval> intervals, CancellationToken ct)
    {
        if (intervals.Count == 0) return [];

        // app_catalog: auto-insert não-curado por process_name (display_name = process_name)
        var processNames = intervals
            .Where(i => i.State == IntervalStates.Active && i.ProcessName is not null)
            .Select(i => i.ProcessName!).Distinct().ToList();
        var appIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (processNames.Count > 0)
        {
            foreach (var name in processNames)
            {
                await ExecAsync(conn, tx, """
                    INSERT INTO app_catalog (id, process_name, display_name, curated)
                    VALUES (@id, @p, @p, false) ON CONFLICT (process_name) DO NOTHING
                    """, [("id", Uuid7.NewUuid7()), ("p", name)], ct);
            }
            await using var command = new NpgsqlCommand(
                "SELECT process_name, id FROM app_catalog WHERE process_name = ANY(@names)", conn, tx);
            command.Parameters.AddWithValue("names", processNames);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) appIds[reader.GetString(0)] = reader.GetGuid(1);
        }

        // device_user por (device_id, windows_sid)
        var userIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            "SELECT windows_sid, id FROM device_users WHERE tenant_id = @t AND device_id = @d", conn, tx))
        {
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("d", deviceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) userIds[reader.GetString(0)] = reader.GetGuid(1);
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(orgTimezone);
        var rows = new List<IntervalRow>();
        foreach (var interval in intervals)
        foreach (var (start, end, day) in SplitAtLocalMidnights(interval.StartedAt, interval.EndedAt, tz))
        {
            rows.Add(new IntervalRow(
                Uuid7.NewUuid7(),
                interval.WindowsSid is not null && userIds.TryGetValue(interval.WindowsSid, out var uid) ? uid : null,
                start, end, interval.State,
                interval.ProcessName is not null && appIds.TryGetValue(interval.ProcessName, out var aid) ? aid : null,
                interval.WindowTitle, interval.DataIncomplete, day));
        }

        foreach (var chunk in rows.Chunk(200))
        {
            await using var command = new NpgsqlCommand { Connection = conn, Transaction = tx };
            var values = new List<string>();
            var i = 0;
            foreach (var r in chunk)
            {
                values.Add($"(@id{i}, @t, @d, @u{i}, @s{i}, @e{i}, @st{i}, @a{i}, @w{i}, @inc{i}, @day{i})");
                command.Parameters.AddWithValue($"id{i}", r.Id);
                command.Parameters.AddWithValue($"u{i}", (object?)r.DeviceUserId ?? DBNull.Value);
                command.Parameters.AddWithValue($"s{i}", r.StartedAt);
                command.Parameters.AddWithValue($"e{i}", r.EndedAt);
                command.Parameters.AddWithValue($"st{i}", r.State);
                command.Parameters.AddWithValue($"a{i}", (object?)r.AppId ?? DBNull.Value);
                command.Parameters.AddWithValue($"w{i}", (object?)r.WindowTitle ?? DBNull.Value);
                command.Parameters.AddWithValue($"inc{i}", r.DataIncomplete);
                command.Parameters.AddWithValue($"day{i}", r.SourceDay);
                i++;
            }
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("d", deviceId);
            command.CommandText =
                "INSERT INTO activity_intervals (id, tenant_id, device_id, device_user_id, started_at, ended_at, state, app_id, window_title, data_incomplete, source_day) VALUES "
                + string.Join(", ", values);
            await command.ExecuteNonQueryAsync(ct);
        }

        return rows;
    }

    /// <summary>Divide [start, end) nas meias-noites do fuso da org; source_day = dia local do trecho.</summary>
    internal static IEnumerable<(DateTimeOffset Start, DateTimeOffset End, DateOnly Day)> SplitAtLocalMidnights(
        DateTimeOffset start, DateTimeOffset end, TimeZoneInfo tz)
    {
        var cursor = start;
        while (cursor < end)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, tz);
            var day = DateOnly.FromDateTime(local.Date);
            var nextMidnightLocal = local.Date.AddDays(1);
            var nextMidnightUtc = new DateTimeOffset(nextMidnightLocal, tz.GetUtcOffset(nextMidnightLocal)).ToUniversalTime();
            var sliceEnd = nextMidnightUtc < end ? nextMidnightUtc : end;
            if (sliceEnd > cursor) yield return (cursor, sliceEnd, day);
            cursor = sliceEnd;
        }
    }

    // ------------------------------------------------------------ partições mensais
    private async Task EnsureMonthlyPartitionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var month = new DateOnly(from.Year, from.Month, 1);
        var last = new DateOnly(to.Year, to.Month, 1);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        while (month <= last)
        {
            var name = $"activity_intervals_{month:yyyyMM}";
            if (_knownPartitions.Add(name))
            {
                var fromLit = month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var toLit = month.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"CREATE TABLE IF NOT EXISTS {name} PARTITION OF activity_intervals FOR VALUES FROM ('{fromLit}') TO ('{toLit}')";
                try
                {
                    await command.ExecuteNonQueryAsync(ct);
                }
                catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DuplicateTable or PostgresErrorCodes.UniqueViolation)
                {
                    // corrida benigna: partição já existe
                }
            }
            month = month.AddMonths(1);
        }
    }

    // ------------------------------------------------------------ helpers
    private static DateTimeOffset Truncate(DateTimeOffset value, TimeSpan unit)
        => new(value.UtcTicks - value.UtcTicks % unit.Ticks, TimeSpan.Zero);

    private static DateTimeOffset? ParseIso(NpgsqlDataReader reader, int ordinal, TimeSpan offset)
        => reader.IsDBNull(ordinal) ||
           !DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal, out var parsed)
            ? null
            : parsed + offset;

    private static async Task ExecAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql,
        (string Name, object? Value)[] args, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql,
        (string Name, object? Value)[] args, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default : (T)result;
    }
}
