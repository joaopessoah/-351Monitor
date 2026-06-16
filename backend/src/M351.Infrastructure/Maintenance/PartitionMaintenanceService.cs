using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Maintenance;

/// <summary>
/// Job PartitionMaintenance (Secao 7.6; tabela 7.2; entrega F4 N10/N11/N13). Roda 1x/dia (02:00
/// America/Sao_Paulo, agendado pelo Worker). Faz DUAS coisas, em auto-commit (DDL idempotente,
/// fora de qualquer transacao de ingestao/intervalizacao — espelha RawEventPartitionManager e
/// IntervalizationService.EnsureMonthlyPartitionsAsync):
///
///  1. CRIA particoes FUTURAS proativas (CREATE TABLE IF NOT EXISTS, mesmo formato das criadas
///     pela InitialCreate, que so cobriu mes corrente + proximo):
///       - raw_events: diarias ate D+3 (hoje + 3 dias);
///       - activity_intervals e audit_log: mensais ate mes+2.
///     Sem isto, ~ago/2026 os INSERT de raw_events/audit_log cairiam fora de qualquer particao e
///     falhariam (a janela de aceitacao N9 ja cria raw on-demand no ingest, mas audit_log nao tem
///     criador on-demand — este job e a unica rede de seguranca dele).
///
///  2. DROPA particoes EXPIRADAS, alem da retencao (DROP TABLE da particao filha = detach
///     implicito; sem pg_partman — corte 6):
///       - raw_events diarias com bound superior <= now - 90d (N10);
///       - activity_intervals mensais com bound superior <= now - 12m (N11);
///       - audit_log mensais com bound superior <= now - 24m (N13).
///
/// REGRA DE SEGURANCA INEGOCIAVEL (proibicao da fatia): NUNCA dropar a particao corrente, nem
/// futura, nem qualquer uma que possa conter dado DENTRO da retencao. A enumeracao le o bound de
/// CADA particao filha via pg_inherits/pg_class + pg_get_expr(relpartbound) e so marca para drop a
/// particao cujo LIMITE SUPERIOR (exclusivo) e <= ao corte. Como o range e [inf, sup), upper <=
/// corte garante que TODA linha da particao tem occurred_at/started_at < corte: nada vivo e
/// perdido (sem perda de dado dentro da retencao).
///
/// CUIDADO COM O LOCK (NAO basta a folha estar sem escrita): em Postgres 16, DROP TABLE da
/// particao filha exige AccessExclusiveLock no PARENT particionado (altera o partition descriptor),
/// e TODO INSERT da ingestao segura RowExclusiveLock no MESMO parent (raw_events/audit_log) — ainda
/// que mirando a particao CORRENTE, nao a antiga. Esses dois locks CONFLITAM: enquanto o DROP
/// aguarda o lock do parent, ele enfileira e bloqueia os novos INSERT atras dele. Sem lock_timeout,
/// o DROP herdaria o CommandTimeout default do Npgsql (~30s), estendendo o stall — exatamente a
/// classe de travamento do commit 3c7ea3e. Por isso DropExpiredAsync seta lock_timeout curto por
/// sessao ANTES dos DROP e trata 55P03 (LockNotAvailable) como skip benigno (retenta no dia
/// seguinte), em vez de travar a ingestao na janela 02:00 BRT.
///
/// Grava maintenance_runs com as contagens (criadas/dropadas por tabela) — fonte da F4.8.
/// </summary>
public sealed class PartitionMaintenanceService(NpgsqlDataSource dataSource, ILogger<PartitionMaintenanceService>? logger = null)
{
    /// <summary>Retencao N10 de raw_events: 90 dias (particoes diarias).</summary>
    public static readonly TimeSpan RawRetention = TimeSpan.FromDays(90);

    /// <summary>Dias de particoes diarias futuras de raw_events criadas proativamente (D+3).</summary>
    public const int RawFutureDays = 3;

    /// <summary>Retencao N11 de activity_intervals: 12 meses (particoes mensais).</summary>
    public const int IntervalsRetentionMonths = 12;

    /// <summary>Retencao N13 de audit_log: 24 meses (particoes mensais).</summary>
    public const int AuditRetentionMonths = 24;

