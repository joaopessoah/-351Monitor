using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Alerts;

/// <summary>
/// Motor de ALERTAS DE GESTÃO (F6, seção 4 do spec de 07/09/2026). Avalia as regras sobre os
/// AGREGADOS DIÁRIOS (nunca sobre evento cru) e mantém o estado em <c>management_alerts</c>:
/// faz upsert do que está valendo e resolve o que deixou de valer.
///
/// DESENHO COPIADO DO <see cref="FleetAlertService"/> (o irmão de frota), porque a calibragem
/// anti-fadiga é o produto aqui — alerta ruidoso é alerta desligado:
///  - ESTADO em tabela própria, um alerta por (tenant, regra, escopo): reavaliar a cada 15 min
///    atualiza a MESMA linha, então a tela nunca vira uma pilha do mesmo aviso;
///  - COOLDOWN de 24 h: alerta resolvido não pode renascer antes disso. É o que impede o
///    flapping de uma métrica oscilando em volta do limiar (84,9% / 85,1% de cobertura);
///  - SILÊNCIO FORA DO HORÁRIO: fora da janela de trabalho da org nenhum alerta NOVO nasce
///    (nem renasce). Os que já estão vivos continuam sendo atualizados e resolvidos — o estado
///    tem de seguir verdadeiro de madrugada; o que se evita é datar um alerta novo numa hora
///    em que ninguém pode agir sobre ele;
///  - OPT-IN da organização para as regras de escopo PESSOA (decisão 5): com
///    <c>person_alerts_enabled = false</c> as duas regras de pessoa NÃO são avaliadas, e os
///    alertas de pessoa que existiam são RESOLVIDOS no ciclo seguinte (desligar o toggle apaga
///    o que ele havia produzido);
///  - PLANO PRO: alertas de gestão são do Pro (spec seção 4). Org fora do plano é avaliada com
///    conjunto VAZIO de candidatos — o que resolve automaticamente o que ela tinha, em vez de
///    deixar alerta órfão vivo para sempre depois de um downgrade.
///
/// VOCABULÁRIO: este serviço grava NÚMEROS (variação, horas, dias) em <c>detail</c>. A frase
/// exibida é montada no AlertsController a partir deles, para "variação e contexto, nunca
/// julgamento" ter uma fonte única. Nada aqui produz ranking, e ocioso nunca entra como
/// improdutivo: as regras que olham improdutivo somam <c>seconds_not_work_related</c>, que é
/// tempo ATIVO em app classificado como tal — estado de máquina fica de fora.
/// </summary>
public sealed class ManagementAlertService(
    NpgsqlDataSource dataSource,
    ILogger<ManagementAlertService>? logger = null)
{
    /// <summary>Nome do job na trilha de <c>maintenance_runs</c> (evidência de que rodou).</summary>
    public const string JobName = "ManagementAlerts";

    /// <summary>Plano com alertas de gestão (spec seção 4); fora dele, nenhum candidato.</summary>
    public const string RequiredPlan = "pro";

    // ---------------------------------------------------------------- limiares das regras
    /// <summary>Piso da cobertura da classificação na organização (regra 2 da seção 4).</summary>
    public const double CoverageFloor = 0.85;

    /// <summary>Equipe 15% abaixo da média: razão de 0,85 sobre a base de 4 semanas.</summary>
    public const double TeamActiveDropRatio = 0.85;

    /// <summary>Teto do improdutivo sobre o tempo ativo da equipe na semana (regra 7).</summary>
    public const double UnproductiveCeiling = 0.25;

    /// <summary>"Dia longo": máquina ligada acima de 10 h (regra 5).</summary>
    public const int LongDayHours = 10;

    /// <summary>Dias longos que caracterizam a semana (3 ou mais).</summary>
    public const int LongDaysInWeek = 3;

    /// <summary>Atividade fora do horário na semana que vira alerta de equilíbrio (regra 4).</summary>
    public const int AfterHoursWeeklyHours = 5;

    /// <summary>
    /// MÍNIMO DE GRUPO (decisão 3): média comparativa de equipe só com 3 pessoas ou mais.
    /// Abaixo disso a "média da equipe" é a média de uma pessoa com outro nome, e o produto
    /// não faz afirmação sobre indivíduo fora do opt-in.
    /// </summary>
    public const int MinGroupSize = 3;

    /// <summary>Semanas completas que formam a base da comparação de tempo ativo.</summary>
    public const int BaselineWeeks = 4;

    /// <summary>Cooldown anti-flapping: alerta resolvido não renasce antes de 24 h.</summary>
    public const int CooldownHours = 24;

    /// <summary>
    /// Dias com dado exigidos para a projeção da meta valer. Com um único dia a projeção é
    /// ruído (segunda 9h com 1 h registrada projetaria a semana inteira por 1 h).
    /// </summary>
    public const int GoalProjectionMinDays = 2;

    // ---------------------------------------------------------------- kinds e escopos
    public const string KindCoverageBelow = "coverage_below";
    public const string KindTeamActiveDrop = "team_active_drop";
    public const string KindTeamUnproductiveHigh = "team_unproductive_high";
    public const string KindPersonLongDays = "person_long_days";
    public const string KindPersonAfterHours = "person_after_hours";
    public const string KindGoalAtRisk = "goal_at_risk";
    public const string KindBusinessDayNoData = "business_day_no_data";

    public const string ScopeOrganization = "organization";
    public const string ScopeTeam = "team";
    public const string ScopePerson = "person";
    public const string ScopeDevice = "device";

    /// <summary>Escopo da organização inteira não tem chave; '-' evita NULL na PK.</summary>
    public const string OrganizationScopeKey = "-";

    /// <summary>
    /// Separador da chave composta (kind + escopo) usada no resolve em lote. Unit separator
    /// (U+001F) não aparece em etiqueta de equipe nem em SID, então não há colisão possível.
    /// </summary>
    private const char KeySeparator = (char)31;

    /// <summary>Um alerta candidato de um ciclo: a chave do estado mais os números do contexto.</summary>
    private sealed record Candidate(
        string Kind, string ScopeType, string ScopeKey, Dictionary<string, object?> Detail);

    private sealed record OrgRow(
        Guid Id, string Name, string Timezone, string? BusinessHours, string Plan,
        int? GoalWeeklyActiveHours, bool PersonAlertsEnabled);

    /// <summary>
    /// Um ciclo completo: avalia todas as orgs ativas e sincroniza <c>management_alerts</c>.
    /// Devolve quantos alertas ficaram VIVOS no fim do ciclo (número que o job loga).
    ///
    /// Lock consultivo PRÓPRIO (<c>hashtext('management_alerts')</c>, distinto dos demais jobs)
    /// para duas instâncias do worker não avaliarem o mesmo tenant em paralelo — sem ele, duas
    /// avaliações concorrentes poderiam resolver e reabrir o mesmo alerta em sequência.
    /// </summary>
    public async Task<int> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        // Npgsql só aceita offset 0 em timestamptz: normalizar aqui deixa o serviço à prova de
        // um chamador que passe o "agora" no fuso do tenant (e os testes usam exatamente isso).
        nowUtc = nowUtc.ToUniversalTime();

        var startedAt = DateTimeOffset.UtcNow;
        var detail = new Dictionary<string, object?>();
        var vivos = 0;

        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_try_advisory_xact_lock(hashtext('management_alerts'))", conn, tx))
            {
                var acquired = (bool)(await lockCommand.ExecuteScalarAsync(ct))!;
                if (!acquired)
                {
                    logger?.LogInformation("Alertas de gestão: outra instância já rodando; ciclo pulado.");
                    await tx.RollbackAsync(ct);
                    return 0;
                }
            }

            var orgs = await QueryOrgsAsync(conn, tx, ct);
            detail["orgs"] = orgs.Count;

            foreach (var org in orgs)
            {
                var candidates = org.Plan == RequiredPlan
                    ? await EvaluateAsync(conn, tx, org, nowUtc, ct)
                    // fora do plano Pro: conjunto vazio resolve o que a org tinha (downgrade)
                    : [];

                // silêncio fora do horário: alerta NOVO só nasce dentro da janela da org
                var permiteNovos = BusinessHoursWindow.IsWithin(org.BusinessHours, org.Timezone, nowUtc);

                vivos += await SyncAsync(conn, tx, org.Id, candidates, nowUtc, permiteNovos, ct);
            }

            await tx.CommitAsync(ct);
            detail["alertas_vivos"] = vivos;

            await MaintenanceRunRecorder.RecordAsync(
                dataSource, JobName, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusOk, detail, ct);

            logger?.LogInformation(
                "Alertas de gestão: {Orgs} org(s) avaliada(s), {Vivos} alerta(s) vivo(s).",
                orgs.Count, vivos);
            return vivos;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Ciclo de alertas de gestão falhou.");
            detail["error"] = ex.Message;
            await MaintenanceRunRecorder.RecordAsync(
                dataSource, JobName, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusError, detail, CancellationToken.None);
            return 0;
        }
    }

    private static async Task<List<OrgRow>> QueryOrgsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // TODAS as ativas, não só as Pro: a org fora do plano precisa passar pelo Sync para ter
        // os alertas antigos resolvidos depois de um downgrade.
        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, name, timezone, business_hours::text, plan,
                   goal_weekly_active_hours, person_alerts_enabled
            FROM organizations
            WHERE status = 'active'
            ORDER BY id
            """, conn, tx);

        var orgs = new List<OrgRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            orgs.Add(new OrgRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetBoolean(6)));
        }

        return orgs;
    }

    // ------------------------------------------------------------------ avaliação das regras

    /// <summary>
    /// As sete regras da seção 4 sobre uma org. Toda janela é em DATA LOCAL da organização: a
    /// semana do gestor é a dele, não a do servidor em UTC.
    /// </summary>
    private static async Task<List<Candidate>> EvaluateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, OrgRow org, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var candidates = new List<Candidate>();

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(org.Timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc; // fuso inválido não deve derrubar o ciclo da frota inteira
        }

        var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var hoje = DateOnly.FromDateTime(local.Date);
        var isoHoje = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;

        // semana ISO corrente, de segunda até HOJE (semana em curso, não a fechada)
        var semanaInicio = hoje.AddDays(-(isoHoje - 1));
        var ultimos7 = hoje.AddDays(-6);
        var baseFim = semanaInicio.AddDays(-1);
        var baseInicio = semanaInicio.AddDays(-7 * BaselineWeeks);
        var ontem = hoje.AddDays(-1);

        BusinessHoursWindow.TryParse(org.BusinessHours, out var janela);

        // ---- regra 2: cobertura da classificação abaixo de 85% na org nos últimos 7 dias
        var cobertura = await QueryCoverageAsync(conn, tx, org.Id, ultimos7, hoje, ct);
        if (cobertura.Active > 0)
        {
            var taxa = (double)(cobertura.Active - cobertura.Unclassified) / cobertura.Active;
            if (taxa < CoverageFloor)
            {
                candidates.Add(new Candidate(KindCoverageBelow, ScopeOrganization, OrganizationScopeKey,
                    new Dictionary<string, object?>
                    {
                        ["coverage"] = Math.Round(taxa, 4),
                        ["floor"] = CoverageFloor,
                        ["seconds_active"] = cobertura.Active,
                        ["seconds_unclassified"] = cobertura.Unclassified,
                        ["from"] = ultimos7.ToString("yyyy-MM-dd"),
                        ["to"] = hoje.ToString("yyyy-MM-dd"),
                    }));
            }
        }

        // ---- regras 3 e 7: por etiqueta de equipe, semana corrente (base de 4 semanas na 3)
        var semana = await QueryTeamsAsync(conn, tx, org.Id, semanaInicio, hoje, ct);
        var basal = await QueryTeamsAsync(conn, tx, org.Id, baseInicio, baseFim, ct);

        foreach (var (tag, atual) in semana)
        {
            // mínimo de grupo nas DUAS regras: ambas são afirmação sobre um GRUPO, e com menos
            // de 3 pessoas viram afirmação sobre indivíduo — que só existe no opt-in.
            if (atual.People < MinGroupSize || atual.PersonDays == 0)
            {
                continue;
            }

            // regra 3: tempo ativo por pessoa-dia 15% abaixo da média das 4 semanas anteriores.
            // Por PESSOA-DIA, não em total bruto: a equipe que contratou alguém teria total
            // maior com ritmo pior, e a semana em curso é parcial por definição.
            if (basal.TryGetValue(tag, out var anterior) && anterior.PersonDays > 0 && anterior.Active > 0)
            {
                var ritmoAtual = (double)atual.Active / atual.PersonDays;
                var ritmoBase = (double)anterior.Active / anterior.PersonDays;
                if (ritmoBase > 0 && ritmoAtual <= ritmoBase * TeamActiveDropRatio)
                {
                    candidates.Add(new Candidate(KindTeamActiveDrop, ScopeTeam, tag,
                        new Dictionary<string, object?>
                        {
                            ["drop_pct"] = Math.Round((1 - (ritmoAtual / ritmoBase)) * 100, 1),
                            ["seconds_active_per_person_day"] = (long)Math.Round(ritmoAtual),
                            ["baseline_seconds_active_per_person_day"] = (long)Math.Round(ritmoBase),
                            ["people"] = atual.People,
                            ["baseline_weeks"] = BaselineWeeks,
                            ["from"] = semanaInicio.ToString("yyyy-MM-dd"),
                            ["to"] = hoje.ToString("yyyy-MM-dd"),
                        }));
                }
            }

            // regra 7: improdutivo acima de 25% do tempo ATIVO da equipe na semana.
            // Denominador é o tempo ativo — ocioso e bloqueado ficam fora, porque estado de
            // máquina nunca conta como improdutivo.
            if (atual.Active > 0)
            {
                var fatia = (double)atual.Unproductive / atual.Active;
                if (fatia > UnproductiveCeiling)
                {
                    candidates.Add(new Candidate(KindTeamUnproductiveHigh, ScopeTeam, tag,
                        new Dictionary<string, object?>
                        {
                            ["unproductive_pct"] = Math.Round(fatia * 100, 1),
                            ["ceiling_pct"] = Math.Round(UnproductiveCeiling * 100, 1),
                            ["seconds_active"] = atual.Active,
                            ["seconds_not_work_related"] = atual.Unproductive,
                            ["people"] = atual.People,
                            ["from"] = semanaInicio.ToString("yyyy-MM-dd"),
                            ["to"] = hoje.ToString("yyyy-MM-dd"),
                        }));
                }
            }
        }

        // ---- regras 4 e 5: escopo PESSOA, só com o OPT-IN da organização (decisão 5)
        if (org.PersonAlertsEnabled)
        {
            foreach (var (sid, dias, pico) in await QueryLongDaysAsync(conn, tx, org.Id, semanaInicio, hoje, ct))
            {
                candidates.Add(new Candidate(KindPersonLongDays, ScopePerson, sid,
                    new Dictionary<string, object?>
                    {
                        ["days"] = dias,
                        ["threshold_days"] = LongDaysInWeek,
                        ["threshold_hours"] = LongDayHours,
                        ["peak_seconds_on"] = pico,
                        ["from"] = semanaInicio.ToString("yyyy-MM-dd"),
                        ["to"] = hoje.ToString("yyyy-MM-dd"),
                    }));
            }

            // sem janela de trabalho configurada NÃO existe "fora do horário" a medir — a mesma
            // régua do relatório de fora do horário, que devolve estado vazio explicativo.
            if (janela is not null)
            {
                foreach (var (sid, segundos) in await QueryAfterHoursAsync(
                             conn, tx, org.Id, semanaInicio, hoje, janela, ct))
                {
                    candidates.Add(new Candidate(KindPersonAfterHours, ScopePerson, sid,
                        new Dictionary<string, object?>
                        {
                            ["seconds_active"] = segundos,
                            ["threshold_hours"] = AfterHoursWeeklyHours,
                            ["from"] = semanaInicio.ToString("yyyy-MM-dd"),
                            ["to"] = hoje.ToString("yyyy-MM-dd"),
                        }));
                }
            }
        }

        // ---- regra 6: meta da semana em risco pela projeção do ritmo dos dias registrados
        if (org.GoalWeeklyActiveHours is { } meta && meta > 0)
        {
            var (ativo, diasComDado) = await QueryWeekPaceAsync(conn, tx, org.Id, semanaInicio, hoje, ct);
            var diasUteis = BusinessDaysIn(janela, semanaInicio, semanaInicio.AddDays(6));
            if (diasComDado >= GoalProjectionMinDays && diasUteis > 0)
            {
                var horasAteAgora = ativo / 3600.0;
                var projecao = horasAteAgora / diasComDado * diasUteis;
                if (projecao < meta)
                {
                    candidates.Add(new Candidate(KindGoalAtRisk, ScopeOrganization, OrganizationScopeKey,
                        new Dictionary<string, object?>
                        {
                            ["goal_hours"] = meta,
                            ["projected_hours"] = Math.Round(projecao, 1),
                            ["hours_so_far"] = Math.Round(horasAteAgora, 1),
                            ["days_with_data"] = diasComDado,
                            ["business_days"] = diasUteis,
                            ["from"] = semanaInicio.ToString("yyyy-MM-dd"),
                            ["to"] = hoje.ToString("yyyy-MM-dd"),
                        }));
                }
            }
        }

        // ---- regra 8: dia útil sem dados em dispositivo ativo. Olha ONTEM, um dia FECHADO:
        // o de hoje ainda está sendo escrito e acusaria toda máquina antes do primeiro café.
        if (IsBusinessDay(janela, ontem))
        {
            // dispositivo recém-inscrito não tem por que ter dado de ontem (UUIDv7 carrega a
            // data de inscrição, mesma leitura que o FleetAlertService faz da ciência pendente)
            var corteInscricao = nowUtc.AddDays(-1);
            foreach (var (deviceId, nome) in await QueryDevicesWithoutDataAsync(conn, tx, org.Id, ontem, ct))
            {
                if (Uuid7.TimestampOf(deviceId) > corteInscricao)
                {
                    continue;
                }

                candidates.Add(new Candidate(KindBusinessDayNoData, ScopeDevice, deviceId.ToString(),
                    new Dictionary<string, object?>
                    {
                        ["device_name"] = nome,
                        ["day"] = ontem.ToString("yyyy-MM-dd"),
                    }));
            }
        }

        return candidates;
    }

    // ------------------------------------------------------------------ consultas dos agregados

    private sealed record CoverageRow(long Active, long Unclassified);

    private static async Task<CoverageRow> QueryCoverageAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(sum(s.seconds_active), 0)::bigint,
                   COALESCE(sum(s.seconds_unclassified), 0)::bigint
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @t
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @from::date AND @to::date
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("to", to.ToString("yyyy-MM-dd"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new CoverageRow(reader.GetInt64(0), reader.GetInt64(1));
    }

    private sealed record TeamRow(long Active, long Unproductive, int PersonDays, int People);

    /// <summary>
    /// Agregado por ETIQUETA de equipe (a equipe é <c>devices.tags[]</c> até a entidade
    /// <c>teams</c> da F7). Duas escolhas que mudam o número:
    ///  - o JOIN em <c>device_users</c> derruba a lane-máquina (o UUID zero não tem titular),
    ///    então "máquina ligada sem usuário" não entra em métrica de pessoa (decisão 8);
    ///  - a identidade é o <c>windows_sid</c> RESOLVIDO pela mesclagem de <c>people</c>: a
    ///    mesma pessoa em dois notebooks conta como UMA no mínimo de grupo, não como duas.
    /// </summary>
    private static async Task<Dictionary<string, TeamRow>> QueryTeamsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            WITH lanes AS (
                SELECT u.t AS tag,
                       COALESCE(p.merged_into_sid, du.windows_sid) AS sid,
                       s.summary_date,
                       sum(s.seconds_active)           AS seconds_active,
                       sum(s.seconds_not_work_related) AS seconds_not_work_related,
                       sum(s.seconds_on)               AS seconds_on
                FROM daily_device_summaries s
                JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
                JOIN device_users du ON du.id = s.device_user_id AND du.tenant_id = s.tenant_id
                LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
                CROSS JOIN LATERAL unnest(COALESCE(d.tags, ARRAY[]::text[])) AS u(t)
                WHERE s.tenant_id = @t
                  AND d.status <> 'archived'
                  AND s.summary_date BETWEEN @from::date AND @to::date
                GROUP BY u.t, COALESCE(p.merged_into_sid, du.windows_sid), s.summary_date
            )
            SELECT tag,
                   COALESCE(sum(seconds_active), 0)::bigint,
                   COALESCE(sum(seconds_not_work_related), 0)::bigint,
                   count(*) FILTER (WHERE seconds_on > 0)::int,
                   count(DISTINCT sid) FILTER (WHERE seconds_on > 0)::int
            FROM lanes
            GROUP BY tag
            ORDER BY tag
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("to", to.ToString("yyyy-MM-dd"));

        var rows = new Dictionary<string, TeamRow>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows[reader.GetString(0)] = new TeamRow(
                reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4));
        }

        return rows;
    }

    /// <summary>
    /// Dias longos por PESSOA na semana. <c>seconds_on</c> é somado entre os dispositivos da
    /// pessoa no dia, como em toda métrica por pessoa do produto (GET /people): duas máquinas
    /// ligadas ao mesmo tempo somam, e a leitura é "quanto de máquina ligada houve no dia dela".
    /// </summary>
    private static async Task<List<(string Sid, int Days, long PeakSecondsOn)>> QueryLongDaysAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            WITH pessoa_dia AS (
                SELECT COALESCE(p.merged_into_sid, du.windows_sid) AS sid,
                       s.summary_date,
                       sum(s.seconds_on)::bigint AS seconds_on
                FROM daily_device_summaries s
                JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
                JOIN device_users du ON du.id = s.device_user_id AND du.tenant_id = s.tenant_id
                LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
                WHERE s.tenant_id = @t
                  AND d.status <> 'archived'
                  AND s.summary_date BETWEEN @from::date AND @to::date
                GROUP BY COALESCE(p.merged_into_sid, du.windows_sid), s.summary_date
            )
            SELECT sid, count(*)::int, max(seconds_on)::bigint
            FROM pessoa_dia
            WHERE seconds_on > @limite
            GROUP BY sid
            HAVING count(*) >= @minDias
            ORDER BY sid
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("to", to.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("limite", (long)LongDayHours * 3600);
        cmd.Parameters.AddWithValue("minDias", LongDaysInWeek);

        var rows = new List<(string, int, long)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        }

        return rows;
    }

    /// <summary>
    /// Atividade fora do horário por PESSOA na semana, a partir de <c>hourly_activity</c>.
    ///
    /// A hora é um BALDE de 60 min, então a régua é generosa de propósito: o balde que encosta
    /// na janela conta como DENTRO. Com janela 08:30–18:00 a hora 8 é "dentro" (encosta em
    /// 08:30) e a hora 18 é "fora". Alerta de equilíbrio errado é pior que alerta ausente.
    /// </summary>
    private static async Task<List<(string Sid, long SecondsActive)>> QueryAfterHoursAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, DateOnly from, DateOnly to,
        BusinessHoursWindow.Schedule janela, CancellationToken ct)
    {
        // fim do último balde considerado dentro: 18:00 fecha em 18; 18:30 ainda ocupa a hora 18
        var fimBalde = janela.End.Minute > 0 ? janela.End.Hour + 1 : janela.End.Hour;

        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(p.merged_into_sid, du.windows_sid) AS sid,
                   COALESCE(sum(h.seconds_active), 0)::bigint AS seconds_active
            FROM hourly_activity h
            JOIN devices d ON d.id = h.device_id AND d.tenant_id = h.tenant_id
            JOIN device_users du ON du.id = h.device_user_id AND du.tenant_id = h.tenant_id
            LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
            WHERE h.tenant_id = @t
              AND d.status <> 'archived'
              AND h.summary_date BETWEEN @from::date AND @to::date
              AND (
                    NOT (extract(isodow FROM h.summary_date)::int = ANY(@dias))
                 OR h.hour_local::int < @inicio
                 OR h.hour_local::int >= @fim
              )
            GROUP BY COALESCE(p.merged_into_sid, du.windows_sid)
            HAVING COALESCE(sum(h.seconds_active), 0) > @limite
            ORDER BY 1
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("to", to.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("dias", janela.IsoDays);
        cmd.Parameters.AddWithValue("inicio", janela.Start.Hour);
        cmd.Parameters.AddWithValue("fim", fimBalde);
        cmd.Parameters.AddWithValue("limite", (long)AfterHoursWeeklyHours * 3600);

        var rows = new List<(string, long)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }

    /// <summary>Tempo ativo da org na semana e quantos dias têm dado (base da projeção da meta).</summary>
    private static async Task<(long SecondsActive, int DaysWithData)> QueryWeekPaceAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(sum(s.seconds_active), 0)::bigint,
                   count(DISTINCT s.summary_date) FILTER (WHERE s.seconds_active > 0)::int
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @t
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @from::date AND @to::date
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("to", to.ToString("yyyy-MM-dd"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt32(1));
    }

    /// <summary>Dispositivos ATIVOS sem nenhum segundo ligado no dia (candidatos da regra 8).</summary>
    private static async Task<List<(Guid DeviceId, string Name)>> QueryDevicesWithoutDataAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, DateOnly day, CancellationToken ct)
    {
        // só status 'active': paused e archived são decisão deliberada do gestor, não alerta
        await using var cmd = new NpgsqlCommand(
            """
            SELECT d.id, COALESCE(d.display_name, d.hostname)
            FROM devices d
            WHERE d.tenant_id = @t
              AND d.status = 'active'
              AND NOT EXISTS (
                    SELECT 1 FROM daily_device_summaries s
                    WHERE s.tenant_id = d.tenant_id
                      AND s.device_id = d.id
                      AND s.summary_date = @day::date
                      AND s.seconds_on > 0
              )
            ORDER BY d.id
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("day", day.ToString("yyyy-MM-dd"));

        var rows = new List<(Guid, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    // ------------------------------------------------------------------ estado dos alertas

    /// <summary>
    /// Sincroniza o estado da org: faz upsert dos candidatos e resolve o que não está mais na
    /// lista. Devolve quantos ficaram vivos.
    /// </summary>
    private static async Task<int> SyncAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, List<Candidate> candidates,
        DateTimeOffset nowUtc, bool permiteNovos, CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            await UpsertAsync(conn, tx, tenantId, candidate, nowUtc, permiteNovos, ct);
        }

        var vivos = candidates
            .Select(c => string.Join(KeySeparator, c.Kind, c.ScopeType, c.ScopeKey))
            .ToArray();

        // lista vazia resolve TUDO: `<> ALL('{}')` é verdadeiro para toda linha, que é
        // exatamente o comportamento desejado quando a org deixou de ter candidatos.
        await using (var resolve = new NpgsqlCommand(
            """
            UPDATE management_alerts
            SET resolved_at = @now
            WHERE tenant_id = @t
              AND resolved_at IS NULL
              AND (kind || chr(31) || scope_type || chr(31) || scope_key) <> ALL(@vivos)
            """, conn, tx))
        {
            resolve.Parameters.AddWithValue("t", tenantId);
            resolve.Parameters.AddWithValue("now", nowUtc);
            resolve.Parameters.AddWithValue("vivos", vivos);
            await resolve.ExecuteNonQueryAsync(ct);
        }

        await using var count = new NpgsqlCommand(
            "SELECT count(*)::int FROM management_alerts WHERE tenant_id = @t AND resolved_at IS NULL",
            conn, tx);
        count.Parameters.AddWithValue("t", tenantId);
        return (int)(await count.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Upsert de UM alerta. Duas formas, conforme o silêncio fora do horário:
    ///  - dentro da janela: INSERT ... ON CONFLICT que atualiza o vivo (mantendo
    ///    <c>first_seen_at</c>, que é a idade exibida) e reabre o resolvido SÓ depois do
    ///    cooldown de 24 h;
    ///  - fora da janela: UPDATE que toca apenas o que já está vivo. Nada nasce de madrugada.
    /// </summary>
    private static async Task UpsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Candidate candidate,
        DateTimeOffset nowUtc, bool permiteNovos, CancellationToken ct)
    {
        var detailJson = JsonSerializer.Serialize(candidate.Detail);

        var sql = permiteNovos
            ? """
              INSERT INTO management_alerts
                  (tenant_id, kind, scope_type, scope_key, first_seen_at, last_seen_at, resolved_at, detail)
              VALUES (@t, @k, @st, @sk, @now, @now, NULL, @detail::jsonb)
              ON CONFLICT (tenant_id, kind, scope_type, scope_key) DO UPDATE
                SET last_seen_at = @now,
                    detail = EXCLUDED.detail,
                    first_seen_at = CASE
                        WHEN management_alerts.resolved_at IS NULL THEN management_alerts.first_seen_at
                        ELSE @now
                    END,
                    resolved_at = NULL
                WHERE management_alerts.resolved_at IS NULL
                   OR management_alerts.resolved_at < @corte
              """
            : """
              UPDATE management_alerts
              SET last_seen_at = @now, detail = @detail::jsonb
              WHERE tenant_id = @t AND kind = @k AND scope_type = @st AND scope_key = @sk
                AND resolved_at IS NULL
              """;

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("k", candidate.Kind);
        cmd.Parameters.AddWithValue("st", candidate.ScopeType);
        cmd.Parameters.AddWithValue("sk", candidate.ScopeKey);
        cmd.Parameters.AddWithValue("now", nowUtc);
        cmd.Parameters.AddWithValue("detail", detailJson);
        if (permiteNovos)
        {
            cmd.Parameters.AddWithValue("corte", nowUtc.AddHours(-CooldownHours));
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ dias úteis

    /// <summary>
    /// Dia útil pela janela da organização. Sem janela configurada, segunda a sexta — o
    /// default do mercado, e o mesmo que o portal assume.
    ///
    /// FERIADOS FICAM DE FORA nesta leva: entram na F7 junto com a entidade <c>teams</c> e a
    /// jornada por equipe (spec seção 6). Até lá, feriado nacional conta como dia útil e pode
    /// gerar um "dia útil sem dados" que o gestor reconhece na hora.
    /// </summary>
    private static bool IsBusinessDay(BusinessHoursWindow.Schedule? janela, DateOnly day)
    {
        var iso = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
        return janela is null ? iso is >= 1 and <= 5 : janela.IsoDays.Contains(iso);
    }

    private static int BusinessDaysIn(BusinessHoursWindow.Schedule? janela, DateOnly from, DateOnly to)
    {
        var dias = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (IsBusinessDay(janela, day))
            {
                dias++;
            }
        }

        return dias;
    }
}
