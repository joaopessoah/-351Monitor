using System.Text.Json;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain.Entities;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// Catálogo de apps na visão do TENANT (F3.3, Seção 7.4). O catálogo (app_catalog) é GLOBAL,
/// mas a LISTAGEM é o recorte do tenant (decisão documentada): união dos apps com uso em
/// daily_app_usage do tenant com os apps mapeados em tenant_app_categories do tenant.
/// A listagem NÃO exclui devices archived (decisão documentada: esta é a tela de MAPEAMENTO,
/// não um dashboard; o histórico de devices arquivados continua mapeável).
///
/// PUT do mapeamento reagrega os últimos 30 dias e audita update_category na MESMA transação
/// da mutação (o mapeamento vale para o tenant inteiro, spec Seção 8.6). Drill-down de títulos
/// SEMPRE audita view_report (dado pessoal, spec linha 1004).
/// </summary>
[Route("api/v1/app-catalog")]
[Authorize] // Viewer+ nas leituras; o PUT exige AdminPlus
public class AppCatalogController(
    NpgsqlDataSource dataSource,
    M351DbContext db,
    AuditWriter audit,
    TimeProvider clock) : ApiControllerBase
{
    /// <summary>Teto da listagem (decisão documentada: a tela tem busca; paginação fica para v1.1).</summary>
    public const int MaxItems = 500;

    public const int TopTitles = 20;
    private const int MaxCustomNameLength = 200;

    /// <summary>
    /// GET /api/v1/app-catalog?q=&amp;uncategorized=true (Viewer): recorte do tenant, ordenado
    /// por seconds_active_30d desc, máximo 500 itens. Janela 30d = hoje no fuso do tenant
    /// menos 30 dias (mesmo corte da reagregação). q busca em process_name/display_name/
    /// custom_display_name (ILIKE). uncategorized_count ignora os filtros (badge global).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "uncategorized")] bool uncategorized = false,
        CancellationToken ct = default)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var timezone = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        if (timezone is null) return NotFoundProblem();

        // string yyyy-MM-dd + cast ::date no SQL — mesmo padrão de datas do dashboard F3.2
        var cutoff = TodayInTenantTz(timezone).AddDays(-ReaggregationRequester.WindowDays).ToString("yyyy-MM-dd");
        var pattern = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%";

        var rows = (await connection.QueryAsync<CatalogRow>(new CommandDefinition(
            """
            WITH usage_30d AS (
                SELECT u.app_id,
                       sum(u.seconds_active)::bigint AS seconds_active_30d,
                       count(DISTINCT u.device_id)::int AS device_count_30d
                FROM daily_app_usage u
                WHERE u.tenant_id = @TenantId AND u.summary_date >= @Cutoff::date
                GROUP BY u.app_id
            ), known AS (
                -- recorte do tenant: apps com uso registrado OU mapeados pelo tenant
                SELECT DISTINCT u.app_id FROM daily_app_usage u WHERE u.tenant_id = @TenantId
                UNION
                SELECT tac.app_id FROM tenant_app_categories tac WHERE tac.tenant_id = @TenantId
            )
            SELECT a.id AS app_id, a.process_name, a.display_name, tac.custom_display_name,
                   c.id AS category_id, c.name AS category_name,
                   c.classification AS category_classification, c.color AS category_color,
                   COALESCE(u.seconds_active_30d, 0) AS seconds_active_30d,
                   COALESCE(u.device_count_30d, 0) AS device_count_30d
            FROM known k
            JOIN app_catalog a ON a.id = k.app_id
            LEFT JOIN usage_30d u ON u.app_id = k.app_id
            LEFT JOIN tenant_app_categories tac ON tac.tenant_id = @TenantId AND tac.app_id = k.app_id
            LEFT JOIN categories c ON c.tenant_id = @TenantId AND c.id = tac.category_id
            WHERE (@Pattern::text IS NULL
                   OR a.process_name ILIKE @Pattern
                   OR a.display_name ILIKE @Pattern
                   OR tac.custom_display_name ILIKE @Pattern)
              AND (@UncategorizedOnly = false OR tac.category_id IS NULL)
            ORDER BY seconds_active_30d DESC, a.process_name
            LIMIT @Limit
            """,
            new { TenantId = tenantId, Cutoff = cutoff, Pattern = pattern, UncategorizedOnly = uncategorized, Limit = MaxItems },
            cancellationToken: ct))).ToList();

        // badge "N apps sem categoria": recorte inteiro, sem os filtros da listagem.
        // Apps mapeados têm categoria por definição, então basta varrer o lado do uso.
        var uncategorizedCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(DISTINCT u.app_id)::int
            FROM daily_app_usage u
            WHERE u.tenant_id = @TenantId
              AND NOT EXISTS (
                  SELECT 1 FROM tenant_app_categories tac
                  WHERE tac.tenant_id = u.tenant_id AND tac.app_id = u.app_id)
            """,
            new { TenantId = tenantId }, cancellationToken: ct));

        var items = rows.Select(r => new AppCatalogItemResponse(
                r.AppId, r.ProcessName, r.DisplayName, r.CustomDisplayName,
                ToCategory(r.CategoryId, r.CategoryName, r.CategoryClassification, r.CategoryColor),
                r.SecondsActive30d, r.DeviceCount30d))
            .ToList();

        return Ok(new AppCatalogListResponse(items, uncategorizedCount));
    }

    /// <summary>
    /// PUT /api/v1/app-catalog/{appId}/category (Admin): upsert/remoção do mapeamento do
    /// TENANT. category_id null = desmapear (a linha sai inteira, custom_display_name junto).
    /// Categoria de outro tenant/inexistente e app inexistente respondem 404. Sempre reagrega
    /// os últimos 30 dias (contrato F3.3) e audita update_category.
    /// </summary>
    [HttpPut("{appId:guid}/category")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> SetCategory(
        Guid appId, [FromBody] SetAppCategoryRequest request, CancellationToken ct)
    {
        var customName = string.IsNullOrWhiteSpace(request.CustomDisplayName) ? null : request.CustomDisplayName.Trim();
        if (customName is { Length: > MaxCustomNameLength })
            return ProblemResponse(StatusCodes.Status400BadRequest, $"Nome customizado inválido (máximo {MaxCustomNameLength} caracteres).");

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var app = await connection.QuerySingleOrDefaultAsync<AppRow>(new CommandDefinition(
            "SELECT id AS app_id, process_name, display_name FROM app_catalog WHERE id = @AppId",
            new { AppId = appId }, cancellationToken: ct));
        if (app is null) return NotFoundProblem(); // app desconhecido do catálogo global

        CategoryRefRow? category = null;
        if (request.CategoryId is { } categoryId)
        {
            category = await connection.QuerySingleOrDefaultAsync<CategoryRefRow>(new CommandDefinition(
                "SELECT id, name, classification, color FROM categories WHERE tenant_id = @TenantId AND id = @Id",
                new { TenantId = tenantId, Id = categoryId }, cancellationToken: ct));
            if (category is null) return NotFoundProblem(); // inexistente OU de outro tenant
        }

        var fromCategoryId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT category_id FROM tenant_app_categories WHERE tenant_id = @TenantId AND app_id = @AppId",
            new { TenantId = tenantId, AppId = appId }, cancellationToken: ct));

        await using var tx = await connection.BeginTransactionAsync(ct);

        if (request.CategoryId is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tenant_app_categories WHERE tenant_id = @TenantId AND app_id = @AppId",
                new { TenantId = tenantId, AppId = appId }, transaction: tx, cancellationToken: ct));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO tenant_app_categories (tenant_id, app_id, category_id, custom_display_name)
                VALUES (@TenantId, @AppId, @CategoryId, @CustomName)
                ON CONFLICT (tenant_id, app_id) DO UPDATE
                SET category_id = EXCLUDED.category_id, custom_display_name = EXCLUDED.custom_display_name
                """,
                new { TenantId = tenantId, AppId = appId, CategoryId = request.CategoryId, CustomName = customName },
                transaction: tx, cancellationToken: ct));
        }

        // o mapeamento muda os baldes de classificação dos agregados: reagrega 30 dias
        await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);

        // trilha na MESMA transação da mutação: o mapeamento jamais persiste sem audit
        await AuditWriter.AddInTransactionAsync(connection, tx, tenantId, AuditActions.UpdateCategory,
            actorUserId: Auth.CurrentUser.UserId(User),
            targetType: "app", targetId: appId,
            detailJson: JsonSerializer.Serialize(new
            {
                app_id = appId,
                process_name = app.ProcessName,
                from_category_id = fromCategoryId,
                to_category_id = request.CategoryId,
            }), ct: ct);

        await tx.CommitAsync(ct);

        return Ok(new AppCategoryMappingResponse(
            app.AppId, app.ProcessName, app.DisplayName,
            request.CategoryId is null ? null : customName,
            category is null
                ? null
                : new AppCategoryResponse(category.Id, category.Name, category.Classification, category.Color)));
    }

    /// <summary>
    /// GET /api/v1/app-catalog/{appId}/titles?from&amp;to (Viewer): top 20 títulos por tempo
    /// ativo do app + masked_seconds (window_title NULL) + total_seconds. Fonte:
    /// activity_intervals (não há agregado de títulos; rota nova documentada para o silêncio
    /// da spec). Devices archived ficam fora (consistência com /reports/usage, de onde o
    /// drill-down parte). SEMPRE audita view_report (dado pessoal, spec linha 1004).
    /// </summary>
    [HttpGet("{appId:guid}/titles")]
    public async Task<IActionResult> Titles(
        Guid appId,
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        CancellationToken ct)
    {
        var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
        if (invalid is not null) return invalid;

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var appExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app_catalog WHERE id = @AppId)",
            new { AppId = appId }, cancellationToken: ct));
        if (!appExists) return NotFoundProblem();

        var timezone = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        if (timezone is null) return NotFoundProblem();

        // janela [from 00:00, to+1 00:00) no fuso do tenant — mesmo corte da timeline; o
        // filtro em started_at é a chave de partição (pruning) e o worker já divide os
        // intervalos na meia-noite do tenant (nada cruza a borda)
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var windowStart = LocalMidnightUtc(fromDay, tz);
        var windowEnd = LocalMidnightUtc(toDay.AddDays(1), tz);

        var totals = await connection.QuerySingleAsync<TitleTotalsRow>(new CommandDefinition(
            """
            SELECT floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                       FILTER (WHERE i.window_title IS NULL), 0))::bigint AS masked_seconds,
                   floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at)), 0))::bigint AS total_seconds
            FROM activity_intervals i
            JOIN devices d ON d.id = i.device_id AND d.tenant_id = i.tenant_id
            WHERE i.tenant_id = @TenantId AND i.app_id = @AppId AND i.state = 'active'
              AND i.started_at >= @Start AND i.started_at < @End
              AND d.status <> 'archived'
            """,
            new { TenantId = tenantId, AppId = appId, Start = windowStart, End = windowEnd },
            cancellationToken: ct));

        var rows = (await connection.QueryAsync<TitleRow>(new CommandDefinition(
            """
            SELECT i.window_title,
                   floor(sum(extract(epoch FROM i.ended_at - i.started_at)))::bigint AS seconds_active
            FROM activity_intervals i
            JOIN devices d ON d.id = i.device_id AND d.tenant_id = i.tenant_id
            WHERE i.tenant_id = @TenantId AND i.app_id = @AppId AND i.state = 'active'
              AND i.started_at >= @Start AND i.started_at < @End
              AND i.window_title IS NOT NULL
              AND d.status <> 'archived'
            GROUP BY i.window_title
            ORDER BY seconds_active DESC, i.window_title
            LIMIT @Limit
            """,
            new { TenantId = tenantId, AppId = appId, Start = windowStart, End = windowEnd, Limit = TopTitles },
            cancellationToken: ct))).ToList();

        // DoD 11.3 / spec linha 1004: drill-down de apps é dado pessoal — audita SEMPRE
        audit.Add(tenantId, AuditActions.ViewReport,
            actorUserId: Auth.CurrentUser.UserId(User),
            targetType: "app", targetId: appId,
            detailJson: JsonSerializer.Serialize(new { app_id = appId, from, to }));
        await db.SaveChangesAsync(ct);

        return Ok(new AppTitlesResponse(
            rows.Select(r => new AppTitleResponse(r.WindowTitle, r.SecondsActive)).ToList(),
            totals.MaskedSeconds,
            totals.TotalSeconds));
    }

    // ------------------------------------------------------------ helpers
    /// <summary>Dia local "hoje" no fuso do tenant (clock injetável para testes).</summary>
    private DateOnly TodayInTenantTz(string timezone)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).DateTime);
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private static AppCategoryResponse? ToCategory(Guid? id, string? name, short? classification, string? color) =>
        id is { } categoryId ? new AppCategoryResponse(categoryId, name!, classification!.Value, color) : null;

    private sealed record CatalogRow(
        Guid AppId,
        string ProcessName,
        string DisplayName,
        string? CustomDisplayName,
        Guid? CategoryId,
        string? CategoryName,
        short? CategoryClassification,
        string? CategoryColor,
        long SecondsActive30d,
        int DeviceCount30d);

    private sealed record AppRow(Guid AppId, string ProcessName, string DisplayName);

    private sealed record CategoryRefRow(Guid Id, string Name, short Classification, string? Color);

    private sealed record TitleTotalsRow(long MaskedSeconds, long TotalSeconds);

    private sealed record TitleRow(string WindowTitle, long SecondsActive);
}
