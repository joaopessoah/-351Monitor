using System.Text.Json;
using Dapper;
using M351.Api.Auditing;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// /api/v1/people/* (F6) — a LISTA DE COLABORADORES que o portal não tinha. O site promete
/// "análise por colaborador" desde sempre; o produto só tinha a página de um titular, alcançável
/// por relatório ou pela busca do DSR, sem nenhuma listagem navegável.
///
/// IDENTIDADE: o windows_sid do tenant, resolvido pela mesclagem de people. É o que permite
/// somar a MESMA pessoa em duas máquinas numa linha só — device_users é (dispositivo, SID) e por
/// isso duplica. A lane-máquina (device_user_id UUID zero) NÃO é pessoa e fica fora (decisão 8
/// do spec: "ligada sem usuário" é estado de dispositivo, não hora de gente).
///
/// DECISÃO 2 do spec de 07/09/2026: esta lista EXISTE, com métricas do período e ordenação por
/// coluna, restrita a Viewer+ e AUDITADA. Diferente da listagem de /device-users (que não audita
/// porque é um seletor de nomes), aqui cada linha carrega produtividade de uma pessoa
/// identificada — é leitura de dado pessoal e deixa rastro.
///
/// VOCABULÁRIO: a ordem default é ALFABÉTICA e nenhuma resposta, rótulo ou nome de campo usa a
/// palavra ranking. Ordenar por índice é ferramenta de gestão do gestor autorizado, não placar
/// público (TST condenou exposição de ranking; ver seção 11 do spec).
///
/// Dapper/NpgsqlDataSource porque daily_device_summaries, device_users e people não têm entidade
/// EF: sem filtro global de tenant, então tenant_id vai MANUSCRITO em todo WHERE. Recurso
/// inexistente ou de outro tenant responde 404, jamais 403 (Princípio 4).
/// </summary>
[Route("api/v1/people")]
[Authorize] // Viewer+ na leitura; o PATCH exige AdminPlus
public class PeopleController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    /// <summary>Lane sintética da máquina (spec linha 652): não é pessoa.</summary>
    private static readonly Guid MachineLane = Guid.Empty;

    /// <summary>Teto do apelido — mesmo limite do display_name de device_users.</summary>
    public const int MaxDisplayNameLength = 120;

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>
    /// Colunas ordenáveis, allow-list FECHADA: o valor entra interpolado no ORDER BY, então
    /// nada que venha do query string pode chegar lá sem passar por este dicionário.
    /// </summary>
    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "display_name",
        ["seconds_on"] = "seconds_on",
        ["seconds_active"] = "seconds_active",
        ["seconds_idle"] = "seconds_idle",
        ["seconds_unclassified"] = "seconds_unclassified",
        ["productivity_index"] = "productivity_index",
    };

    /// <summary>
    /// GET /api/v1/people?from&amp;to[&amp;tag][&amp;q][&amp;sort][&amp;dir][&amp;page][&amp;page_size] (F6):
    /// uma linha por pessoa no período, com os seis baldes, índice, cobertura, dispositivos,
    /// dias com dado e as etiquetas de equipe das máquinas em que ela apareceu.
    ///
    /// Devices archived ficam fora (mesma régua do dashboard). Dias sem dado não entram na
    /// contagem days_with_data, então "0h em 3 dias" não vira média enganosa no cliente.
    /// </summary>
    [HttpGet]
    [AuditRead] // decisão 2: a lista com métricas é dado pessoal — view_report via AuditReadFilter
    public async Task<IActionResult> List(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "tag")] string? tag,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "sort")] string? sort,
        [FromQuery(Name = "dir")] string? dir,
        [FromQuery(Name = "page")] int page,
        [FromQuery(Name = "page_size")] int pageSize,
        [FromServices] AuditReadContext readAudit = null!,
        CancellationToken ct = default)
    {
        var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
        if (invalid is not null) return invalid;

        page = page < 1 ? 1 : page;
        pageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var sortColumn = SortColumns.TryGetValue(sort ?? "name", out var column) ? column : "display_name";
        var descending = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        var direction = descending ? "DESC" : "ASC";
        // nome é o desempate SEMPRE: duas pessoas com o mesmo tempo saem em ordem estável
        var orderBy = sortColumn == "display_name"
            ? $"display_name {direction}"
            : $"{sortColumn} {direction} NULLS LAST, display_name ASC";

        var tenantId = CurrentUser.TenantId(User);
        var normalizedTag = NormalizeTeamTag(tag);
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var rows = (await connection.QueryAsync<PersonRow>(new CommandDefinition(
            $"""
            WITH lanes AS (
                SELECT COALESCE(p.merged_into_sid, du.windows_sid) AS sid,
                       du.display_name AS lane_display_name,
                       du.windows_username,
                       du.last_seen_at,
                       s.device_id,
                       d.tags,
                       s.summary_date,
                       s.seconds_on, s.seconds_active, s.seconds_idle, s.seconds_locked,
                       s.seconds_work_related, s.seconds_neutral, s.seconds_not_work_related,
                       s.seconds_unclassified
                FROM daily_device_summaries s
                JOIN device_users du ON du.tenant_id = s.tenant_id AND du.id = s.device_user_id
                JOIN devices d ON d.tenant_id = s.tenant_id AND d.id = s.device_id
                LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
                WHERE s.tenant_id = @TenantId
                  AND s.device_user_id <> @MachineLane
                  AND d.status <> 'archived'
                  AND s.summary_date BETWEEN @From::date AND @To::date
                  AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            ),
            teams AS (
                SELECT l.sid, array_agg(DISTINCT u.t ORDER BY u.t) AS teams
                FROM lanes l
                CROSS JOIN LATERAL unnest(COALESCE(l.tags, ARRAY[]::text[])) AS u(t)
                GROUP BY l.sid
            ),
            agg AS (
                SELECT l.sid,
                       (array_agg(l.lane_display_name ORDER BY l.last_seen_at DESC NULLS LAST)
                            FILTER (WHERE l.lane_display_name IS NOT NULL))[1] AS lane_name,
                       (array_agg(l.windows_username ORDER BY l.last_seen_at DESC NULLS LAST)
                            FILTER (WHERE l.windows_username IS NOT NULL))[1] AS windows_username,
                       sum(l.seconds_on)::bigint AS seconds_on,
                       sum(l.seconds_active)::bigint AS seconds_active,
                       sum(l.seconds_idle)::bigint AS seconds_idle,
                       sum(l.seconds_locked)::bigint AS seconds_locked,
                       sum(l.seconds_work_related)::bigint AS seconds_work_related,
                       sum(l.seconds_neutral)::bigint AS seconds_neutral,
                       sum(l.seconds_not_work_related)::bigint AS seconds_not_work_related,
                       sum(l.seconds_unclassified)::bigint AS seconds_unclassified,
                       count(DISTINCT l.device_id)::int AS device_count,
                       count(DISTINCT l.summary_date) FILTER (WHERE l.seconds_on > 0)::int AS days_with_data
                FROM lanes l
                GROUP BY l.sid
            ),
            calc AS (
                SELECT a.sid AS windows_sid,
                       COALESCE(pp.display_name, a.lane_name, a.windows_username, a.sid) AS display_name,
                       a.seconds_on, a.seconds_active, a.seconds_idle, a.seconds_locked,
                       a.seconds_work_related, a.seconds_neutral, a.seconds_not_work_related,
                       a.seconds_unclassified,
                       -- ::double precision de propósito: round() devolve numeric, que o Dapper
                       -- materializaria como decimal e não casaria com o double? do contrato
                       CASE WHEN (a.seconds_work_related + a.seconds_neutral + a.seconds_not_work_related) > 0
                            THEN round(a.seconds_work_related::numeric
                                 / (a.seconds_work_related + a.seconds_neutral + a.seconds_not_work_related), 4)::double precision
                       END AS productivity_index,
                       CASE WHEN a.seconds_active > 0
                            THEN round((a.seconds_active - a.seconds_unclassified)::numeric / a.seconds_active, 4)::double precision
                       END AS classification_coverage,
                       a.device_count, a.days_with_data,
                       -- text[] materializa como System.Array e o Dapper não casa isso com
                       -- string[] no construtor do record: vem juntado por US (0x1F), separador
                       -- que não pode aparecer numa etiqueta, e o C# divide de volta
                       array_to_string(COALESCE(t.teams, ARRAY[]::text[]), chr(31)) AS teams
                FROM agg a
                LEFT JOIN people pp ON pp.tenant_id = @TenantId AND pp.windows_sid = a.sid
                LEFT JOIN teams t ON t.sid = a.sid
            )
            SELECT c.*, count(*) OVER ()::int AS total_count
            FROM calc c
            WHERE (@Search::text IS NULL OR c.display_name ILIKE '%' || @Search || '%')
            ORDER BY {orderBy}
            LIMIT @PageSize OFFSET @Offset
            """,
            new
            {
                TenantId = tenantId,
                MachineLane,
                From = fromDay.ToString("yyyy-MM-dd"),
                To = toDay.ToString("yyyy-MM-dd"),
                Tag = normalizedTag,
                Search = search,
                PageSize = pageSize,
                Offset = (page - 1) * pageSize,
            },
            cancellationToken: ct))).ToList();

        // Decisão 2: a leitura da lista deixa rastro. Sem alvo individual (a consulta é de
        // várias pessoas), no mesmo desenho do view_timeline de equipe: target_type "people"
        // e o recorte no detail.
        readAudit.Record(tenantId, AuditActions.ViewReport,
            CurrentUser.UserId(User),
            targetType: "people", targetId: null,
            detailJson: JsonSerializer.Serialize(new
            {
                from = fromDay.ToString("yyyy-MM-dd"),
                to = toDay.ToString("yyyy-MM-dd"),
                tag = normalizedTag,
                q = search,
                sort = sortColumn,
                dir = direction,
                page,
                page_size = pageSize,
                returned = rows.Count,
            }));

        var items = rows.Select(r => new PersonRowResponse(
            r.WindowsSid, r.DisplayName,
            r.SecondsOn, r.SecondsActive, r.SecondsIdle, r.SecondsLocked,
            r.SecondsWorkRelated, r.SecondsNeutral, r.SecondsNotWorkRelated, r.SecondsUnclassified,
            r.ProductivityIndex, r.ClassificationCoverage,
            r.DeviceCount, r.DaysWithData, SplitTeams(r.Teams))).ToList();

        return Ok(new PeopleReportResponse(items, rows.FirstOrDefault()?.TotalCount ?? 0, page, pageSize));
    }

    /// <summary>
    /// PATCH /api/v1/people/{sid} (AdminPlus): apelido e mesclagem.
    ///
    /// A mesclagem é o conserto manual do caso que o SID não resolve sozinho: a mesma pessoa com
    /// duas contas do Windows. Cadeia é recusada de propósito (o alvo precisa existir no tenant e
    /// não estar ele mesmo mesclado) — resolver A→B→C na leitura exigiria recursão e abriria
    /// espaço para ciclo. Mutação e trilha na MESMA transação: o nome de uma pessoa nunca muda
    /// sem registro de quem mudou.
    /// </summary>
    [HttpPatch("{sid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Patch(
        string sid, [FromBody] PersonPatchRequest? request, CancellationToken ct)
    {
        var displayName = string.IsNullOrWhiteSpace(request?.DisplayName) ? null : request!.DisplayName!.Trim();
        var mergedInto = string.IsNullOrWhiteSpace(request?.MergedIntoSid) ? null : request!.MergedIntoSid!.Trim();

        if (displayName is { Length: > MaxDisplayNameLength })
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"display_name inválido (máximo {MaxDisplayNameLength} caracteres, ou null para limpar o apelido).");
        }

        var tenantId = CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // a pessoa precisa existir COMO SID conhecido do tenant: inexistente ou de outro
        // tenant responde 404, nunca 403 (Princípio 4)
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM device_users WHERE tenant_id = @TenantId AND windows_sid = @Sid)",
            new { TenantId = tenantId, Sid = sid }, cancellationToken: ct));
        if (!exists) return NotFoundProblem();

        if (mergedInto is not null)
        {
            if (mergedInto == sid)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "Mesclagem inválida: uma pessoa não pode ser mesclada em si mesma.");
            }

            var targetOk = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM device_users
                                WHERE tenant_id = @TenantId AND windows_sid = @Target)
                   AND NOT EXISTS (SELECT 1 FROM people
                                    WHERE tenant_id = @TenantId AND windows_sid = @Target
                                      AND merged_into_sid IS NOT NULL)
                """,
                new { TenantId = tenantId, Target = mergedInto }, cancellationToken: ct));
            if (!targetOk)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "Destino da mesclagem inválido: precisa ser uma pessoa deste tenant que ainda não foi mesclada.");
            }
        }

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO people (tenant_id, windows_sid, display_name, merged_into_sid, created_at, updated_at)
                VALUES (@TenantId, @Sid, @DisplayName, @Merged, now(), now())
                ON CONFLICT (tenant_id, windows_sid) DO UPDATE SET
                    display_name = EXCLUDED.display_name,
                    merged_into_sid = EXCLUDED.merged_into_sid,
                    updated_at = now()
                """,
                new { TenantId = tenantId, Sid = sid, DisplayName = displayName, Merged = mergedInto },
                transaction: tx, cancellationToken: ct));

            await AuditWriter.AddInTransactionAsync(connection, tx, tenantId, AuditActions.UpdatePerson,
                actorUserId: CurrentUser.UserId(User),
                actorIp: HttpContext.Connection.RemoteIpAddress,
                targetType: "person", targetId: null,
                detailJson: JsonSerializer.Serialize(new
                {
                    windows_sid = sid,
                    display_name = displayName,
                    merged_into_sid = mergedInto,
                }), ct: ct);

            await tx.CommitAsync(ct);
        }

        return Ok(new PersonPatchResponse(sid, displayName, mergedInto));
    }

    // ------------------------------------------------------------ linha crua do SQL
    private sealed record PersonRow(
        string WindowsSid,
        string DisplayName,
        long SecondsOn,
        long SecondsActive,
        long SecondsIdle,
        long SecondsLocked,
        long SecondsWorkRelated,
        long SecondsNeutral,
        long SecondsNotWorkRelated,
        long SecondsUnclassified,
        double? ProductivityIndex,
        double? ClassificationCoverage,
        int DeviceCount,
        int DaysWithData,
        string? Teams,
        int TotalCount);

    /// <summary>Desfaz o array_to_string do SQL — vazio devolve lista vazia, nunca [""].</summary>
    private static IReadOnlyList<string> SplitTeams(string? joined) =>
        string.IsNullOrEmpty(joined) ? [] : joined.Split('');
}
