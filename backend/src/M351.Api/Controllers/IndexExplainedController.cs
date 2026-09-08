using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Infrastructure.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// GET /api/v1/dashboard/index-explained (item 1 do estudo, "índice explicável").
///
/// O QUE ENTREGA: a variação do índice entre o período pedido e o imediatamente anterior de mesma
/// duração, repartida entre APLICATIVOS, EQUIPES e DIAS — a frase "menos 4 pontos: mais 6 h em
/// WhatsApp na equipe Comercial na terça", com número em vez de impressão.
///
/// POR QUE A REPARTIÇÃO FECHA: o índice é P/C, e a variação admite a identidade
/// P₁/C₁ − P₀/C₀ = Σₖ (C₀·Δpₖ − P₀·Δcₖ)/(C₁C₀). A matemática mora em IndexDecomposition
/// (Infrastructure/Analytics), com teste próprio; aqui só se lê o Postgres e se monta o contrato.
///
/// POR QUE FICA FORA DO DashboardController: aquele arquivo já passa de 500 linhas e junta oito
/// leituras diferentes. A decomposição tem SQL próprio (três recortes, uma CTE de equipe) e um
/// motivo próprio para mudar.
///
/// AUDITORIA: nenhuma. É agregado de organização e de equipe, como o /overview — não identifica
/// pessoa. A dimensão por equipe respeita a etiqueta legada de device do mesmo jeito que a
/// agregação, e a lane-máquina (UUID zero) cai em "Sem equipe": não é pessoa (decisão 8), mas os
/// segundos dela contam nos totais e precisam aparecer para a soma fechar.
///
/// PRECISÃO: as dimensões por equipe e por dia particionam exatamente daily_device_summaries,
/// então a soma das parcelas é o delta ao último bit. A dimensão por aplicativo lê
/// daily_app_usage, cujo floor() é aplicado por (lane, app) em vez de por lane — a diferença é de
/// alguns segundos numa frota inteira, o que move a parcela na quinta casa decimal. Nada que a
/// tela, que mostra uma casa, consiga exibir; e a alternativa (uma fatia "Outros" de 0,00001
/// ponto) seria pior de ler do que o erro que corrige.
/// </summary>
[Route("api/v1/dashboard")]
[Authorize] // Viewer+, como o restante do dashboard
public class IndexExplainedController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    /// <summary>Quantas contribuições cada dimensão devolve por padrão.</summary>
    public const int DefaultLimit = 8;

    /// <summary>Teto do limit: acima disso a lista deixa de ser "onde a variação está" e vira dump.</summary>
    public const int MaxLimit = 25;

    /// <summary>Motivo em pt-BR quando não há variação a explicar — a tela mostra esta frase.</summary>
    public const string SemDenominador =
        "Não há tempo classificado suficiente para comparar os dois períodos: sem denominador não " +
        "existe índice, e sem índice nos dois lados não existe variação a explicar.";

    [HttpGet("index-explained")]
    public async Task<IActionResult> Get(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "tag")] string? tag,
        [FromQuery(Name = "limit")] int? limit,
        CancellationToken ct)
    {
        var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
        if (invalid is not null)
        {
            return invalid;
        }

        // período anterior: mesma duração, terminando na véspera. Idêntico ao compare=true do
        // /overview, de propósito — as duas telas têm de contar a mesma história.
        var length = toDay.DayNumber - fromDay.DayNumber + 1;
        var prevTo = fromDay.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(length - 1));

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var tenantId = CurrentUser.TenantId(User);
        var normalizedTag = NormalizeTeamTag(tag);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var args = new
        {
            TenantId = tenantId,
            From = fromDay.ToString("yyyy-MM-dd"),
            To = toDay.ToString("yyyy-MM-dd"),
            PrevFrom = prevFrom.ToString("yyyy-MM-dd"),
            Tag = normalizedTag,
        };

        var byApp = await ReadAsync(connection, ByAppSql, args, ct);
        var byTeam = await ReadAsync(connection, ByTeamSql, args, ct);
        var byDay = await ReadDaysAsync(connection, args, fromDay, prevFrom, ct);

        // o headline sai de daily_device_summaries — a MESMA fonte do /overview, para o número
        // grande da tela nunca divergir do KPI ao lado
        var canonical = byTeam;
        var index = IndexOf(canonical, current: true);
        var previousIndex = IndexOf(canonical, current: false);
        var delta = IndexDecomposition.DeltaPoints(canonical);

        return Ok(new IndexExplainedResponse(
            new OverviewPeriodResponse(args.From, args.To, length),
            new OverviewPeriodResponse(args.PrevFrom, prevTo.ToString("yyyy-MM-dd"), length),
            index,
            previousIndex,
            delta,
            delta is null ? SemDenominador : null,
            Top(byApp, take),
            Top(byTeam, take),
            Top(byDay, take)));
    }

    // ------------------------------------------------------------------------------- montagem

    /// <summary>Índice de um dos dois lados, pela fórmula única (decisão 4). null sem denominador.</summary>
    private static double? IndexOf(IReadOnlyList<MemberSeconds> members, bool current)
    {
        var work = current ? members.Sum(m => m.WorkCurrent) : members.Sum(m => m.WorkPrevious);
        var classified = current ? members.Sum(m => m.ClassifiedCurrent) : members.Sum(m => m.ClassifiedPrevious);
        return classified > 0 ? Math.Round((double)work / classified, 4) : null;
    }

    /// <summary>
    /// As maiores contribuições EM MÓDULO, com o sinal preservado. Ordenar por módulo é o ponto:
    /// a pergunta é "onde a variação está", e uma queda de 4 pontos importa tanto quanto uma
    /// alta de 4. Membro que não mexeu no índice (parcela zero) não entra — ocuparia linha sem
    /// dizer nada.
    /// </summary>
    private static IReadOnlyList<IndexContributionResponse> Top(IReadOnlyList<MemberSeconds> members, int take)
    {
        var parcels = IndexDecomposition.Decompose(members);
        if (parcels is null)
        {
            return [];
        }

        return parcels
            .OrderByDescending(p => Math.Abs(p.Points))
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(p => new IndexContributionResponse(
                p.Key, p.Label, Math.Round(p.Points, 4),
                p.WorkCurrent, p.WorkPrevious, p.ClassifiedCurrent, p.ClassifiedPrevious))
            .ToList();
    }

    private static async Task<IReadOnlyList<MemberSeconds>> ReadAsync(
        NpgsqlConnection connection, string sql, object args, CancellationToken ct) =>
        (await connection.QueryAsync<ContributionRow>(new CommandDefinition(sql, args, cancellationToken: ct)))
        .Select(r => new MemberSeconds(
            r.Key, r.Label, r.WorkCurrent, r.WorkPrevious, r.ClassifiedCurrent, r.ClassifiedPrevious))
        .ToList();

    /// <summary>
    /// A dimensão por DIA é a única que precisa de pareamento: o dia i do período compara com o
    /// dia i do período anterior (o anterior tem a mesma duração e termina na véspera, então o
    /// pareamento por deslocamento ordinal é exato). O SQL devolve uma linha por data; o
    /// casamento acontece aqui, onde a aritmética de datas é legível.
    /// </summary>
    private static async Task<IReadOnlyList<MemberSeconds>> ReadDaysAsync(
        NpgsqlConnection connection, object args, DateOnly fromDay, DateOnly prevFrom, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<DayRow>(new CommandDefinition(ByDaySql, args, cancellationToken: ct)))
            .ToDictionary(r => r.Date, r => r);

        var length = fromDay.DayNumber - prevFrom.DayNumber; // = duração do período
        var members = new List<MemberSeconds>(length);

        for (var offset = 0; offset < length; offset++)
        {
            var day = fromDay.AddDays(offset);
            var previousDay = prevFrom.AddDays(offset);
            var atual = rows.GetValueOrDefault(day.ToString("yyyy-MM-dd"));
            var anterior = rows.GetValueOrDefault(previousDay.ToString("yyyy-MM-dd"));

            if (atual is null && anterior is null)
            {
                continue; // dia sem dado em nenhum dos dois lados não tem nada a explicar
            }

            members.Add(new MemberSeconds(
                Key: day.ToString("yyyy-MM-dd"),
                Label: day.ToString("yyyy-MM-dd"),
                WorkCurrent: atual?.Work ?? 0,
                WorkPrevious: anterior?.Work ?? 0,
                ClassifiedCurrent: atual?.Classified ?? 0,
                ClassifiedPrevious: anterior?.Classified ?? 0));
        }

        return members;
    }

    // ------------------------------------------------------------------------------------ SQL

    /// <summary>
    /// EQUIPE DE CADA LANE do tenant. Cópia fiel da CTE do DailyAggregationService (F7), sem o
    /// recorte por device: mesma precedência — equipe da PESSOA pelo SID canônico, depois a
    /// etiqueta legada que alguma equipe declarou em teams.tag, depois nenhuma.
    ///
    /// O LATERAL com LIMIT 1 é obrigatório: sem ele, um dispositivo com duas etiquetas
    /// reivindicadas por duas equipes devolveria DUAS linhas por lane e dobraria os segundos.
    /// </summary>
    private const string LaneTeamCte = """
        WITH lane_team AS (
            SELECT du.id AS device_user_id,
                   COALESCE(tm.team_id, tag_team.team_id) AS team_id
            FROM device_users du
            LEFT JOIN people p
                   ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
            LEFT JOIN team_members tm
                   ON tm.tenant_id = du.tenant_id
                  AND tm.windows_sid = COALESCE(p.merged_into_sid, du.windows_sid)
            LEFT JOIN devices dev
                   ON dev.tenant_id = du.tenant_id AND dev.id = du.device_id
            LEFT JOIN LATERAL (
                SELECT t2.id AS team_id
                FROM teams t2
                WHERE t2.tenant_id = du.tenant_id
                  AND t2.tag IS NOT NULL AND t2.tag = ANY(dev.tags)
                ORDER BY t2.name
                LIMIT 1
            ) tag_team ON true
            WHERE du.tenant_id = @TenantId
        )
        """;

    /// <summary>
    /// Por APLICATIVO, de daily_app_usage. O JOIN em categories é fechado (não LEFT) de
    /// propósito: sem categoria o tempo é SEM CLASSIFICAÇÃO, fica fora de C e portanto fora da
    /// decomposição — exatamente como fica fora da fórmula do índice.
    ///
    /// A precedência de classificação é a da F5, reaplicada na leitura: regra da EQUIPE da lane
    /// vence a regra da ORGANIZAÇÃO. Os dois períodos são contíguos (o anterior termina na
    /// véspera do atual), então uma varredura só resolve os dois com FILTER.
    /// </summary>
    private static readonly string ByAppSql = $"""
        {LaneTeamCte},
        uso AS (
            SELECT a.app_id,
                   (a.summary_date >= @From::date) AS atual,
                   a.seconds_active,
                   c.classification
            FROM daily_app_usage a
            JOIN devices d ON d.id = a.device_id AND d.tenant_id = a.tenant_id
            LEFT JOIN lane_team lt ON lt.device_user_id = a.device_user_id
            LEFT JOIN tenant_app_team_categories tatc
                   ON tatc.tenant_id = a.tenant_id AND tatc.app_id = a.app_id
                  AND tatc.team_id = lt.team_id
            LEFT JOIN tenant_app_categories tac
                   ON tac.tenant_id = a.tenant_id AND tac.app_id = a.app_id
            JOIN categories c
                   ON c.tenant_id = a.tenant_id
                  AND c.id = COALESCE(tatc.category_id, tac.category_id)
            WHERE a.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
              AND a.summary_date BETWEEN @PrevFrom::date AND @To::date
        )
        SELECT ac.process_name AS Key,
               COALESCE(NULLIF(ac.display_name, ''), ac.process_name) AS Label,
               COALESCE(sum(u.seconds_active) FILTER (WHERE u.atual AND u.classification = 1), 0)::bigint AS WorkCurrent,
               COALESCE(sum(u.seconds_active) FILTER (WHERE NOT u.atual AND u.classification = 1), 0)::bigint AS WorkPrevious,
               COALESCE(sum(u.seconds_active) FILTER (WHERE u.atual), 0)::bigint AS ClassifiedCurrent,
               COALESCE(sum(u.seconds_active) FILTER (WHERE NOT u.atual), 0)::bigint AS ClassifiedPrevious
        FROM uso u
        JOIN app_catalog ac ON ac.id = u.app_id
        GROUP BY 1, 2
        """;

    /// <summary>
    /// Por EQUIPE, de daily_device_summaries — os mesmos baldes do /overview, então as parcelas
    /// desta dimensão particionam exatamente o total e a soma fecha ao último bit. Lane sem
    /// equipe (inclusive a lane-máquina) cai em "Sem equipe": não é pessoa, mas os segundos
    /// contam e precisam aparecer.
    /// </summary>
    private static readonly string ByTeamSql = $"""
        {LaneTeamCte}
        SELECT COALESCE(t.name, 'Sem equipe') AS Key,
               COALESCE(t.name, 'Sem equipe') AS Label,
               COALESCE(sum(s.seconds_work_related)
                   FILTER (WHERE s.summary_date >= @From::date), 0)::bigint AS WorkCurrent,
               COALESCE(sum(s.seconds_work_related)
                   FILTER (WHERE s.summary_date < @From::date), 0)::bigint AS WorkPrevious,
               COALESCE(sum(s.seconds_work_related + s.seconds_neutral + s.seconds_not_work_related)
                   FILTER (WHERE s.summary_date >= @From::date), 0)::bigint AS ClassifiedCurrent,
               COALESCE(sum(s.seconds_work_related + s.seconds_neutral + s.seconds_not_work_related)
                   FILTER (WHERE s.summary_date < @From::date), 0)::bigint AS ClassifiedPrevious
        FROM daily_device_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        LEFT JOIN lane_team lt ON lt.device_user_id = s.device_user_id
        LEFT JOIN teams t ON t.tenant_id = s.tenant_id AND t.id = lt.team_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
          AND s.summary_date BETWEEN @PrevFrom::date AND @To::date
        GROUP BY 1, 2
        """;

    /// <summary>Uma linha por data, dos dois períodos. O pareamento dia i ↔ dia i acontece em C#.</summary>
    private const string ByDaySql = """
        SELECT s.summary_date::text AS Date,
               COALESCE(sum(s.seconds_work_related), 0)::bigint AS Work,
               COALESCE(sum(s.seconds_work_related + s.seconds_neutral + s.seconds_not_work_related), 0)::bigint AS Classified
        FROM daily_device_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
          AND s.summary_date BETWEEN @PrevFrom::date AND @To::date
        GROUP BY s.summary_date
        """;

    private sealed record ContributionRow(
        string Key,
        string Label,
        long WorkCurrent,
        long WorkPrevious,
        long ClassifiedCurrent,
        long ClassifiedPrevious);

    private sealed record DayRow(string Date, long Work, long Classified);
}
