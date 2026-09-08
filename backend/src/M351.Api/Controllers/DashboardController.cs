using System.Text.Json;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

[Route("api/v1/dashboard")]
[Authorize] // Viewer+
public class DashboardController(
    NpgsqlDataSource dataSource,
    M351DbContext db,
    AuditWriter audit,
    TimeProvider clock) : ApiControllerBase
{
    /// <summary>Janela do "online agora" (N6): último contato ≤ 180 s.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(180);

    /// <summary>Intervalo máximo dos dashboards históricos: 92 dias (um trimestre).</summary>
    public const int MaxRangeDays = 92;

    /// <summary>
    /// Grão MENSAL (F9): janela máxima de 24 meses. É exatamente a retenção dos agregados
    /// (RetentionPurgeService.SummariesRetentionMonths), então a janela máxima do produto passa a
    /// ser a do dado que ele guarda — pedir mais devolveria meses vazios com cara de meses sem
    /// trabalho.
    /// </summary>
    public const int MaxRangeMonths = 24;

    /// <summary>Os dois grãos aceitos em ?grain. Ausente equivale a "day".</summary>
    public const string GrainDay = "day";
    public const string GrainMonth = "month";

    public const int TopAppsDefaultLimit = 10;
    public const int TopAppsMaxLimit = 50;

    /// <summary>
    /// GET /api/v1/dashboard/presence[?tag] (Seção 7.4): tabela "Equipe agora" a partir de
    /// device_current_state. Estado exibido (presence_state) segue a regra N6: `state` se o
    /// último contato ≤ 180 s; senão "Sem comunicação" (no_data) — a menos que o último evento
    /// tenha sido desligamento limpo (off_clean), que continua "Desligada".
    ///
    /// F5 — ?tag: filtro de VISUALIZAÇÃO por etiqueta de device ("me mostra só o comercial",
    /// a primeira pergunta do gestor com mais de 30 máquinas). NÃO é escopo de permissão, e
    /// portanto não conflita com o papel Manager-por-equipe adiado para a v1.1: qualquer papel
    /// continua vendo tudo, só escolhe o recorte exibido. Etiqueta inexistente devolve lista
    /// vazia (tag não é recurso com dono, logo não há 404 a dar).
    /// </summary>
    [HttpGet("presence")]
    public async Task<IActionResult> Presence([FromQuery(Name = "tag")] string? tag, CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);
        var now = clock.GetUtcNow();

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<PresenceRow>(new CommandDefinition(
            """
            SELECT d.id AS device_id,
                   COALESCE(d.display_name, d.hostname) AS device_name,
                   d.hostname,
                   s.state, s.windows_username, s.foreground_process, s.foreground_title,
                   s.state_since, s.app_since, s.last_contact_at
            FROM device_current_state s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @TenantId AND d.status <> 'archived'
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            ORDER BY COALESCE(d.display_name, d.hostname)
            """,
            new { TenantId = tenantId, Tag = NormalizeTeamTag(tag) }, cancellationToken: ct));

        var items = rows.Select(r => new PresenceItemResponse(
                r.DeviceId, r.DeviceName, r.Hostname, r.State,
                DerivePresenceState(r.State, r.LastContactAt, now),
                r.WindowsUsername, r.ForegroundProcess, r.ForegroundTitle,
                r.StateSince, r.AppSince, r.LastContactAt))
            .ToList();

        return Ok(new PresenceResponse(items, now));
    }

    private static string DerivePresenceState(string state, DateTimeOffset lastContactAt, DateTimeOffset now)
    {
        if (state == "off_clean")
        {
            return "off_clean"; // desligamento limpo: "Desligada", sem alerta
        }

        return now - lastContactAt <= OnlineWindow ? state : "no_data";
    }

    /// <summary>
    /// GET /api/v1/dashboard/summary?from&amp;to[&amp;device_id][&amp;device_user_id] (Seção 7.4):
    /// KPIs históricos somados de daily_device_summaries por summary_date (que JÁ é o dia
    /// local do TENANT — a agregação F3.1 corta na meia-noite da org; zero matemática de
    /// fuso aqui). Devices status='archived' ficam FORA (spec linha 954: "sai dos
    /// dashboards"). Dias sem linhas não aparecem em days (o portal preenche zeros).
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "device_id")] Guid? deviceId,
        [FromQuery(Name = "device_user_id")] Guid? deviceUserId,
        [FromQuery(Name = "tag")] string? tag,
        CancellationToken ct)
    {
        var invalid = ValidateRange(from, to);
        if (invalid is not null) return invalid;

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // filtro individual aponta para um recurso: inexistente OU de outro tenant → 404
        // (Princípio 4 — nunca 403, que confirmaria a existência)
        if (deviceId is not null)
        {
            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM devices WHERE tenant_id = @TenantId AND id = @Id)",
                new { TenantId = tenantId, Id = deviceId }, cancellationToken: ct));
            if (!exists) return NotFoundProblem();
        }

        // UUID zero é a lane-máquina (sintética, spec linha 652) — válida sem lookup
        if (deviceUserId is not null && deviceUserId != Guid.Empty)
        {
            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM device_users WHERE tenant_id = @TenantId AND id = @Id)",
                new { TenantId = tenantId, Id = deviceUserId }, cancellationToken: ct));
            if (!exists) return NotFoundProblem();
        }

        // GROUPING SETS: as linhas por dia E a linha de totais saem do MESMO snapshot —
        // o device_count distinct do período não é derivável da soma dos dias
        var rows = (await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            """
            SELECT s.summary_date::text AS date,
                   COALESCE(sum(s.seconds_active), 0)::bigint AS seconds_active,
                   COALESCE(sum(s.seconds_idle), 0)::bigint AS seconds_idle,
                   COALESCE(sum(s.seconds_locked), 0)::bigint AS seconds_locked,
                   COALESCE(sum(s.seconds_on), 0)::bigint AS seconds_on,
                   COALESCE(sum(s.seconds_work_related), 0)::bigint AS seconds_work_related,
                   COALESCE(sum(s.seconds_neutral), 0)::bigint AS seconds_neutral,
                   COALESCE(sum(s.seconds_not_work_related), 0)::bigint AS seconds_not_work_related,
                   COALESCE(sum(s.seconds_unclassified), 0)::bigint AS seconds_unclassified,
                   COALESCE(bool_or(s.data_incomplete), false) AS data_incomplete,
                   count(DISTINCT s.device_id)::int AS device_count
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @From::date AND @To::date
              AND (@DeviceId::uuid IS NULL OR s.device_id = @DeviceId)
              AND (@DeviceUserId::uuid IS NULL OR s.device_user_id = @DeviceUserId)
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            GROUP BY GROUPING SETS ((s.summary_date), ())
            ORDER BY s.summary_date NULLS LAST
            """,
            new
            {
                TenantId = tenantId, From = from, To = to,
                DeviceId = deviceId, DeviceUserId = deviceUserId, Tag = NormalizeTeamTag(tag),
            },
            cancellationToken: ct))).ToList();

        // a linha () do GROUPING SETS (date NULL) existe SEMPRE, mesmo sem dia algum
        var totalsRow = rows.Single(r => r.Date is null);
        var days = rows.Where(r => r.Date is not null)
            .Select(r => new DashboardSummaryDayResponse(
                r.Date!, r.SecondsActive, r.SecondsIdle, r.SecondsLocked, r.SecondsOn,
                r.SecondsWorkRelated, r.SecondsNeutral, r.SecondsNotWorkRelated, r.SecondsUnclassified,
                r.DataIncomplete, r.DeviceCount))
            .ToList();
        var totals = new DashboardSummaryTotalsResponse(
            totalsRow.SecondsActive, totalsRow.SecondsIdle, totalsRow.SecondsLocked, totalsRow.SecondsOn,
            totalsRow.SecondsWorkRelated, totalsRow.SecondsNeutral, totalsRow.SecondsNotWorkRelated,
            totalsRow.SecondsUnclassified, totalsRow.DataIncomplete, totalsRow.DeviceCount);

        // DoD 11.3: COM filtro individual é visualização de dado PESSOAL → audit view_report
        // (padrão do view_timeline). SEM filtro é agregado de equipe — decisão documentada:
        // NÃO audita (não há titular identificado). Com os dois filtros, o alvo é o mais
        // específico (device_user).
        if (deviceId is not null || deviceUserId is not null)
        {
            audit.Add(tenantId, AuditActions.ViewReport,
                actorUserId: Auth.CurrentUser.UserId(User),
                targetType: deviceUserId is not null ? "device_user" : "device",
                targetId: deviceUserId ?? deviceId,
                detailJson: JsonSerializer.Serialize(new { from, to, device_id = deviceId, device_user_id = deviceUserId }));
            await db.SaveChangesAsync(ct);
        }

        return Ok(new DashboardSummaryResponse(days, totals));
    }

    /// <summary>
    /// GET /api/v1/dashboard/top-apps?from&amp;to&amp;limit (Seção 7.4): ranking de
    /// daily_app_usage por seconds_active, com a categoria do TENANT (tenant_app_categories
    /// → categories) e devices archived excluídos. total_seconds_active soma TODOS os apps
    /// do período (denominador de %), não só o top. Agregado de equipe: sem audit.
    /// </summary>
    [HttpGet("top-apps")]
    public async Task<IActionResult> TopApps(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "limit")] int? limit,
        [FromQuery(Name = "tag")] string? tag,
        CancellationToken ct)
    {
        var invalid = ValidateRange(from, to);
        if (invalid is not null) return invalid;

        // default 10, teto 50 (contrato 7.4); fora da faixa é clampado, não erro
        var effectiveLimit = Math.Clamp(limit ?? TopAppsDefaultLimit, 1, TopAppsMaxLimit);

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<TopAppRow>(new CommandDefinition(
            """
            SELECT u.app_id, a.process_name, a.display_name, tac.custom_display_name,
                   c.id AS category_id, c.name AS category_name,
                   c.classification AS category_classification, c.color AS category_color,
                   sum(u.seconds_active)::bigint AS seconds_active,
                   count(DISTINCT u.device_id)::int AS device_count
            FROM daily_app_usage u
            JOIN devices d ON d.id = u.device_id AND d.tenant_id = u.tenant_id
            JOIN app_catalog a ON a.id = u.app_id
            LEFT JOIN tenant_app_categories tac ON tac.tenant_id = u.tenant_id AND tac.app_id = u.app_id
            LEFT JOIN categories c ON c.tenant_id = u.tenant_id AND c.id = tac.category_id
            WHERE u.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND u.summary_date BETWEEN @From::date AND @To::date
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            GROUP BY u.app_id, a.process_name, a.display_name, tac.custom_display_name,
                     c.id, c.name, c.classification, c.color
            ORDER BY seconds_active DESC, a.process_name
            LIMIT @Limit
            """,
            new { TenantId = tenantId, From = from, To = to, Limit = effectiveLimit, Tag = NormalizeTeamTag(tag) },
            cancellationToken: ct))).ToList();

        var totalSecondsActive = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COALESCE(sum(u.seconds_active), 0)::bigint
            FROM daily_app_usage u
            JOIN devices d ON d.id = u.device_id AND d.tenant_id = u.tenant_id
            WHERE u.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND u.summary_date BETWEEN @From::date AND @To::date
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            """,
            new { TenantId = tenantId, From = from, To = to, Tag = NormalizeTeamTag(tag) }, cancellationToken: ct));

        var items = rows.Select(r => new DashboardTopAppResponse(
                r.AppId, r.ProcessName, r.DisplayName, r.CustomDisplayName,
                r.CategoryId is { } categoryId
                    ? new DashboardAppCategoryResponse(categoryId, r.CategoryName!, r.CategoryClassification!.Value, r.CategoryColor)
                    : null,
                r.SecondsActive, r.DeviceCount))
            .ToList();

        return Ok(new DashboardTopAppsResponse(items, totalSecondsActive));
    }

    // ------------------------------------------------------------ helpers

    // ------------------------------------------------------------ F6: atividade por hora
    /// <summary>
    /// GET /api/v1/dashboard/activity-by-hour?from&amp;to[&amp;tag] (F6): distribuição do tempo
    /// ativo/ocioso pelas 24 horas LOCAIS do tenant, lida de hourly_activity (preenchida na
    /// mesma transação da agregação diária). Devices archived ficam fora, mesma régua do
    /// summary. Agregado de equipe: sem auditoria.
    ///
    /// As 24 horas vêm SEMPRE, inclusive as vazias: o gráfico da Visão Geral desenha o dia
    /// inteiro e um buraco no meio da série leria como "sem dado" em vez de "ninguém ativo".
    /// </summary>
    [HttpGet("activity-by-hour")]
    public async Task<IActionResult> ActivityByHour(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "tag")] string? tag,
        CancellationToken ct)
    {
        var invalid = ValidateRange(from, to);
        if (invalid is not null) return invalid;

        var tenantId = Auth.CurrentUser.TenantId(User);
        var normalizedTag = NormalizeTeamTag(tag);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var rows = (await connection.QueryAsync<HourRow>(new CommandDefinition(
            """
            SELECT h.hour_local::int AS hour,
                   COALESCE(sum(h.seconds_active), 0)::bigint AS seconds_active,
                   COALESCE(sum(h.seconds_idle), 0)::bigint AS seconds_idle
            FROM hourly_activity h
            JOIN devices d ON d.id = h.device_id AND d.tenant_id = h.tenant_id
            WHERE h.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND h.summary_date BETWEEN @From::date AND @To::date
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            GROUP BY h.hour_local
            """,
            new { TenantId = tenantId, From = from, To = to, Tag = normalizedTag },
            cancellationToken: ct))).ToDictionary(r => r.Hour);

        // dias COM dado no recorte: denominador da média de pessoas simultâneas
        var daysWithData = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(DISTINCT s.summary_date)::int
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @From::date AND @To::date
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
              AND s.seconds_on > 0
            """,
            new { TenantId = tenantId, From = from, To = to, Tag = normalizedTag },
            cancellationToken: ct));

        var hours = Enumerable.Range(0, 24).Select(h =>
        {
            rows.TryGetValue(h, out var row);
            var active = row?.SecondsActive ?? 0;
            var idle = row?.SecondsIdle ?? 0;
            double? avg = daysWithData > 0 ? Math.Round(active / 3600.0 / daysWithData, 4) : null;
            return new ActivityByHourItemResponse(h, active, idle, avg);
        }).ToList();

        return Ok(new ActivityByHourResponse(hours, daysWithData));
    }

    // ------------------------------------------------------------ F6: visão geral
    /// <summary>
    /// GET /api/v1/dashboard/overview?from&amp;to[&amp;tag][&amp;compare] (F6): a Visão Geral numa
    /// chamada. O ÍNDICE DE PRODUTIVIDADE e a COBERTURA DA CLASSIFICAÇÃO são calculados aqui —
    /// fonte única da fórmula (decisão 4 do spec de 07/09/2026), e o portal nunca recalcula.
    /// compare=true devolve também o período imediatamente anterior de MESMA duração, que é a
    /// base de toda variação exibida na tela. Agregado de equipe: sem auditoria.
    /// </summary>
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "tag")] string? tag,
        [FromQuery(Name = "compare")] bool compare,
        [FromQuery(Name = "grain")] string? grain,
        CancellationToken ct)
    {
        var invalid = ValidateGrainAndRange(from, to, grain, out var monthly);
        if (invalid is not null) return invalid;

        var fromDay = DateOnly.ParseExact(from!, "yyyy-MM-dd");
        var toDay = DateOnly.ParseExact(to!, "yyyy-MM-dd");
        var tenantId = Auth.CurrentUser.TenantId(User);
        var normalizedTag = NormalizeTeamTag(tag);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var days = await OverviewDaysAsync(connection, tenantId, fromDay, toDay, normalizedTag, monthly, ct);
        var totals = await OverviewTotalsAsync(connection, tenantId, fromDay, toDay, normalizedTag, monthly, ct);

        OverviewTotalsResponse? previous = null;
        if (compare)
        {
            // período imediatamente anterior, de mesma duração (semana vs semana, mês vs mês)
            var length = toDay.DayNumber - fromDay.DayNumber + 1;
            var prevTo = fromDay.AddDays(-1);
            var prevFrom = prevTo.AddDays(-(length - 1));
            previous = await OverviewTotalsAsync(connection, tenantId, prevFrom, prevTo, normalizedTag, monthly, ct);
        }

        var goals = await connection.QuerySingleAsync<GoalsRow>(new CommandDefinition(
            """
            SELECT goal_weekly_active_hours AS weekly, goal_work_related_pct AS pct
            FROM organizations WHERE id = @TenantId
            """,
            new { TenantId = tenantId }, cancellationToken: ct));

        return Ok(new OverviewResponse(
            new OverviewPeriodResponse(
                fromDay.ToString("yyyy-MM-dd"), toDay.ToString("yyyy-MM-dd"),
                toDay.DayNumber - fromDay.DayNumber + 1),
            totals, previous, days,
            new OverviewGoalsResponse(goals.Weekly, goals.Pct)));
    }

    /// <summary>
    /// Régua de período do overview, sensível ao GRÃO (F9). O grão mensal lê de
    /// monthly_summaries e por isso admite 24 meses onde o diário admite 92 dias; grão
    /// desconhecido é 400, e não um silencioso "day", porque a tela que pediu "trimestre" tem de
    /// saber que recebeu outra coisa.
    /// </summary>
    private ObjectResult? ValidateGrainAndRange(string? from, string? to, string? grain, out bool monthly)
    {
        monthly = string.Equals(grain, GrainMonth, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(grain)
            && !monthly
            && !string.Equals(grain, GrainDay, StringComparison.OrdinalIgnoreCase))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Parâmetro grain deve ser {GrainDay} ou {GrainMonth}.");
        }

        if (!monthly)
        {
            return ValidateRange(from, to);
        }

        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", out var fromDay))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro from é obrigatório no formato yyyy-MM-dd.");
        if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", out var toDay))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro to é obrigatório no formato yyyy-MM-dd.");
        if (fromDay > toDay)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Intervalo inválido: from deve ser anterior ou igual a to.");

        // contagem em MESES DE CALENDÁRIO tocados, não em dias: "de janeiro a dezembro" são 12
        // meses independentemente de o recorte começar no dia 1 ou no 15
        var months = ((toDay.Year - fromDay.Year) * 12) + toDay.Month - fromDay.Month + 1;
        if (months > MaxRangeMonths)
            return ProblemResponse(StatusCodes.Status400BadRequest, $"Intervalo máximo de {MaxRangeMonths} meses no grão mensal.");

        return null;
    }

    /// <summary>
    /// Série do overview: mesmos baldes do summary, sem a linha de totais. No grão mensal a
    /// leitura sai de monthly_summaries e cada ponto é um MÊS, mas o contrato é o mesmo — `date`
    /// traz o primeiro dia do mês. Um segundo contrato para a mesma tela seria a forma mais
    /// barata de deixar os dois divergirem.
    /// </summary>
    private static async Task<List<DashboardSummaryDayResponse>> OverviewDaysAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, string? tag, bool monthly,
        CancellationToken ct) =>
        (await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            monthly ? OverviewDaysMonthlySql : OverviewDaysDailySql,
            new
            {
                TenantId = tenantId,
                From = from.ToString("yyyy-MM-dd"),
                To = to.ToString("yyyy-MM-dd"),
                Tag = tag,
            },
            cancellationToken: ct)))
        .Select(r => new DashboardSummaryDayResponse(
            r.Date!, r.SecondsActive, r.SecondsIdle, r.SecondsLocked, r.SecondsOn,
            r.SecondsWorkRelated, r.SecondsNeutral, r.SecondsNotWorkRelated, r.SecondsUnclassified,
            r.DataIncomplete, r.DeviceCount))
        .ToList();

    /// <summary>
    /// Totais do período + índice e cobertura. person_count/person_days ignoram a lane-máquina
    /// (UUID zero) porque máquina não é pessoa (decisão 8 do spec).
    ///
    /// No grão mensal a fonte é monthly_summaries. Os baldes somam igual; o que muda é
    /// person_days, que na diária é a contagem de LINHAS com tempo ligado (uma por lane e dia) e
    /// no mensal já vem somado em days_with_data — a coluna existe exatamente porque essa
    /// contagem se perderia ao somar os dias fora e não daria para reconstruir depois.
    /// </summary>
    private static async Task<OverviewTotalsResponse> OverviewTotalsAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, string? tag, bool monthly,
        CancellationToken ct)
    {
        var row = await connection.QuerySingleAsync<OverviewTotalsRow>(new CommandDefinition(
            monthly ? OverviewTotalsMonthlySql : OverviewTotalsDailySql,
            new
            {
                TenantId = tenantId,
                From = from.ToString("yyyy-MM-dd"),
                To = to.ToString("yyyy-MM-dd"),
                Tag = tag,
            },
            cancellationToken: ct));

        var classified = row.SecondsWorkRelated + row.SecondsNeutral + row.SecondsNotWorkRelated;
        double? index = classified > 0
            ? Math.Round((double)row.SecondsWorkRelated / classified, 4)
            : null;
        double? coverage = row.SecondsActive > 0
            ? Math.Round((double)(row.SecondsActive - row.SecondsUnclassified) / row.SecondsActive, 4)
            : null;

        return new OverviewTotalsResponse(
            row.SecondsOn, row.SecondsActive, row.SecondsIdle, row.SecondsLocked,
            row.SecondsWorkRelated, row.SecondsNeutral, row.SecondsNotWorkRelated, row.SecondsUnclassified,
            index, coverage, row.DeviceCount, row.PersonCount, row.PersonDays, row.DataIncomplete);
    }

    // ------------------------------------------------------------------ SQL do overview, por grão
    //
    // Quatro constantes em vez de uma string montada por interpolação: o par diário/mensal de
    // cada leitura fica lado a lado, e a diferença entre eles é legível de uma vez — que é o que
    // se quer conferir quando alguém desconfia de os dois grãos discordarem.

    /// <summary>Série por DIA (fonte diária).</summary>
    private const string OverviewDaysDailySql = """
        SELECT s.summary_date::text AS date,
               COALESCE(sum(s.seconds_active), 0)::bigint AS seconds_active,
               COALESCE(sum(s.seconds_idle), 0)::bigint AS seconds_idle,
               COALESCE(sum(s.seconds_locked), 0)::bigint AS seconds_locked,
               COALESCE(sum(s.seconds_on), 0)::bigint AS seconds_on,
               COALESCE(sum(s.seconds_work_related), 0)::bigint AS seconds_work_related,
               COALESCE(sum(s.seconds_neutral), 0)::bigint AS seconds_neutral,
               COALESCE(sum(s.seconds_not_work_related), 0)::bigint AS seconds_not_work_related,
               COALESCE(sum(s.seconds_unclassified), 0)::bigint AS seconds_unclassified,
               COALESCE(bool_or(s.data_incomplete), false) AS data_incomplete,
               count(DISTINCT s.device_id)::int AS device_count
        FROM daily_device_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND s.summary_date BETWEEN @From::date AND @To::date
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
        GROUP BY s.summary_date
        ORDER BY s.summary_date
        """;

    /// <summary>
    /// Série por MÊS (fonte mensal). `date` traz o primeiro dia do mês: o contrato é o mesmo da
    /// série diária, e quem lê a tela sabe pelo grão pedido o que cada ponto significa.
    /// O recorte é por mês TOCADO (o mês entra inteiro se o intervalo pega qualquer dia dele) —
    /// coerente com a régua de 24 meses, que também conta meses de calendário.
    /// </summary>
    private const string OverviewDaysMonthlySql = """
        SELECT s.month_start::text AS date,
               COALESCE(sum(s.seconds_active), 0)::bigint AS seconds_active,
               COALESCE(sum(s.seconds_idle), 0)::bigint AS seconds_idle,
               COALESCE(sum(s.seconds_locked), 0)::bigint AS seconds_locked,
               COALESCE(sum(s.seconds_on), 0)::bigint AS seconds_on,
               COALESCE(sum(s.seconds_work_related), 0)::bigint AS seconds_work_related,
               COALESCE(sum(s.seconds_neutral), 0)::bigint AS seconds_neutral,
               COALESCE(sum(s.seconds_not_work_related), 0)::bigint AS seconds_not_work_related,
               COALESCE(sum(s.seconds_unclassified), 0)::bigint AS seconds_unclassified,
               COALESCE(bool_or(s.data_incomplete), false) AS data_incomplete,
               count(DISTINCT s.device_id)::int AS device_count
        FROM monthly_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND s.month_start BETWEEN date_trunc('month', @From::date)::date AND @To::date
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
        GROUP BY s.month_start
        ORDER BY s.month_start
        """;

    /// <summary>Totais do período (fonte diária). person_days = linhas de lane com tempo ligado.</summary>
    private const string OverviewTotalsDailySql = """
        SELECT COALESCE(sum(s.seconds_on), 0)::bigint AS seconds_on,
               COALESCE(sum(s.seconds_active), 0)::bigint AS seconds_active,
               COALESCE(sum(s.seconds_idle), 0)::bigint AS seconds_idle,
               COALESCE(sum(s.seconds_locked), 0)::bigint AS seconds_locked,
               COALESCE(sum(s.seconds_work_related), 0)::bigint AS seconds_work_related,
               COALESCE(sum(s.seconds_neutral), 0)::bigint AS seconds_neutral,
               COALESCE(sum(s.seconds_not_work_related), 0)::bigint AS seconds_not_work_related,
               COALESCE(sum(s.seconds_unclassified), 0)::bigint AS seconds_unclassified,
               COALESCE(bool_or(s.data_incomplete), false) AS data_incomplete,
               count(DISTINCT s.device_id)::int AS device_count,
               count(DISTINCT s.device_user_id) FILTER (
                   WHERE s.device_user_id <> '00000000-0000-0000-0000-000000000000'::uuid
                     AND s.seconds_on > 0)::int AS person_count,
               count(*) FILTER (
                   WHERE s.device_user_id <> '00000000-0000-0000-0000-000000000000'::uuid
                     AND s.seconds_on > 0)::int AS person_days
        FROM daily_device_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND s.summary_date BETWEEN @From::date AND @To::date
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
        """;

    /// <summary>
    /// Totais do período (fonte mensal). person_days vem de days_with_data somado: no mensal
    /// cada linha é uma lane no MÊS, então contar linhas daria "lanes ativas", não "pares
    /// (pessoa, dia)" — que é o denominador de toda média por pessoa por dia da tela.
    /// </summary>
    private const string OverviewTotalsMonthlySql = """
        SELECT COALESCE(sum(s.seconds_on), 0)::bigint AS seconds_on,
               COALESCE(sum(s.seconds_active), 0)::bigint AS seconds_active,
               COALESCE(sum(s.seconds_idle), 0)::bigint AS seconds_idle,
               COALESCE(sum(s.seconds_locked), 0)::bigint AS seconds_locked,
               COALESCE(sum(s.seconds_work_related), 0)::bigint AS seconds_work_related,
               COALESCE(sum(s.seconds_neutral), 0)::bigint AS seconds_neutral,
               COALESCE(sum(s.seconds_not_work_related), 0)::bigint AS seconds_not_work_related,
               COALESCE(sum(s.seconds_unclassified), 0)::bigint AS seconds_unclassified,
               COALESCE(bool_or(s.data_incomplete), false) AS data_incomplete,
               count(DISTINCT s.device_id)::int AS device_count,
               count(DISTINCT s.device_user_id) FILTER (
                   WHERE s.device_user_id <> '00000000-0000-0000-0000-000000000000'::uuid
                     AND s.seconds_on > 0)::int AS person_count,
               COALESCE(sum(s.days_with_data) FILTER (
                   WHERE s.device_user_id <> '00000000-0000-0000-0000-000000000000'::uuid), 0)::int AS person_days
        FROM monthly_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND s.month_start BETWEEN date_trunc('month', @From::date)::date AND @To::date
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
        """;

    /// <summary>
    /// from/to no fuso do tenant, inclusivos, formato yyyy-MM-dd; from ≤ to e janela
    /// de no máximo 92 dias — fora disso, 400 ProblemDetails.
    /// </summary>
    private ObjectResult? ValidateRange(string? from, string? to)
    {
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", out var fromDay))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro from é obrigatório no formato yyyy-MM-dd.");
        if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", out var toDay))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro to é obrigatório no formato yyyy-MM-dd.");
        if (fromDay > toDay)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Intervalo inválido: from deve ser anterior ou igual a to.");
        if (toDay.DayNumber - fromDay.DayNumber + 1 > MaxRangeDays)
            return ProblemResponse(StatusCodes.Status400BadRequest, $"Intervalo máximo de {MaxRangeDays} dias.");
        return null;
    }

    private sealed record SummaryRow(
        string? Date,
        long SecondsActive,
        long SecondsIdle,
        long SecondsLocked,
        long SecondsOn,
        long SecondsWorkRelated,
        long SecondsNeutral,
        long SecondsNotWorkRelated,
        long SecondsUnclassified,
        bool DataIncomplete,
        int DeviceCount);

    private sealed record HourRow(int Hour, long SecondsActive, long SecondsIdle);

    private sealed record OverviewTotalsRow(
        long SecondsOn,
        long SecondsActive,
        long SecondsIdle,
        long SecondsLocked,
        long SecondsWorkRelated,
        long SecondsNeutral,
        long SecondsNotWorkRelated,
        long SecondsUnclassified,
        bool DataIncomplete,
        int DeviceCount,
        int PersonCount,
        int PersonDays);

    private sealed record GoalsRow(int? Weekly, int? Pct);

    private sealed record TopAppRow(
        Guid AppId,
        string ProcessName,
        string DisplayName,
        string? CustomDisplayName,
        Guid? CategoryId,
        string? CategoryName,
        short? CategoryClassification,
        string? CategoryColor,
        long SecondsActive,
        int DeviceCount);

    private sealed record PresenceRow(
        Guid DeviceId,
        string DeviceName,
        string Hostname,
        string State,
        string? WindowsUsername,
        string? ForegroundProcess,
        string? ForegroundTitle,
        DateTimeOffset? StateSince,
        DateTimeOffset? AppSince,
        DateTimeOffset LastContactAt);
}
