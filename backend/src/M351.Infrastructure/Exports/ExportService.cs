using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using M351.Infrastructure.Reports;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace M351.Infrastructure.Exports;

/// <summary>
/// Geração assíncrona de CSVs (F3.5 — Seções 7.4/7.6/8.6, DoD 11.3). Vive na Infrastructure
/// (padrão IntervalizationService): NpgsqlDataSource + RunOnceAsync invocável pelos testes;
/// o Worker apenas agenda (ExportJob, a cada 15 s).
///
/// Um ciclo de RunOnceAsync:
///  1. sweep barato: deleta do disco os arquivos de jobs com expires_at &lt; now (e zera
///     file_path para o sweep não revisitar o job) — o download passa a responder 410;
///  2. recuperação de órfãos: jobs presos em 'running' há mais de 15 min (kill -9/OOM/queda
///     do worker — só o shutdown gracioso devolve à fila) voltam para 'queued' e são
///     reprocessados do zero; um job "venenoso" que derrube a geração cai no catch e vira
///     'failed' no reprocesso (não loopa);
///  3. claim TRANSACIONAL de UM job: UPDATE ... WHERE id IN (SELECT ... WHERE
///     status='queued' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING — dois
///     workers jamais processam o mesmo job; o claim carimba started_at (base do timeout do
///     passo 2); fila vazia → retorna 0 (o ExportJob drena chamando em loop até 0);
///  4. gera o CSV em STREAMING para {Exports:Directory}/{tenant_id}/{job_id}.csv
///     (StreamWriter, nunca o arquivo inteiro em memória) e marca done + row_count +
///     truncated + expires_at = now + 7 dias. Falha → status='failed' + log (sem coluna de
///     erro no schema: o Serilog é a fonte) e o arquivo parcial é removido.
///
/// Regras INEGOCIÁVEIS do CSV (spec linhas 949/1078-1082):
///  - SEMPRE UTF-8 COM BOM e separador ';' (Excel pt-BR); campos com ';', aspas ou quebra
///    de linha entre aspas duplas (RFC 4180 adaptado ao ';'); CRLF determinístico;
///  - horários no FUSO DO TENANT (datas dd/mm/aaaa, horas HH:mm); durações "6h 40min"
///    (mesma régua do portal — nunca decimal) + coluna extra de horas decimais;
///  - horas decimais com VÍRGULA (decisão documentada: o Excel pt-BR lendo CSV separado
///    por ';' espera vírgula decimal; ponto viraria texto);
///  - jornada: colunas "Primeiro evento"/"Último evento" (JAMAIS outra nomenclatura de
///    ponto) e DISCLAIMER VERBATIM como ÚLTIMA linha de TODO CSV de jornada;
///  - teto de 500.000 linhas de DADOS (spec linha 804): o SQL leva LIMIT teto+1 — a linha
///    extra só sinaliza truncated=true (exposto na listagem para o usuário SABER que o CSV
///    é parcial e estreitar o filtro), nunca é escrita; o teto é injetável (maxDataRows)
///    para os testes exercitarem o corte com valores pequenos;
///  - números da jornada = MESMO SQL do endpoint (JornadaReportSql) — consistência 11.3
///    por construção; o CSV de uso espelha as queries de ReportsController (F3.3).
///
/// file_path é gravado RELATIVO ao diretório de exports ({tenant_id}/{job_id}.csv):
/// API e worker podem montar o volume em caminhos diferentes — cada lado resolve contra a
/// própria config Exports:Directory.
/// </summary>
public sealed partial class ExportService(
    NpgsqlDataSource dataSource,
    string exportsDirectory,
    ILogger<ExportService>? logger = null,
    int maxDataRows = ExportService.MaxDataRows)
{
    /// <summary>Teto de linhas de DADOS por arquivo (spec linha 804); atingiu → trunca + truncated=true.</summary>
    public const int MaxDataRows = 500_000;

    /// <summary>CSVs de relatório expiram em 7 dias (spec linha 738).</summary>
    public static readonly TimeSpan FileRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Pacotes DSR (dsr_subject/dsr_device/tenant_full) expiram em 72h (spec linha 738) — o
    /// link de download de dado pessoal do titular vive bem menos que o CSV de relatório.
    /// </summary>
    public static readonly TimeSpan DsrFileRetention = TimeSpan.FromHours(72);

    /// <summary>Kinds que geram um pacote ZIP (DSR/offboarding) em vez de um CSV de relatório.</summary>
    private static readonly string[] ZipKinds = ["dsr_subject", "dsr_device", "tenant_full"];

    /// <summary>true para os kinds de pacote DSR/offboarding (ZIP, retenção 72h).</summary>
    private static bool IsZipKind(string kind) => ZipKinds.Contains(kind);

    /// <summary>
    /// Job em 'running' há mais que isto = worker morreu sem shutdown gracioso (kill -9,
    /// OOM, queda) — o sweep devolve à fila. Folgado: o maior CSV (500 k linhas) leva
    /// segundos, não minutos.
    /// </summary>
    public static readonly TimeSpan StaleRunningTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Disclaimer da jornada (Seção 8.6, DoD 11.3) — VERBATIM, idêntico ao banner do portal
    /// (portal/src/pages/relatorios/JornadaPage.tsx). Última linha de TODO CSV de jornada.
    /// </summary>
    public const string JornadaDisclaimer =
        "Relatório gerencial de uso da estação de trabalho. Não constitui registro eletrônico de "
        + "ponto (Portaria 671/MTE) e não substitui o controle de jornada do art. 74 da CLT.";

    /// <summary>Cabeçalho FIXO do CSV de jornada (mesmas colunas/ordem da tela — Seção 8.6).</summary>
    public const string JornadaHeader =
        "Data;Dia da semana;Dispositivo;Usuários;Primeiro evento;Último evento;"
        + "Tempo ligada;Tempo ativo;Tempo ocioso;Tempo bloqueado;Horas decimais (ativo);Observação";

    /// <summary>
    /// Cabeçalho FIXO do CSV de atividade fora do horário de trabalho. Vocabulário de
    /// EQUILÍBRIO: jamais hora extra, jornada extraordinária ou banco de horas.
    /// </summary>
    public const string ForaDoHorarioHeader =
        "Dispositivo;Tempo ativo no período;Atividade fora do horário;Antes do horário;"
        + "Depois do horário;Em dias fora da escala;Dias com atividade fora;Horas decimais (fora do horário)";

    /// <summary>
    /// A janela deixou de existir entre o POST (que a exige) e a geração: o arquivo sai com o
    /// motivo em vez de uma tabela vazia sem explicação.
    /// </summary>
    public const string ForaDoHorarioSemJanela =
        "Horário de trabalho não configurado para a organização: sem janela declarada não há "
        + "como apurar atividade fora dela.";

    /// <summary>Dias da semana pt-BR fixos (independe de ICU do runtime).</summary>
    private static readonly string[] WeekdayNames =
        ["domingo", "segunda-feira", "terça-feira", "quarta-feira", "quinta-feira", "sexta-feira", "sábado"];

    /// <summary>
    /// Um ciclo: sweep de expirados + claim e processamento de NO MÁXIMO um job.
    /// Retorna 1 se um job foi claimado (done OU failed), 0 com a fila vazia.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        await SweepExpiredAsync(ct);
        await RequeueStaleRunningAsync(ct);

        // claim transacional (UPDATE único é atômico): SKIP LOCKED garante exclusão mútua
        // entre instâncias sem advisory lock; commit imediato — crash durante a geração
        // deixa o job em 'running' até o RequeueStaleRunningAsync de um ciclo futuro
        ExportJobRow? job = null;
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand("""
            UPDATE export_jobs SET status = 'running', started_at = now()
            WHERE id IN (
                SELECT id FROM export_jobs
                WHERE status = 'queued'
                ORDER BY created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING id, tenant_id, kind, params::text
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                job = new ExportJobRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3));
            }
        }

        if (job is null) return 0;

        // pacotes DSR/offboarding são .zip com retenção 72h; CSVs de relatório, .csv com 7 dias
        var zip = IsZipKind(job.Kind);
        var extension = zip ? "zip" : "csv";
        var retention = zip ? DsrFileRetention : FileRetention;

        var relativePath = $"{job.TenantId}/{job.Id}.{extension}";
        var absolutePath = Path.GetFullPath(Path.Combine(exportsDirectory, job.TenantId.ToString(), $"{job.Id}.{extension}"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            var (rowCount, truncated) = await GenerateAsync(job, absolutePath, ct);

            await ExecAsync("""
                UPDATE export_jobs
                SET status = 'done', file_path = @path, row_count = @rows, truncated = @truncated, expires_at = @expires
                WHERE id = @id
                """,
                [("path", relativePath), ("rows", rowCount), ("truncated", truncated),
                 ("expires", DateTimeOffset.UtcNow + retention), ("id", job.Id)], ct);

            logger?.LogInformation(
                "Export {JobId} ({Kind}) concluído: {Rows} linha(s){Truncated}.",
                job.Id, job.Kind, rowCount, truncated ? " (TRUNCADO no teto)" : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown: devolve o job à fila — o próximo ciclo reprocessa do zero
            TryDeleteFile(absolutePath);
            await ExecAsync("UPDATE export_jobs SET status = 'queued', started_at = NULL WHERE id = @id",
                [("id", job.Id)], CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // sem coluna de erro no schema — o Serilog é a fonte da causa
            logger?.LogError(ex, "Export {JobId} ({Kind}) falhou.", job.Id, job.Kind);
            TryDeleteFile(absolutePath);
            await ExecAsync("UPDATE export_jobs SET status = 'failed' WHERE id = @id", [("id", job.Id)], CancellationToken.None);
        }

        return 1;
    }

    // ------------------------------------------------------------ geração
    private async Task<(int Rows, bool Truncated)> GenerateAsync(ExportJobRow job, string absolutePath, CancellationToken ct)
    {
        // pacotes DSR/offboarding: ZIP em STREAMING (ZipArchive sobre FileStream — jamais o
        // ZIP inteiro em memória); CSVs de relatório: o caminho clássico StreamWriter
        if (IsZipKind(job.Kind))
            return await GenerateDsrZipAsync(job, absolutePath, ct);

        var p = ParseParams(job.ParamsJson);

        string timezone = await TenantTimezoneAsync(job.TenantId, ct);

        // BOM explícito (UTF8Encoding(true)) + CRLF determinístico (RFC 4180)
        await using var stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.NewLine = "\r\n";

        return job.Kind switch
        {
            "jornada_csv" => await WriteJornadaAsync(writer, job.TenantId, p, timezone, ct),
            "usage_csv" => await WriteUsageAsync(writer, job.TenantId, p, ct),
            "fora_horario_csv" => await WriteForaDoHorarioAsync(writer, job.TenantId, p, timezone, ct),
            _ => throw new InvalidOperationException($"Kind de export não suportado: {job.Kind}."),
        };
    }

    /// <summary>business_hours cru da org (null quando a janela não está configurada).</summary>
    private async Task<string?> TenantBusinessHoursAsync(Guid tenantId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT business_hours::text FROM organizations WHERE id = @t", connection);
        command.Parameters.AddWithValue("t", tenantId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private async Task<string> TenantTimezoneAsync(Guid tenantId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT timezone FROM organizations WHERE id = @t", connection);
        command.Parameters.AddWithValue("t", tenantId);
        return await command.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException($"Organização {tenantId} não encontrada.");
    }

    /// <summary>
    /// CSV de jornada: MESMO SQL do endpoint (JornadaReportSql.Rows, sem paginação) —
    /// mesmas linhas, mesmos números (11.3). LIMIT teto+1: a linha extra só prova o
    /// truncamento (truncated=true), nunca é escrita — e o dispose do reader não drena
    /// resto de result set pela rede. Última linha SEMPRE o disclaimer, inclusive em
    /// arquivo truncado.
    /// </summary>
    private async Task<(int Rows, bool Truncated)> WriteJornadaAsync(
        StreamWriter writer, Guid tenantId, ExportParams p, string timezone, CancellationToken ct)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        await writer.WriteLineAsync(JornadaHeader);

        var rows = 0;
        var truncated = false;
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand($"{JornadaReportSql.Rows}\nLIMIT @RowLimit", connection))
        {
            AddRangeParameters(command, tenantId, p);
            command.Parameters.AddWithValue("RowLimit", maxDataRows + 1);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (rows == maxDataRows)
                {
                    truncated = true;
                    break;
                }

                var day = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var secondsActive = reader.GetInt64(7);
                await writer.WriteLineAsync(string.Join(';',
                    day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    WeekdayNames[(int)day.DayOfWeek],
                    Csv(reader.GetString(2)),                                     // dispositivo
                    Csv(reader.IsDBNull(3) ? null : reader.GetString(3)),         // usuários
                    FormatHm(reader, 4, tz),                                      // primeiro evento
                    FormatHm(reader, 5, tz),                                      // último evento
                    FormatDuration(reader.GetInt64(6)),                           // tempo ligada
                    FormatDuration(secondsActive),                                // tempo ativo
                    FormatDuration(reader.GetInt64(8)),                           // tempo ocioso
                    FormatDuration(reader.GetInt64(9)),                           // tempo bloqueado
                    DecimalHours(secondsActive),
                    NoteLabel(reader.IsDBNull(10) ? null : reader.GetString(10))));
                rows++;
            }
        }

        await writer.WriteLineAsync(JornadaDisclaimer);
        return (rows, truncated);
    }

    /// <summary>
    /// CSV de uso: colunas do group_by escolhido + horas decimais. As queries espelham as
    /// do GET /reports/usage (ReportsController, F3.3) sem paginação: devices archived
    /// SEMPRE fora, mesmo recorte por etiqueta de equipe (@Tag, F5), ordenação por tempo
    /// ativo desc. SEM disclaimer (não é jornada).
    /// </summary>
    private async Task<(int Rows, bool Truncated)> WriteUsageAsync(
        StreamWriter writer, Guid tenantId, ExportParams p, CancellationToken ct)
    {
        var (header, sql, writeRow) = UsagePlan(p.GroupBy
            ?? throw new InvalidOperationException("Export usage_csv sem group_by."));

        await writer.WriteLineAsync(header);

        var rows = 0;
        var truncated = false;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        // mesmo padrão da jornada: LIMIT teto+1 — a linha extra só sinaliza truncated
        await using var command = new NpgsqlCommand($"{sql}\nLIMIT @RowLimit", connection);
        AddRangeParameters(command, tenantId, p);
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

    /// <summary>
    /// CSV de atividade fora do horário de trabalho: MESMO SQL do endpoint
    /// (ForaDoHorarioReportSql.Rows, sem paginação), mesmas linhas, mesmos números (11.3).
    /// Uma linha por dispositivo COM atividade fora da janela, ordenada por tempo fora desc.
    ///
    /// Disclaimer da Portaria 671/MTE como ÚLTIMA linha, inclusive em arquivo truncado, pelo
    /// mesmo motivo da jornada: o arquivo circula fora do portal e não pode ser lido como
    /// registro de ponto. O POST /exports já recusa o pedido quando a janela não está
    /// configurada ou quando a coleta é restrita ao horário de trabalho; se a configuração
    /// mudar entre o POST e a geração, o arquivo sai com o motivo em vez de números falsos.
    /// </summary>
    private async Task<(int Rows, bool Truncated)> WriteForaDoHorarioAsync(
        StreamWriter writer, Guid tenantId, ExportParams p, string timezone, CancellationToken ct)
    {
        await writer.WriteLineAsync(ForaDoHorarioHeader);

        if (!BusinessHoursWindow.TryParse(await TenantBusinessHoursAsync(tenantId, ct), out var schedule))
        {
            await writer.WriteLineAsync(Csv(ForaDoHorarioSemJanela));
            await writer.WriteLineAsync(JornadaDisclaimer);
            return (0, false);
        }

        var rows = 0;
        var truncated = false;
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand($"{ForaDoHorarioReportSql.Rows}\nLIMIT @RowLimit", connection))
        {
            AddRangeParameters(command, tenantId, p);
            command.Parameters.AddWithValue("Timezone", timezone);
            command.Parameters.AddWithValue("BusinessDays", schedule!.IsoDays);
            command.Parameters.AddWithValue("BusinessStart", schedule.Start.ToString("HH\\:mm"));
            command.Parameters.AddWithValue("BusinessEnd", schedule.End.ToString("HH\\:mm"));
            // mesmo padrão da jornada: LIMIT teto+1, a linha extra só sinaliza truncated
            command.Parameters.AddWithValue("RowLimit", maxDataRows + 1);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (rows == maxDataRows)
                {
                    truncated = true;
                    break;
                }

                var secondsOutside = reader.GetInt64(3);
                await writer.WriteLineAsync(string.Join(';',
                    Csv(reader.GetString(1)),                 // dispositivo
                    FormatDuration(reader.GetInt64(2)),       // tempo ativo no período
                    FormatDuration(secondsOutside),           // atividade fora do horário
                    FormatDuration(reader.GetInt64(4)),       // antes do horário
                    FormatDuration(reader.GetInt64(5)),       // depois do horário
                    FormatDuration(reader.GetInt64(6)),       // em dias fora da escala
                    reader.GetInt32(7).ToString(CultureInfo.InvariantCulture),
                    DecimalHours(secondsOutside)));
                rows++;
            }
        }

        await writer.WriteLineAsync(JornadaDisclaimer);
        return (rows, truncated);
    }

    /// <summary>Cabeçalho + SQL + projeção de linha por group_by (vocabulário fixo da Seção 8.7).</summary>
    private static (string Header, string Sql, Func<NpgsqlDataReader, string> WriteRow) UsagePlan(string groupBy) => groupBy switch
    {
        "app" => (
            "Aplicativo;Nome de exibição;Categoria;Classificação;Tempo ativo;Horas decimais (ativo);Dispositivos",
            """
            SELECT a.process_name,
                   COALESCE(tac.custom_display_name, a.display_name) AS display_name,
                   c.name AS category_name, c.classification,
                   sum(u.seconds_active)::bigint AS seconds_active,
                   count(DISTINCT u.device_id)::int AS device_count
            FROM daily_app_usage u
            JOIN devices d ON d.id = u.device_id AND d.tenant_id = u.tenant_id
            JOIN app_catalog a ON a.id = u.app_id
            LEFT JOIN tenant_app_categories tac ON tac.tenant_id = u.tenant_id AND tac.app_id = u.app_id
            LEFT JOIN categories c ON c.tenant_id = u.tenant_id AND c.id = tac.category_id
            WHERE u.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND u.summary_date BETWEEN @From::date AND @To::date
              AND (@FilterDevices = false OR u.device_id = ANY(@DeviceIds))
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            GROUP BY a.process_name, COALESCE(tac.custom_display_name, a.display_name), c.name, c.classification
            ORDER BY seconds_active DESC, a.process_name
            """,
            reader =>
            {
                var secondsActive = reader.GetInt64(4);
                return string.Join(';',
                    Csv(reader.GetString(0)),
                    Csv(reader.GetString(1)),
                    Csv(reader.IsDBNull(2) ? "Não categorizado" : reader.GetString(2)),
                    ClassificationLabel(reader.IsDBNull(3) ? null : reader.GetInt16(3)),
                    FormatDuration(secondsActive),
                    DecimalHours(secondsActive),
                    reader.GetInt32(5).ToString(CultureInfo.InvariantCulture));
            }),

        "category" => (
            "Categoria;Classificação;Tempo ativo;Horas decimais (ativo);Apps",
            """
            SELECT c.name, c.classification,
                   sum(u.seconds_active)::bigint AS seconds_active,
                   count(DISTINCT u.app_id)::int AS app_count
            FROM daily_app_usage u
            JOIN devices d ON d.id = u.device_id AND d.tenant_id = u.tenant_id
            LEFT JOIN tenant_app_categories tac ON tac.tenant_id = u.tenant_id AND tac.app_id = u.app_id
            LEFT JOIN categories c ON c.tenant_id = u.tenant_id AND c.id = tac.category_id
            WHERE u.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND u.summary_date BETWEEN @From::date AND @To::date
              AND (@FilterDevices = false OR u.device_id = ANY(@DeviceIds))
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            GROUP BY c.name, c.classification
            ORDER BY seconds_active DESC, c.name NULLS LAST
            """,
            reader =>
            {
                var secondsActive = reader.GetInt64(2);
                return string.Join(';',
                    Csv(reader.IsDBNull(0) ? "Não categorizado" : reader.GetString(0)),
                    ClassificationLabel(reader.IsDBNull(1) ? null : reader.GetInt16(1)),
                    FormatDuration(secondsActive),
                    DecimalHours(secondsActive),
                    reader.GetInt32(3).ToString(CultureInfo.InvariantCulture));
            }),

        "device" => (
            "Dispositivo;Tempo ativo;Tempo ocioso;Tempo bloqueado;Tempo ligada;"
            + "Relacionado ao trabalho;Neutro;Não relacionado ao trabalho;Horas decimais (ativo)",
            """
            SELECT COALESCE(d.display_name, d.hostname) AS device_name,
                   sum(s.seconds_active)::bigint AS seconds_active,
                   sum(s.seconds_idle)::bigint AS seconds_idle,
                   sum(s.seconds_locked)::bigint AS seconds_locked,
                   sum(s.seconds_on)::bigint AS seconds_on,
                   sum(s.seconds_work_related)::bigint AS seconds_work_related,
                   sum(s.seconds_neutral)::bigint AS seconds_neutral,
                   sum(s.seconds_not_work_related)::bigint AS seconds_not_work_related
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @From::date AND @To::date
              AND (@FilterDevices = false OR s.device_id = ANY(@DeviceIds))
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            GROUP BY s.device_id, COALESCE(d.display_name, d.hostname)
            ORDER BY seconds_active DESC, device_name
            """,
            reader =>
            {
                var secondsActive = reader.GetInt64(1);
                return string.Join(';',
                    Csv(reader.GetString(0)),
                    FormatDuration(secondsActive),
                    FormatDuration(reader.GetInt64(2)),
                    FormatDuration(reader.GetInt64(3)),
                    FormatDuration(reader.GetInt64(4)),
                    FormatDuration(reader.GetInt64(5)),
                    FormatDuration(reader.GetInt64(6)),
                    FormatDuration(reader.GetInt64(7)),
                    DecimalHours(secondsActive));
            }),

        "device_user" => (
            "Usuário;Usuário Windows;Dispositivo;Tempo ativo;Tempo ocioso;Tempo bloqueado;Tempo ligada;"
            + "Relacionado ao trabalho;Neutro;Não relacionado ao trabalho;Horas decimais (ativo)",
            """
            SELECT s.device_user_id,
                   du.display_name AS user_display_name,
                   du.windows_username AS windows_user,
                   COALESCE(d.display_name, d.hostname) AS device_name,
                   sum(s.seconds_active)::bigint AS seconds_active,
                   sum(s.seconds_idle)::bigint AS seconds_idle,
                   sum(s.seconds_locked)::bigint AS seconds_locked,
                   sum(s.seconds_on)::bigint AS seconds_on,
                   sum(s.seconds_work_related)::bigint AS seconds_work_related,
                   sum(s.seconds_neutral)::bigint AS seconds_neutral,
                   sum(s.seconds_not_work_related)::bigint AS seconds_not_work_related
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            LEFT JOIN device_users du ON du.tenant_id = s.tenant_id AND du.id = s.device_user_id
            WHERE s.tenant_id = @TenantId
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @From::date AND @To::date
              AND (@FilterDevices = false OR s.device_id = ANY(@DeviceIds))
              AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            GROUP BY s.device_user_id, s.device_id, du.display_name, du.windows_username,
                     COALESCE(d.display_name, d.hostname)
            ORDER BY seconds_active DESC, device_name, s.device_user_id
            """,
            reader =>
            {
                // mesma regra de exibição do endpoint: UUID zero = lane-máquina;
                // titular removido por DSR fica com rótulo neutro
                var laneName = reader.GetGuid(0) == Guid.Empty
                    ? "Máquina (sem usuário)"
                    : !reader.IsDBNull(1) ? reader.GetString(1)
                    : !reader.IsDBNull(2) ? reader.GetString(2)
                    : "Usuário desconhecido";
                var secondsActive = reader.GetInt64(4);
                return string.Join(';',
                    Csv(laneName),
                    Csv(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    Csv(reader.GetString(3)),
                    FormatDuration(secondsActive),
                    FormatDuration(reader.GetInt64(5)),
                    FormatDuration(reader.GetInt64(6)),
                    FormatDuration(reader.GetInt64(7)),
                    FormatDuration(reader.GetInt64(8)),
                    FormatDuration(reader.GetInt64(9)),
                    FormatDuration(reader.GetInt64(10)),
                    DecimalHours(secondsActive));
            }),

        _ => throw new InvalidOperationException($"group_by não suportado: {groupBy}."),
    };

    // ------------------------------------------------------------ sweep de expirados
    /// <summary>
    /// Remove do DISCO os arquivos de jobs vencidos e zera file_path (a linha fica para a
    /// trilha de 30 dias; o download responde 410). Barato: a query só retorna algo quando
    /// há job vencido ainda não varrido.
    /// </summary>
    private async Task SweepExpiredAsync(CancellationToken ct)
    {
        var expired = new List<(Guid Id, string FilePath)>();
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand(
            "SELECT id, file_path FROM export_jobs WHERE expires_at < now() AND file_path IS NOT NULL LIMIT 100", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) expired.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        foreach (var (id, filePath) in expired)
        {
            TryDeleteFile(Path.GetFullPath(Path.Combine(exportsDirectory, filePath)));
            await ExecAsync("UPDATE export_jobs SET file_path = NULL WHERE id = @id", [("id", id)], ct);
            logger?.LogInformation("Export {JobId} expirado: arquivo removido.", id);
        }
    }

    // ------------------------------------------------------------ recuperação de órfãos
    /// <summary>
    /// Jobs presos em 'running' além do timeout voltam para 'queued' (reprocesso do zero —
    /// a geração sobrescreve o arquivo com FileMode.Create). Cobre worker morto sem shutdown
    /// gracioso E falha transitória do UPDATE para 'failed'. Sem isto, um órfão ficaria
    /// "Gerando" para sempre na tela (e o portal pollando a cada 5 s indefinidamente).
    /// Job venenoso não loopa: o reprocesso cai no catch e vira 'failed'.
    /// </summary>
    private async Task RequeueStaleRunningAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE export_jobs SET status = 'queued', started_at = NULL
            WHERE status = 'running'
              AND (started_at IS NULL OR started_at < now() - @timeout)
            """, connection);
        command.Parameters.AddWithValue("timeout", StaleRunningTimeout);
        var requeued = await command.ExecuteNonQueryAsync(ct);
        if (requeued > 0)
            logger?.LogWarning(
                "{Jobs} export(s) preso(s) em 'running' há mais de {Timeout} devolvido(s) à fila (worker caiu durante a geração?).",
                requeued, StaleRunningTimeout);
    }

    // ------------------------------------------------------------ helpers
    private static void AddRangeParameters(NpgsqlCommand command, Guid tenantId, ExportParams p)
    {
        command.Parameters.AddWithValue("TenantId", tenantId);
        command.Parameters.AddWithValue("From", p.From);
        command.Parameters.AddWithValue("To", p.To);
        command.Parameters.AddWithValue("FilterDevices", p.DeviceIds.Length > 0);
        command.Parameters.AddWithValue("DeviceIds", p.DeviceIds);
        // @Tag (F5): tipo EXPLÍCITO — sem filtro o valor é NULL e o Npgsql não infere o tipo
        // de um DBNull cru; o ::text do predicado só resolve o lado do Postgres.
        command.Parameters.Add(new NpgsqlParameter("Tag", NpgsqlDbType.Text)
        {
            Value = (object?)p.Tag ?? DBNull.Value,
        });
    }

    /// <summary>Params do job (já validados no POST /exports com os validadores dos endpoints).</summary>
    private static ExportParams ParseParams(string paramsJson)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var root = doc.RootElement;
        var from = root.GetProperty("from").GetString()!;
        var to = root.GetProperty("to").GetString()!;
        var deviceIds = root.TryGetProperty("device_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(e => e.GetGuid()).ToArray()
            : [];
        var groupBy = root.TryGetProperty("group_by", out var g) ? g.GetString() : null;
        // tag (F5): ausente nos params = sem recorte de equipe (jobs criados antes do filtro
        // continuam válidos e seguem exportando a organização inteira)
        var tag = root.TryGetProperty("tag", out var t) ? t.GetString() : null;
        return new ExportParams(from, to, deviceIds, groupBy, tag);
    }

    /// <summary>Campo CSV: aspas duplas quando contém ';', aspas ou quebra de linha (RFC 4180 com ';').</summary>
    internal static string Csv(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        return field.IndexOfAny([';', '"', '\n', '\r']) >= 0
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
    }

    /// <summary>Duração "6h 40min" / "12min" / "45s" — espelho de formatDuration do portal; nunca decimal.</summary>
    internal static string FormatDuration(long totalSeconds)
    {
        var s = Math.Max(0, totalSeconds);
        if (s < 60) return $"{s}s";
        var h = s / 3600;
        var m = s % 3600 / 60;
        if (h == 0) return $"{m}min";
        return m == 0 ? $"{h}h" : $"{h}h {m:00}min";
    }

    /// <summary>Horas decimais com VÍRGULA (Excel pt-BR + separador ';'), independente de ICU.</summary>
    internal static string DecimalHours(long seconds) =>
        (seconds / 3600.0).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>HH:mm no fuso do tenant; vazio quando o dia não tem evento.</summary>
    private static string FormatHm(NpgsqlDataReader reader, int ordinal, TimeZoneInfo tz) =>
        reader.IsDBNull(ordinal)
            ? ""
            : TimeZoneInfo.ConvertTime(reader.GetFieldValue<DateTimeOffset>(ordinal), tz)
                .ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Observação humana — mesmos três casos do endpoint (vocabulário neutro da Seção 8.6).</summary>
    private static string NoteLabel(string? note) => note switch
    {
        "dados_incompletos" => "Dados incompletos",
        "sem_comunicacao" => "Sem comunicação",
        "sem_dados" => "Sem dados",
        _ => "",
    };

    /// <summary>Vocabulário fixo de classificação (Seção 8.7) — JAMAIS outra nomenclatura.</summary>
    private static string ClassificationLabel(short? classification) => classification switch
    {
        1 => "Relacionado ao trabalho",
        -1 => "Não relacionado ao trabalho",
        0 => "Neutro",
        _ => "Não categorizado",
    };

    private static void TryDeleteFile(string absolutePath)
    {
        try
        {
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
        }
        catch (IOException)
        {
            // arquivo em uso (download em andamento): o próximo sweep tenta de novo
        }
    }

    private async Task ExecAsync(string sql, (string Name, object? Value)[] args, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record ExportJobRow(Guid Id, Guid TenantId, string Kind, string ParamsJson);

    private sealed record ExportParams(string From, string To, Guid[] DeviceIds, string? GroupBy, string? Tag);
}
