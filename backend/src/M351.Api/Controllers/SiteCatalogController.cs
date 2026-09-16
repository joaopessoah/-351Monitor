using System.Text.Json;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain.Entities;
using M351.Infrastructure.Aggregation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// Catálogo de SITES na visão do TENANT — irmão gêmeo do <see cref="AppCatalogController"/>.
/// O catálogo (site_catalog) é GLOBAL e guarda só o DOMÍNIO, que é público e igual para todo
/// mundo; a LISTAGEM é o recorte do tenant (união dos sites com uso em daily_site_usage com os
/// sites que ele mapeou) e a CLASSIFICAÇÃO é dele (tenant_site_categories).
///
/// POR QUE UM CONTROLLER PRÓPRIO, e não um parâmetro no de apps: são duas entidades com chaves
/// diferentes (process_name × domain) e duas tabelas de regra. Um endpoint polimórfico
/// economizaria arquivo e pagaria com um `if` em cada consulta — inclusive na mais quente.
///
/// ORDEM DE PRECEDÊNCIA na LEITURA (a mesma do DailyAggregationService, que é quem manda):
///   1. regra de site da equipe consultada → category_scope "team"
///   2. regra de site da organização       → category_scope "organization" (HERANÇA, não omissão)
///   3. nenhuma das duas                   → category null, "sem classificação"
/// E, na hora de CLASSIFICAR o tempo, a regra de site ainda vence a regra do navegador: é a
/// mais específica. Marcar "mercadolivre.com.br" como improdutivo muda o número mesmo com
/// "chrome.exe" classificado como Navegação.
///
/// PUT reagrega os últimos 30 dias e audita update_category na MESMA transação da mutação.
/// </summary>
[Route("api/v1/site-catalog")]
[Authorize] // Viewer+ nas leituras; os PUT exigem AdminPlus
public class SiteCatalogController(
    NpgsqlDataSource dataSource,
    TimeProvider clock) : ApiControllerBase
{
    /// <summary>Teto da listagem (mesma régua do catálogo de apps).</summary>
    public const int MaxItems = 500;

    /// <summary>Teto do PUT em lote: o lote nasce da tela, que lista no máximo 500.</summary>
    public const int MaxBatchItems = MaxItems;

    private const int MaxCustomNameLength = 200;

    /// <summary>Ordenação "fila de classificação": sem categoria primeiro, depois tempo desc.</summary>
    public const string SortImpacto = AppCatalogController.SortImpacto;

    /// <summary>
    /// GET /api/v1/site-catalog?q=&amp;uncategorized=true&amp;sort=impacto&amp;team_id= (Viewer):
    /// recorte do tenant, máximo 500 itens, janela de 30 dias no fuso do tenant (mesmo corte da
    /// reagregação sob demanda). q busca em domain/display_name/custom_display_name.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "uncategorized")] bool uncategorized = false,
        [FromQuery(Name = "sort")] string? sort = null,
        [FromQuery(Name = "team_id")] Guid? teamId = null,
        CancellationToken ct = default)
    {
        var tenantId = CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // equipe inexistente OU de outro tenant → 404 (nunca 403, Princípio 4)
        if (teamId is not null
            && !await AppCatalogController.TeamExistsAsync(connection, null, tenantId, teamId.Value, ct))
            return NotFoundProblem();

        var timezone = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        if (timezone is null) return NotFoundProblem();

        var cutoff = TodayInTenantTz(timezone).AddDays(-ReaggregationRequester.WindowDays).ToString("yyyy-MM-dd");
        var pattern = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%";
        var byImpact = string.Equals(sort, SortImpacto, StringComparison.OrdinalIgnoreCase);

        var rows = (await connection.QueryAsync<CatalogRow>(new CommandDefinition(
            """
            WITH usage_30d AS (
                SELECT u.site_id,
                       sum(u.seconds_active)::bigint AS seconds_active_30d,
                       count(DISTINCT u.device_id)::int AS device_count_30d
                FROM daily_site_usage u
                WHERE u.tenant_id = @TenantId AND u.summary_date >= @Cutoff::date
                GROUP BY u.site_id
            ), known AS (
                SELECT DISTINCT u.site_id FROM daily_site_usage u WHERE u.tenant_id = @TenantId
                UNION
                SELECT tsc.site_id FROM tenant_site_categories tsc WHERE tsc.tenant_id = @TenantId
                UNION
                SELECT r.site_id FROM tenant_site_team_categories r
                 WHERE r.tenant_id = @TenantId AND r.team_id = @TeamId
            )
            SELECT s.id AS site_id, s.domain, s.display_name, s.default_category,
                   tsc.custom_display_name,
                   c.id AS category_id, c.name AS category_name,
                   c.classification AS category_classification, c.color AS category_color,
                   CASE WHEN tstc.category_id IS NOT NULL THEN 'team'
                        WHEN tsc.category_id  IS NOT NULL THEN 'organization'
                   END AS category_scope,
                   COALESCE(u.seconds_active_30d, 0) AS seconds_active_30d,
                   COALESCE(u.device_count_30d, 0) AS device_count_30d
            FROM known k
            JOIN site_catalog s ON s.id = k.site_id
            LEFT JOIN usage_30d u ON u.site_id = k.site_id
            LEFT JOIN tenant_site_team_categories tstc
                   ON tstc.tenant_id = @TenantId AND tstc.site_id = k.site_id AND tstc.team_id = @TeamId
            LEFT JOIN tenant_site_categories tsc ON tsc.tenant_id = @TenantId AND tsc.site_id = k.site_id
            LEFT JOIN categories c
                   ON c.tenant_id = @TenantId AND c.id = COALESCE(tstc.category_id, tsc.category_id)
            WHERE (@Pattern::text IS NULL
                   OR s.domain ILIKE @Pattern
                   OR s.display_name ILIKE @Pattern
                   OR tsc.custom_display_name ILIKE @Pattern)
              AND (@UncategorizedOnly = false OR COALESCE(tstc.category_id, tsc.category_id) IS NULL)
            ORDER BY CASE WHEN @ByImpact AND COALESCE(tstc.category_id, tsc.category_id) IS NULL
                          THEN 0 ELSE 1 END,
                     seconds_active_30d DESC, s.domain
            LIMIT @Limit
            """,
            new
            {
                TenantId = tenantId, Cutoff = cutoff, Pattern = pattern,
                UncategorizedOnly = uncategorized, ByImpact = byImpact, Limit = MaxItems,
                TeamId = teamId,
            },
            cancellationToken: ct))).ToList();

        var uncategorizedCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(DISTINCT u.site_id)::int
            FROM daily_site_usage u
            WHERE u.tenant_id = @TenantId
              AND NOT EXISTS (
                  SELECT 1 FROM tenant_site_categories tsc
                  WHERE tsc.tenant_id = u.tenant_id AND tsc.site_id = u.site_id)
              AND NOT EXISTS (
                  SELECT 1 FROM tenant_site_team_categories r
                  WHERE r.tenant_id = u.tenant_id AND r.site_id = u.site_id AND r.team_id = @TeamId)
            """,
            new { TenantId = tenantId, TeamId = teamId }, cancellationToken: ct));

        var coverage = await connection.QuerySingleAsync<CoverageRow>(new CommandDefinition(
            """
            SELECT COALESCE(sum(u.seconds_active), 0)::bigint AS total_seconds_active,
                   COALESCE(sum(u.seconds_active) FILTER (
                       WHERE NOT EXISTS (
                           SELECT 1 FROM tenant_site_categories tsc
                           WHERE tsc.tenant_id = u.tenant_id AND tsc.site_id = u.site_id)
                         AND NOT EXISTS (
                           SELECT 1 FROM tenant_site_team_categories r
                           WHERE r.tenant_id = u.tenant_id AND r.site_id = u.site_id
                             AND r.team_id = @TeamId)), 0)::bigint
                       AS uncategorized_seconds_active
            FROM daily_site_usage u
            WHERE u.tenant_id = @TenantId AND u.summary_date >= @Cutoff::date
            """,
            new { TenantId = tenantId, Cutoff = cutoff, TeamId = teamId }, cancellationToken: ct));

        var items = rows.Select(r => new SiteCatalogItemResponse(
                r.SiteId, r.Domain, r.DisplayName, r.CustomDisplayName,
                ToCategory(r.CategoryId, r.CategoryName, r.CategoryClassification, r.CategoryColor),
                r.DefaultCategory,
                r.SecondsActive30d, r.DeviceCount30d, r.CategoryScope))
            .ToList();

        return Ok(new SiteCatalogListResponse(
            items, uncategorizedCount,
            coverage.UncategorizedSecondsActive, coverage.TotalSecondsActive));
    }

    /// <summary>
    /// PUT /api/v1/site-catalog/{siteId}/category (Admin): upsert/remoção do mapeamento.
    /// category_id null = desmapear. Site inexistente, categoria e equipe inexistentes ou de
    /// outro tenant respondem 404. Reagrega os últimos 30 dias e audita update_category.
    /// </summary>
    [HttpPut("{siteId:guid}/category")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> SetCategory(
        Guid siteId, [FromBody] SetSiteCategoryRequest request, CancellationToken ct)
    {
        var customName = string.IsNullOrWhiteSpace(request.CustomDisplayName) ? null : request.CustomDisplayName.Trim();
        if (customName is { Length: > MaxCustomNameLength })
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Nome customizado inválido (máximo {MaxCustomNameLength} caracteres).");

        var tenantId = CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var site = await connection.QuerySingleOrDefaultAsync<SiteRow>(new CommandDefinition(
            "SELECT id AS site_id, domain, display_name FROM site_catalog WHERE id = @SiteId",
            new { SiteId = siteId }, cancellationToken: ct));
        if (site is null) return NotFoundProblem();

        if (request.TeamId is { } scopeTeamId
            && !await AppCatalogController.TeamExistsAsync(connection, null, tenantId, scopeTeamId, ct))
            return NotFoundProblem();

        CategoryRefRow? category = null;
        if (request.CategoryId is { } categoryId)
        {
            category = await connection.QuerySingleOrDefaultAsync<CategoryRefRow>(new CommandDefinition(
                "SELECT id, name, classification, color FROM categories WHERE tenant_id = @TenantId AND id = @Id",
                new { TenantId = tenantId, Id = categoryId }, cancellationToken: ct));
            if (category is null) return NotFoundProblem();
        }

        // estado anterior NO MESMO ESCOPO que está sendo escrito (de→para da trilha)
        var fromCategoryId = request.TeamId is null
            ? await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT category_id FROM tenant_site_categories WHERE tenant_id = @TenantId AND site_id = @SiteId",
                new { TenantId = tenantId, SiteId = siteId }, cancellationToken: ct))
            : await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                """
                SELECT category_id FROM tenant_site_team_categories
                WHERE tenant_id = @TenantId AND site_id = @SiteId AND team_id = @TeamId
                """,
                new { TenantId = tenantId, SiteId = siteId, TeamId = request.TeamId }, cancellationToken: ct));

        await using var tx = await connection.BeginTransactionAsync(ct);

        await WriteMappingAsync(connection, tx, tenantId, siteId, request.CategoryId, request.TeamId, customName, ct);

        // o mapeamento muda os baldes de classificação dos agregados: reagrega 30 dias
        await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);

        await AuditWriter.AddInTransactionAsync(connection, tx, tenantId, AuditActions.UpdateCategory,
            actorUserId: CurrentUser.UserId(User),
            actorIp: HttpContext.Connection.RemoteIpAddress,
            targetType: "site", targetId: siteId,
            detailJson: JsonSerializer.Serialize(new
            {
                site_id = siteId,
                domain = site.Domain,
                from_category_id = fromCategoryId,
                to_category_id = request.CategoryId,
                team_id = request.TeamId,
            }), ct: ct);

        await tx.CommitAsync(ct);

        return Ok(new SiteCategoryMappingResponse(
            site.SiteId, site.Domain, site.DisplayName,
            request.TeamId is null && request.CategoryId is not null ? customName : null,
            category is null
                ? null
                : new AppCategoryResponse(category.Id, category.Name, category.Classification, category.Color),
            request.TeamId));
    }

    /// <summary>
    /// PUT /api/v1/site-catalog/categories/batch (Admin): N mapeamentos site→categoria numa
    /// ÚNICA transação, com UMA ÚNICA reagregação de 30 dias no fim. É o "aplicar todas as
    /// sugestões do dicionário" da tela de Sites — N PUTs individuais custariam N reagregações.
    /// </summary>
    [HttpPut("categories/batch")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> SetCategoriesBatch(
        [FromBody] BatchSiteCategoryRequest? request, CancellationToken ct)
    {
        if (request?.Items is not { Count: > 0 } items)
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Corpo inválido: items deve conter ao menos um mapeamento {site_id, category_id}.");

        if (items.Count > MaxBatchItems)
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Lote muito grande: máximo de {MaxBatchItems} mapeamentos por chamada.");

        if (items.Select(i => i.SiteId).Distinct().Count() != items.Count)
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Lote inválido: há site_id repetido em items.");

        var tenantId = CurrentUser.TenantId(User);
        var userId = CurrentUser.UserId(User);
        var teamId = request.TeamId;

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // ----- validação ANTES da transação: um id ruim não aplica NADA do lote -----
        if (teamId is { } scopeTeamId
            && !await AppCatalogController.TeamExistsAsync(connection, null, tenantId, scopeTeamId, ct))
            return NotFoundProblem();

        var siteIds = items.Select(i => i.SiteId).ToArray();
        var sites = (await connection.QueryAsync<SiteRow>(new CommandDefinition(
            "SELECT id AS site_id, domain, display_name FROM site_catalog WHERE id = ANY(@SiteIds)",
            new { SiteIds = siteIds }, cancellationToken: ct))).ToDictionary(s => s.SiteId);
        if (sites.Count != siteIds.Length) return NotFoundProblem();

        var categoryIds = items.Where(i => i.CategoryId is not null).Select(i => i.CategoryId!.Value).Distinct().ToArray();
        var categories = (await connection.QueryAsync<CategoryRefRow>(new CommandDefinition(
            "SELECT id, name, classification, color FROM categories WHERE tenant_id = @TenantId AND id = ANY(@Ids)",
            new { TenantId = tenantId, Ids = categoryIds }, cancellationToken: ct))).ToDictionary(c => c.Id);
        if (categories.Count != categoryIds.Length) return NotFoundProblem();

        var previous = (await connection.QueryAsync<MappingRow>(new CommandDefinition(
            teamId is null
                ? """
                  SELECT site_id, category_id, custom_display_name
                  FROM tenant_site_categories WHERE tenant_id = @TenantId AND site_id = ANY(@SiteIds)
                  """
                : """
                  SELECT site_id, category_id, NULL::text AS custom_display_name
                  FROM tenant_site_team_categories
                  WHERE tenant_id = @TenantId AND team_id = @TeamId AND site_id = ANY(@SiteIds)
                  """,
            new { TenantId = tenantId, SiteIds = siteIds, TeamId = teamId }, cancellationToken: ct)))
            .ToDictionary(r => r.SiteId);

        var actorIp = HttpContext.Connection.RemoteIpAddress;

        await using var tx = await connection.BeginTransactionAsync(ct);

        foreach (var item in items)
        {
            // custom_display_name fica FORA do lote: um nome custom definido antes pelo PUT
            // individual SOBREVIVE (a decisão do cliente sempre vence).
            await WriteMappingAsync(connection, tx, tenantId, item.SiteId, item.CategoryId, teamId, null, ct);

            await AuditWriter.AddInTransactionAsync(connection, tx, tenantId, AuditActions.UpdateCategory,
                actorUserId: userId,
                actorIp: actorIp,
                targetType: "site", targetId: item.SiteId,
                detailJson: JsonSerializer.Serialize(new
                {
                    site_id = item.SiteId,
                    domain = sites[item.SiteId].Domain,
                    from_category_id = previous.TryGetValue(item.SiteId, out var from) ? from.CategoryId : (Guid?)null,
                    to_category_id = item.CategoryId,
                    team_id = teamId,
                    batch = true,
                }), ct: ct);
        }

        var reaggregationDays = await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);

        await tx.CommitAsync(ct);

        var applied = items.Select(item =>
        {
            var site = sites[item.SiteId];
            var category = item.CategoryId is { } id ? categories[id] : null;
            var customName = item.CategoryId is not null && previous.TryGetValue(item.SiteId, out var before)
                ? before.CustomDisplayName
                : null;
            return new SiteCategoryMappingResponse(
                site.SiteId, site.Domain, site.DisplayName, customName,
                category is null
                    ? null
                    : new AppCategoryResponse(category.Id, category.Name, category.Classification, category.Color),
                teamId);
        }).ToList();

        return Ok(new BatchSiteCategoryResponse(applied.Count, applied, reaggregationDays));
    }

    // ------------------------------------------------------------ helpers
    /// <summary>
    /// Escreve (ou remove) o mapeamento no escopo pedido. Escopo de EQUIPE remove só a regra
    /// dela — o site volta a HERDAR a regra da organização, não vira "sem classificação".
    /// </summary>
    private static async Task WriteMappingAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId, Guid siteId,
        Guid? categoryId, Guid? teamId, string? customName, CancellationToken ct)
    {
        if (teamId is { } teamScope)
        {
            if (categoryId is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    DELETE FROM tenant_site_team_categories
                    WHERE tenant_id = @TenantId AND site_id = @SiteId AND team_id = @TeamId
                    """,
                    new { TenantId = tenantId, SiteId = siteId, TeamId = teamScope },
                    transaction: tx, cancellationToken: ct));
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tenant_site_team_categories (tenant_id, team_id, site_id, category_id)
                    VALUES (@TenantId, @TeamId, @SiteId, @CategoryId)
                    ON CONFLICT (tenant_id, team_id, site_id) DO UPDATE
                    SET category_id = EXCLUDED.category_id
                    """,
                    new { TenantId = tenantId, SiteId = siteId, TeamId = teamScope, CategoryId = categoryId },
                    transaction: tx, cancellationToken: ct));
            }
            return;
        }

        if (categoryId is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tenant_site_categories WHERE tenant_id = @TenantId AND site_id = @SiteId",
                new { TenantId = tenantId, SiteId = siteId }, transaction: tx, cancellationToken: ct));
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tenant_site_categories (tenant_id, site_id, category_id, custom_display_name)
            VALUES (@TenantId, @SiteId, @CategoryId, @CustomName)
            ON CONFLICT (tenant_id, site_id) DO UPDATE
            SET category_id = EXCLUDED.category_id,
                custom_display_name = COALESCE(EXCLUDED.custom_display_name, tenant_site_categories.custom_display_name)
            """,
            new { TenantId = tenantId, SiteId = siteId, CategoryId = categoryId, CustomName = customName },
            transaction: tx, cancellationToken: ct));
    }

    /// <summary>Dia local "hoje" no fuso do tenant (clock injetável para testes).</summary>
    private DateOnly TodayInTenantTz(string timezone)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).DateTime);
    }

    private static AppCategoryResponse? ToCategory(Guid? id, string? name, short? classification, string? color) =>
        id is { } categoryId ? new AppCategoryResponse(categoryId, name!, classification!.Value, color) : null;

    private sealed record CatalogRow(
        Guid SiteId,
        string Domain,
        string DisplayName,
        string? DefaultCategory,
        string? CustomDisplayName,
        Guid? CategoryId,
        string? CategoryName,
        short? CategoryClassification,
        string? CategoryColor,
        string? CategoryScope,
        long SecondsActive30d,
        int DeviceCount30d);

    private sealed record CoverageRow(long TotalSecondsActive, long UncategorizedSecondsActive);

    private sealed record SiteRow(Guid SiteId, string Domain, string DisplayName);

    private sealed record CategoryRefRow(Guid Id, string Name, short Classification, string? Color);

    private sealed record MappingRow(Guid SiteId, Guid CategoryId, string? CustomDisplayName);
}
