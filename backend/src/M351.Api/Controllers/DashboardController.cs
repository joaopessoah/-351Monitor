using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

[Route("api/v1/dashboard")]
[Authorize] // Viewer+
public class DashboardController(NpgsqlDataSource dataSource, TimeProvider clock) : ApiControllerBase
{
    /// <summary>Janela do "online agora" (N6): último contato ≤ 180 s.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(180);

    /// <summary>
    /// GET /api/v1/dashboard/presence (Seção 7.4): tabela "Equipe agora" a partir de
    /// device_current_state. Estado exibido (presence_state) segue a regra N6: `state` se o
    /// último contato ≤ 180 s; senão "Sem comunicação" (no_data) — a menos que o último evento
    /// tenha sido desligamento limpo (off_clean), que continua "Desligada".
    /// </summary>
    [HttpGet("presence")]
    public async Task<IActionResult> Presence(CancellationToken ct)
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
            ORDER BY COALESCE(d.display_name, d.hostname)
            """,
            new { TenantId = tenantId }, cancellationToken: ct));

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
