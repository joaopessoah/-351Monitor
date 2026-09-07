using System.Globalization;
using System.Text.Json;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Capacity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// /api/v1/teams (F7, spec seção 2.3) — EQUIPES DE VERDADE: entidade por organização, vinculada
/// a PESSOA (windows_sid) e não a dispositivo, com jornada declarada própria.
///
/// AS DUAS NOÇÕES DE EQUIPE COEXISTEM, e isso é decisão de produto, não dívida:
///  - <c>devices.tags</c> continua sendo a etiqueta livre por DISPOSITIVO e continua sendo o
///    filtro <c>?tag</c> de todos os dashboards, relatórios, timeline e exports. NADA foi
///    removido e nenhum contrato existente mudou;
///  - <c>teams</c> é a entidade nova: nome canônico, jornada e regra de classificação própria
///    (F5). Ela DECLARA em <c>tag</c> qual etiqueta legada representa, o que é o atalho de
///    migração — quem só tem etiqueta continua funcionando, e quem já vinculou pessoas ganha a
///    identidade estável que a etiqueta nunca teve (a pessoa troca de notebook sem trocar de
///    equipe);
///  - onde as duas respondem, a EQUIPE DA PESSOA VENCE. A ordem está escrita e implementada em
///    um lugar só, o DailyAggregationService (CTE lane_team).
///
/// Papéis: Viewer+ lê (é informação de organização, não dado pessoal de titular — a listagem
/// não expõe nome de pessoa, só contagem); Admin+ escreve.
///
/// REAGREGAÇÃO: criar/excluir equipe, mudar a <c>tag</c> e mudar a COMPOSIÇÃO enfileiram
/// reagregação dos últimos 30 dias na MESMA transação da mutação — as três mudam a equipe
/// resolvida de alguma lane e portanto os baldes de classificação do histórico recente.
/// Renomear e mudar a jornada NÃO reagregam: nome é rótulo, e a jornada só entra no
/// denominador da capacidade, calculado na leitura.
/// </summary>
[Route("api/v1/teams")]
[Authorize] // Viewer+ nas leituras; rotas de escrita exigem AdminPlus
public class TeamsController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    private const int MaxNameLength = 100;
    private const int MaxTagLength = 60;

    /// <summary>
    /// Teto de pessoas por PUT de composição. Casa com o teto de dispositivos do plano maior;
    /// a tela envia a composição inteira a cada salvamento, então o limite é da chamada.
    /// </summary>
    public const int MaxMembers = 2000;

    // ============================================================================ CRUD

    /// <summary>
    /// GET /api/v1/teams (Viewer): equipes da organização em ordem alfabética, com a contagem
    /// de pessoas e de regras de classificação próprias, mais a jornada EFETIVA de cada uma
    /// (a da equipe, ou a da organização quando a equipe não declarou).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var orgWorkHours = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT business_hours::text FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));

        var rows = (await connection.QueryAsync<TeamRow>(new CommandDefinition(
            """
            SELECT t.id, t.name, t.tag, t.work_hours::text AS work_hours,
                   (SELECT count(*)::int FROM team_members m
                     WHERE m.tenant_id = t.tenant_id AND m.team_id = t.id) AS member_count,
                   (SELECT count(*)::int FROM tenant_app_team_categories r
                     WHERE r.tenant_id = t.tenant_id AND r.team_id = t.id) AS team_rule_count
            FROM teams t
            WHERE t.tenant_id = @TenantId
            ORDER BY t.name
            """,
            new { TenantId = tenantId }, cancellationToken: ct))).ToList();

        return Ok(new TeamListResponse(
            rows.Select(r => ToResponse(r, orgWorkHours)).ToList(),
            ParseJson(orgWorkHours)));
    }

    /// <summary>
    /// POST /api/v1/teams (Admin): 201. Nome duplicado no tenant → 409; etiqueta já
    /// reivindicada por outra equipe → 409 (uma etiqueta legada tem no máximo uma equipe dona).
    /// Reagrega 30 dias somente quando a equipe nasce COM etiqueta: sem etiqueta e sem pessoas
    /// ela ainda não resolve equipe para lane nenhuma.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Corpo inválido: envie um objeto JSON.");

        var name = ReadText(body, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
            return ProblemResponse(StatusCodes.Status400BadRequest, $"Nome inválido (1 a {MaxNameLength} caracteres).");
        name = name.Trim();

        var tag = NormalizeTag(ReadText(body, "tag"));
        if (tag is { Length: > MaxTagLength })
            return ProblemResponse(StatusCodes.Status400BadRequest, $"Etiqueta inválida (máximo {MaxTagLength} caracteres).");

        var workHoursError = ReadWorkHours(body, out _, out var workHours);
        if (workHoursError is not null) return workHoursError;

        var tenantId = Auth.CurrentUser.TenantId(User);
        var id = Uuid7.NewUuid7();

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var orgWorkHours = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT business_hours::text FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));

        await using var tx = await connection.BeginTransactionAsync(ct);

        // ON CONFLICT DO NOTHING no nome: detecta duplicado sem corrida com outro POST.
        // A unicidade da etiqueta é índice parcial, então vem como 23505 e é traduzida abaixo.
        int inserted;
        try
        {
            inserted = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO teams (id, tenant_id, name, tag, work_hours)
                VALUES (@Id, @TenantId, @Name, @Tag, @WorkHours::jsonb)
                ON CONFLICT (tenant_id, name) DO NOTHING
                """,
                new { Id = id, TenantId = tenantId, Name = name, Tag = tag, WorkHours = workHours },
                transaction: tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return ProblemResponse(StatusCodes.Status409Conflict,
                "Essa etiqueta já pertence a outra equipe.");
        }

        if (inserted == 0)
        {
            await tx.RollbackAsync(ct);
            return ProblemResponse(StatusCodes.Status409Conflict, "Já existe uma equipe com esse nome.");
        }

        if (tag is not null)
        {
            // a etiqueta passa a resolver equipe para as lanes dos dispositivos com ela
            await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);
        }

        await AuditTeamAsync(connection, tx, tenantId, id, new { name, tag, created = true }, ct);
        await tx.CommitAsync(ct);

        var row = new TeamRow(id, name, tag, workHours, 0, 0);
        return Created($"/api/v1/teams/{id}", ToResponse(row, orgWorkHours));
    }

    /// <summary>
    /// PATCH /api/v1/teams/{id} (Admin): parcial. Corpo cru para distinguir campo AUSENTE (não
    /// muda) de <c>null</c> (limpa: <c>tag</c> some, <c>work_hours</c> volta a herdar a jornada
    /// da organização). 404 para inexistente ou de outro tenant.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Update(Guid id, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Corpo inválido: envie um objeto JSON.");

        var hasName = body.TryGetProperty("name", out var nameEl);
        string? name = null;
        if (hasName)
        {
            name = nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
                return ProblemResponse(StatusCodes.Status400BadRequest, $"Nome inválido (1 a {MaxNameLength} caracteres).");
        }

        var hasTag = body.TryGetProperty("tag", out var tagEl);
        string? tag = null;
        if (hasTag && tagEl.ValueKind != JsonValueKind.Null)
        {
            if (tagEl.ValueKind != JsonValueKind.String)
                return ProblemResponse(StatusCodes.Status400BadRequest, "tag deve ser texto ou null.");
            tag = NormalizeTag(tagEl.GetString());
            if (tag is { Length: > MaxTagLength })
                return ProblemResponse(StatusCodes.Status400BadRequest, $"Etiqueta inválida (máximo {MaxTagLength} caracteres).");
        }

        var workHoursError = ReadWorkHours(body, out var hasWorkHours, out var workHours);
        if (workHoursError is not null) return workHoursError;

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var current = await connection.QuerySingleOrDefaultAsync<TeamRow>(new CommandDefinition(
            """
            SELECT t.id, t.name, t.tag, t.work_hours::text AS work_hours,
                   (SELECT count(*)::int FROM team_members m
                     WHERE m.tenant_id = t.tenant_id AND m.team_id = t.id) AS member_count,
                   (SELECT count(*)::int FROM tenant_app_team_categories r
                     WHERE r.tenant_id = t.tenant_id AND r.team_id = t.id) AS team_rule_count
            FROM teams t WHERE t.tenant_id = @TenantId AND t.id = @Id
            """,
            new { TenantId = tenantId, Id = id }, cancellationToken: ct));
        if (current is null) return NotFoundProblem(); // inexistente OU de outro tenant

        var orgWorkHours = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT business_hours::text FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));

        var newName = hasName ? name! : current.Name;
        var newTag = hasTag ? tag : current.Tag;
        var newWorkHours = hasWorkHours ? workHours : current.WorkHours;
        // só a ETIQUETA muda a equipe resolvida das lanes; nome e jornada não tocam agregado
        var tagChanged = hasTag && !string.Equals(newTag, current.Tag, StringComparison.Ordinal);

        await using var tx = await connection.BeginTransactionAsync(ct);

        int updated;
        try
        {
            updated = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE teams
                SET name = @Name, tag = @Tag, work_hours = @WorkHours::jsonb, updated_at = now()
                WHERE tenant_id = @TenantId AND id = @Id
                  AND NOT EXISTS (
                      SELECT 1 FROM teams d
                      WHERE d.tenant_id = @TenantId AND d.name = @Name AND d.id <> @Id)
                """,
                new { TenantId = tenantId, Id = id, Name = newName, Tag = newTag, WorkHours = newWorkHours },
                transaction: tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return ProblemResponse(StatusCodes.Status409Conflict, "Essa etiqueta já pertence a outra equipe.");
        }

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return ProblemResponse(StatusCodes.Status409Conflict, "Já existe uma equipe com esse nome.");
        }

        if (tagChanged)
        {
            await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);
        }

        await AuditTeamAsync(connection, tx, tenantId, id, new
        {
            name = newName,
            from_tag = current.Tag,
            to_tag = newTag,
            work_hours_changed = hasWorkHours,
        }, ct);
        await tx.CommitAsync(ct);

        return Ok(ToResponse(
            new TeamRow(id, newName, newTag, newWorkHours, current.MemberCount, current.TeamRuleCount),
            orgWorkHours));
    }

    /// <summary>
    /// DELETE /api/v1/teams/{id} (Admin): 204. A composição e as REGRAS DE CLASSIFICAÇÃO
    /// próprias saem junto (ON DELETE CASCADE) — as pessoas voltam a "sem equipe" e os apps que
    /// tinham regra própria dela voltam à regra da organização. Por isso reagrega 30 dias.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var current = await connection.QuerySingleOrDefaultAsync<TeamRow>(new CommandDefinition(
            """
            SELECT t.id, t.name, t.tag, t.work_hours::text AS work_hours,
                   (SELECT count(*)::int FROM team_members m
                     WHERE m.tenant_id = t.tenant_id AND m.team_id = t.id) AS member_count,
                   (SELECT count(*)::int FROM tenant_app_team_categories r
                     WHERE r.tenant_id = t.tenant_id AND r.team_id = t.id) AS team_rule_count
            FROM teams t WHERE t.tenant_id = @TenantId AND t.id = @Id
            """,
            new { TenantId = tenantId, Id = id }, cancellationToken: ct));
        if (current is null) return NotFoundProblem();

        await using var tx = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM teams WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = id }, transaction: tx, cancellationToken: ct));

        await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);

        await AuditTeamAsync(connection, tx, tenantId, id, new
        {
            name = current.Name,
            deleted = true,
            unlinked_people = current.MemberCount,
            dropped_rules = current.TeamRuleCount,
        }, ct);
        await tx.CommitAsync(ct);

        return NoContent();
    }

    // ==================================================================== composição (pessoas)

    /// <summary>
    /// GET /api/v1/teams/{id}/members (Viewer): as pessoas da equipe, com o nome já resolvido
    /// pela mesma régua da lista de colaboradores (apelido de people &gt; nome do Windows).
    /// </summary>
    [HttpGet("{id:guid}/members")]
    public async Task<IActionResult> Members(Guid id, CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var name = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM teams WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = id }, cancellationToken: ct));
        if (name is null) return NotFoundProblem();

        var rows = (await connection.QueryAsync<MemberRow>(new CommandDefinition(
            MembersSql, new { TenantId = tenantId, Id = id }, cancellationToken: ct))).ToList();

        return Ok(new TeamMembersResponse(id, name,
            rows.Select(r => new TeamMemberResponse(r.WindowsSid, r.DisplayName)).ToList()));
    }

    /// <summary>
    /// PUT /api/v1/teams/{id}/members (Admin): composição DECLARATIVA — a lista enviada passa a
    /// ser a composição exata. Uma pessoa está em no máximo uma equipe (PK de team_members):
    /// enviar alguém que estava em outra equipe a MOVE, sem erro, e a trilha registra o
    /// movimento. Reagrega 30 dias porque a equipe da pessoa decide a regra de classificação
    /// aplicada ao tempo dela.
    /// </summary>
    [HttpPut("{id:guid}/members")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> SetMembers(
        Guid id, [FromBody] SetTeamMembersRequest? request, CancellationToken ct)
    {
        var sids = (request?.WindowsSids ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sids.Length > MaxMembers)
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Lista muito grande: máximo de {MaxMembers} pessoas por chamada.");

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var teamName = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM teams WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = id }, cancellationToken: ct));
        if (teamName is null) return NotFoundProblem();

        await using var tx = await connection.BeginTransactionAsync(ct);

        // quem sai: estava nesta equipe e não veio na lista
        var removed = await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM team_members
            WHERE tenant_id = @TenantId AND team_id = @Id AND NOT (windows_sid = ANY(@Sids::text[]))
            """,
            new { TenantId = tenantId, Id = id, Sids = sids }, transaction: tx, cancellationToken: ct));

        var added = 0;
        if (sids.Length > 0)
        {
            // DO UPDATE (e não DO NOTHING): a pessoa que estava em OUTRA equipe é MOVIDA para
            // esta. Com DO NOTHING o vínculo antigo sobreviveria em silêncio e a tela mentiria.
            added = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO team_members (tenant_id, team_id, windows_sid)
                SELECT @TenantId, @Id, s FROM unnest(@Sids::text[]) AS s
                ON CONFLICT (tenant_id, windows_sid) DO UPDATE SET team_id = EXCLUDED.team_id
                WHERE team_members.team_id <> EXCLUDED.team_id
                """,
                new { TenantId = tenantId, Id = id, Sids = sids }, transaction: tx, cancellationToken: ct));
        }

        var enqueued = 0;
        if (added > 0 || removed > 0)
        {
            enqueued = await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);
            await AuditTeamAsync(connection, tx, tenantId, id, new
            {
                name = teamName, members = sids.Length, added, removed,
            }, ct);
        }

        var rows = (await connection.QueryAsync<MemberRow>(new CommandDefinition(
            MembersSql, new { TenantId = tenantId, Id = id }, transaction: tx, cancellationToken: ct))).ToList();

        await tx.CommitAsync(ct);

        return Ok(new SetTeamMembersResponse(id,
            rows.Select(r => new TeamMemberResponse(r.WindowsSid, r.DisplayName)).ToList(),
            added, removed, enqueued));
    }

    // ============================================================================ capacidade

    /// <summary>
    /// GET /api/v1/teams/capacity?from&amp;to (Viewer): a base do denominador da CAPACIDADE
    /// UTILIZADA no período, uma linha para a organização e uma por equipe.
    ///
    /// <c>business_days</c> já vem SEM os feriados da organização — é a correção que a F7 traz
    /// (a pendência registrada em `EquipesLadoALado.tsx`: "uma semana com feriado aparece com
    /// capacidade subestimada"). Cada linha traz também a jornada EFETIVA (a da equipe, ou a da
    /// organização quando ela não declarou) e <c>capacity_seconds_per_person</c> pronto.
    ///
    /// PONTO DE CONSUMO NO PORTAL (arquivo de outro agente nesta leva, não editado aqui):
    /// `portal/src/components/dashboard/EquipesLadoALado.tsx`, função `baseDeCapacidade` — ela
    /// hoje deriva jornada e dias úteis do `business_hours` no cliente e conta feriado como dia
    /// útil. A troca é: buscar este endpoint uma vez por período, casar cada linha do cartão
    /// pelo `tag` (que é o mesmo `?tag` do overview) e usar `capacity_seconds_per_person ×
    /// pessoas` como denominador, apagando `baseDeCapacidade` e o rodapé que avisa da
    /// subestimação. A conta continua sendo `ativo ÷ denominador`, e o mínimo de grupo de 3
    /// pessoas continua valendo no cliente.
    /// </summary>
    [HttpGet("capacity")]
    public async Task<IActionResult> Capacity(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        CancellationToken ct)
    {
        var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
        if (invalid is not null) return invalid;

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var org = await connection.QuerySingleOrDefaultAsync<OrgRow>(new CommandDefinition(
            "SELECT name, business_hours::text AS business_hours FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        if (org is null) return NotFoundProblem();

        // ::text + parse: convenção de datas do repo com Dapper (mesmo padrão de
        // summary_date::text AS date no dashboard) — nada de conversão implícita de date
        var holidays = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT holiday_date::text FROM organization_holidays
            WHERE tenant_id = @TenantId AND holiday_date BETWEEN @From::date AND @To::date
            """,
            new { TenantId = tenantId, From = fromDay.ToString("yyyy-MM-dd"), To = toDay.ToString("yyyy-MM-dd") },
            cancellationToken: ct)))
            .Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToHashSet();

        var teams = (await connection.QueryAsync<TeamRow>(new CommandDefinition(
            """
            SELECT t.id, t.name, t.tag, t.work_hours::text AS work_hours,
                   (SELECT count(*)::int FROM team_members m
                     WHERE m.tenant_id = t.tenant_id AND m.team_id = t.id) AS member_count,
                   0 AS team_rule_count
            FROM teams t WHERE t.tenant_id = @TenantId ORDER BY t.name
            """,
            new { TenantId = tenantId }, cancellationToken: ct))).ToList();

        var scopes = new List<CapacityScopeResponse>
        {
            Scope("organization", null, org.Name, null, org.BusinessHours, 0),
        };
        scopes.AddRange(teams.Select(t => Scope(
            "team", t.Id, t.Name, t.Tag,
            // precedência da JORNADA: a da equipe vence; ausente, herda a da organização
            t.WorkHours ?? org.BusinessHours, t.MemberCount)));

        return Ok(new CapacityResponse(
            fromDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            toDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            scopes));

        CapacityScopeResponse Scope(
            string scopeType, Guid? teamId, string label, string? tag, string? workHours, int members)
        {
            var result = CapacityBase.Compute(workHours, fromDay, toDay, holidays);
            return new CapacityScopeResponse(
                scopeType, teamId, label, tag,
                result.DailyHours, result.BusinessDays, result.HolidaysExcluded,
                result.SecondsPerPerson, members);
        }
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>
    /// Nome exibido de cada pessoa da equipe: apelido de <c>people</c> quando existe, senão o
    /// nome do Windows mais recente entre os dispositivos. Mesma régua da lista de colaboradores.
    /// </summary>
    private const string MembersSql =
        """
        SELECT m.windows_sid,
               COALESCE(p.display_name,
                        (SELECT du.windows_username FROM device_users du
                          WHERE du.tenant_id = m.tenant_id AND du.windows_sid = m.windows_sid
                          ORDER BY du.last_seen_at DESC LIMIT 1),
                        m.windows_sid) AS display_name
        FROM team_members m
        LEFT JOIN people p ON p.tenant_id = m.tenant_id AND p.windows_sid = m.windows_sid
        WHERE m.tenant_id = @TenantId AND m.team_id = @Id
        ORDER BY display_name
        """;

    /// <summary>Trilha update_team na MESMA transação da mutação (nunca a mudança sem a trilha).</summary>
    private Task AuditTeamAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId, Guid teamId,
        object detail, CancellationToken ct) =>
        AuditWriter.AddInTransactionAsync(connection, tx, tenantId, AuditActions.UpdateTeam,
            actorUserId: Auth.CurrentUser.UserId(User),
            actorIp: HttpContext.Connection.RemoteIpAddress,
            targetType: "team", targetId: teamId,
            detailJson: JsonSerializer.Serialize(detail), ct: ct);

    private static string? ReadText(JsonElement body, string field) =>
        body.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>Etiqueta normalizada igual ao PATCH /devices: trim, vazio vira null.</summary>
    private static string? NormalizeTag(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();

    /// <summary>
    /// work_hours: ausente = não muda; null = volta a herdar a jornada da organização; objeto =
    /// substitui. Valida pelo MESMO parser do business_hours da org (BusinessHoursWindow), para
    /// não existirem duas noções de jornada válida no produto.
    /// </summary>
    private ObjectResult? ReadWorkHours(JsonElement body, out bool present, out string? json)
    {
        json = null;
        present = body.TryGetProperty("work_hours", out var el);
        if (!present || el.ValueKind == JsonValueKind.Null) return null;

        if (el.ValueKind != JsonValueKind.Object)
            return ProblemResponse(StatusCodes.Status400BadRequest, "work_hours deve ser um objeto JSON ou null.");

        var raw = el.GetRawText();
        if (!BusinessHoursWindow.TryParse(raw, out _))
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "work_hours inválido: use {\"days\":[1,2,3,4,5],\"start\":\"08:00\",\"end\":\"18:00\"} com start anterior a end.");

        json = raw;
        return null;
    }

    private static JsonElement? ParseJson(string? raw)
    {
        if (raw is null) return null;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static TeamResponse ToResponse(TeamRow row, string? orgWorkHours)
    {
        var inherited = row.WorkHours is null;
        return new TeamResponse(
            row.Id, row.Name, row.Tag,
            ParseJson(row.WorkHours),
            ParseJson(row.WorkHours ?? orgWorkHours),
            inherited,
            row.MemberCount, row.TeamRuleCount);
    }

    private sealed record TeamRow(
        Guid Id, string Name, string? Tag, string? WorkHours, int MemberCount, int TeamRuleCount);

    private sealed record MemberRow(string WindowsSid, string DisplayName);

    private sealed record OrgRow(string Name, string? BusinessHours);
}
