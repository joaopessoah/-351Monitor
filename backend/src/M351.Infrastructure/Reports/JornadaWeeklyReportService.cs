using System.Globalization;
using System.Text;
using System.Text.Json;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Email;
using M351.Infrastructure.Exports;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Reports;

/// <summary>
/// Relatório de jornada da semana anterior por e-mail (F5, o "relatório agendado" do plano Pro
/// na forma mais barata possível). Nada aqui gera CSV: o serviço só ENFILEIRA no pipeline
/// assíncrono que já existe (kind jornada_csv, claim com SKIP LOCKED, retenção de 7 dias, que
/// casa exatamente com a cadência semanal) e, quando o arquivo fica pronto, manda o LINK.
///
/// Duas etapas, um único RunOnceAsync (o job chama de 5 em 5 minutos):
///  1. ENFILEIRAR: nas orgs cuja hora local é segunda 07h, um export por assinante com o
///     período da semana fechada (segunda a domingo anteriores, em datas locais do tenant);
///  2. ENTREGAR: as entregas pendentes cujo export virou 'done' viram e-mail com o link.
///     O intervalo curto do job é justamente para o link sair logo depois do arquivo ficar
///     pronto, e não só na hora cheia seguinte.
///
/// DECISÕES QUE NÃO PODEM REGREDIR:
///  - NUNCA ANEXO. O EmailMessage nem suporta, e anexar dado pessoal a e-mail piora a postura
///    LGPD: o corpo leva o link do download AUTENTICADO no portal, que exige sessão válida;
///  - requested_by é NOT NULL e recebe o PRÓPRIO ASSINANTE, não um usuário de sistema: a
///    trilha responde "quem gerou" com o mesmo rigor do POST /exports feito na tela, e a
///    linha export_csv em audit_log é gravada na MESMA transação do INSERT (jamais job sem
///    trilha);
///  - GATE DE PLANO: relatório agendado é exclusivo do Pro (docs/design/05-produto-mvp.md).
///    O gate vive no plano da org (a flag por tenant do backoffice), e é reavaliado também na
///    ENTREGA, para um downgrade entre a segunda e o envio não deixar escapar um e-mail pago;
///  - disclaimer da Portaria 671 VERBATIM no corpo (o mesmo do banner do portal e do rodapé do
///    CSV), porque o e-mail circula fora do portal e o aviso precisa viajar junto;
///  - vocabulário NEUTRO: "primeiro e último evento", jamais entrada/saída, jamais hora extra.
/// </summary>
public class JornadaWeeklyReportService(
    NpgsqlDataSource dataSource,
    IEmailSender emailSender,
    string portalBaseUrl,
    ILogger<JornadaWeeklyReportService> logger)
{
    /// <summary>Hora local (da org) em que o export da semana é enfileirado: segunda, 07h.</summary>
    public const int EnqueueHourLocal = 7;

    /// <summary>Plano com relatórios agendados (os demais exportam sob demanda, na tela).</summary>
    public const string RequiredPlan = "pro";

    /// <summary>
    /// Depois disso a entrega é abandonada em silêncio: o export ficou preso ou foi expurgado, e
    /// um e-mail apontando para um arquivo que já expirou (7 dias) seria pior que nenhum.
    /// </summary>
    public static readonly TimeSpan DeliveryGiveUp = TimeSpan.FromHours(24);

    /// <summary>Teto de entregas avaliadas por ciclo, para um acúmulo não segurar o job.</summary>
    private const int DeliveryBatchSize = 200;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private sealed record OrgRow(Guid Id, string Timezone);

    private sealed record SubscriberRow(Guid UserId, string Email);

    private sealed record DeliveryRow(
        Guid Id, Guid TenantId, Guid ExportJobId, string Email, string OrgName,
        DateOnly WeekStart, DateOnly WeekEnd, DateTimeOffset QueuedAt,
        string JobStatus, int? RowCount, bool Truncated, DateTimeOffset? ExpiresAt);

    /// <summary>Uma passada: enfileira o que vence agora e entrega o que já está pronto. Retorna quantos e-mails saíram.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await EnqueueDueAsync(connection, nowUtc, ct);
        return await DeliverReadyAsync(connection, nowUtc, ct);
    }

    // ----------------------------------------------------------------- etapa 1: enfileirar
    private async Task EnqueueDueAsync(NpgsqlConnection connection, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var orgs = new List<OrgRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, timezone
            FROM organizations
            WHERE status = 'active' AND plan = @plan
            """, connection))
        {
            cmd.Parameters.AddWithValue("plan", RequiredPlan);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                orgs.Add(new OrgRow(reader.GetGuid(0), reader.GetString(1)));
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
                logger.LogWarning(
                    "Jornada semanal: fuso desconhecido {Timezone} na org {OrgId}, pulando", org.Timezone, org.Id);
                continue;
            }

            var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
            if (local.DayOfWeek != DayOfWeek.Monday || local.Hour != EnqueueHourLocal)
            {
                continue;
            }

            // semana fechada: segunda a domingo ANTERIORES, em datas LOCAIS do tenant (é o que
            // summary_date guarda, e é a mesma régua do digest semanal)
            var weekStart = DateOnly.FromDateTime(local.Date.AddDays(-7));
            var weekEnd = DateOnly.FromDateTime(local.Date.AddDays(-1));

            var subscribers = await QuerySubscribersAsync(connection, org.Id, ct);
            foreach (var subscriber in subscribers)
            {
                if (await TryEnqueueAsync(connection, org.Id, subscriber, weekStart, weekEnd, nowUtc, ct))
                {
                    logger.LogInformation(
                        "Jornada semanal enfileirada: org {OrgId}, usuário {UserId}, semana {WeekStart} a {WeekEnd}",
                        org.Id, subscriber.UserId, weekStart, weekEnd);
                }
            }
        }
    }

    /// <summary>
    /// Assinantes ATIVOS da org. Sem COALESCE aqui de propósito: o default de jornada_weekly é
    /// DESLIGADO (linha ausente = não assina), ao contrário do digest e dos alertas de frota.
    /// Todo papel pode assinar, porque todo papel já exporta a jornada na tela (F3.5).
    /// </summary>
    private static async Task<List<SubscriberRow>> QuerySubscribersAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT u.id, u.email
            FROM users u
            JOIN user_email_prefs p ON p.user_id = u.id
            WHERE u.tenant_id = @t AND u.status = 'active' AND p.jornada_weekly
            ORDER BY u.email
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);

        var rows = new List<SubscriberRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SubscriberRow(reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>
    /// Export + entrega + trilha numa ÚNICA transação. O UNIQUE (user_id, week_start) é a
    /// idempotência: se a semana já foi enfileirada para este assinante, o INSERT da entrega não
    /// afeta linha nenhuma e a transação inteira volta atrás (o export job nem sobra órfão).
    /// Retorna true quando enfileirou de fato.
    /// </summary>
    private static async Task<bool> TryEnqueueAsync(
        NpgsqlConnection connection, Guid tenantId, SubscriberRow subscriber,
        DateOnly weekStart, DateOnly weekEnd, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // params NORMALIZADOS no mesmo formato do POST /exports (é o que o ExportService lê e o
        // que a tela de Exportações mostra): período da semana, org inteira, sem group_by
        var normalizedParams = new Dictionary<string, object?>
        {
            ["from"] = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = weekEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var paramsJson = JsonSerializer.Serialize(normalizedParams);

        var jobId = Uuid7.NewUuid7();
        var deliveryId = Uuid7.NewUuid7();

        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var insertJob = new NpgsqlCommand(
            """
            INSERT INTO export_jobs (id, tenant_id, requested_by, kind, params, status)
            VALUES (@id, @t, @u, 'jornada_csv', @params::jsonb, 'queued')
            """, connection, tx))
        {
            insertJob.Parameters.AddWithValue("id", jobId);
            insertJob.Parameters.AddWithValue("t", tenantId);
            insertJob.Parameters.AddWithValue("u", subscriber.UserId);
            insertJob.Parameters.AddWithValue("params", paramsJson);
            await insertJob.ExecuteNonQueryAsync(ct);
        }

        int claimed;
        await using (var insertDelivery = new NpgsqlCommand(
            """
            INSERT INTO jornada_report_deliveries
                (id, tenant_id, user_id, export_job_id, week_start, week_end, queued_at)
            VALUES (@id, @t, @u, @j, @weekStart, @weekEnd, @now)
            ON CONFLICT (user_id, week_start) DO NOTHING
            """, connection, tx))
        {
            insertDelivery.Parameters.AddWithValue("id", deliveryId);
            insertDelivery.Parameters.AddWithValue("t", tenantId);
            insertDelivery.Parameters.AddWithValue("u", subscriber.UserId);
            insertDelivery.Parameters.AddWithValue("j", jobId);
            insertDelivery.Parameters.AddWithValue("weekStart", weekStart);
            insertDelivery.Parameters.AddWithValue("weekEnd", weekEnd);
            insertDelivery.Parameters.AddWithValue("now", nowUtc);
            claimed = await insertDelivery.ExecuteNonQueryAsync(ct);
        }

        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // trilha export_csv na MESMA transação, igual ao POST /exports; source distingue o
        // agendamento do clique na tela sem inventar uma ação nova no vocabulário da auditoria
        await using (var audit = new NpgsqlCommand(
            """
            INSERT INTO audit_log (id, tenant_id, actor_user_id, actor_ip, action, target_type, target_id, detail, occurred_at)
            VALUES (@id, @t, @u, NULL, @action, 'export_job', @target, @detail::jsonb, @now)
            """, connection, tx))
        {
            audit.Parameters.AddWithValue("id", Uuid7.NewUuid7());
            audit.Parameters.AddWithValue("t", tenantId);
            audit.Parameters.AddWithValue("u", subscriber.UserId);
            audit.Parameters.AddWithValue("action", AuditActions.ExportCsv);
            audit.Parameters.AddWithValue("target", jobId);
            audit.Parameters.AddWithValue("detail", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["kind"] = "jornada_csv",
                ["params"] = normalizedParams,
                ["source"] = "assinatura_semanal",
            }));
            audit.Parameters.AddWithValue("now", nowUtc);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    // ------------------------------------------------------------------ etapa 2: entregar
    private async Task<int> DeliverReadyAsync(NpgsqlConnection connection, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var pending = new List<DeliveryRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT d.id, d.tenant_id, d.export_job_id, u.email, o.name,
                   d.week_start, d.week_end, d.queued_at,
                   j.status, j.row_count, j.truncated, j.expires_at
            FROM jornada_report_deliveries d
            JOIN users u ON u.id = d.user_id
            JOIN organizations o ON o.id = d.tenant_id
            JOIN export_jobs j ON j.id = d.export_job_id
            LEFT JOIN user_email_prefs p ON p.user_id = d.user_id
            WHERE d.emailed_at IS NULL AND d.gave_up_at IS NULL
              AND u.status = 'active'
              AND o.status = 'active' AND o.plan = @plan
              AND COALESCE(p.jornada_weekly, false)
            ORDER BY d.queued_at
            LIMIT @limit
            """, connection))
        {
            cmd.Parameters.AddWithValue("plan", RequiredPlan);
            cmd.Parameters.AddWithValue("limit", DeliveryBatchSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pending.Add(new DeliveryRow(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                    reader.GetString(3), reader.GetString(4),
                    reader.GetFieldValue<DateOnly>(5), reader.GetFieldValue<DateOnly>(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.GetBoolean(10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11)));
            }
        }

        var sent = 0;
        foreach (var delivery in pending)
        {
            if (delivery.JobStatus == "failed")
            {
                await StampAsync(connection, delivery.Id, "gave_up_at", nowUtc, ct);
                logger.LogWarning(
                    "Jornada semanal: export {JobId} falhou, entrega {DeliveryId} abandonada",
                    delivery.ExportJobId, delivery.Id);
                continue;
            }

            if (delivery.JobStatus != "done")
            {
                // ainda na fila: o ExportService roda a cada 15 s, então isso é normal por
                // alguns ciclos; só desiste se o job encalhou de vez
                if (nowUtc - delivery.QueuedAt > DeliveryGiveUp)
                {
                    await StampAsync(connection, delivery.Id, "gave_up_at", nowUtc, ct);
                    logger.LogWarning(
                        "Jornada semanal: export {JobId} preso em '{Status}' há mais de {Horas}h, entrega {DeliveryId} abandonada",
                        delivery.ExportJobId, delivery.JobStatus, DeliveryGiveUp.TotalHours, delivery.Id);
                }

                continue;
            }

            // CLAIM antes do envio (mesmo padrão do cooldown dos alertas de frota): o UPDATE
            // condicional é atômico, então duas instâncias do worker nunca mandam o mesmo e-mail
            if (!await StampAsync(connection, delivery.Id, "emailed_at", nowUtc, ct))
            {
                continue;
            }

            var subject = "Relatório de jornada da semana no +351 Monitor, "
                + $"{delivery.WeekStart.ToString("dd/MM", PtBr)} a {delivery.WeekEnd.ToString("dd/MM", PtBr)}";
            await emailSender.SendAsync(
                new EmailMessage(delivery.Email, subject, BuildHtml(delivery), IsHtml: true), ct);
            sent++;

            logger.LogInformation(
                "Jornada semanal entregue: org {OrgId}, export {JobId}, semana {WeekStart} a {WeekEnd}",
                delivery.TenantId, delivery.ExportJobId, delivery.WeekStart, delivery.WeekEnd);
        }

        return sent;
    }

    /// <summary>
    /// Carimba emailed_at ou gave_up_at só se a entrega ainda estiver pendente. Retorna false
    /// quando outra instância chegou primeiro. O nome da coluna é literal do código, jamais
    /// entrada de usuário, então a interpolação não abre injeção.
    /// </summary>
    private static async Task<bool> StampAsync(
        NpgsqlConnection connection, Guid deliveryId, string column, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE jornada_report_deliveries SET {column} = @now
            WHERE id = @id AND emailed_at IS NULL AND gave_up_at IS NULL
            """, connection);
        cmd.Parameters.AddWithValue("now", nowUtc);
        cmd.Parameters.AddWithValue("id", deliveryId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ------------------------------------------------------------------------ corpo do e-mail
    private string BuildHtml(DeliveryRow delivery)
    {
        var baseUrl = portalBaseUrl.TrimEnd('/');
        var sb = new StringBuilder();

        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:0 auto;color:#1a2233;\">");
        sb.Append("<h2 style=\"font-size:18px;\">Relatório de jornada da semana</h2>");
        sb.Append($"<p style=\"color:#5a6478;font-size:13px;\">{Html(delivery.OrgName)}<br>");
        sb.Append($"{delivery.WeekStart.ToString("dd/MM/yyyy", PtBr)} a {delivery.WeekEnd.ToString("dd/MM/yyyy", PtBr)}</p>");

        // estado vazio: semana sem nenhuma linha (frota parada, feriado prolongado, agente
        // recém-instalado). O e-mail sai assim mesmo, dizendo o que houve, para o silêncio
        // nunca ser confundido com falha do produto.
        if (delivery.RowCount is 0)
        {
            sb.Append("<p style=\"font-size:14px;\">Nenhum dispositivo registrou atividade nesta semana. "
                + "A planilha foi gerada mesmo assim, só com o cabeçalho.</p>");
        }
        else if (delivery.RowCount is { } rows)
        {
            var linhas = rows == 1 ? "1 linha" : $"{rows.ToString("N0", PtBr)} linhas";
            sb.Append($"<p style=\"font-size:14px;\">A planilha traz {linhas} de dispositivo por dia, "
                + "com primeiro e último evento, tempo ligada, ativo, ocioso e bloqueado.</p>");
        }

        if (delivery.Truncated)
        {
            sb.Append("<p style=\"font-size:13px;color:#8a5a00;\">A planilha atingiu o teto de linhas e foi truncada. "
                + "Para o período inteiro, exporte na tela do relatório com um filtro de dispositivos mais estreito.</p>");
        }

        sb.Append($"<p style=\"margin-top:20px;\"><a href=\"{baseUrl}/relatorios/exportacoes\" style=\"background:#c8f542;color:#1c2506;padding:10px 18px;border-radius:6px;text-decoration:none;font-weight:600;\">Baixar no portal</a></p>");

        // por que link e não anexo: dado pessoal não circula por e-mail, e o download exige sessão
        sb.Append("<p style=\"color:#5a6478;font-size:12px;\">O arquivo não vai anexado: o download acontece dentro do portal, "
            + "com a sua sessão autenticada, para o dado pessoal da equipe não circular por e-mail. "
            + "O link fica disponível por 7 dias, até a próxima edição semanal.</p>");

        sb.Append("<hr style=\"border:none;border-top:1px solid #e4e8ef;margin:24px 0 12px;\">");
        sb.Append($"<p style=\"color:#8a94a8;font-size:11px;\">{Html(ExportService.JornadaDisclaimer)}</p>");
        sb.Append("<p style=\"color:#8a94a8;font-size:11px;\">Você recebe este e-mail porque assinou o relatório de jornada semanal. "
            + "Para deixar de receber, desative a assinatura na tela do Relatório de Jornada ou em Configurações.</p>");
        sb.Append("</div>");

        return sb.ToString();
    }

    private static string Html(string value) => HtmlText.Escape(value);
}
