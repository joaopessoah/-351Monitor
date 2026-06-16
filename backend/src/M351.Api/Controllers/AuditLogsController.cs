using System.Text.Json;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// GET /api/v1/audit-logs (F4.7, Seção 9.5 / endpoint 7.4 l.810): trilha de auditoria do tenant
/// para a tela /configuracoes/auditoria. Owner+Admin (PolicyAdminPlus) — Viewer não vê.
///
/// Filtros: from/to (datas no fuso do TENANT, inclusivas; default últimos 30 dias; range máx. 92
/// dias com a MESMA régua/ProblemDetails do dashboard), actor (user_id exato, opcional), action
/// (string exata, opcional). Ordenado occurred_at desc. page_size default 50, máx. 100.
///
/// DECISÃO DOCUMENTADA (contrato 1, anti-recursão): a leitura de /audit-logs NÃO se audita — não
/// tem [AuditRead] nem grava trilha. Auditar quem lê a trilha geraria recursão (cada abertura da
/// tela criaria uma linha que, relida, criaria outra) sem ganho de prestação de contas: o acesso
/// à tela de auditoria já é por papel (Admin/Owner) e a própria trilha é a evidência das AÇÕES,
/// não da sua leitura.
///
/// actor_name vem do LEFT JOIN com users (display_name → email): a trilha guarda só o actor_user_id;
/// a tela mostra nome. null = ação de sistema/CLI (sob tenant-sentinela, fora deste tenant) ou ator
/// já removido. detail é devolvido como o jsonb cru (objeto), não string.
/// </summary>
[Route("api/v1/audit-logs")]
[Authorize(Policy = AuthConstants.PolicyAdminPlus)] // Owner + Admin
public class AuditLogsController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>Janela default quando from/to ausentes: últimos 30 dias (inclusivo).</summary>
    public const int DefaultWindowDays = 30;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "actor")] Guid? actor,
        [FromQuery(Name = "action")] string? action,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var timezone = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        if (timezone is null) return NotFoundProblem();

        // default: últimos 30 dias no fuso do tenant (hoje inclusive)
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);

        DateOnly fromDay, toDay;
        if (from is null && to is null)
        {
            toDay = today;
            fromDay = today.AddDays(-(DefaultWindowDays - 1));
        }
        else
        {
            // mesma régua do dashboard (from <= to, janela <= 92 dias, yyyy-MM-dd) com ProblemDetails
            var invalid = ValidateRange(from, to, out fromDay, out toDay);
            if (invalid is not null) return invalid;
        }

        // janela [from 00:00 tenant, (to+1) 00:00 tenant) em UTC — occurred_at é timestamptz (UTC)
        var windowStart = LocalMidnightUtc(fromDay, tz);
        var windowEnd = LocalMidnightUtc(toDay.AddDays(1), tz);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var args = new
        {
            TenantId = tenantId,
            Start = windowStart,
            End = windowEnd,
            FilterActor = actor is not null,
            Actor = actor ?? Guid.Empty,
            FilterAction = !string.IsNullOrWhiteSpace(action),
            Action = action,
            Limit = pageSize,
            Offset = (page - 1) * pageSize,
        };

        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT count(*)::bigint
            FROM audit_log a
            WHERE a.tenant_id = @TenantId
              AND a.occurred_at >= @Start AND a.occurred_at < @End
              AND (@FilterActor = false OR a.actor_user_id = @Actor)
              AND (@FilterAction = false OR a.action = @Action)
            """,
            args, cancellationToken: ct));

        var rows = (await connection.QueryAsync<AuditRow>(new CommandDefinition(
            """
            SELECT a.id, a.occurred_at, a.actor_user_id,
                   COALESCE(u.display_name, u.email::text) AS actor_name,
                   host(a.actor_ip) AS actor_ip,
                   a.action, a.target_type, a.target_id,
                   a.detail::text AS detail
            FROM audit_log a
            LEFT JOIN users u ON u.tenant_id = a.tenant_id AND u.id = a.actor_user_id
            WHERE a.tenant_id = @TenantId
              AND a.occurred_at >= @Start AND a.occurred_at < @End
              AND (@FilterActor = false OR a.actor_user_id = @Actor)
              AND (@FilterAction = false OR a.action = @Action)
            ORDER BY a.occurred_at DESC, a.id DESC
            LIMIT @Limit OFFSET @Offset
            """,
            args, cancellationToken: ct))).ToList();

        var items = rows.Select(r => new AuditLogItemResponse(
                r.Id, r.OccurredAt, r.ActorUserId, r.ActorName, r.ActorIp,
                r.Action, r.TargetType, r.TargetId,
                // detail jsonb → objeto (não string) na resposta; null vira null
                r.Detail is null ? null : JsonSerializer.Deserialize<JsonElement>(r.Detail)))
            .ToList();

        return Ok(new AuditLogListResponse(items, total, page, pageSize));
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed record AuditRow(
        Guid Id,
        DateTimeOffset OccurredAt,
        Guid? ActorUserId,
        string? ActorName,
        string? ActorIp,
        string Action,
        string? TargetType,
        Guid? TargetId,
        string? Detail);
}
