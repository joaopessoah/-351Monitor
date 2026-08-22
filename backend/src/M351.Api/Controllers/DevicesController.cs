using System.Text.Json;
using Dapper;
using M351.Api.Agent;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure;
using M351.Infrastructure.Data;
using M351.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace M351.Api.Controllers;

[Route("api/v1/devices")]
[Authorize] // Viewer+ (policy default exige token de acesso pleno)
public class DevicesController(M351DbContext db, AuditWriter audit, NpgsqlDataSource dataSource) : ApiControllerBase
{
    private const int MaxPageSize = 100;

    /// <summary>
    /// min_version do release current do canal 'stable' (F4.2) — lida UMA vez por request e
    /// comparada em memória (SemVer no backend, sem confiar no portal). Null quando não há release
    /// publicado: nesse caso nenhum device é "desatualizado".
    /// </summary>
    private async Task<string?> CurrentStableMinVersionAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT min_version FROM agent_releases WHERE channel = @Channel AND is_current",
            new { Channel = AgentUpdateEndpoints.DefaultChannel }, cancellationToken: ct));
    }

    /// <summary>
    /// Release current do canal 'stable': a versão para onde a frota deveria estar indo e a
    /// min_version abaixo da qual o update é forçado. Ambos null quando não há release publicado.
    /// </summary>
    private async Task<(string? Version, string? MinVersion)> CurrentStableReleaseAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<(string? Version, string? MinVersion)>(
            new CommandDefinition(
                "SELECT version, min_version FROM agent_releases WHERE channel = @Channel AND is_current",
                new { Channel = AgentUpdateEndpoints.DefaultChannel }, cancellationToken: ct));
        return row;
    }

    /// <summary>Projeção EF crua (sem semver): a flag agent_outdated é calculada em memória.</summary>
    private sealed record DeviceRow(
        Guid Id, string Hostname, string? DisplayName, string OsType, string? OsVersion,
        string? AgentVersion, string Status, string[]? Tags, DateTimeOffset? LastSeenAt,
        int? TzOffsetMin, long ClockOffsetMs, DateTimeOffset? NoticeAckedAt,
        DateTimeOffset? LastTamperAt, string? LastTamperReason);

    /// <summary>Monta o DeviceResponse aplicando o SemVer.IsOutdated com o min_version do request.</summary>
    private static DeviceResponse ToResponse(DeviceRow d, string? minVersion) =>
        new(d.Id, d.Hostname, d.DisplayName, d.OsType, d.OsVersion, d.AgentVersion,
            d.Status, d.Tags, d.LastSeenAt, d.TzOffsetMin, d.ClockOffsetMs,
            d.NoticeAckedAt, d.LastTamperAt, d.LastTamperReason,
            AgentOutdated: SemVer.IsOutdated(d.AgentVersion, minVersion));

    // ===== F5 — saúde de frota SERVER-SIDE (os mesmos limiares do deviceHealth.ts do portal;
    // até aqui a derivação era 100% client-side e os totais valiam só para a página de 50) =====

    /// <summary>N6: contato > 180 s sem desligamento limpo = sem comunicação.</summary>
    public const int OfflineLimitSeconds = 180;

    /// <summary>Banner global (Seção 8.1): sem comunicação há > 30 min em horário de trabalho.</summary>
    public const int OfflineSevereSeconds = 30 * 60;

    /// <summary>Seção 8.7: |offset| > 2 min = relógio dessincronizado.</summary>
    public const long ClockSkewLimitMs = 120_000;

    /// <summary>Só adulteração recente é destaque (raw_events expira em 90 d).</summary>
    public const int TamperWindowDays = 7;

    private sealed record HealthRow(
        DateTimeOffset? LastSeenAt, long ClockOffsetMs, string? AgentVersion,
        DateTimeOffset? NoticeAckedAt, DateTimeOffset? LastTamperAt);

    private static bool HasAlert(HealthRow d, DateTimeOffset now, string? minVersion, bool withinBusinessHours)
    {
        var sinceLastSeen = d.LastSeenAt is { } seen ? now - seen : (TimeSpan?)null;
        var offline = sinceLastSeen is null || sinceLastSeen.Value.TotalSeconds > OfflineLimitSeconds;
        return offline
            || Math.Abs(d.ClockOffsetMs) > ClockSkewLimitMs
            || SemVer.IsOutdated(d.AgentVersion, minVersion)
            || (d.LastTamperAt is { } tamper && now - tamper <= TimeSpan.FromDays(TamperWindowDays))
            || d.NoticeAckedAt is null;
    }

    /// <summary>
    /// GET /devices/health-summary (F5): contagens de saúde sobre a FROTA INTEIRA numa
    /// passada (status active; paused/archived são estado deliberado do gestor, não alerta).
    /// Alimenta o card "X dispositivos precisam de atenção" da Visão Geral e os chips totais
    /// da tela Dispositivos, que antes contavam só a página corrente.
    /// </summary>
    [HttpGet("health-summary")]
    public async Task<IActionResult> HealthSummary(CancellationToken ct)
    {
        var org = await db.Organizations.FirstAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var withinBusinessHours = BusinessHoursWindow.IsWithin(org.BusinessHours, org.Timezone, now);
        var minVersion = await CurrentStableMinVersionAsync(ct);

        var rows = await db.Devices
            .Where(d => d.Status == "active")
            .Select(d => new HealthRow(d.LastSeenAt, d.ClockOffsetMs, d.AgentVersion, d.NoticeAckedAt, d.LastTamperAt))
            .ToListAsync(ct);

        int offline = 0, offlineSevere = 0, clockSkewed = 0, outdated = 0, tampered = 0, noticePending = 0, withAlert = 0;
        foreach (var d in rows)
        {
            var sinceLastSeen = d.LastSeenAt is { } seen ? now - seen : (TimeSpan?)null;
            var isOffline = sinceLastSeen is null || sinceLastSeen.Value.TotalSeconds > OfflineLimitSeconds;
            var isSevere = isOffline
                && (sinceLastSeen is null || sinceLastSeen.Value.TotalSeconds > OfflineSevereSeconds)
                && withinBusinessHours;
            var isSkewed = Math.Abs(d.ClockOffsetMs) > ClockSkewLimitMs;
            var isOutdated = SemVer.IsOutdated(d.AgentVersion, minVersion);
            var isTampered = d.LastTamperAt is { } tamper && now - tamper <= TimeSpan.FromDays(TamperWindowDays);
            var isNoticePending = d.NoticeAckedAt is null;

            if (isOffline) offline++;
            if (isSevere) offlineSevere++;
            if (isSkewed) clockSkewed++;
            if (isOutdated) outdated++;
            if (isTampered) tampered++;
            if (isNoticePending) noticePending++;
            if (isOffline || isSkewed || isOutdated || isTampered || isNoticePending) withAlert++;
        }

        return Ok(new DeviceHealthSummaryResponse(
            rows.Count, offline, offlineSevere, clockSkewed, outdated, tampered, noticePending,
            withAlert, withinBusinessHours, now));
    }

    // ===== Vigilância de rollout: distribuição de versões da frota e falhas de atualização =====

    /// <summary>Só falha RECENTE é destaque (mesma janela do tamper, pelo mesmo motivo).</summary>
    public const int UpdateFailureWindowDays = 7;

    /// <summary>Teto de linhas de falha detalhadas: a tela mostra os casos, não a frota inteira.</summary>
    private const int MaxRecentFailures = 20;

    private sealed record VersionRow(
        Guid Id, string Hostname, string? DisplayName, string? AgentVersion,
        DateTimeOffset? LastUpdateFailureAt, string? LastUpdateFailureReason, string? LastUpdateTargetVersion);

    /// <summary>
    /// GET /devices/version-summary (Viewer+): quantas máquinas estão em cada versão do agente e
    /// quais falharam ao atualizar nos últimos 7 dias. Server-side, no padrão do health-summary:
    /// uma passada sobre os devices active do tenant, agregação em memória (dimensionado para o
    /// mesmo volume, ~2.500 devices).
    ///
    /// Até aqui a única vigilância de rollout era o contador "desatualizados", que diz QUANTOS
    /// ficaram para trás mas não em QUAL versão eles pararam nem POR QUÊ. As duas leituras juntas
    /// separam "release novo ainda subindo" de "release travado numa etapa".
    /// </summary>
    [HttpGet("version-summary")]
    public async Task<IActionResult> VersionSummary(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (currentVersion, minVersion) = await CurrentStableReleaseAsync(ct);
        var failureSince = now - TimeSpan.FromDays(UpdateFailureWindowDays);

        var rows = await db.Devices
            .Where(d => d.Status == "active")
            .Select(d => new VersionRow(
                d.Id, d.Hostname, d.DisplayName, d.AgentVersion,
                d.LastUpdateFailureAt, d.LastUpdateFailureReason, d.LastUpdateTargetVersion))
            .ToListAsync(ct);

        // Agrupamento por versão exata (string do agente, não a normalizada): duas máquinas com
        // "1.0.0" e "v1.0.0" seriam a mesma versão semântica, mas exibir o que a máquina reporta é
        // o que permite reconhecer um agente empacotado errado.
        var versions = rows
            .GroupBy(d => string.IsNullOrWhiteSpace(d.AgentVersion) ? null : d.AgentVersion!.Trim())
            .Select(g => new FleetVersionRow(g.Key, g.Count(), SemVer.IsOutdated(g.Key, minVersion)))
            .OrderByDescending(v => SemVer.TryParse(v.Version, out var parsed) ? parsed : default)
            .ThenBy(v => v.Version is null) // versão desconhecida sempre por último
            .ThenBy(v => v.Version, StringComparer.Ordinal)
            .ToList();

        var failing = rows
            .Where(d => d.LastUpdateFailureAt is { } at && at >= failureSince && d.LastUpdateFailureReason is not null)
            .OrderByDescending(d => d.LastUpdateFailureAt)
            .ToList();

        var recentFailures = failing
            .Take(MaxRecentFailures)
            .Select(d => new UpdateFailureRow(
                d.Id, d.Hostname, d.DisplayName, d.LastUpdateFailureReason!,
                d.LastUpdateTargetVersion, d.LastUpdateFailureAt!.Value))
            .ToList();

        return Ok(new DeviceVersionSummaryResponse(
            ActiveDevices: rows.Count,
            CurrentVersion: currentVersion,
            MinVersion: minVersion,
            Versions: versions,
            UpdateFailures: failing.Count,
            RecentFailures: recentFailures,
            UpdateFailureWindowDays: UpdateFailureWindowDays,
            ServerTime: now));
    }

    /// <summary>
    /// Lista paginada de devices do tenant com filtros da F2 (Seção 7.4):
    /// ?status (active|paused|archived|revoked), ?tag (match em tags[]), ?q (hostname/nome).
    /// F3.7: ?include_archived (default TRUE, preservando o comportamento existente) — false
    /// esconde os archived (toggle "incluir arquivados" da tela Dispositivos, spec linha 954).
    /// O filtro explícito ?status=archived continua funcionando e IGNORA include_archived.
    /// F5: ?health=alert filtra a FROTA INTEIRA pelos mesmos limiares do health-summary
    /// (derivação em memória sobre o conjunto filtrado; dimensionado para ~2.500 devices).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 50,
        [FromQuery] string? status = null, [FromQuery] string? tag = null, [FromQuery] string? q = null,
        [FromQuery(Name = "include_archived")] bool includeArchived = true,
        [FromQuery] string? health = null,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        IQueryable<Device> filtered = db.Devices;
        if (!string.IsNullOrWhiteSpace(status)) filtered = filtered.Where(d => d.Status == status);
        else if (!includeArchived) filtered = filtered.Where(d => d.Status != "archived");
        if (!string.IsNullOrWhiteSpace(tag)) filtered = filtered.Where(d => d.Tags != null && d.Tags.Contains(tag));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = $"%{q.Trim()}%";
            filtered = filtered.Where(d =>
                EF.Functions.ILike(d.Hostname, needle) ||
                (d.DisplayName != null && EF.Functions.ILike(d.DisplayName, needle)));
        }

        var query = filtered.OrderBy(d => d.Hostname);

        // min_version lido UMA vez por request; agent_outdated comparado em memória (SemVer no backend)
        var minVersion = await CurrentStableMinVersionAsync(ct);

        List<DeviceRow> rows;
        int total;
        if (health == "alert")
        {
            // filtro de saúde vale sobre TODO o conjunto filtrado (não só a página): carrega a
            // projeção completa e pagina em memória — aceitável no dimensionamento do MVP
            var org = await db.Organizations.FirstAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var within = BusinessHoursWindow.IsWithin(org.BusinessHours, org.Timezone, now);

            var all = await query
                .Select(d => new DeviceRow(
                    d.Id, d.Hostname, d.DisplayName, d.OsType, d.OsVersion, d.AgentVersion,
                    d.Status, d.Tags, d.LastSeenAt, d.TzOffsetMin, d.ClockOffsetMs,
                    d.NoticeAckedAt, d.LastTamperAt, d.LastTamperReason))
                .ToListAsync(ct);

            var alerting = all.Where(d => HasAlert(
                new HealthRow(d.LastSeenAt, d.ClockOffsetMs, d.AgentVersion, d.NoticeAckedAt, d.LastTamperAt),
                now, minVersion, within)).ToList();

            total = alerting.Count;
            rows = alerting.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }
        else
        {
            total = await query.CountAsync(ct);
            rows = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(d => new DeviceRow(
                    d.Id, d.Hostname, d.DisplayName, d.OsType, d.OsVersion, d.AgentVersion,
                    d.Status, d.Tags, d.LastSeenAt, d.TzOffsetMin, d.ClockOffsetMs,
                    d.NoticeAckedAt, d.LastTamperAt, d.LastTamperReason))
                .ToListAsync(ct);
        }

        var items = rows.Select(d => ToResponse(d, minVersion)).ToList();

        return Ok(new PagedResponse<DeviceResponse>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var row = await db.Devices
            .Where(d => d.Id == id)
            .Select(d => new DeviceRow(
                d.Id, d.Hostname, d.DisplayName, d.OsType, d.OsVersion, d.AgentVersion,
                d.Status, d.Tags, d.LastSeenAt, d.TzOffsetMin, d.ClockOffsetMs,
                d.NoticeAckedAt, d.LastTamperAt, d.LastTamperReason))
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return NotFoundProblem();
        }

        var minVersion = await CurrentStableMinVersionAsync(ct);
        return Ok(ToResponse(row, minVersion));
    }

    /// <summary>
    /// GET /devices/{id}/transparency-link (Admin+): a URL da página pública daquele dispositivo
    /// (/t/{token}) — a MESMA que o tray do agente abre na máquina do funcionário, para o gestor
    /// poder enviá-la a quem pedir sem precisar ler o banco.
    ///
    /// Fora do DeviceResponse por decisão de segurança: o token é um segredo de baixo valor mas é
    /// um segredo, e o DeviceResponse é lido por Viewer e vai na listagem inteira. Aqui sai um
    /// device por vez, só sob Admin+, e a url NUNCA entra em log (nem no audit, que registraria a
    /// url num detail legível por quem lê a trilha).
    ///
    /// Device sem token (só aconteceria com linha anterior ao backfill que nunca re-enrollou)
    /// responde 404, o mesmo do device inexistente — não há link a oferecer.
    /// </summary>
    [HttpGet("{id:guid}/transparency-link")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> TransparencyLink(
        Guid id, [FromServices] AgentConfigService configService, CancellationToken ct)
    {
        var token = await db.Devices
            .Where(d => d.Id == id)
            .Select(d => d.TransparencyToken)
            .FirstOrDefaultAsync(ct);

        if (token is not { } value)
        {
            return NotFoundProblem(); // inexistente, de outro tenant OU sem token — nunca 403
        }

        return Ok(new DeviceTransparencyLinkResponse(id, configService.DeviceTransparencyUrl(value)));
    }

    private const int MaxDisplayNameLength = 200;
    private const int MaxTagLength = 100;
    private const int MaxTags = 50;

    /// <summary>
    /// PATCH /api/v1/devices/{id} (Seção 7.4 linha 799 — Admin+): atualização parcial de
    /// display_name, tags e status (active|paused|archived). Campos AUSENTES não mudam;
    /// display_name null limpa o apelido (o portal volta a exibir o hostname). Device revoked
    /// é terminal (só re-enroll revive): qualquer PATCH responde 400. Mutação EF + audit
    /// update_device (detail com de→para por campo alterado) no MESMO SaveChanges, padrão do
    /// Revoke — a mudança jamais persiste sem a trilha. Corpo cru (JsonElement) porque o
    /// contrato distingue "display_name ausente" (não muda) de "display_name: null" (limpa).
    /// </summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Update(Guid id, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Corpo inválido: envie um objeto JSON.");
        }

        // ----- display_name: ausente = não muda; null = limpa; string = renomeia -----
        var hasDisplayName = body.TryGetProperty("display_name", out var displayNameEl);
        string? newDisplayName = null;
        if (hasDisplayName && displayNameEl.ValueKind != JsonValueKind.Null)
        {
            if (displayNameEl.ValueKind != JsonValueKind.String)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest, "display_name deve ser string ou null.");
            }

            newDisplayName = displayNameEl.GetString()!.Trim();
            if (newDisplayName.Length is 0 or > MaxDisplayNameLength)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    $"display_name inválido (1 a {MaxDisplayNameLength} caracteres, ou null para limpar o apelido).");
            }
        }

        // ----- tags: ausente = não muda; lista substitui INTEIRA; null limpa (decisão p/
        // silêncio do contrato — equivale a lista vazia, sem tags) -----
        var hasTags = body.TryGetProperty("tags", out var tagsEl);
        string[]? newTags = null;
        if (hasTags && tagsEl.ValueKind != JsonValueKind.Null)
        {
            if (tagsEl.ValueKind != JsonValueKind.Array)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest, "tags deve ser uma lista de strings.");
            }

            var parsed = new List<string>();
            foreach (var tagEl in tagsEl.EnumerateArray())
            {
                if (tagEl.ValueKind != JsonValueKind.String)
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest, "tags deve ser uma lista de strings.");
                }

                var tag = tagEl.GetString()!.Trim();
                if (tag.Length is 0 or > MaxTagLength)
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest,
                        $"Tag inválida (1 a {MaxTagLength} caracteres).");
                }

                if (!parsed.Contains(tag)) parsed.Add(tag); // dedupe preservando a ordem
            }

            if (parsed.Count > MaxTags)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest, $"Máximo de {MaxTags} tags por device.");
            }

            newTags = [.. parsed];
        }

        // ----- status: só as transições do portal; revogação é fluxo próprio (POST /revoke) -----
        var hasStatus = body.TryGetProperty("status", out var statusEl);
        string? newStatus = null;
        if (hasStatus)
        {
            newStatus = statusEl.ValueKind == JsonValueKind.String ? statusEl.GetString() : null;
            if (newStatus is not ("active" or "paused" or "archived"))
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "status inválido. Use active, paused ou archived (revogação é via POST /devices/{id}/revoke).");
            }
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return NotFoundProblem(); // inexistente OU de outro tenant — nunca 403
        }

        if (device.Status == "revoked")
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Device revogado não pode ser alterado: somente um novo enroll o reativa.");
        }

        // aplica somente o que mudou de fato e registra o de→para por campo (detail do audit)
        var changes = new Dictionary<string, object?>();
        if (hasDisplayName && device.DisplayName != newDisplayName)
        {
            changes["display_name"] = new { from = device.DisplayName, to = newDisplayName };
            device.DisplayName = newDisplayName;
        }

        if (hasTags && !TagsEqual(device.Tags, newTags))
        {
            changes["tags"] = new { from = device.Tags, to = newTags };
            device.Tags = newTags;
        }

        var pausedNow = false;
        if (hasStatus && device.Status != newStatus)
        {
            changes["status"] = new { from = device.Status, to = newStatus };
            device.Status = newStatus!;
            pausedNow = newStatus == "paused";
        }

        if (changes.Count > 0)
        {
            audit.Add(Auth.CurrentUser.TenantId(User), AuditActions.UpdateDevice, Auth.CurrentUser.UserId(User),
                HttpContext.Connection.RemoteIpAddress, targetType: "device", targetId: device.Id,
                detailJson: JsonSerializer.Serialize(changes));

            await db.SaveChangesAsync(ct);

            if (pausedNow)
            {
                // pausa vale na hora, não só no próximo batch: a projeção de presença não pode
                // continuar exibindo usuário/título de um device que o gestor acabou de pausar
                // (a ingestão também zera e descarta enquanto status = paused)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE device_current_state
                    SET state = 'no_data', windows_sid = NULL, windows_username = NULL,
                        foreground_process = NULL, foreground_title = NULL,
                        state_since = NULL, app_since = NULL, updated_at = now()
                    WHERE tenant_id = {Auth.CurrentUser.TenantId(User)} AND device_id = {device.Id}
                    """, ct);
            }
        }

        // mesmo shape do GET {id} (inclui as dimensões de saúde da F4.4)
        var minVersion = await CurrentStableMinVersionAsync(ct);
        var row = new DeviceRow(
            device.Id, device.Hostname, device.DisplayName, device.OsType, device.OsVersion,
            device.AgentVersion, device.Status, device.Tags, device.LastSeenAt,
            device.TzOffsetMin, device.ClockOffsetMs, device.NoticeAckedAt,
            device.LastTamperAt, device.LastTamperReason);
        return Ok(ToResponse(row, minVersion));
    }

    /// <summary>null e lista vazia são equivalentes (sem tags): troca entre eles não é mudança.</summary>
    private static bool TagsEqual(string[]? a, string[]? b) => (a ?? []).SequenceEqual(b ?? []);

    /// <summary>
    /// Revogação de device (Seção 7.4 — POST /devices/{id}/revoke, Admin+): status=revoked,
    /// token vigente invalidado (hash substituído → próximo batch 401) e UNENROLL enfileirado
    /// para entrega no próximo ack (o agente re-enrolla após o 401 e recebe o comando).
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public Task<IActionResult> Revoke(Guid id, CancellationToken ct) => RevokeCoreAsync(id, ct);

    /// <summary>Alias DELETE do fluxo de revogação (mesmo efeito do POST /revoke).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => RevokeCoreAsync(id, ct);

    private async Task<IActionResult> RevokeCoreAsync(Guid id, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return NotFoundProblem(); // inexistente OU de outro tenant — nunca 403
        }

        if (device.Status != "revoked")
        {
            device.Status = "revoked";
            // invalida o token vigente: hash substituído por um valor nunca emitido
            device.TokenHash = TokenGenerator.Sha256(TokenGenerator.NewOpaqueToken());

            db.DeviceCommands.Add(new DeviceCommand
            {
                Id = Uuid7.NewUuid7(),
                TenantId = device.TenantId,
                DeviceId = device.Id,
                Type = DeviceCommand.TypeUnenroll,
            });

            audit.Add(Auth.CurrentUser.TenantId(User), AuditActions.RevokeDevice, Auth.CurrentUser.UserId(User),
                HttpContext.Connection.RemoteIpAddress, targetType: "device", targetId: device.Id);

            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }
}
