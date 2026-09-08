using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Aggregation;

/// <summary>
/// ROLLUP MENSAL (item 3 do estudo, "agregados mensais"). Resume daily_device_summaries em
/// monthly_summaries, no mesmo grão menos o dia.
///
/// POR QUE EXISTE: os endpoints históricos têm teto de 92 dias porque varrer a diária num ano
/// inteiro não é leitura de tela. Com o teto, trimestre e ano eram impossíveis de prometer. O
/// mensal custa 1/30 das linhas e destrava janelas de até 24 meses — que é exatamente a retenção
/// dos agregados, então a janela máxima do produto passa a ser a do dado que ele guarda.
///
/// MARCA-D'ÁGUA, não "recompute o mês corrente": o job roda de hora em hora e não pode revarrer
/// 24 meses a cada ciclo. monthly_rollup_state.watermark guarda até onde a diária já foi
/// consumida; cada ciclo pergunta só "que meses têm linha com computed_at acima da marca?".
/// Isso resolve de graça a REAGREGAÇÃO RETROATIVA (decisão 7 do spec): reagregar reescreve
/// computed_at das linhas afetadas, então o mês antigo reaparece na pergunta sozinho — sem
/// ninguém precisar avisar o rollup.
///
/// A marca nova é lida ANTES de recomputar e o recorte é (watermark, novaMarca], mas quem
/// realmente fecha a janela de perda é a SafetyMargin — ver o comentário dela: computed_at é
/// o INÍCIO da transação, então uma agregação concorrente pode gravar um carimbo anterior à
/// marca e ficar invisível a esta leitura.
///
/// Recompute por DELETE + INSERT do mês inteiro, dentro de uma transação, e não upsert: o
/// delete-and-rebuild elimina linha obsoleta — a lane de um usuário que sumiu do mês depois de
/// um rebuild — que o upsert sozinho deixaria para sempre. Mesma escolha, pelo mesmo motivo,
/// que o DailyAggregationService faz por (device, dia).
///
/// Exclusão entre instâncias: pg_try_advisory_xact_lock com prefixo próprio. Se outra instância
/// já está no rollup deste tenant, esta sai limpa — o ciclo da outra cobre o trabalho.
/// </summary>
public sealed class MonthlyRollupService(NpgsqlDataSource dataSource, ILogger<MonthlyRollupService>? logger = null)
{
    /// <summary>
    /// MARGEM DE SEGURANÇA da marca-d'água. A marca gravada é <c>max(computed_at) − margem</c>,
    /// e não o máximo cru.
    ///
    /// POR QUÊ: computed_at é <c>now()</c>, que no Postgres é o INÍCIO da transação. Uma
    /// agregação diária que começou ANTES de este ciclo ler o máximo e só commitou DEPOIS grava
    /// um carimbo anterior à marca — invisível na leitura, e portanto perdida para sempre se a
    /// marca avançasse até o máximo cru. Silenciosamente: o mês ficaria eternamente defasado
    /// sem nenhum erro em lugar nenhum.
    ///
    /// O CUSTO é revisitar os meses tocados nos últimos cinco minutos, que na prática é UM mês,
    /// e recompor um mês é um DELETE + INSERT barato. O que a marca continua evitando — varrer
    /// 24 meses de diária a cada ciclo — permanece evitado.
    /// </summary>
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Um ciclo: para cada organização, recompõe os meses tocados desde a marca-d'água.
    /// Devolve quantos pares (tenant, mês) foram reprocessados. Num ciclo sem novidade o número
    /// é zero ou, quando houve escrita nos últimos minutos, o punhado de meses dentro da
    /// SafetyMargin — nunca o histórico inteiro, que é o ponto da marca.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var tenants = new List<Guid>();
        await using (var command = new NpgsqlCommand("SELECT id FROM organizations", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                tenants.Add(reader.GetGuid(0));
            }
        }

        var processed = 0;
        foreach (var tenantId in tenants)
        {
            processed += await RollUpTenantAsync(connection, tenantId, ct);
        }

        if (processed > 0)
        {
            logger?.LogInformation("Rollup mensal: {Months} par(es) (tenant, mês) recomputado(s).", processed);
        }

