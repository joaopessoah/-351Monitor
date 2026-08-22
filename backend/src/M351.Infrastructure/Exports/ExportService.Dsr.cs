using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace M351.Infrastructure.Exports;

/// <summary>
/// Geração do PACOTE DSR (F4.5, Seção 9.3): pacote ZIP com TODOS os eventos/intervalos/
/// agregados do titular (direito de acesso/portabilidade, art. 18 II/V LGPD; insumo da
/// resposta da controladora em 15 dias, art. 19). Três escopos:
///  - dsr_subject: um device_user (params {device_user_id});
///  - dsr_device:  todos os device_users de um device (params {device_id});
///  - tenant_full: o acervo inteiro do tenant (offboarding; params {} ).
///
/// PRIVACIDADE (lista FECHADA da Seção 9.1): o pacote é do PRÓPRIO titular, então window_title
/// do titular PODE constar no pacote dele; JAMAIS dados fora da lista fechada e JAMAIS dados de
/// OUTRO titular/tenant. Todas as queries têm tenant_id no WHERE e (subject/device) recortam
/// pelo titular — nunca varrem o tenant inteiro fora do tenant_full.
///
/// STREAMING: o ZIP é montado com ZipArchive sobre o FileStream do arquivo de destino e cada
/// CSV é escrito direto na entry (StreamWriter sobre o stream da entry) — NUNCA o ZIP inteiro
/// nem um CSV inteiro em memória. CSVs com UTF-8 BOM e separador ';' (padrão do projeto).
/// </summary>
public sealed partial class ExportService
{
    /// <summary>Disclaimer de finalidade gravado no manifest.json do pacote DSR.</summary>
    public const string DsrManifestDisclaimer =
        "Pacote de dados pessoais do titular emitido em atendimento a solicitação de acesso/"
        + "portabilidade (art. 18 da LGPD). Contém apenas dados do próprio titular, dentro da lista "
        + "fechada da política de privacidade (identificação da máquina/usuário, eventos de sessão/"
        + "energia, aplicativo e título de janela do titular, fato da ociosidade e saúde do agente). "
        + "Link de download válido por 72 horas.";

    private async Task<(int Rows, bool Truncated)> GenerateDsrZipAsync(
        ExportJobRow job, string absolutePath, CancellationToken ct)
    {
        var timezone = await TenantTimezoneAsync(job.TenantId, ct);

        // resolve os titulares do escopo (subject = 1; device = N; tenant_full = todos do tenant)
        var subjects = await ResolveSubjectsAsync(job, ct);

        // ZipArchive sobre o FileStream de destino: streaming puro
        await using var fileStream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        var totalRows = 0;
        var truncated = false;

        var (eventos, t1) = await WriteRawEventsEntryAsync(archive, job, subjects, timezone, ct);
        totalRows += eventos;
        truncated |= t1;

        var (intervalos, t2) = await WriteIntervalsEntryAsync(archive, job, subjects, timezone, ct);
        totalRows += intervalos;
        truncated |= t2;

        var (agregados, t3) = await WriteAggregatesEntryAsync(archive, job, subjects, ct);
        totalRows += agregados;
        truncated |= t3;

        // Relatório legível do PRÓPRIO titular (só no pacote de titular): o ZIP de CSVs atende à
        // portabilidade, mas não à COMPREENSÃO — e o art. 9º da LGPD dá ao titular direito a
        // informação clara sobre o tratamento. O hash do HTML vai no manifest como recibo.
        var receipt = await WriteAboutMeEntryAsync(archive, job, subjects, timezone, eventos, intervalos, agregados, ct);

        await WriteManifestEntryAsync(
            archive, job, subjects, eventos, intervalos, agregados, truncated, receipt, ct);

        return (totalRows, truncated);
    }

