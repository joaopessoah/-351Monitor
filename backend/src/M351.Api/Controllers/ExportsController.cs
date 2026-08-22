using System.Text.Json;
using Dapper;
using M351.Api.Agent;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Domain;
using M351.Domain.Entities;
using M351.Api.Services;
using M351.Infrastructure;
using M351.Infrastructure.Exports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// /api/v1/exports (F3.5, Seções 7.4/8.6): CSV assíncrono — o POST só enfileira (202); o
/// ExportService (worker) gera o arquivo; o download é servido daqui (volume compartilhado
/// API+worker em staging — infra/docker-compose.staging.yml).
///
/// Regras e decisões documentadas:
///  - kinds criados AQUI: usage_csv | jornada_csv | fora_horario_csv. Pacotes DSR/offboarding
///    (dsr_subject/dsr_device/tenant_full) NÃO nascem deste POST genérico (→ 400): são criados
///    pelos endpoints /privacy/* (F4.5). A LISTAGEM e o DOWNLOAD os servem aqui — o download de
///    pacote DSR é application/zip (.zip) e expira em 72h, não os 7d do CSV de relatório;
///  - GATE DE PAPEL DOS PACOTES DSR (LGPD, Seção 7.4): o ZIP carrega window_title/eventos brutos
///    do titular, então o ARTEFATO herda o mesmo papel da CRIAÇÃO em /privacy/*: dsr_subject /
///    dsr_device exigem AdminPlus e tenant_full exige OwnerOnly. Papel insuficiente NÃO lista o
///    job (GET /exports filtra os kinds DSR fora do alcance) nem o baixa (download/get → 404, não
///    403 — Princípio 4: não confirmar a existência). Um Viewer jamais alcança um pacote DSR;
///  - params validados com os MESMOS validadores dos endpoints de leitura (régua de datas
///    do dashboard, group_by do usage, gate 404 de device_ids cross-tenant); group_by em
///    jornada_csv/fora_horario_csv → 400 (não se aplica, decisão p/ silêncio da spec);
///  - fora_horario_csv exige a organização com horário de trabalho configurado e coleta
///    contínua: sem janela declarada, ou com collection_window = BUSINESS_HOURS, o pedido vira
///    409 explicativo (mesma régua dos estados vazios do GET /reports/fora-do-horario, um CSV
///    de zeros seria lido como "ninguém trabalha fora do horário", que é falso);
///  - GET /exports lista os últimos 30 dias DO TENANT, desc, máx. 100 — trilha "quem gerou,
///    quando, com que filtros" (spec linha 949): exports de relatório (CSV) para todos os papéis;
///    pacotes DSR só para o papel que poderia criá-los;
///  - job de outro tenant → 404 (nunca 403 — Princípio 4);
///  - download: job não-done → 409; expirado (expires_at vencido OU arquivo removido pelo
///    sweep) → 410; senão stream text/csv; charset=utf-8 com Content-Disposition attachment;
///  - auditoria: export_csv no POST com detail {kind, params}, na MESMA transação do INSERT.
/// </summary>
[Route("api/v1/exports")]
[Authorize] // Viewer+
public class ExportsController(
    NpgsqlDataSource dataSource,
    IOptions<ExportOptions> exportOptions,
    TimeProvider clock) : ApiControllerBase
{
    private static readonly string[] ValidKinds = ["usage_csv", "jornada_csv", "fora_horario_csv"];

    private const string ItemSql = """
        SELECT j.id, j.kind, j.status, j.created_at, j.params::text AS params_json,
               j.row_count, j.truncated, j.expires_at, j.file_path,
               COALESCE(u.display_name, 'Usuário removido') AS requested_by_name
        FROM export_jobs j
        LEFT JOIN users u ON u.id = j.requested_by
        """;

    // ------------------------------------------------------------ POST /exports
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ExportCreateRequest? body, CancellationToken ct)
    {
        if (body?.Kind is null || !ValidKinds.Contains(body.Kind))
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Parâmetro kind deve ser usage_csv, jornada_csv ou fora_horario_csv. "
                + "Pacotes DSR (dsr_subject/dsr_device/tenant_full) são criados pelos endpoints /privacy/*.");

        if (body.Params is null)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro params é obrigatório.");

        // MESMA régua de datas dos endpoints de leitura (fuso do tenant, máx. 92 dias)
        var invalid = ValidateRange(body.Params.From, body.Params.To, out _, out _);
        if (invalid is not null) return invalid;

        // group_by: obrigatório e válido para usage_csv (validador do GET /reports/usage);
        // não se aplica a jornada_csv → 400 para não enfileirar um job com param morto
        if (body.Kind == "usage_csv" &&
            (body.Params.GroupBy is null || !ReportsController.ValidGroupBys.Contains(body.Params.GroupBy)))
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Parâmetro group_by é obrigatório para usage_csv: app, category, device ou device_user.");
        if (body.Kind is "jornada_csv" or "fora_horario_csv" && body.Params.GroupBy is not null)
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Parâmetro group_by não se aplica a {body.Kind}.");

        Guid[]? deviceIds = null;
        if (body.Params.DeviceIds is { Length: > 0 })
        {
            var parsed = new List<Guid>(body.Params.DeviceIds.Length);
            foreach (var raw in body.Params.DeviceIds)
            {
                if (!Guid.TryParse(raw, out var deviceId))
                    return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro device_ids deve conter apenas UUIDs.");
                parsed.Add(deviceId);
            }
            deviceIds = parsed.Distinct().ToArray();
        }

        var tenantId = Auth.CurrentUser.TenantId(User);
        var userId = Auth.CurrentUser.UserId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // mesmo gate dos relatórios: id inexistente OU de outro tenant → 404 (nunca 403)
        if (deviceIds is { Length: > 0 })
        {
            var found = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM devices WHERE tenant_id = @TenantId AND id = ANY(@DeviceIds)",
                new { TenantId = tenantId, DeviceIds = deviceIds }, cancellationToken: ct));
            if (found != deviceIds.Length) return NotFoundProblem();
        }

        // fora_horario_csv depende da configuração da ORGANIZAÇÃO, não do pedido: sem janela
        // declarada não existe "fora dela", e com a coleta restrita ao horário de trabalho não
        // há o que somar fora dele (por design do agente). Os dois casos viram 409 explicativo,
        // nunca um CSV cheio de zeros, que o gestor leria como "ninguém trabalha fora do
        // horário". É a MESMA régua dos estados vazios do GET /reports/fora-do-horario.
        if (body.Kind == "fora_horario_csv")
        {
            var config = await connection.QuerySingleAsync<ForaDoHorarioConfigRow>(new CommandDefinition(
                """
                SELECT o.business_hours::text AS business_hours,
                       c.collection_window::text AS collection_window
                FROM organizations o
                LEFT JOIN tenant_agent_configs c ON c.tenant_id = o.id
                WHERE o.id = @TenantId
                """,
                new { TenantId = tenantId }, cancellationToken: ct));

            if (!BusinessHoursWindow.TryParse(config.BusinessHours, out _))
                return ProblemResponse(StatusCodes.Status409Conflict,
                    "Horário de trabalho não configurado.",
                    detail: "Defina o horário de trabalho da organização em Configurações para apurar atividade fora dele.");

            if (AgentConfigService.ParseCollectionWindow(config.CollectionWindow).Mode == "BUSINESS_HOURS")
                return ProblemResponse(StatusCodes.Status409Conflict,
                    "Coleta restrita ao horário de trabalho.",
                    detail: "A organização escolheu coletar apenas dentro do horário de trabalho, então não há atividade registrada fora dele.");
        }

        // params NORMALIZADOS (snake_case, sem nulos) — é o que o worker lê e o que a
        // listagem devolve; o mesmo objeto vai para o detail da auditoria
        var normalizedParams = new Dictionary<string, object?>
        {
            ["from"] = body.Params.From,
            ["to"] = body.Params.To,
        };
        if (deviceIds is { Length: > 0 }) normalizedParams["device_ids"] = deviceIds;
        if (body.Kind == "usage_csv") normalizedParams["group_by"] = body.Params.GroupBy;
        var paramsJson = JsonSerializer.Serialize(normalizedParams);

        var jobId = Uuid7.NewUuid7();

        // INSERT + trilha export_csv na MESMA transação (jamais job sem trilha)
        DateTimeOffset createdAt;
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            createdAt = await connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition(
                """
                INSERT INTO export_jobs (id, tenant_id, requested_by, kind, params, status)
                VALUES (@Id, @TenantId, @RequestedBy, @Kind, @Params::jsonb, 'queued')
                RETURNING created_at
                """,
                new { Id = jobId, TenantId = tenantId, RequestedBy = userId, Kind = body.Kind, Params = paramsJson },
                transaction: tx, cancellationToken: ct));

            await AuditWriter.AddInTransactionAsync(
                connection, tx, tenantId, AuditActions.ExportCsv,
                actorUserId: userId,
                actorIp: HttpContext.Connection.RemoteIpAddress,
                targetType: "export_job", targetId: jobId,
                detailJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["kind"] = body.Kind,
                    ["params"] = normalizedParams,
                }),
                ct: ct);

            await tx.CommitAsync(ct);
        }

        return Accepted(new ExportCreateResponse(jobId, body.Kind, "queued", createdAt));
    }

    // ------------------------------------------------------------ GET /exports
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);
        var role = Auth.CurrentUser.Role(User);

        // Pacotes DSR (window_title/eventos brutos do titular) só aparecem para o papel que
        // poderia criá-los: dsr_subject/dsr_device → Admin+; tenant_full → Owner. Demais kinds
        // (usage_csv/jornada_csv) para todos. Filtra no SQL para não vazar nem o id/kind do job.
        var allowedDsrKinds = AllowedDsrKindsFor(role);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<JobRow>(new CommandDefinition(
            $"""
            {ItemSql}
            WHERE j.tenant_id = @TenantId AND j.created_at >= now() - interval '30 days'
              AND (NOT (j.kind = ANY(@DsrKinds)) OR j.kind = ANY(@AllowedDsrKinds))
            ORDER BY j.created_at DESC
            LIMIT 100
            """,
            new { TenantId = tenantId, DsrKinds = DsrKinds, AllowedDsrKinds = allowedDsrKinds },
            cancellationToken: ct))).ToList();

        var now = clock.GetUtcNow();
        return Ok(new ExportsResponse(rows.Select(r => ToItem(r, now)).ToList()));
    }

    // ------------------------------------------------------------ GET /exports/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var row = await FindJobAsync(id, ct);
        // Papel insuficiente para um pacote DSR é indistinguível de inexistente (404 — Princípio 4)
        if (row is null || !CanAccessKind(row.Kind)) return NotFoundProblem();
        return Ok(ToItem(row, clock.GetUtcNow()));
    }

    // ------------------------------------------------------------ GET /exports/{id}/download
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var row = await FindJobAsync(id, ct);
        // Gate de papel ANTES de qualquer 409/410: um pacote DSR fora do alcance do papel é
        // indistinguível de inexistente (404 — Princípio 4: não confirma sequer a existência).
        if (row is null || !CanAccessKind(row.Kind)) return NotFoundProblem();

        if (row.Status != "done")
            return ProblemResponse(StatusCodes.Status409Conflict,
                "Exportação ainda não concluída.",
                detail: $"Status atual: {row.Status}.");

        var absolutePath = row.FilePath is null ? null : AbsolutePath(row.FilePath);
        if (row.ExpiresAt is null || row.ExpiresAt < clock.GetUtcNow()
            || absolutePath is null || !System.IO.File.Exists(absolutePath))
            return ProblemResponse(StatusCodes.Status410Gone,
                "Exportação expirada.",
                detail: IsDsrPackage(row.Kind)
                    ? "O pacote ficou disponível por 72 horas. Solicite uma nova exportação."
                    : "O arquivo ficou disponível por 7 dias. Solicite uma nova exportação.");

        // pacote DSR/offboarding = application/zip (.zip); CSV de relatório = text/csv
        var (contentType, fileName) = IsDsrPackage(row.Kind)
            ? ("application/zip", DownloadFileName(row))
            : ("text/csv; charset=utf-8", DownloadFileName(row));
        return PhysicalFile(absolutePath, contentType, fileName);
    }

    // ------------------------------------------------------------ helpers
    private async Task<JobRow?> FindJobAsync(Guid id, CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        // tenant_id no WHERE: job de outro tenant é indistinguível de inexistente (404)
        return await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            $"{ItemSql}\nWHERE j.tenant_id = @TenantId AND j.id = @Id",
            new { TenantId = tenantId, Id = id }, cancellationToken: ct));
    }

    private ExportJobItemResponse ToItem(JobRow row, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(row.ParamsJson);
        return new ExportJobItemResponse(
            row.Id, row.Kind, row.Status, row.CreatedAt, row.RequestedByName,
            doc.RootElement.Clone(), row.RowCount, row.Truncated, row.ExpiresAt, IsExpired(row, now));
    }

    /// <summary>expired SÓ para job done: prazo vencido OU arquivo já removido pelo sweep.</summary>
    private bool IsExpired(JobRow row, DateTimeOffset now) =>
        row.Status == "done"
        && (row.ExpiresAt is null || row.ExpiresAt < now
            || row.FilePath is null || !System.IO.File.Exists(AbsolutePath(row.FilePath)));

    /// <summary>file_path é RELATIVO ao diretório de exports (decisão do ExportService).</summary>
    private string AbsolutePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(exportOptions.Value.Directory, relativePath));

    /// <summary>Pacote DSR/offboarding (dsr_subject/dsr_device/tenant_full): ZIP de 72h, não CSV de 7d.</summary>
    private static bool IsDsrPackage(string kind) =>
        kind is "dsr_subject" or "dsr_device" or "tenant_full";

    /// <summary>Todos os kinds de pacote DSR — espelha IsDsrPackage para uso em parâmetro SQL.</summary>
    private static readonly string[] DsrKinds = ["dsr_subject", "dsr_device", "tenant_full"];

    /// <summary>
    /// Pacotes DSR que o papel pode ALCANÇAR (listar/baixar), espelhando o gate de CRIAÇÃO em
    /// /privacy/*: dsr_subject/dsr_device = AdminPlus (admin+owner); tenant_full = OwnerOnly.
    /// Viewer não alcança nenhum. Kinds não-DSR (CSV) ficam fora desta régua (todos os papéis).
    /// </summary>
    private static string[] AllowedDsrKindsFor(UserRole role) => role switch
    {
        UserRole.Owner => ["dsr_subject", "dsr_device", "tenant_full"],
        UserRole.Admin => ["dsr_subject", "dsr_device"],
        _ => [],
    };

    /// <summary>
    /// O papel atual pode acessar o ARTEFATO deste kind? CSV (não-DSR) sempre; pacote DSR só se
    /// o papel poderia tê-lo criado (mesmo gate de /privacy/*). Usado no get/download (404 caso não).
    /// </summary>
    private bool CanAccessKind(string kind) =>
        !IsDsrPackage(kind) || AllowedDsrKindsFor(Auth.CurrentUser.Role(User)).Contains(kind);

    /// <summary>
    /// CSV: jornada_2026-06-01_2026-06-10.csv (uso_ para usage_csv). Pacote DSR: dsr_subject_{id}.zip
    /// (o id do job no nome basta — o conteúdo identifica o titular no manifest.json).
    /// </summary>
    private static string DownloadFileName(JobRow row)
    {
        if (IsDsrPackage(row.Kind))
            return $"{row.Kind}_{row.Id}.zip";

        using var doc = JsonDocument.Parse(row.ParamsJson);
        var from = doc.RootElement.TryGetProperty("from", out var f) ? f.GetString() : null;
        var to = doc.RootElement.TryGetProperty("to", out var t) ? t.GetString() : null;
        var prefix = row.Kind switch
        {
            "jornada_csv" => "jornada",
            "fora_horario_csv" => "fora-do-horario",
            _ => "uso",
        };
        return $"{prefix}_{from}_{to}.csv";
    }

    /// <summary>Configuração da org que decide se fora_horario_csv pode ser gerado.</summary>
    private sealed record ForaDoHorarioConfigRow(string? BusinessHours, string? CollectionWindow);

    private sealed record JobRow(
        Guid Id,
        string Kind,
        string Status,
        DateTimeOffset CreatedAt,
        string ParamsJson,
        int? RowCount,
        bool Truncated,
        DateTimeOffset? ExpiresAt,
        string? FilePath,
        string RequestedByName);
}
