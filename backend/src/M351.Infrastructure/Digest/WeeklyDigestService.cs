using System.Text;
using M351.Infrastructure.Exports;
using M351.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Digest;

/// <summary>
/// DIGEST DUPLO por e-mail (F6, diferencial 4 do estudo — nenhum concorrente pesquisado
/// combina os dois). Toda segunda 08h NO FUSO DE CADA ORG o job horário produz:
///
///  1. o DIGEST DO GESTOR (F5, evoluído): resumo AGREGADO da semana fechada para Owners e
///     Admins ativos que não desligaram a preferência — índice de produtividade, variação
///     em PONTOS vs a semana anterior, composição nos quatro baldes, cobertura da
///     classificação, alertas de gestão vivos, aplicativos mais usados e saúde da frota.
///     JAMAIS uma lista de pessoas ordenada por métrica: agregado por organização e por
///     equipe, e nada mais. A palavra "ranking" não aparece, e alerta de escopo pessoa
///     viaja só como contagem (o nome fica na central de alertas, que é auditada);
///
///  2. o DIGEST PESSOAL (novo): um e-mail para o PRÓPRIO colaborador com os dados DELE —
///     horas ligadas, ativas, ociosidade, índice, tempo sem classificação —, a frase
///     pedagógica do ocioso e o link da página de transparência do dispositivo dele.
///     Não compara o colaborador com ninguém.
///
/// OPT-IN DO DIGEST PESSOAL: reutiliza <c>organizations.person_alerts_enabled</c>. É a
/// ÚNICA decisão registrada e AUDITADA da organização sobre tratar e comunicar resultado
/// no nível da PESSOA em vez de só no nível da equipe (decisão 5 do spec; a trilha
/// {person_alerts_enabled: de→para} está documentada em AuditLogEntry), e já é ajustável
/// em PATCH /organization e visível na tela de alertas. Uma coluna nova
/// (personal_digest_enabled) seria conceitualmente mais limpa — mandar o dado da pessoa
/// PARA a própria pessoa é transparência, não vigilância, e é o oposto de mandar o dado
/// dela para o gestor —, mas nasceria SEM interruptor: o PATCH que a ligaria vive em
/// OrganizationController, fora do alcance desta entrega. Preferimos um recurso operável
/// sob a chave existente a um recurso correto e inalcançável. Quando o endpoint da
/// organização for tocado, separar as duas chaves é a evolução recomendada.
///
/// OPT-OUT DO COLABORADOR: <c>user_email_prefs.weekly_digest</c>, a mesma preferência do
/// "resumo semanal por e-mail" que o portal já expõe — quem desligou o resumo semanal não
/// recebe resumo semanal nenhum, e não precisamos de uma segunda chave para dizer isso.
///
/// IDEMPOTÊNCIA: dois carimbos independentes na organização,
/// <c>last_weekly_digest_at</c> (gestor) e <c>last_personal_digest_at</c> (pessoal), cada
/// um gravado logo depois do seu próprio envio. Separados de propósito: falha no lote
/// pessoal não faz o gestor receber o e-mail duas vezes na hora seguinte.
/// </summary>
public class WeeklyDigestService(
    NpgsqlDataSource dataSource,
    IEmailSender emailSender,
    string portalBaseUrl,
    ILogger<WeeklyDigestService> logger)
{
    /// <summary>Hora local (da org) do envio: segunda, 08h.</summary>
    public const int SendHourLocal = 8;

    private sealed record OrgRow(
        Guid Id, string Name, string Slug, string Timezone, string Vocabulary,
        bool PersonalDigestEnabled, DateTimeOffset? LastManagerAt, DateTimeOffset? LastPersonalAt);

    private sealed record Attention(int Silent24h, int NoticePending, int TamperLast7d);

    /// <summary>Uma passada: envia os digests das orgs cuja hora local é segunda 08h. Retorna quantos e-mails saíram.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var sent = 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var orgs = new List<OrgRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, name, slug, timezone, classification_vocabulary,
                   person_alerts_enabled, last_weekly_digest_at, last_personal_digest_at
            FROM organizations
            WHERE status = 'active'
            """,
            connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                orgs.Add(new OrgRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
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

            // semana fechada: segunda a domingo ANTERIORES, em datas locais do tenant
            // (summary_date é o dia local — split à meia-noite do fuso da org na agregação)
            var weekStart = DateOnly.FromDateTime(local.Date.AddDays(-7));
            var weekEnd = DateOnly.FromDateTime(local.Date.AddDays(-1));

            sent += await SendManagerDigestAsync(connection, org, nowUtc, weekStart, weekEnd, ct);
            sent += await SendPersonalDigestsAsync(connection, org, nowUtc, weekStart, weekEnd, ct);
        }

        return sent;
    }

    // ------------------------------------------------------------------ gestor

    private async Task<int> SendManagerDigestAsync(
        NpgsqlConnection connection, OrgRow org, DateTimeOffset nowUtc,
        DateOnly weekStart, DateOnly weekEnd, CancellationToken ct)
    {
        // idempotência: já enviado nesta janela (o job roda de hora em hora)
        if (org.LastManagerAt is { } last && nowUtc - last < TimeSpan.FromDays(6))
        {
            return 0;
        }

        var recipients = await QueryManagerRecipientsAsync(connection, org.Id, ct);
        if (recipients.Count == 0)
        {
            return 0;
        }

        var summary = await LoadSummaryAsync(connection, org.Id, weekStart, weekEnd, ct);
        var attention = await QueryAttentionAsync(connection, org.Id, ct);

        var subject = $"Resumo da semana no +351 Monitor, {DigestText.Short(weekStart)} a {DigestText.Short(weekEnd)}";
        var html = BuildManagerHtml(org, weekStart, weekEnd, summary, attention);

        var sent = 0;
        foreach (var recipient in recipients)
        {
            await emailSender.SendAsync(new EmailMessage(recipient, subject, html, IsHtml: true), ct);
            sent++;
        }

        await StampAsync(connection, "last_weekly_digest_at", org.Id, nowUtc, ct);
        logger.LogInformation(
            "Digest do gestor enviado: org {OrgId}, {Recipients} destinatário(s)", org.Id, recipients.Count);
        return sent;
    }

    // ------------------------------------------------------------------ colaborador

    private async Task<int> SendPersonalDigestsAsync(
        NpgsqlConnection connection, OrgRow org, DateTimeOffset nowUtc,
        DateOnly weekStart, DateOnly weekEnd, CancellationToken ct)
    {
        if (!org.PersonalDigestEnabled)
        {
            return 0;
        }

        if (org.LastPersonalAt is { } last && nowUtc - last < TimeSpan.FromDays(6))
        {
            return 0;
        }

        var people = await WeeklySummary.PeopleTotalsAsync(connection, org.Id, weekStart, weekEnd, ct);
        if (people.Count == 0)
        {
            return 0;
        }

        var matches = await WeeklySummary.PersonEmailMatchesAsync(connection, org.Id, ct);

        // casamento ÚNICO nos DOIS sentidos: um e-mail para a pessoa E uma pessoa para o
        // e-mail. Duas pessoas apontando para o mesmo endereço, ou uma pessoa apontando
        // para dois endereços, é ambiguidade — e dado pessoal não vai para endereço
        // adivinhado. Nesses casos ninguém recebe e o log diz por quê.
        var bySid = matches
            .GroupBy(m => m.Sid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var sidsPerEmail = matches
            .GroupBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Sid).Distinct(StringComparer.Ordinal).Count(),
                StringComparer.OrdinalIgnoreCase);

        var sent = 0;
        var withoutRecipient = 0;

        foreach (var person in people)
        {
            var candidates = bySid.GetValueOrDefault(person.Sid) ?? [];
            if (candidates.Count != 1 || sidsPerEmail.GetValueOrDefault(candidates[0].Email) != 1)
            {
                // NÃO INVENTAR endereço: sem casamento, ou com casamento ambíguo, o digest
                // pessoal simplesmente não sai. O SID identifica a pessoa no log; o nome e o
                // e-mail não vão para log (dado pessoal fora de arquivo de texto).
                withoutRecipient++;
                logger.LogInformation(
                    "Digest pessoal sem destinatário: org {OrgId}, pessoa {Sid}, {Matches} casamento(s) de e-mail. "
                    + "Cadastre o usuário do portal com o e-mail correspondente ao usuário do Windows.",
                    org.Id, person.Sid, candidates.Count);
                continue;
            }

            if (!candidates[0].WantsWeekly)
            {
                continue; // opt-out explícito do próprio colaborador
            }

            var token = await WeeklySummary.LatestTransparencyTokenAsync(connection, org.Id, person.Sid, ct);
            var subject = $"Seu resumo da semana, {DigestText.Short(weekStart)} a {DigestText.Short(weekEnd)}";
            var html = BuildPersonalHtml(org, weekStart, weekEnd, person, token);

            await emailSender.SendAsync(new EmailMessage(candidates[0].Email, subject, html, IsHtml: true), ct);
            sent++;
        }

        await StampAsync(connection, "last_personal_digest_at", org.Id, nowUtc, ct);
        logger.LogInformation(
            "Digest pessoal enviado: org {OrgId}, {Sent} e-mail(is), {Missing} pessoa(s) sem destinatário conhecido",
            org.Id, sent, withoutRecipient);
        return sent;
    }

    // ------------------------------------------------------------------ dados

    /// <summary>Semana + semana anterior + apps + alertas vivos, para o e-mail e para o PDF.</summary>
    private static async Task<ManagerSummary> LoadSummaryAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly weekStart, DateOnly weekEnd, CancellationToken ct)
    {
        var previousStart = weekStart.AddDays(-7);
        var previousEnd = weekStart.AddDays(-1);

        return new ManagerSummary(
            await WeeklySummary.TotalsAsync(connection, tenantId, weekStart, weekEnd, ct),
            await WeeklySummary.TotalsAsync(connection, tenantId, previousStart, previousEnd, ct),
            await WeeklySummary.TopAppsAsync(connection, tenantId, weekStart, weekEnd, ct),
            await WeeklySummary.LiveAlertsAsync(connection, tenantId, ct));
    }

    private static async Task StampAsync(
        NpgsqlConnection connection, string column, Guid tenantId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // nome de coluna vem de literal do próprio código (nunca de entrada externa)
        await using var update = new NpgsqlCommand(
            $"UPDATE organizations SET {column} = @now WHERE id = @id", connection);
        update.Parameters.AddWithValue("now", nowUtc);
        update.Parameters.AddWithValue("id", tenantId);
        await update.ExecuteNonQueryAsync(ct);
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

    private static async Task<List<string>> QueryManagerRecipientsAsync(
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

    // ------------------------------------------------------------------ HTML do gestor

    private string BuildManagerHtml(
        OrgRow org, DateOnly weekStart, DateOnly weekEnd, ManagerSummary summary, Attention attention)
    {
        var baseUrl = portalBaseUrl.TrimEnd('/');
        var (workLabel, neutralLabel, notWorkLabel, unclassifiedLabel) = WeeklySummary.Labels(org.Vocabulary);
        var totals = summary.Week;
        var previous = summary.Previous;
        var sb = new StringBuilder();

        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:0 auto;color:#1a2233;\">");
        sb.Append($"<h2 style=\"font-size:18px;\">Resumo da semana, {Html(org.Name)}</h2>");
        sb.Append($"<p style=\"color:#5a6478;font-size:13px;\">{DigestText.Long(weekStart)} a {DigestText.Long(weekEnd)}"
            + $" &middot; {totals.PersonCount} pessoa(s) com dado, {totals.DeviceCount} dispositivo(s)</p>");

        // índice SEMPRE com a cobertura ao lado (decisão 4 do spec: nunca um sem o outro)
        sb.Append("<div style=\"background:#f4f7f0;border-radius:8px;padding:14px 16px;margin:16px 0;\">");
        sb.Append($"<p style=\"margin:0;font-size:15px;\"><strong>Índice de produtividade: {DigestText.Pct(totals.Index)}</strong>"
            + $" <span style=\"color:#5a6478;font-size:13px;\">({DigestText.IndexDelta(totals.Index, previous.Index)})</span></p>");
        sb.Append($"<p style=\"margin:6px 0 0;font-size:14px;\">Cobertura da classificação: <strong>{DigestText.Pct(totals.Coverage)}</strong></p>");
        sb.Append($"<p style=\"margin:6px 0 0;color:#5a6478;font-size:12px;\">Índice = {workLabel.ToLowerInvariant()} "
            + $"÷ tempo ativo já classificado; o tempo em aplicativos sem categoria fica fora da conta e aparece "
            + $"na cobertura. {Html(WeeklySummary.Framing)}.</p>");
        sb.Append("</div>");

        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:14px;\">");
        AppendMetricRow(sb, "Horas ativas da equipe", totals.SecondsActive, previous.SecondsActive);
        AppendMetricRow(sb, "Horas com a máquina ligada", totals.SecondsOn, previous.SecondsOn);
        AppendMetricRow(sb, "Horas ociosas", totals.SecondsIdle, previous.SecondsIdle);
        sb.Append("</table>");
        sb.Append($"<p style=\"color:#5a6478;font-size:12px;margin-top:6px;\">{Html(WeeklySummary.IdleLesson)}</p>");

        // composição nos QUATRO baldes, sempre sobre o tempo ATIVO
        sb.Append("<h3 style=\"font-size:15px;margin-top:20px;\">Composição do tempo ativo</h3>");
        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:13px;\">");
        AppendBucketRow(sb, workLabel, totals.SecondsWork, totals.SecondsActive);
        AppendBucketRow(sb, neutralLabel, totals.SecondsNeutral, totals.SecondsActive);
        AppendBucketRow(sb, notWorkLabel, totals.SecondsNotWork, totals.SecondsActive);
        AppendBucketRow(sb, unclassifiedLabel, totals.SecondsUnclassified, totals.SecondsActive);
        sb.Append("</table>");

        if (summary.Alerts.Count > 0)
        {
            sb.Append("<h3 style=\"font-size:15px;margin-top:20px;\">Alertas de gestão em aberto</h3><ul style=\"font-size:13px;padding-left:18px;\">");
            foreach (var group in summary.Alerts)
            {
                sb.Append($"<li style=\"margin-bottom:4px;\">{Html(DigestText.Alert(group))}</li>");
            }

            sb.Append("</ul>");
            sb.Append($"<p style=\"font-size:12px;\"><a href=\"{baseUrl}/alertas\" style=\"color:#5a6478;\">Ver a central de alertas</a></p>");
        }

        if (summary.TopApps.Count > 0)
        {
            sb.Append("<h3 style=\"font-size:15px;margin-top:20px;\">Aplicativos mais usados</h3>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:13px;\">");
            foreach (var app in summary.TopApps)
            {
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:4px 0;border-bottom:1px solid #e4e8ef;\">{Html(app.DisplayName)} "
                    + $"<span style=\"color:#5a6478;\">({Html(WeeklySummary.AppLabel(app.Classification, org.Vocabulary))})</span></td>");
                sb.Append($"<td style=\"padding:4px 0;border-bottom:1px solid #e4e8ef;text-align:right;\">{DigestText.Hours(app.SecondsActive)}</td>");
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
        sb.Append("<p style=\"color:#8a94a8;font-size:11px;\">Este resumo é AGREGADO por organização e por equipe. "
            + "O produto não ordena pessoas por desempenho e nunca publica comparação entre colegas.</p>");
        sb.Append($"<p style=\"color:#8a94a8;font-size:11px;\">{Html(ExportService.JornadaDisclaimer)}</p>");
        sb.Append($"<p style=\"color:#8a94a8;font-size:11px;\">Política de coleta e transparência: <a href=\"{baseUrl}/transparencia/{Uri.EscapeDataString(org.Slug)}\" style=\"color:#5a6478;\">{baseUrl}/transparencia/{Html(org.Slug)}</a><br>");
        sb.Append("Para deixar de receber este resumo, desative a preferência de e-mail no portal ou responda a esta mensagem.</p>");
        sb.Append("</div>");

        return sb.ToString();
    }

    // ------------------------------------------------------------------ HTML do colaborador

    private string BuildPersonalHtml(
        OrgRow org, DateOnly weekStart, DateOnly weekEnd, PersonWeek person, Guid? transparencyToken)
    {
        var baseUrl = portalBaseUrl.TrimEnd('/');
        var (workLabel, _, _, unclassifiedLabel) = WeeklySummary.Labels(org.Vocabulary);
        var t = person.Totals;
        var sb = new StringBuilder();

        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:0 auto;color:#1a2233;\">");
        sb.Append($"<h2 style=\"font-size:18px;\">Seu resumo da semana, {Html(person.Label)}</h2>");
        sb.Append($"<p style=\"color:#5a6478;font-size:13px;\">{DigestText.Long(weekStart)} a {DigestText.Long(weekEnd)}"
            + $" &middot; {Html(org.Name)}</p>");
        sb.Append("<p style=\"font-size:13px;\">Estes são os SEUS números, enviados só para você. "
            + "O +351 Monitor não ordena pessoas por desempenho nem compara você com colegas.</p>");

        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:14px;\">");
        AppendPlainRow(sb, "Horas com a máquina ligada", DigestText.Hours(t.SecondsOn));
        AppendPlainRow(sb, "Horas ativas (teclado e mouse)", DigestText.Hours(t.SecondsActive));
        AppendPlainRow(sb, "Horas ociosas", $"{DigestText.Hours(t.SecondsIdle)} ({DigestText.Pct(t.IdleShare)} do tempo ligado)");
        AppendPlainRow(sb, $"Tempo em aplicativos {unclassifiedLabel.ToLowerInvariant()}", DigestText.Hours(t.SecondsUnclassified));
        sb.Append("</table>");

        sb.Append("<div style=\"background:#f4f7f0;border-radius:8px;padding:14px 16px;margin:16px 0;\">");
        sb.Append($"<p style=\"margin:0;font-size:15px;\"><strong>Seu índice de produtividade: {DigestText.Pct(t.Index)}</strong></p>");
        sb.Append($"<p style=\"margin:6px 0 0;font-size:14px;\">Cobertura da classificação: <strong>{DigestText.Pct(t.Coverage)}</strong></p>");
        sb.Append($"<p style=\"margin:6px 0 0;color:#5a6478;font-size:12px;\">Índice = {workLabel.ToLowerInvariant()} "
            + $"÷ tempo ativo já classificado. {Html(WeeklySummary.Framing)}, sobre aplicativos, nunca sobre pessoas. "
            + "Cobertura baixa significa que boa parte do seu tempo está em aplicativos ainda sem categoria.</p>");
        sb.Append("</div>");

        sb.Append($"<p style=\"color:#5a6478;font-size:12px;\">{Html(WeeklySummary.IdleLesson)}</p>");

        var link = transparencyToken is { } token
            ? $"{baseUrl}/t/{token}"
            : $"{baseUrl}/transparencia/{Uri.EscapeDataString(org.Slug)}";
        sb.Append($"<p style=\"margin-top:24px;\"><a href=\"{link}\" style=\"background:#c8f542;color:#1c2506;padding:10px 18px;border-radius:6px;text-decoration:none;font-weight:600;\">Ver o que é coletado no seu dispositivo</a></p>");
        sb.Append($"<p style=\"color:#5a6478;font-size:12px;\">Página de transparência: <a href=\"{link}\" style=\"color:#5a6478;\">{Html(link)}</a></p>");

        sb.Append("<hr style=\"border:none;border-top:1px solid #e4e8ef;margin:24px 0 12px;\">");
        sb.Append($"<p style=\"color:#8a94a8;font-size:11px;\">{Html(ExportService.JornadaDisclaimer)}</p>");
        sb.Append("<p style=\"color:#8a94a8;font-size:11px;\">Para deixar de receber este resumo, desative a preferência de e-mail no portal.</p>");
        sb.Append("</div>");

        return sb.ToString();
    }

    // ------------------------------------------------------------------ blocos de tabela

    private static void AppendMetricRow(StringBuilder sb, string label, long seconds, long previousSeconds)
    {
        sb.Append("<tr>");
        sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;\">{label}</td>");
        sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;text-align:right;font-weight:600;\">{DigestText.Hours(seconds)}</td>");
        sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;text-align:right;color:#5a6478;font-size:12px;\">{DigestText.HoursDelta(seconds, previousSeconds)}</td>");
        sb.Append("</tr>");
    }

    private static void AppendBucketRow(StringBuilder sb, string label, long seconds, long activeSeconds)
    {
        sb.Append("<tr>");
        sb.Append($"<td style=\"padding:4px 0;border-bottom:1px solid #e4e8ef;\">{Html(label)}</td>");
        sb.Append($"<td style=\"padding:4px 0;border-bottom:1px solid #e4e8ef;text-align:right;\">{DigestText.Hours(seconds)}</td>");
        sb.Append($"<td style=\"padding:4px 0;border-bottom:1px solid #e4e8ef;text-align:right;color:#5a6478;\">{DigestText.Share(seconds, activeSeconds)}</td>");
        sb.Append("</tr>");
    }

    private static void AppendPlainRow(StringBuilder sb, string label, string value)
    {
        sb.Append("<tr>");
        sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;\">{Html(label)}</td>");
        sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;text-align:right;font-weight:600;\">{Html(value)}</td>");
        sb.Append("</tr>");
    }

    private static string Html(string value) => HtmlText.Escape(value);
}

/// <summary>Semana + comparação + apps + alertas vivos: o que o e-mail do gestor e o PDF mostram.</summary>
public sealed record ManagerSummary(
    WeekTotals Week, WeekTotals Previous, List<TopApp> TopApps, List<AlertGroup> Alerts);