    /// <summary>Meses de particoes mensais futuras criadas proativamente (mes+2).</summary>
    public const int FutureMonths = 2;

    /// <summary>
    /// extrai os literais do bound "FOR VALUES FROM ('2026-06-01 00:00:00+00') TO ('2026-07-01 ...')".
    /// O upper bound (segundo grupo) e o que decide o drop: range [from, to), to exclusivo.
    /// </summary>
    private static readonly Regex BoundRegex = new(
        @"FROM \('(?<from>[^']+)'\) TO \('(?<to>[^']+)'\)", RegexOptions.Compiled);

    private sealed record PartitionInfo(string Name, DateTimeOffset UpperBound);

    /// <summary>
    /// Contagens de uma execucao (criadas/dropadas por tabela) — espelha o que vai em
    /// maintenance_runs.detail e permite aos testes asseridarem o ciclo sem reler a trilha global.
    /// </summary>
    public sealed record PartitionMaintenanceResult(
        int RawCreated, int IntervalsCreated, int AuditCreated,
        int RawDropped, int IntervalsDropped, int AuditDropped);

    /// <summary>Um ciclo completo: cria futuras + dropa expiradas das tres tabelas particionadas.</summary>
    public async Task<PartitionMaintenanceResult> RunOnceAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var now = startedAt;
        var detail = new Dictionary<string, object>();
        try
        {
            // ----- 1. criacao proativa de particoes futuras -----
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var rawCreated = await EnsureDailyPartitionsAsync(today, today.AddDays(RawFutureDays), ct);

            var thisMonth = new DateOnly(now.Year, now.Month, 1);
            var lastMonth = thisMonth.AddMonths(FutureMonths);
            var intervalsCreated = await EnsureMonthlyPartitionsAsync("activity_intervals", thisMonth, lastMonth, ct);
            var auditCreated = await EnsureMonthlyPartitionsAsync("audit_log", thisMonth, lastMonth, ct);

            // ----- 2. drop de particoes alem da retencao -----
            var rawDropped = await DropExpiredAsync("raw_events", now - RawRetention, ct);
            var intervalsDropped = await DropExpiredAsync("activity_intervals", now.AddMonths(-IntervalsRetentionMonths), ct);
            var auditDropped = await DropExpiredAsync("audit_log", now.AddMonths(-AuditRetentionMonths), ct);

            detail["raw_events_created"] = rawCreated;
            detail["activity_intervals_created"] = intervalsCreated;
            detail["audit_log_created"] = auditCreated;
            detail["raw_events_dropped"] = rawDropped;
            detail["activity_intervals_dropped"] = intervalsDropped;
            detail["audit_log_dropped"] = auditDropped;

            await MaintenanceRunRecorder.RecordAsync(
                dataSource, MaintenanceRunRecorder.PartitionMaintenance, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusOk, detail, ct);

            logger?.LogInformation(
                "PartitionMaintenance: criadas raw={RawNew}/intervals={IntNew}/audit={AuditNew}; "
                + "dropadas raw={RawDel}/intervals={IntDel}/audit={AuditDel}.",
                rawCreated, intervalsCreated, auditCreated, rawDropped, intervalsDropped, auditDropped);

            return new PartitionMaintenanceResult(
                rawCreated, intervalsCreated, auditCreated, rawDropped, intervalsDropped, auditDropped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "PartitionMaintenance falhou.");
            detail["error"] = ex.Message;
            // a trilha tem de registrar a falha (a Transparencia nao pode mostrar sucesso falso)
            await SafeRecordErrorAsync(MaintenanceRunRecorder.PartitionMaintenance, startedAt, detail);
            // contagens parciais nao sao confiaveis apos a excecao — zeradas no retorno
            return new PartitionMaintenanceResult(0, 0, 0, 0, 0, 0);
        }
    }

    // ------------------------------------------------------------ criacao de futuras
    /// <summary>
    /// lock_timeout curto por sessao tambem na CRIACAO: CREATE TABLE ... PARTITION OF tambem exige
    /// AccessExclusiveLock no PARENT (confirmado empiricamente em PG16 — bloqueia atras do
    /// RowExclusiveLock de um INSERT da ingestao igual ao DROP). So roda DDL no dia de borda em que
    /// a particao futura ainda nao existe (o to_regclass curto-circuita o resto), mas nesse dia, sob
    /// ingestao viva, sem lock_timeout o CREATE estenderia o stall ate o CommandTimeout (~30s).
    /// </summary>
    private const string DdlLockTimeout = "2s";

    /// <summary>Cria particoes DIARIAS [from, to) (to exclusivo) de raw_events. Retorna quantas criou.</summary>
    private async Task<int> EnsureDailyPartitionsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var created = 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await SetLockTimeoutAsync(connection, ct);
        for (var day = from; day < to; day = day.AddDays(1))
        {
            var name = $"raw_events_{day:yyyyMMdd}";
            var lo = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var hi = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (await CreatePartitionAsync(connection, name, "raw_events", lo, hi, ct)) created++;
        }
        return created;
    }

    /// <summary>Cria particoes MENSAIS [from, to] de uma tabela mensal. Retorna quantas criou.</summary>
    private async Task<int> EnsureMonthlyPartitionsAsync(
        string table, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var created = 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await SetLockTimeoutAsync(connection, ct);
        for (var month = from; month <= to; month = month.AddMonths(1))
        {
            var name = $"{table}_{month:yyyyMM}";
            var lo = month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var hi = month.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (await CreatePartitionAsync(connection, name, table, lo, hi, ct)) created++;
        }
        return created;
    }

    /// <summary>SET lock_timeout curto na sessao — DDL que disputa o lock do parent aborta rapido (55P03).</summary>
    private static async Task SetLockTimeoutAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var setTimeout = connection.CreateCommand();
        setTimeout.CommandText = $"SET lock_timeout = '{DdlLockTimeout}'";
        await setTimeout.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Cria a particao [lo, hi) se ainda nao existir. Retorna true SO quando de fato criou (a
    /// contagem de "criadas" precisa ser fiel — CREATE TABLE IF NOT EXISTS sozinho nao lanca quando
    /// a particao ja existe, entao contar todo CREATE infla o numero). Checa to_regclass antes;
    /// mantem o IF NOT EXISTS para a corrida benigna com ingest/outra instancia entre o check e o
    /// create (idempotente — armadilha do commit 3c7ea3e: CREATE de particao nao pode colidir).
    ///
    /// Com lock_timeout curto na sessao (SetLockTimeoutAsync): se o lock do parent estiver ocupado
    /// pela ingestao, o CREATE aborta com 55P03 e a particao e PULADA (retorna false, nao conta) —
    /// nao trava a ingestao. Pular e benigno: a proxima execucao diaria refaz com a mesma folga
    /// (raw D+3, mensais mes+2); raw ainda tem o criador on-demand no proprio ingest como rede.
    /// </summary>
    private async Task<bool> CreatePartitionAsync(
        NpgsqlConnection connection, string name, string parent, string lo, string hi, CancellationToken ct)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT to_regclass(@n) IS NOT NULL";
            exists.Parameters.AddWithValue("n", name);
            if ((bool)(await exists.ExecuteScalarAsync(ct))!) return false; // ja existia
        }

        await using var command = connection.CreateCommand();
        // nomes/literais sao internos (derivados de datas), nunca de input do usuario
        command.CommandText =
            $"CREATE TABLE IF NOT EXISTS {name} PARTITION OF {parent} FOR VALUES FROM ('{lo}') TO ('{hi}')";
        try
        {
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DuplicateTable or PostgresErrorCodes.UniqueViolation)
        {
            // corrida benigna: alguem criou entre o check e o create (ingest/outra instancia)
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            // lock do parent ocupado pela ingestao: pular (nao travar). Retenta na proxima execucao.
            logger?.LogInformation(
                "PartitionMaintenance: criacao da particao {Partition} pulada (lock do parent {Parent} ocupado por ingestao); retenta na proxima execucao.",
                name, parent);
            return false;
        }
    }

    // ------------------------------------------------------------ drop de expiradas
    /// <summary>Lock curto por sessao antes de cada DROP — barra o stall da ingestao (ver doc da classe).</summary>
    private const string DropLockTimeout = "2s";

    /// <summary>
    /// Dropa as particoes filhas de <paramref name="parent"/> cujo LIMITE SUPERIOR (exclusivo) e
    /// &lt;= <paramref name="cutoff"/> — i.e. toda linha dentro delas e mais antiga que o corte.
    /// Particao corrente/futura tem upper &gt; cutoff e fica intacta. Retorna quantas dropou.
    ///
    /// O DROP exige AccessExclusiveLock no PARENT, que conflita com o RowExclusiveLock de qualquer
    /// INSERT da ingestao (ver doc da classe). Por isso seta lock_timeout curto na sessao: se o lock
    /// do parent nao vier em DropLockTimeout, o Postgres aborta com 55P03 e a particao e PULADA (sem
    /// estender o stall ate o CommandTimeout de ~30s) — a proxima execucao diaria a dropa.
    /// </summary>
    private async Task<int> DropExpiredAsync(string parent, DateTimeOffset cutoff, CancellationToken ct)
    {
        var partitions = await EnumeratePartitionsAsync(parent, ct);
        var dropped = 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // lock_timeout curto na SESSAO: o DROP que nao consegue o lock do parent aborta rapido (55P03)
        // em vez de travar os INSERT da ingestao enfileirados atras dele ate o CommandTimeout.
        await using (var setTimeout = connection.CreateCommand())
        {
            setTimeout.CommandText = $"SET lock_timeout = '{DropLockTimeout}'";
            await setTimeout.ExecuteNonQueryAsync(ct);
        }

        foreach (var partition in partitions.Where(p => p.UpperBound <= cutoff))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var command = connection.CreateCommand();
                // DROP da particao filha = detach implicito + remocao; nome interno (enumerado do catalogo)
                command.CommandText = $"DROP TABLE IF EXISTS {partition.Name}";
                await command.ExecuteNonQueryAsync(ct);
                dropped++;
                logger?.LogInformation(
                    "PartitionMaintenance: particao {Partition} (ate {Upper:yyyy-MM-dd}) dropada — alem do corte {Cutoff:yyyy-MM-dd}.",
                    partition.Name, partition.UpperBound, cutoff);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                // lock do parent ocupado pela ingestao: pular esta particao (nao travar a ingestao).
                // A particao continua alem do corte; a proxima execucao diaria (02:00 BRT) a dropa.
                logger?.LogInformation(
                    "PartitionMaintenance: particao {Partition} pulada (lock do parent {Parent} ocupado por ingestao); retenta na proxima execucao.",
                    partition.Name, parent);
            }
        }
        return dropped;
    }

    /// <summary>
    /// Enumera as particoes filhas de uma tabela particionada via pg_inherits + pg_class, lendo o
    /// bound declarado (pg_get_expr(relpartbound)) e extraindo o limite superior. Particao DEFAULT
    /// ou bound nao reconhecido e ignorada (jamais dropada) — seguranca por construcao.
    /// </summary>
    private async Task<List<PartitionInfo>> EnumeratePartitionsAsync(string parent, CancellationToken ct)
    {
        var result = new List<PartitionInfo>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT child.relname, pg_get_expr(child.relpartbound, child.oid)
            FROM pg_inherits
            JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
            JOIN pg_class child  ON child.oid  = pg_inherits.inhrelid
            WHERE parent.relname = @parent
            """, connection);
        command.Parameters.AddWithValue("parent", parent);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var bound = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (bound is null) continue; // particao DEFAULT (sem range): nunca dropar
            var match = BoundRegex.Match(bound);
            if (!match.Success) continue; // bound inesperado: pular por seguranca
            if (!DateTimeOffset.TryParse(match.Groups["to"].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var upper))
                continue;
            result.Add(new PartitionInfo(name, upper));
        }
        return result;
    }

    private async Task SafeRecordErrorAsync(string jobName, DateTimeOffset startedAt, object detail)
    {
        try
        {
            await MaintenanceRunRecorder.RecordAsync(
                dataSource, jobName, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusError, detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // se nem a trilha grava, o Serilog e a unica fonte — nao re-lanca (o worker nao cai)
            logger?.LogError(ex, "Falha ao gravar maintenance_runs (status=error) de {Job}.", jobName);
        }
    }
}
