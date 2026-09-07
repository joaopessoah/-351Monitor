using System.Text.Json;
using Dapper;
using M351.Api.Auditing;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// /api/v1/people/{sid}/notes (F6, decisão 6 do spec de 07/09/2026): ANOTAÇÃO DE PERÍODO e
/// CONTESTAÇÃO DE CLASSIFICAÇÃO, com revisão do gestor.
///
/// POR QUE ISTO EXISTE: a medição sabe QUANTO tempo a máquina ficou ativa e o que estava na
/// tela — nunca POR QUE. Reunião presencial, treinamento, visita a cliente, dia de máquina em
/// manutenção: tudo aparece como ausência de atividade, e o número sozinho mente por omissão.
/// A anotação é o contexto que gente escreve sobre um período. A contestação é a discordância
/// registrada sobre COMO um aplicativo foi classificado — o direito de dizer "este uso não é o
/// que o rótulo diz" dentro do produto, em vez de fora dele.
///
/// CONTESTAÇÃO ACEITA NÃO ALTERA AGREGADO. Nada aqui escreve em daily_device_summaries,
/// daily_app_usage, hourly_activity ou activity_intervals — este controller só lê e escreve
/// person_notes (e a trilha). Aceitar é um ATO DE REGISTRO e insumo da curadoria: o caminho
/// para o número mudar é o gestor remapear a categoria em Configurações › Classificação e
/// reagregar. Se aceitar mexesse no agregado, o histórico deixaria de ser reprodutível a
/// partir dos eventos e o dado perderia o valor probatório que a Portaria 671 exige. A
/// resposta do PATCH devolve essa frase em <c>effect</c>, para que a tela DIGA isso ao gestor
/// no momento do clique em vez de deixá-lo esperando um número que não vai mudar.
///
/// PAPÉIS: criar é Viewer+ (quem enxerga a pessoa pode dar contexto sobre ela); revisar é
/// Admin+ (o gestor). Viewer que tenta revisar toma 403 — não 404: o recurso existe e é do
/// tenant dele, o que falta é poder.
///
/// IDENTIDADE (o mesmo aperto de /people): a pessoa é o windows_sid do tenant, resolvido pela
/// mesclagem de people. O segmento {sid} aceita DUAS formas de propósito — o próprio
/// windows_sid OU o uuid de um device_users.id — porque a página do colaborador do portal
/// (/pessoas/:id) navega por device_user_id e GET /device-users/{id} não devolve o SID
/// (conferido em DeviceUserContracts.cs). Em vez de adivinhar o SID por nome no cliente (o que
/// arriscaria anotar a PESSOA ERRADA), a resolução acontece AQUI, no servidor, pelo par
/// (tenant, id) que é chave real. A resposta sempre devolve o windows_sid canônico resolvido.
///
/// person_notes não é entidade do EF (como daily_*, people e management_alerts): Dapper puro e
/// tenant_id MANUSCRITO em todo WHERE — não há filtro global. Recurso inexistente ou de outro
/// tenant responde 404, jamais 403 (Princípio 4).
/// </summary>
[Route("api/v1/people/{sid}/notes")]
[Authorize] // Viewer+ lê e cria; a revisão exige AdminPlus (atributo no PATCH)
public class PersonNotesController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    /// <summary>Lane sintética da máquina: não é pessoa e não recebe anotação.</summary>
    private static readonly Guid MachineLane = Guid.Empty;

    /// <summary>Teto do texto da anotação e da resposta do gestor.</summary>
    public const int MaxBodyLength = 2000;

    /// <summary>Janela máxima que uma anotação pode cobrir: a mesma dos relatórios (92 dias).</summary>
    public const int MaxNoteSpanDays = MaxReportRangeDays;

    /// <summary>
    /// A frase única sobre o efeito de ACEITAR uma contestação. Vive aqui, no servidor, para que
    /// portal, e-mail e export digam exatamente a mesma coisa (e para que mudá-la seja uma
    /// mudança de contrato, visível em teste).
    /// </summary>
    public const string EffectAceita =
        "Contestação aceita e registrada. O registro NÃO altera os números já agregados: ele é " +
        "insumo da curadoria da classificação. Para o tempo passar a ser contado de outra forma, " +
        "ajuste a categoria do aplicativo em Configurações › Classificação e recalcule o histórico.";

    public const string EffectRecusada =
        "Contestação recusada e registrada, com a sua resposta visível para quem a abriu. Nada " +
        "nos números agregados muda — nem antes, nem depois desta decisão.";

    public const string EffectAnotacaoAceita =
        "Anotação aceita e registrada: ela passa a acompanhar o período na página da pessoa como " +
        "contexto reconhecido. Os números agregados não mudam.";

    public const string EffectAnotacaoRecusada =
        "Anotação recusada e registrada, com a sua resposta visível para quem a abriu. Os números " +
        "agregados não mudam.";

    // -------------------------------------------------------------------------------- GET
    /// <summary>
    /// GET /api/v1/people/{sid}/notes?from&amp;to (Viewer+): as anotações e contestações da
    /// pessoa cujo período CRUZA o recorte (não só as que começam nele — uma anotação de
    /// segunda a sexta tem de aparecer quando a tela mostra a quarta).
    ///
    /// Mesma régua de período das outras leituras (yyyy-MM-dd no fuso do tenant, from &lt;= to,
    /// janela de 92 dias) e a MESMA auditoria: view_report, porque a lista traz texto escrito
    /// sobre uma pessoa identificada — é leitura de dado pessoal e deixa rastro (decisão 2).
    /// </summary>
    [HttpGet]
    [AuditRead] // leitura de dado pessoal: view_report via AuditReadFilter, só em 2xx
    public async Task<IActionResult> List(
        string sid,
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromServices] AuditReadContext readAudit = null!,
        CancellationToken ct = default)
    {
        var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
        if (invalid is not null) return invalid;

        var tenantId = CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var tz = await TenantTimeZoneAsync(connection, tenantId, ct);
        if (tz is null) return NotFoundProblem();

        var resolved = await ResolveSidAsync(connection, tenantId, sid, ct);
        if (resolved is null) return NotFoundProblem();

        // janela [from 00:00 tenant, (to+1) 00:00 tenant) em UTC — started_at/ended_at são
        // timestamptz. O cruzamento é o clássico (início < fim da janela AND fim >= início dela).
        var windowStart = LocalMidnightUtc(fromDay, tz);
        var windowEnd = LocalMidnightUtc(toDay.AddDays(1), tz);

        var rows = (await connection.QueryAsync<NoteRow>(new CommandDefinition(
            """
            SELECT n.id,
                   n.windows_sid,
                   n.kind,
                   n.started_at,
                   n.ended_at,
                   n.app_id,
                   a.process_name AS app_process_name,
                   a.display_name AS app_display_name,
                   n.body,
                   n.status,
                   n.created_by_user_id,
                   cu.display_name AS created_by_name,
                   n.created_at,
                   n.reviewed_by_user_id,
                   ru.display_name AS reviewed_by_name,
                   n.reviewed_at,
                   n.review_note
            FROM person_notes n
            LEFT JOIN app_catalog a ON a.id = n.app_id
            -- os autores são usuários DO MESMO tenant: o join carrega tenant_id de propósito,
            -- para que um id de outro tenant nunca resolva nome aqui
            LEFT JOIN users cu ON cu.tenant_id = n.tenant_id AND cu.id = n.created_by_user_id
            LEFT JOIN users ru ON ru.tenant_id = n.tenant_id AND ru.id = n.reviewed_by_user_id
            WHERE n.tenant_id = @TenantId
              AND n.windows_sid = @Sid
              AND n.started_at < @WindowEnd
              AND n.ended_at >= @WindowStart
            ORDER BY n.started_at DESC, n.created_at DESC
            """,
            new
            {
                TenantId = tenantId,
                Sid = resolved,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
            },
            cancellationToken: ct))).ToList();

        // O rastro leva o SID e o recorte, NUNCA o texto das anotações: audit_log é append-only
        // com 24 meses de retenção e o conteúdo já vive em person_notes, de onde pode ser
        // removido a pedido do titular.
        readAudit.Record(tenantId, AuditActions.ViewReport,
            CurrentUser.UserId(User),
            targetType: "person_notes", targetId: null,
            detailJson: JsonSerializer.Serialize(new
            {
                windows_sid = resolved,
                from = fromDay.ToString("yyyy-MM-dd"),
                to = toDay.ToString("yyyy-MM-dd"),
                returned = rows.Count,
            }));

        return Ok(new PersonNotesResponse(rows.Select(ToResponse).ToList()));
    }

    // ------------------------------------------------------------------------------- POST
    /// <summary>
    /// POST /api/v1/people/{sid}/notes (Viewer+): cria a anotação de período ou a contestação
    /// de classificação. Nasce SEMPRE com status "aberta" — o corpo não escolhe status nem quem
    /// revisou. Mutação e trilha na MESMA transação.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        string sid, [FromBody] PersonNoteCreateRequest? request, CancellationToken ct)
    {
        var kind = request?.Kind?.Trim().ToLowerInvariant();
        if (!PersonNoteKinds.IsValid(kind))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "kind inválido: use \"anotacao\" (contexto de um período) ou \"contestacao\" " +
                "(discordância sobre a classificação de um aplicativo).");
        }

        var body = request?.Body?.Trim();
        if (string.IsNullOrEmpty(body))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "body é obrigatório: descreva o período (ex.: \"reunião presencial\", " +
                "\"treinamento\") ou o motivo da contestação.");
        }

        if (body.Length > MaxBodyLength)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"body inválido (máximo {MaxBodyLength} caracteres).");
        }

        if (request?.StartedAt is null || request.EndedAt is null)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "started_at e ended_at são obrigatórios (data e hora com fuso, ISO 8601).");
        }

        var startedAt = request.StartedAt.Value.ToUniversalTime();
        var endedAt = request.EndedAt.Value.ToUniversalTime();
        if (endedAt < startedAt)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Período inválido: ended_at deve ser igual ou posterior a started_at.");
        }

        if ((endedAt - startedAt).TotalDays > MaxNoteSpanDays)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Período inválido: uma anotação cobre no máximo {MaxNoteSpanDays} dias.");
        }

        // app_id só faz sentido na contestação: numa anotação de período ele não significa
        // nada, e gravar um campo mudo é pior do que recusar a chamada.
        if (request.AppId is not null && kind != PersonNoteKinds.Contestacao)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "app_id só é aceito em kind \"contestacao\": é o aplicativo cuja classificação " +
                "se contesta.");
        }

        var tenantId = CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var resolved = await ResolveSidAsync(connection, tenantId, sid, ct);
        if (resolved is null) return NotFoundProblem();

        if (request.AppId is not null)
        {
            // app_catalog é GLOBAL (sem tenant_id): a existência é a única checagem possível, e
            // um id inexistente é erro do cliente, não recurso escondido de outro tenant.
            var appExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM app_catalog WHERE id = @AppId)",
                new { AppId = request.AppId }, cancellationToken: ct));
            if (!appExists)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "app_id inválido: aplicativo inexistente no catálogo.");
            }
        }

        var noteId = Uuid7.NewUuid7();
        var actor = CurrentUser.UserId(User);

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO person_notes (
                    id, tenant_id, windows_sid, kind, started_at, ended_at, app_id, body,
                    status, created_by_user_id, created_at)
                VALUES (
                    @Id, @TenantId, @Sid, @Kind, @StartedAt, @EndedAt, @AppId, @Body,
                    'aberta', @Actor, now())
                """,
                new
                {
                    Id = noteId,
                    TenantId = tenantId,
                    Sid = resolved,
                    Kind = kind,
                    StartedAt = startedAt,
                    EndedAt = endedAt,
                    AppId = request.AppId,
                    Body = body,
                    Actor = actor,
                },
                transaction: tx, cancellationToken: ct));

            // detail SEM o corpo do texto: ver o comentário de AuditActions.CreatePersonNote.
            await AuditWriter.AddInTransactionAsync(connection, tx, tenantId,
                AuditActions.CreatePersonNote,
                actorUserId: actor,
                actorIp: HttpContext.Connection.RemoteIpAddress,
                targetType: "person_note", targetId: noteId,
                detailJson: JsonSerializer.Serialize(new
                {
                    note_id = noteId,
                    windows_sid = resolved,
                    kind,
                    started_at = startedAt,
                    ended_at = endedAt,
                    app_id = request.AppId,
                }), ct: ct);

            await tx.CommitAsync(ct);
        }

        var created = await LoadAsync(connection, tenantId, noteId, ct);
        if (created is null) return NotFoundProblem();
        return Created($"/api/v1/people/{Uri.EscapeDataString(resolved)}/notes/{noteId}", created);
    }

    // ------------------------------------------------------------------------------ PATCH
    /// <summary>
    /// PATCH /api/v1/people/{sid}/notes/{id} (Admin+): a REVISÃO DO GESTOR — aceitar ou recusar,
    /// com uma resposta ao colaborador.
    ///
    /// Só de "aberta" para "aceita"/"recusada": revisar duas vezes responde 409, porque a
    /// revisão é um fato datado e assinado, e sobrescrevê-la apagaria quem decidiu o quê.
    /// review_note é OBRIGATÓRIA na recusa — recusar sem dizer por quê é o oposto do que esta
    /// feature existe para fazer.
    ///
    /// Aceitar NÃO ALTERA NENHUM AGREGADO (ver o comentário da classe). A resposta carrega
    /// <c>effect</c> dizendo isso em português.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Review(
        string sid, Guid id, [FromBody] PersonNoteReviewRequest? request, CancellationToken ct)
    {
        var status = request?.Status?.Trim().ToLowerInvariant();
        if (!PersonNoteStatuses.IsReviewed(status))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "status inválido: a revisão só aceita \"aceita\" ou \"recusada\".");
        }

        var reviewNote = request?.ReviewNote?.Trim();
        if (reviewNote is { Length: 0 }) reviewNote = null;

        if (reviewNote is { Length: > MaxBodyLength })
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"review_note inválida (máximo {MaxBodyLength} caracteres).");
        }

        if (status == PersonNoteStatuses.Recusada && reviewNote is null)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "review_note é obrigatória na recusa: quem abriu precisa saber o motivo.");
        }

        var tenantId = CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var resolved = await ResolveSidAsync(connection, tenantId, sid, ct);
        if (resolved is null) return NotFoundProblem();

        // tenant_id MANUSCRITO e o SID no WHERE: nota de outro tenant — ou desta nota com a
        // pessoa errada na rota — responde 404, nunca 403.
        var current = await connection.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition(
            """
            SELECT status, kind FROM person_notes
            WHERE tenant_id = @TenantId AND id = @Id AND windows_sid = @Sid
            """,
            new { TenantId = tenantId, Id = id, Sid = resolved }, cancellationToken: ct));
        if (current is null) return NotFoundProblem();

        if (current.Status != PersonNoteStatuses.Aberta)
        {
            return ProblemResponse(StatusCodes.Status409Conflict,
                $"Esta anotação já foi revisada (status \"{current.Status}\"). A revisão é um " +
                "registro datado e não é sobrescrita.");
        }

        var actor = CurrentUser.UserId(User);

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            // O WHERE repete status = 'aberta': duas revisões simultâneas não se sobrescrevem,
            // a segunda não afeta linha nenhuma.
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE person_notes SET
                    status = @Status,
                    review_note = @ReviewNote,
                    reviewed_by_user_id = @Actor,
                    reviewed_at = now()
                WHERE tenant_id = @TenantId AND id = @Id AND status = 'aberta'
                """,
                new
                {
                    TenantId = tenantId,
                    Id = id,
                    Status = status,
                    ReviewNote = reviewNote,
                    Actor = actor,
                },
                transaction: tx, cancellationToken: ct));

            if (affected == 0)
            {
                await tx.RollbackAsync(ct);
                return ProblemResponse(StatusCodes.Status409Conflict,
                    "Esta anotação foi revisada por outra pessoa enquanto você decidia.");
            }

            await AuditWriter.AddInTransactionAsync(connection, tx, tenantId,
                AuditActions.ReviewPersonNote,
                actorUserId: actor,
                actorIp: HttpContext.Connection.RemoteIpAddress,
                targetType: "person_note", targetId: id,
                detailJson: JsonSerializer.Serialize(new
                {
                    note_id = id,
                    windows_sid = resolved,
                    kind = current.Kind,
                    status_from = PersonNoteStatuses.Aberta,
                    status_to = status,
                    // registra QUE houve resposta, sem copiar o texto dela para a trilha
                    review_note_present = reviewNote is not null,
                    // o fato que o produto promete: a revisão não reescreve agregado
                    aggregates_changed = false,
                }), ct: ct);

            await tx.CommitAsync(ct);
        }

        var note = await LoadAsync(connection, tenantId, id, ct);
        if (note is null) return NotFoundProblem();

        var effect = (current.Kind, status) switch
        {
            (PersonNoteKinds.Contestacao, PersonNoteStatuses.Aceita) => EffectAceita,
            (PersonNoteKinds.Contestacao, _) => EffectRecusada,
            (_, PersonNoteStatuses.Aceita) => EffectAnotacaoAceita,
            _ => EffectAnotacaoRecusada,
        };

        return Ok(new PersonNoteReviewResponse(note, effect));
    }

    // ------------------------------------------------------------------------- resolução
    /// <summary>
    /// Resolve o segmento {sid} da rota no windows_sid CANÔNICO da pessoa dentro do tenant, ou
    /// null (→ 404) quando não existe pessoa alguma por trás dele.
    ///
    /// Duas formas aceitas (ver o comentário da classe): o próprio windows_sid, ou o uuid de um
    /// device_users.id — o identificador com que a página do colaborador do portal navega,
    /// porque o contrato de GET /device-users/{id} não expõe o SID. A mesclagem de people é
    /// aplicada nas duas: anotar a conta local de alguém já mesclado grava na pessoa
    /// dominante, que é o que a tela mostra.
    /// </summary>
    private static async Task<string?> ResolveSidAsync(
        NpgsqlConnection connection, Guid tenantId, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();

        if (Guid.TryParse(key, out var deviceUserId))
        {
            if (deviceUserId == MachineLane) return null; // lane-máquina não é pessoa
            return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                """
                SELECT COALESCE(p.merged_into_sid, du.windows_sid)
                FROM device_users du
                LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
                WHERE du.tenant_id = @TenantId AND du.id = @Id
                """,
                new { TenantId = tenantId, Id = deviceUserId }, cancellationToken: ct));
        }

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT COALESCE(p.merged_into_sid, du.windows_sid)
            FROM device_users du
            LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
            WHERE du.tenant_id = @TenantId AND du.windows_sid = @Sid
            LIMIT 1
            """,
            new { TenantId = tenantId, Sid = key }, cancellationToken: ct));
    }

    private static async Task<TimeZoneInfo?> TenantTimeZoneAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        var timezone = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        return timezone is null ? null : TimeZoneInfo.FindSystemTimeZoneById(timezone);
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Uma nota já com nomes e aplicativo resolvidos — o mesmo shape do GET.</summary>
    private static async Task<PersonNoteResponse?> LoadAsync(
        NpgsqlConnection connection, Guid tenantId, Guid id, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<NoteRow>(new CommandDefinition(
            """
            SELECT n.id, n.windows_sid, n.kind, n.started_at, n.ended_at, n.app_id,
                   a.process_name AS app_process_name,
                   a.display_name AS app_display_name,
                   n.body, n.status,
                   n.created_by_user_id, cu.display_name AS created_by_name, n.created_at,
                   n.reviewed_by_user_id, ru.display_name AS reviewed_by_name,
                   n.reviewed_at, n.review_note
            FROM person_notes n
            LEFT JOIN app_catalog a ON a.id = n.app_id
            LEFT JOIN users cu ON cu.tenant_id = n.tenant_id AND cu.id = n.created_by_user_id
            LEFT JOIN users ru ON ru.tenant_id = n.tenant_id AND ru.id = n.reviewed_by_user_id
            WHERE n.tenant_id = @TenantId AND n.id = @Id
            """,
            new { TenantId = tenantId, Id = id }, cancellationToken: ct));

        return row is null ? null : ToResponse(row);
    }

    private static PersonNoteResponse ToResponse(NoteRow r) => new(
        r.Id, r.WindowsSid, r.Kind, r.StartedAt, r.EndedAt,
        r.AppId, r.AppProcessName, r.AppDisplayName,
        r.Body, r.Status,
        r.CreatedByUserId, r.CreatedByName, r.CreatedAt,
        r.ReviewedByUserId, r.ReviewedByName, r.ReviewedAt, r.ReviewNote);

    // ------------------------------------------------------------ linhas cruas do SQL
    private sealed record NoteRow(
        Guid Id,
        string WindowsSid,
        string Kind,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        Guid? AppId,
        string? AppProcessName,
        string? AppDisplayName,
        string Body,
        string Status,
        Guid CreatedByUserId,
        string? CreatedByName,
        DateTimeOffset CreatedAt,
        Guid? ReviewedByUserId,
        string? ReviewedByName,
        DateTimeOffset? ReviewedAt,
        string? ReviewNote);

    private sealed record StatusRow(string Status, string Kind);
}
