using System.Data;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Aggregation;

/// <summary>
/// Job de agregação diária (Seção 7.3 passo final, §7.6) — consome dirty_days e recomputa
/// daily_device_summaries + daily_app_usage por (tenant, device, dia). Vive na Infrastructure
/// (e não no Worker) para ser invocável pelos testes de integração — o Worker apenas agenda.
///
/// RunOnceAsync DRENA a fila em lotes de 500 até o SELECT voltar vazio, com teto de tempo
/// de parede (CycleBudget) para não segurar o scheduler: na escala N25 (~2.500 devices) a
/// intervalização re-suja o dia corrente a cada ciclo de 60 s — um único lote por ciclo de
/// 15 min nunca alcançaria a fila. ORDER BY (day, tenant_id, device_id) dá tie-break
/// determinístico (sem starvation nos empates do dia corrente); cada par é tentado no
/// máximo UMA vez por ciclo (pares re-sujados durante a varredura ficam para o próximo).
///
/// Por par (tenant, device, day), UMA transação REPEATABLE READ:
///  1. pg_advisory_xact_lock(hashtext('dailyagg:' || device_id)) — o prefixo 'dailyagg:'
///     evita colisão com o lock da intervalização, que usa hashtext(device_id::text);
///  2. DELETE da linha de dirty_days. A corrida com a intervalização fecha nas DUAS ordens
///     porque o re-dirty de lá é um upsert no-op com lock de linha (ON CONFLICT DO UPDATE):
///     se o DELETE daqui chega primeiro, o upsert espera o commit e re-insere (o dia volta
///     à fila); se o upsert chega primeiro, o DELETE espera o commit da intervalização — e
///     sob REPEATABLE READ esta transação aborta com 40001 (benigno: nada foi consumido, o
///     dia continua sujo e o próximo ciclo reprocessa já enxergando os intervalos novos).
///     Com DO NOTHING havia furo: linha viva não é lockada, e a agregação podia consumir a
///     marca lendo activity_intervals pré-commit — agregado obsoleto permanente;
///  3. full recompute por DELETE + INSERT, e não pelo ON CONFLICT DO UPDATE citado na spec
///     (linha 777): o delete-and-rebuild dentro da transação elimina linhas obsoletas —
///     p.ex. a lane de um usuário que sumiu do dia após rebuild da intervalização — que o
///     upsert sozinho deixaria para sempre. O resultado é o mesmo full recompute pedido.
///     REPEATABLE READ garante snapshot ÚNICO para os dois INSERT...SELECT (summaries e
///     app_usage saem da MESMA versão de activity_intervals — invariante 11.3 entre elas).
///
/// Regras de cômputo (decisões documentadas onde a spec é silenciosa):
///  - agrupamento por source_day, que JÁ é o dia local da org (o worker divide os intervalos
///    na meia-noite do tenant) — zero matemática de fuso aqui;
///  - lane = device_user_id; NULL (intervalos de máquina: off_clean/no_data) vira o UUID
///    zero (spec linha 652);
///  - seconds_on = active + idle + locked; off_clean/no_data NUNCA contam — mesma definição
///    do rodapé da timeline (TimelineController, consistência 11.3);
///  - DERROGAÇÃO da spec linha 772 ("HEARTBEAT no_session na lane da máquina conta como
///    ligada sem sessão"): o pipeline F2 não materializa intervalo algum para no_session
///    (a presença "ligada sem sessão" é responsabilidade do device_current_state, que não
///    guarda histórico), logo o trecho é irrepresentável aqui e seconds_on da lane-máquina
///    é estruturalmente 0. Decisão de produto registrada; mudar exige o F2 passar a emitir
///    um intervalo de máquina para trechos no_session;
///  - first/last_event_at = bordas dos intervalos de USUÁRIO (active|idle|locked); lane que
///    só tem off_clean/no_data fica com NULL;
///  - durações: soma EXATA (numeric) por (lane, estado) com floor DEPOIS da soma — regra
///    canônica de arredondamento do gate 11.3, espelhada bit a bit pelo rodapé da timeline
///    (soma de ticks por lane com floor por lane; ver TimelineController.SecondsIn);
///  - classificação: SÓ intervalos active contam; app_id → tenant_app_categories →
///    categories.classification (+1/0/−1). App sem mapeamento no tenant (ou active com
///    app_id NULL) cai em seconds_neutral — equivale a "Não categorizado" (classification 0);
///  - seconds_neutral = seconds_active − work − not_work (resto): garante o invariante
///    work + neutral + not_work == seconds_active mesmo com truncamento por balde;
///  - data_incomplete = bool_or(data_incomplete) dos intervalos do dia daquela lane;
///  - computed_at = now();
///  - daily_app_usage: por (lane, app_id) dos intervalos active com app_id; seconds_active =
///    soma; focus_count = COUNT(*) de intervalos (após o merge N20 do pipeline, cada
///    intervalo ≈ um foco).
/// </summary>
public sealed class DailyAggregationService(NpgsqlDataSource dataSource, ILogger<DailyAggregationService>? logger = null)
{
    /// <summary>
    /// Teto de tempo de parede por ciclo: abaixo dos 15 min do agendamento para o
    /// DisallowConcurrentExecution do job nunca acumular misfires no Quartz.
    /// </summary>
    public static readonly TimeSpan CycleBudget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Um ciclo: DRENA dirty_days em lotes de 500 até esvaziar ou estourar o CycleBudget.
    /// Cada par é tentado no máximo uma vez por ciclo. Retorna quantos processou.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempted = new HashSet<(Guid, Guid, DateOnly)>();
        var processed = 0;

