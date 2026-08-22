using System.Globalization;
using System.Net;
using System.Text;
using M351.Infrastructure.Exports;
using M351.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Digest;

/// <summary>
/// Digest semanal por e-mail (F5, antecipação dos "relatórios agendados" da v1.1 na forma
/// mais barata): toda segunda 08h NO FUSO DE CADA ORG, um resumo da semana anterior para
/// Owners e Admins ativos que não desligaram a preferência. Job HORÁRIO no worker; a
/// idempotência é por organizations.last_weekly_digest_at (reinício do worker na mesma
/// janela não reenvia). Vocabulário NEUTRO: baldes de categoria do próprio tenant
/// (relacionado ao trabalho/neutro/não relacionado), jamais ranking de pessoas; contagem
/// de dispositivos rotulada como "ativos", nunca prévia de fatura (billing é manual).
/// </summary>
public class WeeklyDigestService(
    NpgsqlDataSource dataSource,
    IEmailSender emailSender,
    string portalBaseUrl,
    ILogger<WeeklyDigestService> logger)
{
    /// <summary>Hora local (da org) do envio: segunda, 08h.</summary>
    public const int SendHourLocal = 8;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private sealed record OrgRow(Guid Id, string Name, string Slug, string Timezone, DateTimeOffset? LastSentAt);

    private sealed record WeekTotals(
        long SecondsActive, long SecondsIdle,
        long SecondsWork, long SecondsNeutral, long SecondsNotWork,
        int DeviceCount);

    private sealed record TopApp(string DisplayName, short? Classification, long SecondsActive);

    private sealed record Attention(int Silent24h, int NoticePending, int TamperLast7d);

    /// <summary>Uma passada: envia o digest das orgs cuja hora local é segunda 08h. Retorna quantos e-mails saíram.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var sent = 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var orgs = new List<OrgRow>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, name, slug, timezone, last_weekly_digest_at FROM organizations WHERE status = 'active'",
            connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                orgs.Add(new OrgRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        foreach (var org in orgs)
        {
            TimeZoneInfo tz;
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(org.Timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                logger.LogWarning("Digest: fuso desconhecido {Timezone} na org {OrgId}, pulando", org.Timezone, org.Id);
                continue;
            }

            var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
            if (local.DayOfWeek != DayOfWeek.Monday || local.Hour != SendHourLocal)
            {
                continue;
            }

            // idempotência: já enviado nesta janela (o job roda de hora em hora)
            if (org.LastSentAt is { } last && nowUtc - last < TimeSpan.FromDays(6))
            {
                continue;
            }

            // semana fechada: segunda a domingo ANTERIORES, em datas locais do tenant
            // (summary_date é o dia local — split à meia-noite do fuso da org na agregação)
            var weekStart = DateOnly.FromDateTime(local.Date.AddDays(-7));
            var weekEnd = DateOnly.FromDateTime(local.Date.AddDays(-1));
            var prevStart = weekStart.AddDays(-7);
            var prevEnd = weekStart.AddDays(-1);

            var totals = await QueryTotalsAsync(connection, org.Id, weekStart, weekEnd, ct);
            var previous = await QueryTotalsAsync(connection, org.Id, prevStart, prevEnd, ct);
            var topApps = await QueryTopAppsAsync(connection, org.Id, weekStart, weekEnd, ct);
            var attention = await QueryAttentionAsync(connection, org.Id, ct);
            var recipients = await QueryRecipientsAsync(connection, org.Id, ct);

            if (recipients.Count == 0)
            {
                continue;
            }

            var subject = $"Resumo da semana no +351 Monitor, {weekStart.ToString("dd/MM", PtBr)} a {weekEnd.ToString("dd/MM", PtBr)}";
            var html = BuildHtml(org, weekStart, weekEnd, totals, previous, topApps, attention);

            foreach (var recipient in recipients)
            {
                await emailSender.SendAsync(new EmailMessage(recipient, subject, html, IsHtml: true), ct);
                sent++;
            }

            await using (var update = new NpgsqlCommand(
                "UPDATE organizations SET last_weekly_digest_at = @now WHERE id = @id", connection))
            {
                update.Parameters.AddWithValue("now", nowUtc);
                update.Parameters.AddWithValue("id", org.Id);
                await update.ExecuteNonQueryAsync(ct);
            }

            logger.LogInformation("Digest semanal enviado: org {OrgId}, {Recipients} destinatário(s)", org.Id, recipients.Count);
        }

        return sent;
    }

    private static async Task<WeekTotals> QueryTotalsAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        // soma TODAS as lanes: a lane-máquina (uuid zero) tem seconds_active estruturalmente 0,
        // então a soma não conta em dobro (mesma semântica do GROUPING SETS do dashboard)
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(SUM(seconds_active),0), COALESCE(SUM(seconds_idle),0),
                   COALESCE(SUM(seconds_work_related),0), COALESCE(SUM(seconds_neutral),0),
                   COALESCE(SUM(seconds_not_work_related),0),
                   COUNT(DISTINCT device_id)::int
            FROM daily_device_summaries
            WHERE tenant_id = @t AND summary_date BETWEEN @from AND @to
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new WeekTotals(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5));
    }

    private static async Task<List<TopApp>> QueryTopAppsAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(tac.custom_display_name, ac.display_name) AS display_name,
                   c.classification,
                   SUM(dau.seconds_active)::bigint AS seconds_active
            FROM daily_app_usage dau
            JOIN app_catalog ac ON ac.id = dau.app_id
            LEFT JOIN tenant_app_categories tac ON tac.tenant_id = dau.tenant_id AND tac.app_id = dau.app_id
            LEFT JOIN categories c ON c.id = tac.category_id
            WHERE dau.tenant_id = @t AND dau.summary_date BETWEEN @from AND @to
            GROUP BY 1, 2
            ORDER BY seconds_active DESC
            LIMIT 5
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        var apps = new List<TopApp>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            apps.Add(new TopApp(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt16(1),
                reader.GetInt64(2)));
        }

        return apps;
    }

    private static async Task<Attention> QueryAttentionAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT
              COUNT(*) FILTER (WHERE status = 'active' AND (last_seen_at IS NULL OR last_seen_at < now() - interval '24 hours'))::int,
              COUNT(*) FILTER (WHERE status = 'active' AND notice_acked_at IS NULL)::int,
              COUNT(*) FILTER (WHERE status = 'active' AND last_tamper_at > now() - interval '7 days')::int
            FROM devices
            WHERE tenant_id = @t
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new Attention(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<List<string>> QueryRecipientsAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        // Owner/Admin ativos que não desligaram a preferência (linha ausente = ligado)
        await using var cmd = new NpgsqlCommand(
            """
            SELECT u.email
            FROM users u
            LEFT JOIN user_email_prefs p ON p.user_id = u.id
            WHERE u.tenant_id = @t AND u.status = 'active' AND u.role IN ('owner','admin')
              AND COALESCE(p.weekly_digest, true)
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

    private string BuildHtml(
        OrgRow org, DateOnly weekStart, DateOnly weekEnd,
        WeekTotals totals, WeekTotals previous, List<TopApp> topApps, Attention attention)
    {
        var baseUrl = portalBaseUrl.TrimEnd('/');
        var sb = new StringBuilder();

        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:0 auto;color:#1a2233;\">");
        sb.Append($"<h2 style=\"font-size:18px;\">Resumo da semana, {Html(org.Name)}</h2>");
        sb.Append($"<p style=\"color:#5a6478;font-size:13px;\">{weekStart.ToString("dd/MM/yyyy", PtBr)} a {weekEnd.ToString("dd/MM/yyyy", PtBr)}</p>");

        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:14px;\">");
        AppendMetricRow(sb, "Horas ativas da equipe", totals.SecondsActive, previous.SecondsActive);
        AppendMetricRow(sb, "Horas ociosas", totals.SecondsIdle, previous.SecondsIdle);
        AppendMetricRow(sb, "Em apps relacionados ao trabalho", totals.SecondsWork, previous.SecondsWork);
        sb.Append("</table>");

        if (totals.DeviceCount != previous.DeviceCount && previous.DeviceCount > 0)
        {
            sb.Append($"<p style=\"color:#5a6478;font-size:12px;\">Base de comparação: {totals.DeviceCount} dispositivo(s) com dados nesta semana, {previous.DeviceCount} na anterior.</p>");
        }

        if (topApps.Count > 0)
        {
            sb.Append("<h3 style=\"font-size:15px;margin-top:20px;\">Aplicativos mais usados</h3>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:13px;\">");
            foreach (var app in topApps)
            {
                var label = app.Classification switch
                {
                    1 => "relacionado ao trabalho",
                    0 => "neutro",
                    -1 => "não relacionado",
                    _ => "sem categoria",
                };
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:4px 0;border-bottom:1px solid #e4e8ef;\">{Html(app.DisplayName)} <span style=\"color:#5a6478;\">({label})</span></td>");
                sb.Append($"<td style=\"padding:4px 0;border-bottom:1px solid #e4e8ef;text-align:right;\">{FormatHours(app.SecondsActive)}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</table>");
        }

        var attentionItems = new List<string>();
        if (attention.Silent24h > 0)
            attentionItems.Add($"{attention.Silent24h} dispositivo(s) sem comunicação há mais de 24 horas");
        if (attention.NoticePending > 0)
            attentionItems.Add($"{attention.NoticePending} dispositivo(s) com ciência do aviso pendente");
        if (attention.TamperLast7d > 0)
            attentionItems.Add($"{attention.TamperLast7d} dispositivo(s) com sinal de adulteração nos últimos 7 dias");

        if (attentionItems.Count > 0)
        {
            sb.Append("<h3 style=\"font-size:15px;margin-top:20px;\">Precisa de atenção</h3><ul style=\"font-size:13px;padding-left:18px;\">");
            foreach (var item in attentionItems)
            {
                sb.Append($"<li style=\"margin-bottom:4px;\">{item}</li>");
            }

            sb.Append("</ul>");
        }

        sb.Append($"<p style=\"margin-top:24px;\"><a href=\"{baseUrl}/visao-geral\" style=\"background:#c8f542;color:#1c2506;padding:10px 18px;border-radius:6px;text-decoration:none;font-weight:600;\">Abrir o painel</a></p>");

        sb.Append("<hr style=\"border:none;border-top:1px solid #e4e8ef;margin:24px 0 12px;\">");
        sb.Append($"<p style=\"color:#8a94a8;font-size:11px;\">{Html(ExportService.JornadaDisclaimer)}</p>");
        sb.Append($"<p style=\"color:#8a94a8;font-size:11px;\">Política de coleta e transparência: <a href=\"{baseUrl}/transparencia/{Uri.EscapeDataString(org.Slug)}\" style=\"color:#5a6478;\">{baseUrl}/transparencia/{Html(org.Slug)}</a><br>");
        sb.Append("Para deixar de receber este resumo, desative a preferência de e-mail no portal ou responda a esta mensagem.</p>");
        sb.Append("</div>");

        return sb.ToString();
    }

    private static void AppendMetricRow(StringBuilder sb, string label, long seconds, long previousSeconds)
    {
        var delta = FormatDelta(seconds, previousSeconds);
        sb.Append("<tr>");
        sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;\">{label}</td>");
        sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;text-align:right;font-weight:600;\">{FormatHours(seconds)}</td>");
        sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;text-align:right;color:#5a6478;font-size:12px;\">{delta}</td>");
        sb.Append("</tr>");
    }

    /// <summary>Delta NEUTRO vs semana anterior (sem juízo de valor, sem cor de bom/ruim).</summary>
    private static string FormatDelta(long current, long previous)
    {
        if (previous <= 0)
        {
            return "sem base anterior";
        }

        var pct = (double)(current - previous) / previous * 100;
        return pct switch
        {
            > 0.5 => $"{pct.ToString("0", PtBr)}% maior que na semana anterior",
            < -0.5 => $"{Math.Abs(pct).ToString("0", PtBr)}% menor que na semana anterior",
            _ => "estável vs semana anterior",
        };
    }

    private static string FormatHours(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}h{ts.Minutes:00}";
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