    // ------------------------------------------------------------ resolução do escopo
    private async Task<DsrScope> ResolveSubjectsAsync(ExportJobRow job, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(job.ParamsJson);
        var root = doc.RootElement;

        // tenant_full: todos os device_users do tenant (sem recorte por id)
        if (job.Kind == "tenant_full")
        {
            var all = await LoadSubjectsAsync(
                """
                SELECT du.id, du.device_id, du.windows_sid, du.windows_username, du.display_name,
                       du.first_seen_at, du.last_seen_at, COALESCE(d.display_name, d.hostname)
                FROM device_users du
                JOIN devices d ON d.id = du.device_id AND d.tenant_id = du.tenant_id
                WHERE du.tenant_id = @t
                """,
                cmd => cmd.Parameters.AddWithValue("t", job.TenantId), ct);
            return new DsrScope("tenant", null, all);
        }

        if (job.Kind == "dsr_device")
        {
            var deviceId = root.GetProperty("device_id").GetGuid();
            var subjects = await LoadSubjectsAsync(
                """
                SELECT du.id, du.device_id, du.windows_sid, du.windows_username, du.display_name,
                       du.first_seen_at, du.last_seen_at, COALESCE(d.display_name, d.hostname)
                FROM device_users du
                JOIN devices d ON d.id = du.device_id AND d.tenant_id = du.tenant_id
                WHERE du.tenant_id = @t AND du.device_id = @d
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("t", job.TenantId);
                    cmd.Parameters.AddWithValue("d", deviceId);
                }, ct);
            return new DsrScope("device", deviceId, subjects);
        }

        // dsr_subject: um device_user
        var deviceUserId = root.GetProperty("device_user_id").GetGuid();
        var one = await LoadSubjectsAsync(
            """
            SELECT du.id, du.device_id, du.windows_sid, du.windows_username, du.display_name,
                   du.first_seen_at, du.last_seen_at, COALESCE(d.display_name, d.hostname)
            FROM device_users du
            JOIN devices d ON d.id = du.device_id AND d.tenant_id = du.tenant_id
            WHERE du.tenant_id = @t AND du.id = @id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("t", job.TenantId);
                cmd.Parameters.AddWithValue("id", deviceUserId);
            }, ct);
        return new DsrScope("subject", null, one);
    }