        while (stopwatch.Elapsed < CycleBudget)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new List<(Guid TenantId, Guid DeviceId, DateOnly Day)>();
            await using (var connection = await dataSource.OpenConnectionAsync(ct))
            await using (var command = new NpgsqlCommand(
                "SELECT tenant_id, device_id, day FROM dirty_days ORDER BY day, tenant_id, device_id LIMIT 500", connection))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    batch.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateOnly>(2)));
            }

            // só pares ainda não tentados NESTE ciclo: o dia corrente re-sujado pela
            // intervalização durante a varredura (e pares que falharam) ficam para o
            // próximo ciclo — sem este filtro o loop giraria até estourar o teto
            var fresh = batch.Where(p => attempted.Add(p)).ToList();
            if (fresh.Count == 0) break;

            foreach (var (tenantId, deviceId, day) in fresh)
            {
                ct.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed >= CycleBudget) break;
                try
                {
                    await ProcessDayAsync(tenantId, deviceId, day, ct);
                    processed++;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
                {
                    // corrida benigna com o upsert de dirty_days da intervalização (40001 sob
                    // REPEATABLE READ): nada foi consumido — o dia continua sujo e o próximo
                    // ciclo reprocessa já enxergando os intervalos novos
                    logger?.LogInformation(
                        "Agregação diária: corrida com a intervalização no device {DeviceId} dia {Day}; reprocessa no próximo ciclo.",
                        deviceId, day);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // um par com problema não pode parar a varredura dos demais
                    logger?.LogError(ex, "Agregação diária falhou para o device {DeviceId} no dia {Day}", deviceId, day);
                }
            }
        }
        return processed;
    }

    /// <summary>Recomputa os agregados de um (tenant, device, dia) numa única transação.</summary>
    public async Task ProcessDayAsync(Guid tenantId, Guid deviceId, DateOnly day, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // REPEATABLE READ: snapshot único para os dois INSERT...SELECT (summaries e app_usage
        // sempre coerentes entre si); escrita conflitante (o upsert no-op de dirty_days da
        // intervalização) vira 40001 — tratado como benigno em RunOnceAsync
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        // exclusão mútua por device, escopada à transação; prefixo próprio para não colidir
        // com o lock da intervalização (hashtext(device_id::text), sem prefixo)
        await ExecAsync(conn, tx, "SELECT pg_advisory_xact_lock(hashtext('dailyagg:' || @d::text))",
            [("d", deviceId)], ct);

        // consome a marca ANTES do recompute: ingestão que chegar durante a transação
        // re-insere a chave (espera o commit do DELETE) e o próximo ciclo reprocessa
        await ExecAsync(conn, tx,
            "DELETE FROM dirty_days WHERE tenant_id = @t AND device_id = @d AND day = @day",
            [("t", tenantId), ("d", deviceId), ("day", day)], ct);

        // delete-and-rebuild (ver cabeçalho): remove inclusive lanes/apps que deixaram de existir
        await ExecAsync(conn, tx,
            "DELETE FROM daily_device_summaries WHERE tenant_id = @t AND device_id = @d AND summary_date = @day",
            [("t", tenantId), ("d", deviceId), ("day", day)], ct);
        await ExecAsync(conn, tx,
            "DELETE FROM daily_app_usage WHERE tenant_id = @t AND device_id = @d AND summary_date = @day",
            [("t", tenantId), ("d", deviceId), ("day", day)], ct);

        await ExecAsync(conn, tx, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_idle, seconds_locked, seconds_on,
                seconds_work_related, seconds_neutral, seconds_not_work_related,
                first_event_at, last_event_at, data_incomplete, computed_at)
            SELECT @t, @day, @d, lane,
                   s_active, s_idle, s_locked,
                   s_active + s_idle + s_locked,
                   s_work,
                   s_active - s_work - s_not_work,
                   s_not_work,
                   first_event_at, last_event_at, incomplete, now()
            FROM (
                SELECT COALESCE(i.device_user_id, '00000000-0000-0000-0000-000000000000'::uuid) AS lane,
                       floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                           FILTER (WHERE i.state = 'active'), 0))::int AS s_active,
                       floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                           FILTER (WHERE i.state = 'idle'), 0))::int AS s_idle,
                       floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                           FILTER (WHERE i.state = 'locked'), 0))::int AS s_locked,
                       floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                           FILTER (WHERE i.state = 'active' AND c.classification = 1), 0))::int AS s_work,
                       floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                           FILTER (WHERE i.state = 'active' AND c.classification = -1), 0))::int AS s_not_work,
                       min(i.started_at) FILTER (WHERE i.state IN ('active','idle','locked')) AS first_event_at,
                       max(i.ended_at) FILTER (WHERE i.state IN ('active','idle','locked')) AS last_event_at,
                       bool_or(i.data_incomplete) AS incomplete
                FROM activity_intervals i
                LEFT JOIN tenant_app_categories tac ON tac.tenant_id = i.tenant_id AND tac.app_id = i.app_id
                LEFT JOIN categories c ON c.tenant_id = i.tenant_id AND c.id = tac.category_id
                WHERE i.tenant_id = @t AND i.device_id = @d AND i.source_day = @day
                GROUP BY 1
            ) lanes
            """, [("t", tenantId), ("d", deviceId), ("day", day)], ct);

        await ExecAsync(conn, tx, """
            INSERT INTO daily_app_usage (
                tenant_id, summary_date, device_id, device_user_id, app_id, seconds_active, focus_count)
            SELECT @t, @day, @d,
                   COALESCE(i.device_user_id, '00000000-0000-0000-0000-000000000000'::uuid),
                   i.app_id,
                   floor(sum(extract(epoch FROM i.ended_at - i.started_at)))::int,
                   count(*)::int
            FROM activity_intervals i
            WHERE i.tenant_id = @t AND i.device_id = @d AND i.source_day = @day
              AND i.state = 'active' AND i.app_id IS NOT NULL
            GROUP BY COALESCE(i.device_user_id, '00000000-0000-0000-0000-000000000000'::uuid), i.app_id
            """, [("t", tenantId), ("d", deviceId), ("day", day)], ct);

        await tx.CommitAsync(ct);

        logger?.LogInformation("Agregação diária: device {DeviceId} dia {Day} recomputado.", deviceId, day);
    }

    // ------------------------------------------------------------ helpers
    private static async Task ExecAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql,
        (string Name, object? Value)[] args, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
