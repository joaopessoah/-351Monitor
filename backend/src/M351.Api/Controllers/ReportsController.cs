using System.Text.Json;
using Dapper;
using M351.Api.Auditing;
using M351.Api.Contracts;
using M351.Domain.Entities;
using M351.Infrastructure.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// GET /api/v1/reports/usage (F3.3, Seção 7.4): relatório tabular paginado de uso por
/// app | category | device | device_user. Fontes: daily_app_usage (app/category) e
/// daily_device_summaries (device/device_user). Devices archived SEMPRE fora (spec linha
/// 954). Datas com a mesma régua do dashboard (fuso do tenant, máx. 92 dias). Ordenação
/// fixa por seconds_active desc (o portal reordena client-side; "quem ficou mais tempo
/// ocioso" sai do group_by=device pelos seconds_idle).
///
/// Auditoria (DoD 11.3): view_report QUANDO group_by=device|device_user OU device_ids
/// presente (dado pessoal identificável); app/category sem filtro são agregados de equipe.
/// </summary>
[Route("api/v1/reports")]
[Authorize] // Viewer+
public class ReportsController(
    NpgsqlDataSource dataSource,
    AuditReadContext readAudit) : ApiControllerBase
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>Compartilhado com a validação de params do POST /exports (F3.5).</summary>
    internal static readonly string[] ValidGroupBys = ["app", "category", "device", "device_user"];

    /// <summary>UUID zero = lane-máquina (spec linha 652): intervalos sem sessão de usuário.</summary>
    private static readonly Guid MachineLane = Guid.Empty;

    [HttpGet("usage")]
    [AuditRead] // DoD 11.3: view_report CONDICIONAL (device/device_user/device_ids) via AuditReadFilter
    public async Task<IActionResult> Usage(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "group_by")] string? groupBy,
        [FromQuery(Name = "device_ids")] string? deviceIdsRaw,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var invalid = ValidateRange(from, to, out _, out _);
        if (invalid is not null) return invalid;

        if (groupBy is null || !ValidGroupBys.Contains(groupBy))
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Parâmetro group_by é obrigatório: app, category, device ou device_user.");

        Guid[]? deviceIds = null;
        if (!string.IsNullOrWhiteSpace(deviceIdsRaw))
        {
            var parts = deviceIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = new List<Guid>(parts.Length);
            foreach (var part in parts)
            {
                if (!Guid.TryParse(part, out var deviceId))
                    return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro device_ids deve ser uma lista de UUIDs separados por vírgula.");
                parsed.Add(deviceId);
            }
            deviceIds = parsed.Distinct().ToArray();
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // filtro aponta para recursos: QUALQUER id inexistente ou de outro tenant responde 404
        // (mesmo gate do dashboard/summary com device_id de B — Princípio 4, nunca 403)
        if (deviceIds is { Length: > 0 })
        {
            var found = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM devices WHERE tenant_id = @TenantId AND id = ANY(@DeviceIds)",
                new { TenantId = tenantId, DeviceIds = deviceIds }, cancellationToken: ct));
            if (found != deviceIds.Length) return NotFoundProblem();
        }

        // flag + array vazio em vez de array nullable: o Npgsql não infere o tipo de um
        // uuid[] nulo via Dapper
        var args = new
        {
            TenantId = tenantId,
            From = from,
            To = to,
            FilterDevices = deviceIds is { Length: > 0 },
            DeviceIds = deviceIds ?? [],
            Limit = pageSize,
            Offset = (page - 1) * pageSize,
        };

        object response = groupBy switch
        {
            "app" => await UsageByAppAsync(connection, args, page, pageSize, ct),
            "category" => await UsageByCategoryAsync(connection, args, page, pageSize, ct),
            "device" => await UsageByDeviceAsync(connection, args, page, pageSize, ct),
            _ => await UsageByDeviceUserAsync(connection, args, page, pageSize, ct),
        };

        // DoD 11.3: por device/device_user (ou com filtro de devices) é dado pessoal
        // identificável → view_report. Alvo: o device quando o filtro é um só; senão "team"
        // (mesma convenção do timeline/team — o tenant já está em tenant_id). Gravação consolidada
        // no AuditReadFilter (após o 2xx, com actor_ip); CONDICIONAL: só registra quando o recorte
        // é pessoal (app/category sem filtro são agregados de equipe e não auditam).
        if (groupBy is "device" or "device_user" || deviceIds is { Length: > 0 })
        {
            readAudit.Record(tenantId, AuditActions.ViewReport,
                Auth.CurrentUser.UserId(User),
                targetType: deviceIds is { Length: 1 } ? "device" : "team",
                targetId: deviceIds is { Length: 1 } ? deviceIds[0] : null,
                detailJson: JsonSerializer.Serialize(new { from, to, group_by = groupBy, device_ids = deviceIds }));
        }

        return Ok(response);
    }

    /// <summary>
    /// GET /api/v1/reports/jornada (F3.5, Seções 7.4/8.6): uma linha por device × dia do
    /// RANGE INTEIRO — dias sem dados também viram linha, com observação (spec linha 947).
    /// SQL canônico em JornadaReportSql (Infrastructure), COMPARTILHADO com o CSV assíncrono:
    /// arquivo e tela saem da mesma query (DoD 11.3). Mesma régua de datas do dashboard
    /// (fuso do tenant, máx. 92 dias); page_size default 50, máx. 100.
    ///
    /// Decisões documentadas (silêncios da spec):
    ///  - devices archived FORA por default; device_ids EXPLÍCITO inclui archived (o gestor
    ///    pediu aquele histórico — toggle "incluir arquivados" do portal); o gate de tenant
    ///    continua: qualquer id de outro tenant → 404;
    ///  - auditoria: view_report SEMPRE (jornada é dado pessoal mesmo sem filtro — lista
    ///    nomes de usuário por dia), detail {from, to, device_ids};
    ///  - device_totals respondem pelo range inteiro, independente da página.
    /// </summary>
    [HttpGet("jornada")]
    [AuditRead] // DoD 11.3: jornada é SEMPRE dado pessoal — view_report incondicional via AuditReadFilter
    public async Task<IActionResult> Jornada(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "device_ids")] string? deviceIdsRaw,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
        if (invalid is not null) return invalid;

        Guid[]? deviceIds = null;
        if (!string.IsNullOrWhiteSpace(deviceIdsRaw))
        {
            var parts = deviceIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = new List<Guid>(parts.Length);
            foreach (var part in parts)
            {
                if (!Guid.TryParse(part, out var deviceId))
                    return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro device_ids deve ser uma lista de UUIDs separados por vírgula.");
                parsed.Add(deviceId);
            }
            deviceIds = parsed.Distinct().ToArray();
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // mesmo gate do usage: id inexistente OU de outro tenant → 404 (nunca 403)
        if (deviceIds is { Length: > 0 })
        {
            var found = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM devices WHERE tenant_id = @TenantId AND id = ANY(@DeviceIds)",
                new { TenantId = tenantId, DeviceIds = deviceIds }, cancellationToken: ct));
            if (found != deviceIds.Length) return NotFoundProblem();
        }

        var args = new
        {
            TenantId = tenantId,
            From = from,
            To = to,
            FilterDevices = deviceIds is { Length: > 0 },
            DeviceIds = deviceIds ?? [],
            Limit = pageSize,
            Offset = (page - 1) * pageSize,
        };

        var rows = (await connection.QueryAsync<JornadaRow>(new CommandDefinition(
            $"{JornadaReportSql.Rows}\nLIMIT @Limit OFFSET @Offset",
            args, cancellationToken: ct))).ToList();

        // total da paginação SEM materializar o produto: devices do recorte × dias do range
        var deviceCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            JornadaReportSql.DeviceCount, args, cancellationToken: ct));
        var total = deviceCount * (toDay.DayNumber - fromDay.DayNumber + 1);

        var deviceTotals = (await connection.QueryAsync<JornadaTotalsRow>(new CommandDefinition(
            JornadaReportSql.DeviceTotals, args, cancellationToken: ct))).ToList();

        var response = new JornadaReportResponse(
            rows.Select(r => new JornadaRowResponse(
                r.Date, r.DeviceId, r.DeviceName, r.Users, r.FirstEventAt, r.LastEventAt,
                r.SecondsOn, r.SecondsActive, r.SecondsIdle, r.SecondsLocked, r.Note)).ToList(),
            total, page, pageSize,
            deviceTotals.Select(t => new JornadaDeviceTotalsResponse(
                t.DeviceId, t.DeviceName, t.SecondsOn, t.SecondsActive, t.SecondsIdle,
                t.SecondsLocked, t.DaysWithData)).ToList());

        // DoD 11.3: jornada é SEMPRE dado pessoal → view_report incondicional. Gravação
        // consolidada no AuditReadFilter (após o 2xx, com actor_ip preenchido).
        readAudit.Record(tenantId, AuditActions.ViewReport,
            Auth.CurrentUser.UserId(User),
            targetType: deviceIds is { Length: 1 } ? "device" : "team",
            targetId: deviceIds is { Length: 1 } ? deviceIds[0] : null,
            detailJson: JsonSerializer.Serialize(new { from, to, device_ids = deviceIds }));

        return Ok(response);
    }

    // ------------------------------------------------------------ group_by=app
    private static async Task<UsageReportResponse<UsageByAppItemResponse>> UsageByAppAsync(
        NpgsqlConnection connection, object args, int page, int pageSize, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<AppGroupRow>(new CommandDefinition(
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
              AND (@FilterDevices = false OR u.device_id = ANY(@DeviceIds))
            GROUP BY u.app_id, a.process_name, a.display_name, tac.custom_display_name,
                     c.id, c.name, c.classification, c.color
            ORDER BY seconds_active DESC, a.process_name
            LIMIT @Limit OFFSET @Offset
            """,
            args, cancellationToken: ct))).ToList();

        var totals = await connection.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            """
            SELECT count(DISTINCT u.app_id)::int AS total,
                   COALESCE(sum(u.seconds_active), 0)::bigint AS total_seconds_active
            FROM daily_app_usage u
            JOIN devices d ON d.id = u.device_id AND d.tenant_id = u.tenant_id
            WHERE u.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND u.summary_date BETWEEN @From::date AND @To::date
              AND (@FilterDevices = false OR u.device_id = ANY(@DeviceIds))
            """,
            args, cancellationToken: ct));

        var items = rows.Select(r => new UsageByAppItemResponse(
                r.AppId, r.ProcessName, r.DisplayName, r.CustomDisplayName,
                r.CategoryId is { } categoryId
                    ? new AppCategoryResponse(categoryId, r.CategoryName!, r.CategoryClassification!.Value, r.CategoryColor)
                    : null,
                r.SecondsActive, r.DeviceCount))
            .ToList();

        return new UsageReportResponse<UsageByAppItemResponse>(
            items, totals.Total, page, pageSize, totals.TotalSecondsActive);
    }

    // ------------------------------------------------------------ group_by=category
    private static async Task<UsageReportResponse<UsageByCategoryItemResponse>> UsageByCategoryAsync(
        NpgsqlConnection connection, object args, int page, int pageSize, CancellationToken ct)
    {
        const string grouped = """
            SELECT c.id AS category_id, c.name, c.classification, c.color,
                   sum(u.seconds_active)::bigint AS seconds_active,
                   count(DISTINCT u.app_id)::int AS app_count
            FROM daily_app_usage u
            JOIN devices d ON d.id = u.device_id AND d.tenant_id = u.tenant_id
            LEFT JOIN tenant_app_categories tac ON tac.tenant_id = u.tenant_id AND tac.app_id = u.app_id
            LEFT JOIN categories c ON c.tenant_id = u.tenant_id AND c.id = tac.category_id
            WHERE u.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND u.summary_date BETWEEN @From::date AND @To::date
              AND (@FilterDevices = false OR u.device_id = ANY(@DeviceIds))
            GROUP BY c.id, c.name, c.classification, c.color
            """;

        // balde c.id NULL = "Não categorizado" (apps sem mapeamento no tenant)
        var rows = (await connection.QueryAsync<CategoryGroupRow>(new CommandDefinition(
            $"{grouped}\nORDER BY seconds_active DESC, name NULLS LAST\nLIMIT @Limit OFFSET @Offset",
            args, cancellationToken: ct))).ToList();

        var totals = await connection.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            $"SELECT count(*)::int AS total, COALESCE(sum(g.seconds_active), 0)::bigint AS total_seconds_active FROM ({grouped}) g",
            args, cancellationToken: ct));

        var items = rows.Select(r => new UsageByCategoryItemResponse(
                r.CategoryId, r.Name, r.Classification, r.Color, r.SecondsActive, r.AppCount))
            .ToList();

        return new UsageReportResponse<UsageByCategoryItemResponse>(
            items, totals.Total, page, pageSize, totals.TotalSecondsActive);
    }

    // ------------------------------------------------------------ group_by=device
    private static async Task<UsageReportResponse<UsageByDeviceItemResponse>> UsageByDeviceAsync(
        NpgsqlConnection connection, object args, int page, int pageSize, CancellationToken ct)
    {
        const string grouped = """
            SELECT s.device_id,
                   COALESCE(d.display_name, d.hostname) AS device_name,
                   sum(s.seconds_active)::bigint AS seconds_active,
                   sum(s.seconds_idle)::bigint AS seconds_idle,
                   sum(s.seconds_locked)::bigint AS seconds_locked,
                   sum(s.seconds_on)::bigint AS seconds_on,
                   sum(s.seconds_work_related)::bigint AS seconds_work_related,
                   sum(s.seconds_neutral)::bigint AS seconds_neutral,
                   sum(s.seconds_not_work_related)::bigint AS seconds_not_work_related
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @From::date AND @To::date
              AND (@FilterDevices = false OR s.device_id = ANY(@DeviceIds))
            GROUP BY s.device_id, COALESCE(d.display_name, d.hostname)
            """;

        var rows = (await connection.QueryAsync<DeviceGroupRow>(new CommandDefinition(
            $"{grouped}\nORDER BY seconds_active DESC, device_name\nLIMIT @Limit OFFSET @Offset",
            args, cancellationToken: ct))).ToList();

        var totals = await connection.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            $"SELECT count(*)::int AS total, COALESCE(sum(g.seconds_active), 0)::bigint AS total_seconds_active FROM ({grouped}) g",
            args, cancellationToken: ct));

        var items = rows.Select(r => new UsageByDeviceItemResponse(
                r.DeviceId, r.DeviceName, r.SecondsActive, r.SecondsIdle, r.SecondsLocked, r.SecondsOn,
                r.SecondsWorkRelated, r.SecondsNeutral, r.SecondsNotWorkRelated))
            .ToList();

        return new UsageReportResponse<UsageByDeviceItemResponse>(
            items, totals.Total, page, pageSize, totals.TotalSecondsActive);
    }

    // ------------------------------------------------------------ group_by=device_user
    private static async Task<UsageReportResponse<UsageByDeviceUserItemResponse>> UsageByDeviceUserAsync(
        NpgsqlConnection connection, object args, int page, int pageSize, CancellationToken ct)
    {
        const string grouped = """
            SELECT s.device_user_id, s.device_id,
                   COALESCE(d.display_name, d.hostname) AS device_name,
                   du.windows_username AS windows_user,
                   du.display_name AS user_display_name,
                   sum(s.seconds_active)::bigint AS seconds_active,
                   sum(s.seconds_idle)::bigint AS seconds_idle,
                   sum(s.seconds_locked)::bigint AS seconds_locked,
                   sum(s.seconds_on)::bigint AS seconds_on,
                   sum(s.seconds_work_related)::bigint AS seconds_work_related,
                   sum(s.seconds_neutral)::bigint AS seconds_neutral,
                   sum(s.seconds_not_work_related)::bigint AS seconds_not_work_related
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            LEFT JOIN device_users du ON du.tenant_id = s.tenant_id AND du.id = s.device_user_id
            WHERE s.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @From::date AND @To::date
              AND (@FilterDevices = false OR s.device_id = ANY(@DeviceIds))
            GROUP BY s.device_user_id, s.device_id, COALESCE(d.display_name, d.hostname),
                     du.windows_username, du.display_name
            """;

        var rows = (await connection.QueryAsync<DeviceUserGroupRow>(new CommandDefinition(
            $"{grouped}\nORDER BY seconds_active DESC, device_name, s.device_user_id\nLIMIT @Limit OFFSET @Offset",
            args, cancellationToken: ct))).ToList();

        var totals = await connection.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            $"SELECT count(*)::int AS total, COALESCE(sum(g.seconds_active), 0)::bigint AS total_seconds_active FROM ({grouped}) g",
            args, cancellationToken: ct));

        var items = rows.Select(r => new UsageByDeviceUserItemResponse(
                r.DeviceUserId, r.DeviceId, r.DeviceName, r.WindowsUser,
                DeviceUserDisplayName(r),
                r.SecondsActive, r.SecondsIdle, r.SecondsLocked, r.SecondsOn,
                r.SecondsWorkRelated, r.SecondsNeutral, r.SecondsNotWorkRelated))
            .ToList();

        return new UsageReportResponse<UsageByDeviceUserItemResponse>(
            items, totals.Total, page, pageSize, totals.TotalSecondsActive);
    }

    /// <summary>
    /// Nome de exibição da lane: UUID zero = lane-máquina; usuário sem linha em device_users
    /// (caso raro: titular removido via DSR) recebe rótulo neutro.
    /// </summary>
    private static string DeviceUserDisplayName(DeviceUserGroupRow r) =>
        r.DeviceUserId == MachineLane
            ? "Máquina (sem usuário)"
            : r.UserDisplayName ?? r.WindowsUser ?? "Usuário desconhecido";

    // ------------------------------------------------------------ rows
    private sealed record TotalsRow(int Total, long TotalSecondsActive);

    private sealed record AppGroupRow(
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

    private sealed record CategoryGroupRow(
        Guid? CategoryId,
        string? Name,
        short? Classification,
        string? Color,
        long SecondsActive,
        int AppCount);

    private sealed record DeviceGroupRow(
        Guid DeviceId,
        string DeviceName,
        long SecondsActive,
        long SecondsIdle,
        long SecondsLocked,
        long SecondsOn,
        long SecondsWorkRelated,
        long SecondsNeutral,
        long SecondsNotWorkRelated);

    private sealed record DeviceUserGroupRow(
        Guid DeviceUserId,
        Guid DeviceId,
        string DeviceName,
        string? WindowsUser,
        string? UserDisplayName,
        long SecondsActive,
        long SecondsIdle,
        long SecondsLocked,
        long SecondsOn,
        long SecondsWorkRelated,
        long SecondsNeutral,
        long SecondsNotWorkRelated);

    private sealed record JornadaRow(
        string Date,
        Guid DeviceId,
        string DeviceName,
        string? Users,
        DateTimeOffset? FirstEventAt,
        DateTimeOffset? LastEventAt,
        long SecondsOn,
        long SecondsActive,
        long SecondsIdle,
        long SecondsLocked,
        string? Note);

    private sealed record JornadaTotalsRow(
        Guid DeviceId,
        string DeviceName,
        long SecondsOn,
        long SecondsActive,
        long SecondsIdle,
        long SecondsLocked,
        int DaysWithData);
}
