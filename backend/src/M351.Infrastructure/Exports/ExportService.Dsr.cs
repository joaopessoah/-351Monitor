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

        await WriteManifestEntryAsync(archive, job, subjects, eventos, intervalos, agregados, truncated, ct);

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
                "SELECT id, device_id, windows_sid, windows_username, display_name FROM device_users WHERE tenant_id = @t",
                cmd => cmd.Parameters.AddWithValue("t", job.TenantId), ct);
            return new DsrScope("tenant", null, all);
        }

        if (job.Kind == "dsr_device")
        {
            var deviceId = root.GetProperty("device_id").GetGuid();
            var subjects = await LoadSubjectsAsync(
                """
                SELECT id, device_id, windows_sid, windows_username, display_name
                FROM device_users WHERE tenant_id = @t AND device_id = @d
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
            SELECT id, device_id, windows_sid, windows_username, display_name
            FROM device_users WHERE tenant_id = @t AND id = @id
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
                reader.IsDBNull(4) ? null : reader.GetString(4)));
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
        int eventos, int intervalos, int agregados, bool truncated, CancellationToken ct)
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

        var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }, ct);
    }

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
        Guid DeviceUserId, Guid DeviceId, string WindowsSid, string WindowsUsername, string? DisplayName);
}
