using System.Globalization;
using System.Text;
using M351.Domain.Entities;
using M351.Infrastructure.Email;
using M351.Infrastructure.Exports;
using M351.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.AccountHealth;

/// <summary>
/// Score de saúde de conta para o CS (TELEMETRIA INTERNA, jamais visível ao cliente).
///
/// POR QUE EXISTE: a cobrança manual é o único sensor de churn do produto hoje, e ela avisa em
/// D+20, quando a decisão de cancelar já foi tomada. Os sinais abaixo aparecem 30 a 60 dias
/// antes, quando um telefonema ainda salva a conta.
///
/// SEM TABELA NOVA, DE PROPÓSITO: um job semanal fazendo queries diretas sobre o que já existe
/// (users.last_login_at, devices.last_seen_at, daily_device_summaries, audit_log,
/// tenant_app_categories). Score histórico e tendência são coisa de quando houver base para
/// calibrar; antes disso, tabela nova é peso morto. A execução fica registrada em
/// maintenance_runs, como os demais jobs do worker.
///
/// REGRAS INICIAIS (pesos em pontos de RISCO, score de saúde = 100 menos o risco):
///  - nenhum login de usuário há 14 dias (30);
///  - tenant sem eventos há 7 dias, ou seja, nenhum dispositivo se comunicando (30);
///  - queda de mais de 20% nos dispositivos com dados, semana contra semana (25);
///  - nenhuma leitura de relatório nem export em 14 dias, ou seja, ninguém olha (10);
///  - muitos aplicativos sem categoria, ou seja, curadoria nunca feita (5).
///
/// CARÊNCIA POR IDADE DA CONTA: cada regra só vale se a org for MAIS VELHA que a janela dela.
/// Sem isso, toda conta recém-criada nasceria "crítica" e a lista viraria ruído já na primeira
/// semana do piloto.
///
/// LINHA VERMELHA RESPEITADA: o que sai daqui é AGREGADO por organização (contagem de
/// dispositivos, datas de último acesso, contagem de ações de leitura). Nada de dado monitorado
/// de pessoa, nada de nome de titular, nada de título de janela. É telemetria de uso do produto
/// pela conta contratante, não observação de quem trabalha nela.
/// </summary>
public class AccountHealthService(
    NpgsqlDataSource dataSource,
    IEmailSender emailSender,
    string alertEmail,
    string? excludedSlug,
    ILogger<AccountHealthService> logger)
{
    /// <summary>Sem nenhum login de usuário nesta janela, a conta está abandonada pelo gestor.</summary>
    public const int NoLoginDays = 14;

    /// <summary>Sem nenhum dispositivo se comunicando nesta janela, o produto parou de existir na conta.</summary>
    public const int NoEventsDays = 7;

    /// <summary>Queda relativa de dispositivos com dados que dispara o sinal (semana contra semana).</summary>
    public const double DeviceDropThreshold = 0.20;

    /// <summary>Janela das ações de leitura e export no audit_log.</summary>
    public const int ReadWindowDays = 14;

    /// <summary>Acima disto, a curadoria de aplicativos nunca foi feita (onboarding parado).</summary>
    public const int UncategorizedAppsThreshold = 10;

    /// <summary>Janela de uso considerada para contar aplicativos sem categoria.</summary>
    public const int UncategorizedAppsWindowDays = 30;

    public const string SignalNoLogin = "sem_login";
    public const string SignalNoEvents = "sem_eventos";
    public const string SignalDeviceDrop = "queda_dispositivos";
    public const string SignalNoReads = "sem_leitura";
    public const string SignalUncategorizedApps = "apps_sem_categoria";

    /// <summary>Cabeçalho EXATO esperado por crm/import.php (separador ';', UTF-8 com BOM).</summary>
    public const string CsvHeader = "empresa;contato;email;whatsapp;estacoes;origem;observacoes;cnpj";

    /// <summary>Origem do lead no CRM. 'outro' é o balde do import.php para o que não é venda nova.</summary>
    private const string CrmSource = "outro";

    /// <summary>Ações de audit_log que contam como "alguém olhou os dados" (leitura e export).</summary>
    private static readonly string[] ReadActions =
    [
        AuditActions.ViewReport,
        AuditActions.ViewTimeline,
        AuditActions.ExportCsv,
        AuditActions.DsrExport,
    ];

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private sealed record OrgRow(Guid Id, string Name, string Slug, string Plan, string Timezone, DateTimeOffset CreatedAt);

    /// <summary>
    /// Uma passada: avalia todas as orgs ativas, envia o e-mail interno com o CSV anexo quando
    /// houver ao menos uma conta em risco e grava maintenance_runs. Retorna as contas em risco
    /// (lista vazia é resultado legítimo, e nesse caso NENHUM e-mail sai: silêncio é o estado
    /// correto de uma base saudável, e-mail "nada a relatar" toda semana treina o CS a ignorar).
    /// </summary>
    public async Task<AccountHealthReport> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var detail = new Dictionary<string, object>();
        try
        {
            var report = await EvaluateAsync(nowUtc, ct);
            detail["contas_avaliadas"] = report.Evaluated;
            detail["contas_em_risco"] = report.AtRisk.Count;
            detail["criticas"] = report.AtRisk.Count(r => r.HealthScore <= 50);

            if (report.AtRisk.Count > 0)
            {
                var csv = CsvBytes(report.AtRisk);
                var subject = $"Saúde das contas, {report.AtRisk.Count} em risco "
                    + $"({nowUtc.ToString("dd/MM/yyyy", PtBr)})";
                var message = new EmailMessage(
                    alertEmail, subject, BuildHtml(report.AtRisk, nowUtc), IsHtml: true,
                    Attachments: [new EmailAttachment(
                        $"saude-contas-{nowUtc:yyyy-MM-dd}.csv", "text/csv", csv)]);
                await emailSender.SendAsync(message, ct);
                detail["email_enviado"] = true;
            }
            else
            {
                detail["email_enviado"] = false;
            }

            await MaintenanceRunRecorder.RecordAsync(
                dataSource, MaintenanceRunRecorder.AccountHealth, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusOk, detail, ct);

            logger.LogInformation(
                "Saúde de conta: {Avaliadas} conta(s) avaliada(s), {Risco} em risco.",
                report.Evaluated, report.AtRisk.Count);

            return report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Score de saúde de conta falhou.");
            detail["error"] = ex.Message;
            await SafeRecordErrorAsync(startedAt, detail);
            return new AccountHealthReport(0, []);
        }
    }

    /// <summary>
    /// Avalia todas as orgs ativas e devolve SÓ as que acumularam algum ponto de risco,
    /// da mais crítica para a menos. Não envia nada, não grava nada: é o núcleo consultável
    /// pelos testes e por qualquer conferência manual.
    /// </summary>
    public async Task<AccountHealthReport> EvaluateAsync(
        DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var orgs = new List<OrgRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, name, slug, plan, timezone, created_at
            FROM organizations
            WHERE status = 'active' AND (@excluded = '' OR slug <> @excluded)
            ORDER BY name
            """, connection))
        {
            // o tenant de demonstração pública é reiniciado toda semana e nunca é uma conta
            // de verdade: deixá-lo na lista contaminaria o painel do CS com um falso churn
            cmd.Parameters.AddWithValue("excluded", excludedSlug ?? string.Empty);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                orgs.Add(new OrgRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        var rows = new List<AccountHealthRow>();
        foreach (var org in orgs)
        {
            TimeZoneInfo tz;
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(org.Timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                logger.LogWarning(
                    "Saúde de conta: fuso desconhecido {Timezone} na org {OrgId}, pulando",
                    org.Timezone, org.Id);
                continue;
            }

            var row = await EvaluateOrgAsync(connection, org, tz, nowUtc, ct);
            if (row.Signals.Count > 0)
            {
                rows.Add(row);
            }
        }

        return new AccountHealthReport(
            orgs.Count,
            [.. rows
                .OrderBy(r => r.HealthScore)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static async Task<AccountHealthRow> EvaluateOrgAsync(
        NpgsqlConnection connection, OrgRow org, TimeZoneInfo tz, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // datas LOCAIS do tenant: summary_date é o dia local (split à meia-noite do fuso da org
        // na agregação), então comparar com data UTC deslocaria a janela em contas fora do BRT
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).Date);
        var currentTo = localToday.AddDays(-1);              // último dia FECHADO
        var currentFrom = localToday.AddDays(-NoEventsDays);
        var previousTo = currentFrom.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(NoEventsDays - 1));
        var appsFrom = localToday.AddDays(-UncategorizedAppsWindowDays);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT
              (SELECT max(u.last_login_at) FROM users u
                 WHERE u.tenant_id = @t AND u.status = 'active'),
              (SELECT count(*)::int FROM devices d
                 WHERE d.tenant_id = @t AND d.status = 'active'),
              (SELECT count(*)::int FROM devices d
                 WHERE d.tenant_id = @t AND d.status = 'active' AND d.last_seen_at >= @cutEvents),
              (SELECT max(d.last_seen_at) FROM devices d WHERE d.tenant_id = @t),
              (SELECT count(DISTINCT s.device_id)::int FROM daily_device_summaries s
                 WHERE s.tenant_id = @t AND s.summary_date BETWEEN @curFrom AND @curTo),
              (SELECT count(DISTINCT s.device_id)::int FROM daily_device_summaries s
                 WHERE s.tenant_id = @t AND s.summary_date BETWEEN @prevFrom AND @prevTo),
              (SELECT count(*)::int FROM audit_log a
                 WHERE a.tenant_id = @t AND a.occurred_at >= @cutReads AND a.action = ANY(@readActions)),
              (SELECT count(DISTINCT dau.app_id)::int FROM daily_app_usage dau
                 LEFT JOIN tenant_app_categories tac
                   ON tac.tenant_id = dau.tenant_id AND tac.app_id = dau.app_id
                 WHERE dau.tenant_id = @t AND dau.summary_date >= @appsFrom AND tac.app_id IS NULL),
              (SELECT u.display_name FROM users u
                 WHERE u.tenant_id = @t AND u.status = 'active' AND u.role = 'owner'
                 ORDER BY u.email LIMIT 1),
              (SELECT u.email::text FROM users u
                 WHERE u.tenant_id = @t AND u.status = 'active' AND u.role = 'owner'
                 ORDER BY u.email LIMIT 1)
            """, connection);
        cmd.Parameters.AddWithValue("t", org.Id);
        cmd.Parameters.AddWithValue("cutEvents", nowUtc.AddDays(-NoEventsDays));
        cmd.Parameters.AddWithValue("cutReads", nowUtc.AddDays(-ReadWindowDays));
        cmd.Parameters.AddWithValue("curFrom", currentFrom);
        cmd.Parameters.AddWithValue("curTo", currentTo);
        cmd.Parameters.AddWithValue("prevFrom", previousFrom);
        cmd.Parameters.AddWithValue("prevTo", previousTo);
        cmd.Parameters.AddWithValue("appsFrom", appsFrom);
        cmd.Parameters.AddWithValue("readActions", ReadActions);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var lastLoginAt = reader.IsDBNull(0) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(0);
        var activeDevices = reader.GetInt32(1);
        var devicesSeen7d = reader.GetInt32(2);
        var lastSeenAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3);
        var devicesCurrent = reader.GetInt32(4);
        var devicesPrevious = reader.GetInt32(5);
        var readActions14d = reader.GetInt32(6);
        var uncategorizedApps = reader.GetInt32(7);
        var contactName = reader.IsDBNull(8) ? null : reader.GetString(8);
        var contactEmail = reader.IsDBNull(9) ? null : reader.GetString(9);

        var ageDays = (nowUtc - org.CreatedAt).TotalDays;
        var signals = new List<AccountHealthSignal>();

        // CARÊNCIA: cada regra só vale depois de a conta ter idade suficiente para a janela dela.
        if (ageDays >= NoLoginDays && (lastLoginAt is null || lastLoginAt < nowUtc.AddDays(-NoLoginDays)))
        {
            signals.Add(new AccountHealthSignal(
                SignalNoLogin,
                lastLoginAt is null
                    ? "nenhum usuário entrou no portal desde a criação da conta"
                    : $"nenhum login no portal desde {lastLoginAt.Value.ToString("dd/MM/yyyy", PtBr)}",
                30));
        }

        if (ageDays >= NoEventsDays && (lastSeenAt is null || lastSeenAt < nowUtc.AddDays(-NoEventsDays)))
        {
            signals.Add(new AccountHealthSignal(
                SignalNoEvents,
                lastSeenAt is null
                    ? "nenhum dispositivo jamais se comunicou"
                    : $"nenhum dispositivo se comunica desde {lastSeenAt.Value.ToString("dd/MM/yyyy", PtBr)}",
                30));
        }
        else if (devicesPrevious > 0 && devicesCurrent < devicesPrevious * (1 - DeviceDropThreshold))
        {
            // só faz sentido falar em QUEDA se ainda há sinal de vida; conta muda não precisa
            // de dois sinais dizendo a mesma coisa
            var drop = (double)(devicesPrevious - devicesCurrent) / devicesPrevious * 100;
            signals.Add(new AccountHealthSignal(
                SignalDeviceDrop,
                $"queda de {drop.ToString("0", PtBr)}% nos dispositivos com dados "
                + $"({devicesPrevious} na semana anterior, {devicesCurrent} nesta)",
                25));
        }

        if (ageDays >= ReadWindowDays && readActions14d == 0)
        {
            signals.Add(new AccountHealthSignal(
                SignalNoReads,
                $"nenhuma consulta a relatório nem export nos últimos {ReadWindowDays} dias",
                10));
        }

        if (ageDays >= NoEventsDays && uncategorizedApps >= UncategorizedAppsThreshold)
        {
            signals.Add(new AccountHealthSignal(
                SignalUncategorizedApps,
                $"{uncategorizedApps} aplicativos em uso sem categoria definida",
                5));
        }

        return new AccountHealthRow(
            org.Id, org.Name, org.Slug, org.Plan, org.CreatedAt,
            contactName, contactEmail, lastLoginAt, lastSeenAt,
            activeDevices, devicesSeen7d, devicesCurrent, devicesPrevious,
            readActions14d, uncategorizedApps, signals);
    }

    /// <summary>
    /// CSV no formato EXATO que crm/import.php aceita:
    /// empresa;contato;email;whatsapp;estacoes;origem;observacoes;cnpj, separador ';',
    /// cabeçalho com "empresa" (o import pula a primeira linha por causa dele) e UTF-8.
    /// WhatsApp e CNPJ saem VAZIOS de propósito: o produto não guarda esses campos da org, e
    /// o import trata vazio como ausente (só rejeitaria valor inválido). A coluna observações
    /// carrega o score e os sinais, que é o que o CS lê antes de ligar.
    /// </summary>
    public static string BuildCsv(IReadOnlyList<AccountHealthRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append("\r\n");

        foreach (var row in rows)
        {
            var estacoes = row.ActiveDevices > 0
                ? row.ActiveDevices.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            sb.Append(ExportService.Csv(row.Name)).Append(';');
            sb.Append(ExportService.Csv(row.ContactName)).Append(';');
            sb.Append(ExportService.Csv(row.ContactEmail)).Append(';');
            sb.Append(';');                                   // whatsapp: o produto não guarda
            sb.Append(estacoes).Append(';');
            sb.Append(CrmSource).Append(';');
            sb.Append(ExportService.Csv(BuildNotes(row))).Append(';');
            sb.Append("\r\n");                                // cnpj: o produto não guarda
        }

        return sb.ToString();
    }

    /// <summary>Mesmo CSV em bytes, UTF-8 COM BOM (o import.php aceita, e o Excel pt-BR precisa).</summary>
    public static byte[] CsvBytes(IReadOnlyList<AccountHealthRow> rows)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(BuildCsv(rows))];
    }

    /// <summary>
    /// Observações do lead: score, faixa e os sinais em uma linha só. O import.php lê o CSV
    /// LINHA A LINHA, então quebra de linha aqui destruiria o arquivo, o separador é ". ".
    /// </summary>
    private static string BuildNotes(AccountHealthRow row)
    {
        var sinais = string.Join(". ", row.Signals.Select(s => Capitalize(s.Label)));
        return $"Saúde da conta {row.HealthScore}/100 (risco {row.Faixa}), plano {row.Plan}. "
            + $"{sinais}. "
            + $"Dispositivos ativos: {row.ActiveDevices}, vistos nos últimos {NoEventsDays} dias: {row.DevicesSeenLast7d}. "
            + "Telemetria interna de CS, não compartilhar com o cliente.";
    }

    private string BuildHtml(IReadOnlyList<AccountHealthRow> rows, DateTimeOffset nowUtc)
    {
        var sb = new StringBuilder();

        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:720px;margin:0 auto;color:#1a2233;\">");
        sb.Append("<h2 style=\"font-size:18px;\">Saúde das contas</h2>");
        sb.Append($"<p style=\"color:#5a6478;font-size:13px;\">Apuração de {nowUtc.ToString("dd/MM/yyyy", PtBr)}, "
            + $"{rows.Count} conta(s) com sinal de risco.</p>");

        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:13px;\">");
        sb.Append("<tr style=\"text-align:left;color:#5a6478;\">"
            + "<th style=\"padding:6px 0;\">Conta</th>"
            + "<th style=\"padding:6px 0;\">Score</th>"
            + "<th style=\"padding:6px 0;\">Sinais</th>"
            + "<th style=\"padding:6px 0;\">Contato</th></tr>");

        foreach (var row in rows)
        {
            sb.Append("<tr>");
            sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;vertical-align:top;\">{Html(row.Name)}<br>"
                + $"<span style=\"color:#5a6478;font-size:11px;\">plano {Html(row.Plan)}, {row.ActiveDevices} dispositivo(s) ativo(s)</span></td>");
            sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;vertical-align:top;font-weight:600;\">"
                + $"{row.HealthScore}<span style=\"color:#5a6478;font-weight:400;\">/100</span><br>"
                + $"<span style=\"color:#5a6478;font-size:11px;font-weight:400;\">{Html(row.Faixa)}</span></td>");
            sb.Append("<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;vertical-align:top;\"><ul style=\"margin:0;padding-left:16px;\">");
            foreach (var signal in row.Signals)
            {
                sb.Append($"<li>{Html(Capitalize(signal.Label))}</li>");
            }

            sb.Append("</ul></td>");
            sb.Append($"<td style=\"padding:6px 0;border-bottom:1px solid #e4e8ef;vertical-align:top;\">"
                + $"{Html(row.ContactName ?? "sem Owner ativo")}<br>"
                + $"<span style=\"color:#5a6478;font-size:11px;\">{Html(row.ContactEmail ?? "")}</span></td>");
            sb.Append("</tr>");
        }

        sb.Append("</table>");

        sb.Append("<p style=\"font-size:12px;color:#5a6478;margin-top:20px;\">O CSV anexo tem o formato do "
            + "importador do CRM interno (Importar leads), com o score e os sinais na coluna de observações. "
            + "Contas já cadastradas entram marcadas como duplicadas, não viram lead novo.</p>");

        sb.Append("<hr style=\"border:none;border-top:1px solid #e4e8ef;margin:24px 0 12px;\">");
        sb.Append("<p style=\"color:#8a94a8;font-size:11px;\">Telemetria INTERNA de Customer Success. "
            + "Só métricas agregadas de uso do produto por organização, nenhum dado monitorado de pessoa. "
            + "Não encaminhar para o cliente.</p>");
        sb.Append("</div>");

        return sb.ToString();
    }

    private async Task SafeRecordErrorAsync(DateTimeOffset startedAt, object detail)
    {
        try
        {
            await MaintenanceRunRecorder.RecordAsync(
                dataSource, MaintenanceRunRecorder.AccountHealth, startedAt, DateTimeOffset.UtcNow,
                MaintenanceRunRecorder.StatusError, detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao gravar maintenance_runs (status=error) de AccountHealth.");
        }
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpper(value[0], PtBr) + value[1..];

    private static string Html(string value) => HtmlText.Escape(value);
}
