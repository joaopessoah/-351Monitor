using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using M351.Api.Auditing;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// GET /api/v1/timeline/device?device_id&date (Seção 7.4): intervalos do dia de um device,
/// resolução fixa de 1 min server-side com cap ~3.000 (N21), data_incomplete propagado,
/// fuso do device para o badge de divergência. Fonte: SEMPRE activity_intervals (nunca raw).
/// A data é interpretada no FUSO DO TENANT (corte do dia à meia-noite da org — cenário
/// "timezone" da 11.2). Dias passados são imutáveis: ETag + Cache-Control (Seção 8.5).
///
/// Cauda de HOJE (decisão documentada — a spec manda mostrar "Desligada"/"Sem comunicação"
/// ao vivo, mas o worker só fecha intervalos em eventos): o trecho entre o fim do último
/// intervalo e o "agora" é sintetizado na leitura a partir de device_current_state —
/// off_clean se o último evento foi desligamento limpo; no_data se o silêncio já passou de
/// 600 s (N7). Estados de usuário não são estendidos (defasagem máxima ~1 ciclo do worker).
///
/// GET /api/v1/timeline/team?date (F3.4, Seção 8.5): uma lane por device NÃO-archived —
/// mesma agregação, mesma cauda viva e mesmo cache, reusando os helpers do modo device.
/// </summary>
[Route("api/v1/timeline")]
[Authorize] // Viewer+
public class TimelineController(
    NpgsqlDataSource dataSource,
    AuditReadContext readAudit,
    TimeProvider clock) : ApiControllerBase
{
    public const int ResolutionSec = 60;     // N21
    public const int MaxIntervals = 3000;    // N21 (teto defensivo: truncar + flag, jamais 500)
    private static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(600); // N7

    [HttpGet("device")]
    [AuditRead] // DoD 11.3: leitura de dado pessoal — view_timeline gravado pelo AuditReadFilter (2xx)
    public async Task<IActionResult> Device(
        [FromQuery(Name = "device_id")] Guid? deviceId,
        [FromQuery(Name = "date")] string? date,
        CancellationToken ct)
    {
        if (deviceId is null || deviceId == Guid.Empty)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro device_id é obrigatório.");
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro date é obrigatório no formato yyyy-MM-dd.");

        var tenantId = Auth.CurrentUser.TenantId(User);
        var now = clock.GetUtcNow();

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var device = await connection.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition(
            """
            SELECT d.id, COALESCE(d.display_name, d.hostname) AS device_name, d.tz_offset_min, o.timezone
            FROM devices d JOIN organizations o ON o.id = d.tenant_id
            WHERE d.tenant_id = @TenantId AND d.id = @DeviceId
            """,
            new { TenantId = tenantId, DeviceId = deviceId }, cancellationToken: ct));
        if (device is null) return NotFoundProblem();

        // janela do dia no fuso do tenant (corte à meia-noite da org)
        var tz = TimeZoneInfo.FindSystemTimeZoneById(device.Timezone);
        var windowStart = LocalMidnightUtc(day, tz);
        var windowEnd = LocalMidnightUtc(day.AddDays(1), tz);
        var isToday = now >= windowStart && now < windowEnd;

        // limite inferior em started_at (partition pruning): o worker divide intervalos na
        // meia-noite do tenant, então nada que cruza a janela começa mais de ~25 h antes dela;
        // 48 h de folga cobre até troca de fuso da org. Sem isso, o range scan da PK
        // (tenant, device, started_at < @End) varreria TODAS as partições mensais (12 meses, N11).
        var startFloor = windowStart.AddHours(-48);

        var rows = (await connection.QueryAsync<IntervalRow>(new CommandDefinition(
            """
            SELECT i.started_at, i.ended_at, i.state, i.window_title, i.data_incomplete,
                   i.device_user_id, a.id AS app_id, a.process_name, a.display_name
            FROM activity_intervals i
            LEFT JOIN app_catalog a ON a.id = i.app_id
            WHERE i.tenant_id = @TenantId AND i.device_id = @DeviceId
              AND i.started_at >= @StartFloor AND i.started_at < @End AND i.ended_at > @Start
            ORDER BY i.started_at
            """,
            new { TenantId = tenantId, DeviceId = deviceId, StartFloor = startFloor, Start = windowStart, End = windowEnd },
            cancellationToken: ct))).ToList();

        // clip defensivo nas bordas (o worker já divide na meia-noite do tenant)
        ClipToWindow(rows, windowStart, windowEnd);

        // rodapé: computado dos intervalos PLENOS (pré-merge) — consistência com o
        // relatório de jornada (11.3) e com a agregação diária da F3
        var userRows = rows.Where(r => r.State is "active" or "idle" or "locked").ToList();
        var summary = new TimelineSummaryResponse(
            userRows.Count > 0 ? userRows.Min(r => r.StartedAt) : null,
            userRows.Count > 0 ? userRows.Max(r => r.EndedAt) : null,
            SecondsIn(userRows, "active") + SecondsIn(userRows, "idle") + SecondsIn(userRows, "locked"),
            SecondsIn(userRows, "active"),
            SecondsIn(userRows, "idle"),
            SecondsIn(userRows, "locked"));

        if (isToday)
        {
            var current = await connection.QuerySingleOrDefaultAsync<CurrentStateRow>(new CommandDefinition(
                "SELECT device_id, state, last_contact_at FROM device_current_state WHERE tenant_id = @TenantId AND device_id = @DeviceId",
                new { TenantId = tenantId, DeviceId = deviceId }, cancellationToken: ct));
            AppendLiveTail(rows, current, windowStart, now);
        }

        var merged = MergeToResolution(rows);
        var truncated = merged.Count > MaxIntervals;
        if (truncated) merged = merged.Take(MaxIntervals).ToList();

        var intervals = merged.Select(ToIntervalResponse).ToList();

        var response = new TimelineResponse(
            device.Id,
            device.DeviceName,
            day.ToString("yyyy-MM-dd"),
            device.Timezone,
            device.TzOffsetMin,
            ResolutionSec,
            intervals.Any(i => i.DataIncomplete) || truncated,
            now,
            intervals,
            summary);

        // DoD 11.3: toda visualização de dado pessoal gera linha em audit_log. A gravação é
        // CONSOLIDADA no AuditReadFilter (grava view_timeline com actor_ip APÓS o 2xx — não audita
        // o 304 de cache-hit, em que nenhum dado é entregue). Aqui só descrevemos o alvo/detalhe.
        readAudit.Record(tenantId, AuditActions.ViewTimeline,
            Auth.CurrentUser.UserId(User),
            targetType: "device", targetId: deviceId,
            detailJson: JsonSerializer.Serialize(new { date = response.Date }));

        // dias passados são imutáveis → ETag/304; hoje muda a cada ciclo do worker
        if (!isToday && windowEnd <= now)
        {
            var etag = $"\"{ComputeETag(response)}\"";
            Response.Headers.CacheControl = "private, max-age=300";
            Response.Headers.ETag = etag;
            if (Request.Headers.IfNoneMatch.Contains(etag)) return StatusCode(StatusCodes.Status304NotModified);
        }
        else
        {
            Response.Headers.CacheControl = "private, no-cache";
        }

        return Ok(response);
    }

    /// <summary>
    /// GET /api/v1/timeline/team?date (Seção 7.4/8.5, F3.4): uma lane por device NÃO-archived
    /// do tenant — INCLUSIVE devices sem intervalos no dia (lane vazia: o gestor varre a
    /// equipe inteira) — ordenadas por nome de exibição. Mesma agregação do modo device
    /// (merge N21, cauda viva de hoje por device). Os intervalos do dia de TODOS os devices
    /// vêm em UMA query (nada de N+1 por lane); a cauda viva idem, via device_current_state.
    ///
    /// Cap N21 (decisão documentada): o teto de ~3.000 vale para a SOMA de intervalos da
    /// resposta; ao estourar, lanes INTEIRAS deixam de entrar — como um PREFIXO da ordem de
    /// exibição (nunca cortar lane no meio, nunca pular uma lane cheia e incluir as
    /// seguintes, o que embaralharia a varredura alfabética do gestor) — e truncated = true.
    /// A primeira lane entra sempre; se SOZINHA estourar o teto (sessões simultâneas —
    /// terminal server / fast user switching empilham intervalos sobrepostos pós-merge),
    /// é truncada com o MESMO corte do modo device (Take + flag) — a resposta jamais
    /// excede o teto e truncated jamais sai false ao truncar.
    /// </summary>
    [HttpGet("team")]
    [AuditRead] // DoD 11.3: leitura de dado pessoal de VÁRIAS pessoas — view_timeline (target team) via filter
    public async Task<IActionResult> Team([FromQuery(Name = "date")] string? date, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro date é obrigatório no formato yyyy-MM-dd.");

        var tenantId = Auth.CurrentUser.TenantId(User);
        var now = clock.GetUtcNow();

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var timezone = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        if (timezone is null) return NotFoundProblem();

        // janela do dia no fuso do TENANT — mesma interpretação de date do modo device
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var windowStart = LocalMidnightUtc(day, tz);
        var windowEnd = LocalMidnightUtc(day.AddDays(1), tz);
        var isToday = now >= windowStart && now < windowEnd;

        var devices = (await connection.QueryAsync<TeamDeviceRow>(new CommandDefinition(
            """
            SELECT d.id, COALESCE(d.display_name, d.hostname) AS device_name, d.tz_offset_min
            FROM devices d
            WHERE d.tenant_id = @TenantId AND d.status <> 'archived'
            ORDER BY lower(COALESCE(d.display_name, d.hostname)), d.id
            """,
            new { TenantId = tenantId }, cancellationToken: ct))).ToList();
        var deviceIds = devices.Select(d => d.Id).ToArray();

        // mesmo limite inferior do modo device (partition pruning) — aqui é VITAL: a query
        // cobre TODOS os devices do tenant (alvo N25 ~2.500) e é repolled a cada 60 s (N18)
        var startFloor = windowStart.AddHours(-48);

        var rowsByDevice = (await connection.QueryAsync<IntervalRow>(new CommandDefinition(
            """
            SELECT i.device_id, i.started_at, i.ended_at, i.state, i.window_title, i.data_incomplete,
                   i.device_user_id, a.id AS app_id, a.process_name, a.display_name
            FROM activity_intervals i
            LEFT JOIN app_catalog a ON a.id = i.app_id
            WHERE i.tenant_id = @TenantId AND i.device_id = ANY(@DeviceIds)
              AND i.started_at >= @StartFloor AND i.started_at < @End AND i.ended_at > @Start
            ORDER BY i.started_at
            """,
            new { TenantId = tenantId, DeviceIds = deviceIds, StartFloor = startFloor, Start = windowStart, End = windowEnd },
            cancellationToken: ct))).ToLookup(r => r.DeviceId);

        // cauda viva de hoje: estado corrente de TODOS os devices em uma query só
        var currentByDevice = new Dictionary<Guid, CurrentStateRow>();
        if (isToday)
        {
            currentByDevice = (await connection.QueryAsync<CurrentStateRow>(new CommandDefinition(
                "SELECT device_id, state, last_contact_at FROM device_current_state WHERE tenant_id = @TenantId AND device_id = ANY(@DeviceIds)",
                new { TenantId = tenantId, DeviceIds = deviceIds }, cancellationToken: ct)))
                .ToDictionary(c => c.DeviceId);
        }

        var lanes = new List<TeamTimelineLaneResponse>();
        var total = 0;
        var truncated = false;
        foreach (var device in devices)
        {
            var rows = rowsByDevice[device.Id].ToList();
            ClipToWindow(rows, windowStart, windowEnd);
            if (isToday) AppendLiveTail(rows, currentByDevice.GetValueOrDefault(device.Id), windowStart, now);

            var intervals = MergeToResolution(rows).Select(ToIntervalResponse).ToList();
            if (lanes.Count == 0 && intervals.Count > MaxIntervals)
            {
                // primeira lane sozinha estoura (sessões simultâneas): mesmo corte do modo device
                intervals = intervals.Take(MaxIntervals).ToList();
                truncated = true;
            }

            if (lanes.Count > 0 && total + intervals.Count > MaxIntervals)
            {
                truncated = true; // cap N21: esta lane e as seguintes ficam INTEIRAS de fora
                break;
            }

            total += intervals.Count;
            lanes.Add(new TeamTimelineLaneResponse(
                device.Id, device.DeviceName, device.TzOffsetMin,
                intervals.Any(i => i.DataIncomplete), intervals));
        }

        var response = new TeamTimelineResponse(day.ToString("yyyy-MM-dd"), ResolutionSec, now, truncated, lanes);

        // DoD 11.3: visualização de dado pessoal de VÁRIAS pessoas — target_id é nullable no
        // schema (target_type text, target_id uuid), então "team" sem alvo individual (o
        // tenant já está em tenant_id; repetir o tenant em target_id seria ruído) + detail {date}.
        // Gravação consolidada no AuditReadFilter (após o 2xx, com actor_ip; sem auditar o 304).
        readAudit.Record(tenantId, AuditActions.ViewTimeline,
            Auth.CurrentUser.UserId(User),
            targetType: "team", targetId: null,
            detailJson: JsonSerializer.Serialize(new { date = response.Date }));

        // dias passados são imutáveis → ETag/304; hoje muda a cada ciclo do worker
        if (!isToday && windowEnd <= now)
        {
            var etag = $"\"{ComputeTeamETag(response)}\"";
            Response.Headers.CacheControl = "private, max-age=300";
            Response.Headers.ETag = etag;
            if (Request.Headers.IfNoneMatch.Contains(etag)) return StatusCode(StatusCodes.Status304NotModified);
        }
        else
        {
            Response.Headers.CacheControl = "private, no-cache";
        }

        return Ok(response);
    }

    // ------------------------------------------------------------ cauda viva de hoje
    /// <summary>Lógica compartilhada device/equipe — ver a decisão documentada no topo da classe.</summary>
    private static void AppendLiveTail(
        List<IntervalRow> rows, CurrentStateRow? current, DateTimeOffset windowStart, DateTimeOffset now)
    {
        if (current is null) return;

        var lastEnd = rows.Count > 0 ? rows.Max(r => r.EndedAt) : windowStart;
        string? tailState = null;
        if (current.State == "off_clean")
        {
            tailState = "off_clean";
        }
        else if (now - current.LastContactAt >= GapThreshold)
        {
            tailState = "no_data"; // "matar o serviço na marra → após 600 s vira Sem comunicação"
        }

        if (tailState is not null && now > lastEnd)
        {
            rows.Add(new IntervalRow
            {
                StartedAt = lastEnd,
                EndedAt = now,
                State = tailState,
                DataIncomplete = false,
            });
        }
    }

    // ------------------------------------------------------------ resolução N21
    /// <summary>
    /// Intervalos ≥ 1 min passam com timestamps exatos; sequências de intervalos menores
    /// são fundidas por estado DOMINANTE (maior duração acumulada no trecho), com
    /// data_incomplete = OR dos membros — regra de merge do design 03 sob o valor fixo N21.
    /// </summary>
    internal static List<IntervalRow> MergeToResolution(List<IntervalRow> rows)
    {
        var result = new List<IntervalRow>();
        var run = new List<IntervalRow>();

        void FlushRun()
        {
            if (run.Count == 0) return;
            var dominant = run
                .GroupBy(r => r.State)
                .OrderByDescending(g => g.Sum(r => (r.EndedAt - r.StartedAt).Ticks))
                .First()
                .OrderByDescending(r => (r.EndedAt - r.StartedAt).Ticks)
                .First();
            result.Add(new IntervalRow
            {
                StartedAt = run[0].StartedAt,
                EndedAt = run[^1].EndedAt,
                State = dominant.State,
                AppId = dominant.AppId,
                ProcessName = dominant.ProcessName,
                DisplayName = dominant.DisplayName,
                WindowTitle = dominant.WindowTitle,
                DataIncomplete = run.Any(r => r.DataIncomplete),
            });
            run.Clear();
        }

        foreach (var row in rows.OrderBy(r => r.StartedAt))
        {
            if ((row.EndedAt - row.StartedAt).TotalSeconds >= ResolutionSec)
            {
                FlushRun();
                result.Add(row);
                continue;
            }

            // quebra a sequência se houver buraco ou se ela já alcançou 1 min
            if (run.Count > 0 &&
                (row.StartedAt != run[^1].EndedAt ||
                 (run[^1].EndedAt - run[0].StartedAt).TotalSeconds >= ResolutionSec))
            {
                FlushRun();
            }
            run.Add(row);
        }
        FlushRun();
        return result;
    }

    // ------------------------------------------------------------ helpers
    /// <summary>
    /// Regra canônica de arredondamento do gate 11.3: soma EXATA (ticks) por LANE
    /// (device_user_id) com floor POR LANE, depois soma das lanes — espelho bit a bit do
    /// floor(sum(extract(epoch ...)))::int agrupado por device_user_id da agregação diária
    /// (DailyAggregationService). Truncar a soma global em double divergiria do agregado
    /// com 2+ lanes e durações fracionárias (ex.: 100.5s + 100.5s → 201 vs 100 + 100).
    /// </summary>
    private static long SecondsIn(List<IntervalRow> rows, string state) =>
        rows.Where(r => r.State == state)
            .GroupBy(r => r.DeviceUserId)
            .Sum(lane => lane.Sum(r => (r.EndedAt - r.StartedAt).Ticks) / TimeSpan.TicksPerSecond);

    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Clip defensivo nas bordas do dia (o worker já divide na meia-noite do tenant).</summary>
    private static void ClipToWindow(List<IntervalRow> rows, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        foreach (var r in rows)
        {
            if (r.StartedAt < windowStart) r.StartedAt = windowStart;
            if (r.EndedAt > windowEnd) r.EndedAt = windowEnd;
        }
    }

    /// <summary>Shape ÚNICO do intervalo, compartilhado pelos modos device e equipe (contrato F3.4).</summary>
    private static TimelineIntervalResponse ToIntervalResponse(IntervalRow r) => new(
        r.StartedAt, r.EndedAt, r.State,
        r.AppId is { } appId && r.State == "active"
            ? new TimelineAppResponse(appId, r.ProcessName!, r.DisplayName ?? r.ProcessName!, null)
            : null,
        r.State == "active" ? r.WindowTitle : null,
        r.DataIncomplete);

    private static string ComputeETag(TimelineResponse response)
    {
        var builder = new StringBuilder();
        builder.Append(response.DeviceId).Append('|').Append(response.Date);
        AppendIntervalsETag(builder, response.Intervals);
        return HashHex(builder);
    }

    /// <summary>ETag do modo equipe: data + lanes (nome incluso — renomear device invalida o cache).</summary>
    private static string ComputeTeamETag(TeamTimelineResponse response)
    {
        var builder = new StringBuilder();
        builder.Append(response.Date).Append('|').Append(response.Truncated);
        foreach (var lane in response.Lanes)
        {
            builder.Append('|').Append(lane.DeviceId).Append(',').Append(lane.DeviceName);
            AppendIntervalsETag(builder, lane.Intervals);
        }
        return HashHex(builder);
    }

    private static void AppendIntervalsETag(StringBuilder builder, IReadOnlyList<TimelineIntervalResponse> intervals)
    {
        foreach (var i in intervals)
            builder.Append('|').Append(i.StartedAt.UtcTicks).Append(',').Append(i.EndedAt.UtcTicks)
                   .Append(',').Append(i.State).Append(',').Append(i.DataIncomplete);
    }

    private static string HashHex(StringBuilder builder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32];

    private sealed record DeviceRow(Guid Id, string DeviceName, int? TzOffsetMin, string Timezone);

    private sealed record TeamDeviceRow(Guid Id, string DeviceName, int? TzOffsetMin);

    private sealed record CurrentStateRow(Guid DeviceId, string State, DateTimeOffset LastContactAt);

    internal sealed class IntervalRow
    {
        /// <summary>Preenchido só no modo equipe (uma query para todas as lanes).</summary>
        public Guid DeviceId { get; set; }

        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset EndedAt { get; set; }
        public Guid? DeviceUserId { get; set; }
        public string State { get; set; } = "";
        public string? WindowTitle { get; set; }
        public bool DataIncomplete { get; set; }
        public Guid? AppId { get; set; }
        public string? ProcessName { get; set; }
        public string? DisplayName { get; set; }
    }
}