    private async Task<IReadOnlyList<DsrSubject>> LoadSubjectsAsync(
        string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        var list = new List<DsrSubject>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DsrSubject(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetString(7)));
        }

        return list;
    }

    // ------------------------------------------------------------ eventos.csv (raw_events do titular)
    /// <summary>
    /// raw_events do(s) titular(es): a lista fechada da Seção 9.1 permite app+título de janela
    /// do PRÓPRIO titular no pacote dele. Recorte por (tenant_id, device_id, windows_sid) — é o
    /// único vínculo (raw_events não tem device_user_id). tenant_full = tenant inteiro.
    /// </summary>
    private async Task<(int Rows, bool Truncated)> WriteRawEventsEntryAsync(
        ZipArchive archive, ExportJobRow job, DsrScope scope, string timezone, CancellationToken ct)
    {
        const string header = "Data/hora;Tipo de evento;Sessão;Aplicativo;Título da janela";
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);

        var (sql, bind) = scope.Kind == "tenant"
            ? ("""
               SELECT occurred_at, event_type, session_id, process_name, window_title
               FROM raw_events WHERE tenant_id = @t
               ORDER BY device_id, occurred_at
               LIMIT @RowLimit
               """,
               (Action<NpgsqlCommand>)(cmd => cmd.Parameters.AddWithValue("t", job.TenantId)))
            : ("""
               SELECT r.occurred_at, r.event_type, r.session_id, r.process_name, r.window_title
               FROM raw_events r
               JOIN device_users du
                 ON du.tenant_id = r.tenant_id AND du.device_id = r.device_id AND du.windows_sid = r.windows_sid
               WHERE r.tenant_id = @t AND du.id = ANY(@ids)
               ORDER BY r.device_id, r.occurred_at
               LIMIT @RowLimit
               """,
               cmd =>
               {
                   cmd.Parameters.AddWithValue("t", job.TenantId);
                   cmd.Parameters.AddWithValue("ids", scope.SubjectIds);
               });

        return await WriteCsvEntryAsync(archive, "eventos.csv", header, sql, bind, reader =>
            string.Join(';',
                FormatTimestamp(reader, 0, tz),
                Csv(reader.IsDBNull(1) ? null : reader.GetString(1)),
                reader.IsDBNull(2) ? "" : reader.GetInt32(2).ToString(CultureInfo.InvariantCulture),
                Csv(reader.IsDBNull(3) ? null : reader.GetString(3)),
                Csv(reader.IsDBNull(4) ? null : reader.GetString(4))),
            ct);
    }

    // ------------------------------------------------------------ intervalos.csv (activity_intervals do titular)
    private async Task<(int Rows, bool Truncated)> WriteIntervalsEntryAsync(
        ZipArchive archive, ExportJobRow job, DsrScope scope, string timezone, CancellationToken ct)
    {
        const string header = "Início;Fim;Estado;Título da janela;Dados incompletos";
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);

        var (sql, bind) = scope.Kind == "tenant"
            ? ("""
               SELECT started_at, ended_at, state, window_title, data_incomplete
               FROM activity_intervals WHERE tenant_id = @t
               ORDER BY device_user_id, started_at
               LIMIT @RowLimit
               """,
               (Action<NpgsqlCommand>)(cmd => cmd.Parameters.AddWithValue("t", job.TenantId)))
            : ("""
               SELECT started_at, ended_at, state, window_title, data_incomplete
               FROM activity_intervals WHERE tenant_id = @t AND device_user_id = ANY(@ids)
               ORDER BY device_user_id, started_at
               LIMIT @RowLimit
               """,
               cmd =>
               {
                   cmd.Parameters.AddWithValue("t", job.TenantId);
                   cmd.Parameters.AddWithValue("ids", scope.SubjectIds);
               });

        return await WriteCsvEntryAsync(archive, "intervalos.csv", header, sql, bind, reader =>
            string.Join(';',
                FormatTimestamp(reader, 0, tz),
                FormatTimestamp(reader, 1, tz),
                Csv(reader.GetString(2)),
                Csv(reader.IsDBNull(3) ? null : reader.GetString(3)),
                reader.GetBoolean(4) ? "sim" : "não"),
            ct);
    }

    // ------------------------------------------------------------ agregados.csv (daily_* do titular)
    private async Task<(int Rows, bool Truncated)> WriteAggregatesEntryAsync(
        ZipArchive archive, ExportJobRow job, DsrScope scope, CancellationToken ct)
    {
        const string header =
            "Data;Segundos ativo;Segundos ocioso;Segundos bloqueado;Segundos ligada;"
            + "Relacionado ao trabalho;Neutro;Não relacionado ao trabalho";

        var (sql, bind) = scope.Kind == "tenant"
            ? ("""
               SELECT summary_date, seconds_active, seconds_idle, seconds_locked, seconds_on,
                      seconds_work_related, seconds_neutral, seconds_not_work_related
               FROM daily_device_summaries WHERE tenant_id = @t
               ORDER BY device_user_id, summary_date
               LIMIT @RowLimit
               """,
               (Action<NpgsqlCommand>)(cmd => cmd.Parameters.AddWithValue("t", job.TenantId)))
            : ("""
               SELECT summary_date, seconds_active, seconds_idle, seconds_locked, seconds_on,
                      seconds_work_related, seconds_neutral, seconds_not_work_related
               FROM daily_device_summaries WHERE tenant_id = @t AND device_user_id = ANY(@ids)
               ORDER BY device_user_id, summary_date
               LIMIT @RowLimit
               """,
               cmd =>
               {
                   cmd.Parameters.AddWithValue("t", job.TenantId);
                   cmd.Parameters.AddWithValue("ids", scope.SubjectIds);
               });

        return await WriteCsvEntryAsync(archive, "agregados.csv", header, sql, bind, reader =>
            string.Join(';',
                reader.GetFieldValue<DateTime>(0).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                reader.GetInt32(1).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(2).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(3).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(4).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(5).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(6).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(7).ToString(CultureInfo.InvariantCulture)),
            ct);
    }

    // ------------------------------------------------------------ manifest.json
    private async Task WriteManifestEntryAsync(
        ZipArchive archive, ExportJobRow job, DsrScope scope,
        int eventos, int intervalos, int agregados, bool truncated,
        AboutMeReceipt? receipt, CancellationToken ct)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["kind"] = job.Kind,
            ["scope"] = scope.Kind,
            ["tenant_id"] = job.TenantId,
            ["generated_at"] = DateTimeOffset.UtcNow,
            ["expires_at"] = DateTimeOffset.UtcNow + DsrFileRetention,
            ["subjects"] = scope.Subjects.Select(s => new Dictionary<string, object?>
            {
                ["device_user_id"] = s.DeviceUserId,
                ["device_id"] = s.DeviceId,
                ["windows_username"] = s.WindowsUsername,
                ["display_name"] = s.DisplayName,
            }).ToArray(),
            ["counts"] = new Dictionary<string, object?>
            {
                ["raw_events"] = eventos,
                ["activity_intervals"] = intervalos,
                ["daily_summaries"] = agregados,
            },
            ["truncated"] = truncated,
            ["row_limit_per_file"] = maxDataRows,
            ["disclaimer"] = DsrManifestDisclaimer,
        };

        // Recibo do relatório legível: o SHA-256 permite ao titular (ou ao jurídico) provar que o
        // HTML entregue é exatamente o que foi gerado, sem depender da nossa palavra.
        if (receipt is not null)
        {
            manifest["receipt"] = new Dictionary<string, object?>
            {
                ["file"] = receipt.EntryName,
                ["sha256"] = receipt.Sha256,
                ["access_statement_rows"] = receipt.AccessRows,
                ["access_statement_since"] = receipt.Since.ToString("yyyy-MM-dd"),
            };
        }

        var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }, ct);
    }

    // ------------------------------------------------------------ dados-sobre-mim.html
    /// <summary>Nome da entry do relatório legível do titular no pacote DSR.</summary>
    public const string AboutMeEntryName = "dados-sobre-mim.html";

    /// <summary>
    /// Data a partir da qual a trilha de leitura IDENTIFICA o titular consultado (F5 — o
    /// detail de view_report passou a levar device_user_id). Antes dela a trilha existia, mas
    /// registrava alvo device/equipe: um acesso àquela época não é atribuível a uma pessoa
    /// específica. O relatório declara esse limite em vez de sugerir cobertura total.
    /// </summary>
    public static readonly DateOnly AccessStatementSince = new(2026, 8, 21);

    /// <summary>Teto de linhas do extrato de acessos (o relatório é para LER, não um dump).</summary>
    private const int AccessStatementLimit = 500;

    /// <summary>
    /// Escreve o relatório "Dados sobre mim" (HTML legível e imprimível, estilos inline) no
    /// pacote de TITULAR e devolve o recibo com o SHA-256 do arquivo. Só existe para
    /// dsr_subject: é um documento sobre UMA pessoa — um pacote de dispositivo ou de tenant
    /// reúne vários titulares e um relatório "sobre mim" ali seria uma peça sobre terceiros.
    ///
    /// Conteúdo (Seções 9.1/9.3/9.6): identificação do titular, período coberto, contagens do
    /// que foi coletado (as MESMAS do manifest — um número só existe em um lugar), a política de
    /// mascaramento de títulos VIGENTE do tenant, os prazos fixos de retenção e o EXTRATO DE
    /// ACESSOS: quem, do portal, consultou os dados desta pessoa.
    ///
    /// PRIVACIDADE, nas duas direções: o extrato mostra o NOME do usuário do portal que
    /// consultou (join em users) e JAMAIS o IP dele — accountability não é sobre expor o
    /// endereço de rede de um funcionário do RH ao titular. E jamais os masked_patterns crus:
    /// a política aparece descrita, nunca em regex.
    /// </summary>
    private async Task<AboutMeReceipt?> WriteAboutMeEntryAsync(
        ZipArchive archive, ExportJobRow job, DsrScope scope, string timezone,
        int eventos, int intervalos, int agregados, CancellationToken ct)
    {
        if (job.Kind != "dsr_subject" || scope.Subjects.Count != 1)
        {
            return null;
        }

        var subject = scope.Subjects[0];
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);

        var titlePolicy = await WindowTitlePolicyAsync(job.TenantId, ct);
        var accesses = await LoadAccessStatementAsync(job.TenantId, subject.DeviceUserId, ct);

        var html = BuildAboutMeHtml(subject, tz, titlePolicy, eventos, intervalos, agregados, accesses);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(html);

        var entry = archive.CreateEntry(AboutMeEntryName, CompressionLevel.Optimal);
        await using (var stream = entry.Open())
        {
            await stream.WriteAsync(bytes, ct);
        }

        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return new AboutMeReceipt(AboutMeEntryName, sha256, accesses.Count, AccessStatementSince);
    }

    /// <summary>Política de títulos vigente do tenant; sem config, o default de fábrica.</summary>
    private async Task<string> WindowTitlePolicyAsync(Guid tenantId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT window_title_policy FROM tenant_agent_configs WHERE tenant_id = @t", connection);
        command.Parameters.AddWithValue("t", tenantId);
        var value = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(value) ? "MASKED_PATTERNS" : value;
    }

    /// <summary>
    /// Extrato de acessos: linhas view_report do audit_log cujo detail identifica ESTE titular
    /// (detail-&gt;&gt;'device_user_id'), com o nome do usuário do portal que consultou resolvido
    /// por LEFT JOIN em users (ação de sistema fica sem ator). tenant_id no WHERE; o IP do ator
    /// NÃO é selecionado. Mais recentes primeiro, teto de AccessStatementLimit linhas.
    /// </summary>
    private async Task<List<AccessRow>> LoadAccessStatementAsync(Guid tenantId, Guid deviceUserId, CancellationToken ct)
    {
        var rows = new List<AccessRow>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT a.occurred_at, a.action, u.display_name
            FROM audit_log a
            LEFT JOIN users u ON u.id = a.actor_user_id AND u.tenant_id = a.tenant_id
            WHERE a.tenant_id = @t
              AND a.action = 'view_report'
              AND a.detail->>'device_user_id' = @du
            ORDER BY a.occurred_at DESC
            LIMIT @lim
            """, connection);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("du", deviceUserId.ToString());
        command.Parameters.AddWithValue("lim", AccessStatementLimit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AccessRow(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// HTML autocontido (estilos inline, sem nenhuma requisição externa — o titular pode abrir
    /// offline e imprimir). Todo valor vindo do banco passa por <see cref="Html"/>.
    /// </summary>
    private static string BuildAboutMeHtml(
        DsrSubject subject, TimeZoneInfo tz, string titlePolicy,
        int eventos, int intervalos, int agregados, List<AccessRow> accesses)
    {
        const string cardStyle =
            "border:1px solid #d8dee8;border-radius:8px;padding:16px 20px;margin:0 0 16px";
        const string thStyle =
            "text-align:left;font-size:12px;text-transform:uppercase;letter-spacing:.04em;"
            + "color:#5b6577;border-bottom:1px solid #d8dee8;padding:6px 8px";
        const string tdStyle = "padding:6px 8px;border-bottom:1px solid #eef1f6;font-size:14px";
        const string labelStyle = "color:#5b6577;font-size:14px;padding:4px 0;width:38%";
        const string valueStyle = "font-size:14px;padding:4px 0";

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<title>Dados sobre mim</title></head>")
          .Append("<body style=\"margin:0;padding:32px;background:#fff;color:#131a26;")
          .Append("font-family:Segoe UI,Helvetica,Arial,sans-serif;line-height:1.5\">")
          .Append("<div style=\"max-width:820px;margin:0 auto\">");

        sb.Append("<h1 style=\"font-size:24px;margin:0 0 4px\">Dados sobre mim</h1>")
          .Append("<p style=\"margin:0 0 24px;color:#5b6577;font-size:14px\">")
          .Append("Relatório dos dados pessoais tratados pelo monitoramento corporativo das estações ")
          .Append("de trabalho, emitido em atendimento ao direito de acesso do titular (art. 18 da LGPD). ")
          .Append("Gerado em ")
          .Append(Html(FormatInstant(DateTimeOffset.UtcNow, tz)))
          .Append(".</p>");

        // ---- identificação e período
        sb.Append("<div style=\"").Append(cardStyle).Append("\">")
          .Append("<h2 style=\"font-size:16px;margin:0 0 12px\">Quem é o titular deste relatório</h2>")
          .Append("<table style=\"width:100%;border-collapse:collapse\">")
          .Append(Row("Nome", subject.Label, labelStyle, valueStyle))
          .Append(Row("Conta do Windows", subject.WindowsUsername, labelStyle, valueStyle))
          .Append(Row("Dispositivo", subject.DeviceName, labelStyle, valueStyle))
          .Append(Row("Primeiro evento registrado", FormatInstant(subject.FirstSeenAt, tz), labelStyle, valueStyle))
          .Append(Row("Último evento registrado", FormatInstant(subject.LastSeenAt, tz), labelStyle, valueStyle))
          .Append("</table>")
          .Append("<p style=\"margin:12px 0 0;color:#5b6577;font-size:13px\">")
          .Append("O registro é por dispositivo: se você usa mais de uma máquina da empresa, cada uma ")
          .Append("tem um pacote próprio. Horários no fuso da organização (")
          .Append(Html(tz.Id)).Append(").</p></div>");

        // ---- contagens
        sb.Append("<div style=\"").Append(cardStyle).Append("\">")
          .Append("<h2 style=\"font-size:16px;margin:0 0 12px\">O que foi coletado sobre mim</h2>")
          .Append("<table style=\"width:100%;border-collapse:collapse\">")
          .Append(Row("Eventos brutos (arquivo eventos.csv)", Number(eventos), labelStyle, valueStyle))
          .Append(Row("Intervalos de atividade (intervalos.csv)", Number(intervalos), labelStyle, valueStyle))
          .Append(Row("Resumos diários (agregados.csv)", Number(agregados), labelStyle, valueStyle))
          .Append("</table>")
          .Append("<p style=\"margin:12px 0 0;color:#5b6577;font-size:13px\">")
          .Append("São as mesmas contagens do arquivo manifest.json deste pacote. O conteúdo completo ")
          .Append("está nos arquivos .csv, que abrem em qualquer planilha.</p></div>");

        // ---- política de mascaramento vigente
        sb.Append("<div style=\"").Append(cardStyle).Append("\">")
          .Append("<h2 style=\"font-size:16px;margin:0 0 12px\">Política de títulos de janela em vigor</h2>")
          .Append("<p style=\"margin:0;font-size:14px\">").Append(Html(DescribeTitlePolicyPtBr(titlePolicy)))
          .Append("</p><p style=\"margin:12px 0 0;color:#5b6577;font-size:13px\">")
          .Append("O monitoramento nunca captura teclas digitadas, telas, área de transferência, ")
          .Append("conteúdo de arquivos ou mensagens, câmera, microfone ou localização.</p></div>");

        // ---- retenção
        sb.Append("<div style=\"").Append(cardStyle).Append("\">")
          .Append("<h2 style=\"font-size:16px;margin:0 0 12px\">Por quanto tempo os dados ficam guardados</h2>")
          .Append("<table style=\"width:100%;border-collapse:collapse\">")
          .Append(Row("Eventos brutos", "90 dias", labelStyle, valueStyle))
          .Append(Row("Intervalos de atividade", "12 meses", labelStyle, valueStyle))
          .Append(Row("Resumos diários", "24 meses", labelStyle, valueStyle))
          .Append(Row("Trilha de auditoria de acessos", "24 meses", labelStyle, valueStyle))
          .Append("</table>")
          .Append("<p style=\"margin:12px 0 0;color:#5b6577;font-size:13px\">")
          .Append("Prazos máximos fixos do produto. Depois de cada prazo os dados são apagados ")
          .Append("automaticamente.</p></div>");

        // ---- extrato de acessos
        sb.Append("<div style=\"").Append(cardStyle).Append("\">")
          .Append("<h2 style=\"font-size:16px;margin:0 0 12px\">Quem consultou meus dados</h2>");

        if (accesses.Count == 0)
        {
            sb.Append("<p style=\"margin:0;font-size:14px\">")
              .Append("Nenhuma consulta identificada aos seus dados no período coberto por este extrato.")
              .Append("</p>");
        }
        else
        {
            sb.Append("<table style=\"width:100%;border-collapse:collapse\">")
              .Append("<thead><tr>")
              .Append("<th style=\"").Append(thStyle).Append("\">Data e hora</th>")
              .Append("<th style=\"").Append(thStyle).Append("\">O que foi consultado</th>")
              .Append("<th style=\"").Append(thStyle).Append("\">Quem consultou</th>")
              .Append("</tr></thead><tbody>");
            foreach (var access in accesses)
            {
                sb.Append("<tr><td style=\"").Append(tdStyle).Append("\">")
                  .Append(Html(FormatInstant(access.OccurredAt, tz))).Append("</td>")
                  .Append("<td style=\"").Append(tdStyle).Append("\">")
                  .Append(Html(DescribeAction(access.Action))).Append("</td>")
                  .Append("<td style=\"").Append(tdStyle).Append("\">")
                  .Append(Html(access.ActorName ?? "Sistema")).Append("</td></tr>");
            }

            sb.Append("</tbody></table>");
            if (accesses.Count == AccessStatementLimit)
            {
                sb.Append("<p style=\"margin:12px 0 0;color:#5b6577;font-size:13px\">")
                  .Append("Mostrando as ").Append(Number(AccessStatementLimit))
                  .Append(" consultas mais recentes.</p>");
            }
        }

        sb.Append("<p style=\"margin:12px 0 0;color:#5b6577;font-size:13px\">")
          .Append("Este extrato cobre acessos registrados a partir de ")
          .Append(Html(AccessStatementSince.ToString("dd/MM/yyyy")))
          .Append(". Consultas feitas por dispositivo ou por equipe também ficam registradas na ")
          .Append("trilha de auditoria da organização, mas não identificam um titular ")
          .Append("individualmente e por isso não aparecem aqui.</p></div>");

        // ---- rodapé
        sb.Append("<p style=\"margin:24px 0 0;color:#5b6577;font-size:12px\">")
          .Append("Documento gerado automaticamente pelo +351 Monitor a pedido da organização ")
          .Append("controladora dos dados. Conteúdo sujeito a revisão jurídica.</p>")
          .Append("</div></body></html>");

        return sb.ToString();
    }

    /// <summary>Linha rótulo/valor das tabelas de definição do relatório.</summary>
    private static string Row(string label, string value, string labelStyle, string valueStyle) =>
        $"<tr><td style=\"{labelStyle}\">{Html(label)}</td><td style=\"{valueStyle}\">{Html(value)}</td></tr>";

    /// <summary>Descrição pt-BR da política de títulos — JAMAIS o conteúdo dos masked_patterns.</summary>
    private static string DescribeTitlePolicyPtBr(string mode) => mode switch
    {
        "FULL" =>
            "O título da janela em foco é registrado por completo, junto com o nome do aplicativo.",
        "APP_ONLY" =>
            "Apenas o nome do aplicativo em foco é registrado. Os títulos das janelas não são coletados.",
        _ =>
            "O título da janela em foco é registrado com mascaramento: quando o título contém um termo "
            + "sensível definido pela organização, ele é substituído antes de sair da sua máquina.",
    };

    /// <summary>Ação da trilha em linguagem para o TITULAR, não no verbo interno.</summary>
    private static string DescribeAction(string action) => action switch
    {
        "view_report" => "Relatório de uso com os seus dados",
        _ => action,
    };

    private static string FormatInstant(DateTimeOffset instant, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(instant, tz).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString("N0", new CultureInfo("pt-BR"));

    /// <summary>
    /// Escape de HTML — todo valor vindo do banco passa por aqui. Escapa SÓ os caracteres com
    /// significado em markup: o documento declara UTF-8, então acento vai literal (o
    /// WebUtility.HtmlEncode transformaria "Usuário" em entidade numérica, deixando o arquivo
    /// entregue ao titular ilegível na fonte sem ganho algum de segurança).
    /// </summary>
    private static string Html(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Recibo do relatório legível, publicado no manifest.json.</summary>
    private sealed record AboutMeReceipt(string EntryName, string Sha256, int AccessRows, DateOnly Since);

    /// <summary>Uma consulta aos dados do titular (sem o IP do ator — decisão deliberada).</summary>
    private sealed record AccessRow(DateTimeOffset OccurredAt, string Action, string? ActorName);

    // ------------------------------------------------------------ helpers de CSV-em-ZIP
    /// <summary>
    /// Escreve uma entry CSV no ZIP em STREAMING: StreamWriter (UTF-8 BOM, CRLF, ';') sobre o
    /// stream da entry; uma linha por registro do reader. LIMIT teto+1 (mesma régua do CSV de
    /// relatório): a linha extra só sinaliza truncated, nunca é escrita.
    /// </summary>
    private async Task<(int Rows, bool Truncated)> WriteCsvEntryAsync(
        ZipArchive archive, string entryName, string header, string sql,
        Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, string> writeRow, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            NewLine = "\r\n",
        };
        await writer.WriteLineAsync(header);

        var rows = 0;
        var truncated = false;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command);
        command.Parameters.AddWithValue("RowLimit", maxDataRows + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (rows == maxDataRows)
            {
                truncated = true;
                break;
            }

            await writer.WriteLineAsync(writeRow(reader));
            rows++;
        }

        return (rows, truncated);
    }

    private static string FormatTimestamp(NpgsqlDataReader reader, int ordinal, TimeZoneInfo tz) =>
        reader.IsDBNull(ordinal)
            ? ""
            : TimeZoneInfo.ConvertTime(reader.GetFieldValue<DateTimeOffset>(ordinal), tz)
                .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Titulares do escopo do pacote (resolvidos das device_users do tenant).</summary>
    private sealed record DsrScope(string Kind, Guid? DeviceId, IReadOnlyList<DsrSubject> Subjects)
    {
        public Guid[] SubjectIds => Subjects.Select(s => s.DeviceUserId).ToArray();
    }

    private sealed record DsrSubject(
        Guid DeviceUserId, Guid DeviceId, string WindowsSid, string WindowsUsername, string? DisplayName,
        DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, string DeviceName)
    {
        /// <summary>Nome para o titular ler: apelido do portal quando houver, senão a conta do Windows.</summary>
        public string Label => string.IsNullOrWhiteSpace(DisplayName) ? WindowsUsername : DisplayName;
    }
}
