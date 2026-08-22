using System.Text;
using M351.Domain;
using M351.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Alerts;

/// <summary>
/// Alertas de SAÚDE DE FROTA por e-mail (F5, antecipação do item "alertas por e-mail: agente
/// offline" da v1.1). Agente morto é o churn mais silencioso do produto: o dashboard esvazia,
/// o gestor conclui que a ferramenta não funciona e cancela.
///
/// ESCOPO DELIBERADO: só sinais OPERACIONAIS do agente (sem comunicação, adulteração, ciência
/// pendente, versão desatualizada). Alertas de ociosidade e de "app proibido" ficam FORA:
/// tangenciam a camada semântica e vigilância, e o roadmap os deixa para depois.
///
/// CALIBRAGEM ANTI-FADIGA é o produto aqui (alerta ruidoso desliga o cliente):
///  - coalescimento: no máximo UM e-mail por tenant por ciclo, nunca um por device;
///  - cooldown de 24 h por device+tipo (tabela device_alert_state);
///  - quiet hours: só dentro do horário de trabalho da org (fora dele, máquina silenciosa é
///    esperada e ninguém quer e-mail de madrugada);
///  - opt-out por usuário (user_email_prefs.fleet_alerts);
///  - gate por plano: exclusivo do Pro (primeira razão objetiva de upgrade do produto).
/// </summary>
public class FleetAlertService(
    NpgsqlDataSource dataSource,
    IEmailSender emailSender,
    string portalBaseUrl,
    ILogger<FleetAlertService> logger)
{
    /// <summary>Sem comunicação por mais de 30 min em horário de trabalho (banner da Seção 8.1).</summary>
    public const int OfflineSevereMinutes = 30;

    /// <summary>Ciência do aviso pendente há 7 dias ou mais desde o registro do device.</summary>
    public const int NoticePendingDays = 7;

    public const string KindOffline = "offline";
    public const string KindTamper = "tamper";
    public const string KindNoticePending = "notice_pending";

    /// <summary>Plano com alertas de frota (os demais recebem o digest semanal, não os alertas).</summary>
    public const string RequiredPlan = "pro";

    private sealed record OrgRow(Guid Id, string Name, string Timezone, string? BusinessHours, string Plan);

    private sealed record AlertRow(Guid DeviceId, string DeviceName, string Kind, string Detail);

    /// <summary>Uma passada: avalia a frota de cada org elegível e envia no máximo 1 e-mail por org.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var sent = 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var orgs = new List<OrgRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, name, timezone, business_hours::text, plan
            FROM organizations
            WHERE status = 'active' AND plan = @plan
            """, connection))
        {
            cmd.Parameters.AddWithValue("plan", RequiredPlan);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                orgs.Add(new OrgRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            }
        }

        foreach (var org in orgs)
        {
            // quiet hours: fora do horário de trabalho da org, máquina parada é esperada
            if (!BusinessHoursWindow.IsWithin(org.BusinessHours, org.Timezone, nowUtc))
            {
                continue;
            }

            var candidates = await QueryCandidatesAsync(connection, org.Id, nowUtc, ct);
            if (candidates.Count == 0)
            {
                continue;
            }

            // cooldown de 24 h por device+tipo: só o que não foi avisado recentemente
            var fresh = new List<AlertRow>();
            foreach (var candidate in candidates)
            {
                if (await TryClaimAsync(connection, org.Id, candidate, nowUtc, ct))
                {
                    fresh.Add(candidate);
                }
            }

            if (fresh.Count == 0)
            {
                continue;
            }

            var recipients = await QueryRecipientsAsync(connection, org.Id, ct);
            if (recipients.Count == 0)
            {
                continue;
            }

            var subject = fresh.Count == 1
                ? $"+351 Monitor: 1 dispositivo precisa de atenção em {org.Name}"
                : $"+351 Monitor: {fresh.Count} dispositivos precisam de atenção em {org.Name}";
            var html = BuildHtml(org, fresh);

            foreach (var recipient in recipients)
            {
                await emailSender.SendAsync(new EmailMessage(recipient, subject, html, IsHtml: true), ct);
                sent++;
            }

            logger.LogInformation(
                "Alerta de frota enviado: org {OrgId}, {Alerts} alerta(s), {Recipients} destinatário(s)",
                org.Id, fresh.Count, recipients.Count);
        }

        return sent;
    }

    private static async Task<List<AlertRow>> QueryCandidatesAsync(
        NpgsqlConnection connection, Guid tenantId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // só devices ACTIVE: paused e archived são estado deliberado do gestor, não alerta
        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, COALESCE(display_name, hostname) AS device_name,
                   last_seen_at, notice_acked_at, last_tamper_at, last_tamper_reason
            FROM devices
            WHERE tenant_id = @t AND status = 'active'
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);

        var rows = new List<AlertRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var name = reader.GetString(1);
            var lastSeen = reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(2);
            var noticeAcked = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3);
            var lastTamper = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(4);
            var tamperReason = reader.IsDBNull(5) ? null : reader.GetString(5);

            // sem comunicação há mais de 30 min (nunca contatou também conta)
            if (lastSeen is null || nowUtc - lastSeen.Value > TimeSpan.FromMinutes(OfflineSevereMinutes))
            {
                var detail = lastSeen is null
                    ? "nunca comunicou desde a instalação"
                    : $"sem comunicação há {FormatSince(nowUtc - lastSeen.Value)}";
                rows.Add(new AlertRow(id, name, KindOffline, detail));
            }

            // adulteração recente (o agente reporta AGENT_TAMPER, N19)
            if (lastTamper is { } tamper && nowUtc - tamper <= TimeSpan.FromHours(24))
            {
                rows.Add(new AlertRow(id, name, KindTamper,
                    $"sinal de adulteração do agente ({tamperReason ?? "motivo não informado"})"));
            }

            // ciência do aviso pendente há 7 dias ou mais (o device é registrado com UUIDv7)
            if (noticeAcked is null)
            {
                var enrolledAt = Uuid7.TimestampOf(id);
                if (nowUtc - enrolledAt >= TimeSpan.FromDays(NoticePendingDays))
                {
                    rows.Add(new AlertRow(id, name, KindNoticePending,
                        "ciência do aviso de monitoramento pendente desde a instalação"));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Reivindica o alerta respeitando o cooldown de 24 h por device+tipo. O UPDATE condicional
    /// é atômico: duas instâncias do worker nunca mandam o mesmo alerta duas vezes.
    /// </summary>
    private static async Task<bool> TryClaimAsync(
        NpgsqlConnection connection, Guid tenantId, AlertRow alert, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO device_alert_state (tenant_id, device_id, kind, last_notified_at)
            VALUES (@t, @d, @k, @now)
            ON CONFLICT (device_id, kind) DO UPDATE
              SET last_notified_at = EXCLUDED.last_notified_at
              WHERE device_alert_state.last_notified_at < @cutoff
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("d", alert.DeviceId);
        cmd.Parameters.AddWithValue("k", alert.Kind);
        cmd.Parameters.AddWithValue("now", nowUtc);
        cmd.Parameters.AddWithValue("cutoff", nowUtc.AddHours(-24));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task<List<string>> QueryRecipientsAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT u.email
            FROM users u
            LEFT JOIN user_email_prefs p ON p.user_id = u.id
            WHERE u.tenant_id = @t AND u.status = 'active' AND u.role IN ('owner','admin')
              AND COALESCE(p.fleet_alerts, true)
            ORDER BY u.email
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);

        var emails = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            emails.Add(reader.GetString(0));
        }

        return emails;
    }

    private string BuildHtml(OrgRow org, List<AlertRow> alerts)
    {
        var baseUrl = portalBaseUrl.TrimEnd('/');
        var sb = new StringBuilder();

        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:0 auto;color:#1a2233;\">");
        sb.Append("<h2 style=\"font-size:18px;\">Dispositivos que precisam de atenção</h2>");
        sb.Append($"<p style=\"color:#5a6478;font-size:13px;\">{HtmlText.Escape(org.Name)}</p>");
        sb.Append("<ul style=\"font-size:14px;padding-left:18px;\">");

        foreach (var alert in alerts.OrderBy(a => a.DeviceName))
        {
            sb.Append($"<li style=\"margin-bottom:6px;\"><strong>{HtmlText.Escape(alert.DeviceName)}</strong>: {HtmlText.Escape(alert.Detail)}</li>");
        }

        sb.Append("</ul>");
        sb.Append($"<p style=\"margin-top:20px;\"><a href=\"{baseUrl}/dispositivos?health=alert\" style=\"background:#c8f542;color:#1c2506;padding:10px 18px;border-radius:6px;text-decoration:none;font-weight:600;\">Ver dispositivos</a></p>");
        sb.Append("<hr style=\"border:none;border-top:1px solid #e4e8ef;margin:24px 0 12px;\">");
        sb.Append("<p style=\"color:#8a94a8;font-size:11px;\">Você recebe estes avisos porque administra a organização no +351 Monitor. ");
        sb.Append("Cada dispositivo é avisado no máximo uma vez por dia, e nunca fora do horário de trabalho configurado. ");
        sb.Append("Para deixar de receber, desative a preferência de alertas no portal.</p>");
        sb.Append("</div>");

        return sb.ToString();
    }

    private static string FormatSince(TimeSpan since) => since.TotalHours >= 24
        ? $"{(int)since.TotalDays} dia(s)"
        : since.TotalHours >= 1
            ? $"{(int)since.TotalHours} hora(s)"
            : $"{(int)since.TotalMinutes} minuto(s)";
}