        return processed;
    }

    private static async Task<int> RollUpTenantAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_try_advisory_xact_lock(hashtext('monthlyrollup:' || @t::text))", connection, tx))
        {
            lockCommand.Parameters.AddWithValue("t", tenantId);
            if (!(bool)(await lockCommand.ExecuteScalarAsync(ct))!)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }
        }

        // teto do recorte, lido ANTES de recomputar: o que a diária escrever daqui em diante
        // fica para o ciclo seguinte em vez de ser pulado
        var novaMarca = await ScalarAsync<DateTime?>(connection, tx,
            "SELECT max(computed_at) FROM daily_device_summaries WHERE tenant_id = @t",
            ct, ("t", tenantId));

        if (novaMarca is null)
        {
            await tx.RollbackAsync(ct);
            return 0; // tenant sem nenhuma diária: nada a resumir
        }

        // A marca vigente entra por SUBCONSULTA em vez de ida e volta pelo .NET: assim o
        // '-infinity' do primeiro ciclo (que não tem equivalente em DateTime) fica onde sabe
        // ser comparado, e some a chance de o driver reinterpretar o fuso no caminho.
        var meses = new List<DateOnly>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT DISTINCT date_trunc('month', summary_date)::date AS mes
            FROM daily_device_summaries
            WHERE tenant_id = @t
              AND computed_at > COALESCE(
                  (SELECT watermark FROM monthly_rollup_state WHERE tenant_id = @t),
                  '-infinity'::timestamptz)
              AND computed_at <= @nova
            ORDER BY mes
            """, connection, tx))
        {
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("nova", novaMarca.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                meses.Add(reader.GetFieldValue<DateOnly>(0));
            }
        }

        foreach (var mes in meses)
        {
            await ExecAsync(connection, tx,
                "DELETE FROM monthly_summaries WHERE tenant_id = @t AND month_start = @m",
                ct, ("t", tenantId), ("m", mes));

            await ExecAsync(connection, tx, RollUpSql, ct, ("t", tenantId), ("m", mes));
        }

        // a marca recua a margem de segurança: ver SafetyMargin
        await ExecAsync(connection, tx,
            """
            INSERT INTO monthly_rollup_state (tenant_id, watermark, updated_at)
            VALUES (@t, @nova, now())
            ON CONFLICT (tenant_id) DO UPDATE
                SET watermark = EXCLUDED.watermark, updated_at = now()
            """,
            ct, ("t", tenantId), ("nova", novaMarca.Value - SafetyMargin));

        await tx.CommitAsync(ct);
        return meses.Count;
    }

    /// <summary>
    /// O mês inteiro, somado da diária no MESMO grão menos o dia.
    ///
    /// days_with_data conta os dias em que ESTA lane teve tempo ligado — é o person_days do
    /// período, que se perderia ao somar os dias fora e não dá para reconstruir depois. Já
    /// person_count e device_count continuam derivando de COUNT DISTINCT na leitura, porque as
    /// chaves sobrevivem no grão.
    ///
    /// data_incomplete propaga por bool_or: um único dia incompleto marca o mês, que é o
    /// comportamento honesto — o mês não fica "completo" por diluição.
    /// </summary>
    private const string RollUpSql = """
        INSERT INTO monthly_summaries (
            tenant_id, month_start, device_id, device_user_id,
            seconds_active, seconds_idle, seconds_locked, seconds_on,
            seconds_work_related, seconds_neutral, seconds_not_work_related, seconds_unclassified,
            days_with_data, data_incomplete, computed_at)
        SELECT d.tenant_id, @m, d.device_id, d.device_user_id,
               sum(d.seconds_active), sum(d.seconds_idle), sum(d.seconds_locked), sum(d.seconds_on),
               sum(d.seconds_work_related), sum(d.seconds_neutral),
               sum(d.seconds_not_work_related), sum(d.seconds_unclassified),
               count(*) FILTER (WHERE d.seconds_on > 0)::int,
               bool_or(d.data_incomplete),
               now()
        FROM daily_device_summaries d
        WHERE d.tenant_id = @t
          AND d.summary_date >= @m::date
          AND d.summary_date < (@m::date + INTERVAL '1 month')
        GROUP BY d.tenant_id, d.device_id, d.device_user_id
        """;

    private static async Task ExecAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var value2 = await command.ExecuteScalarAsync(ct);
        return value2 is null or DBNull ? default : (T)value2;
    }
}
