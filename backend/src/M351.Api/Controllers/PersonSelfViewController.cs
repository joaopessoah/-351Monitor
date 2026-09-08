using System.Text.Json;
using Dapper;
using M351.Api.Auditing;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// GET /api/v1/people/{sid}/self-view (item 2 do estudo, decisão 10 do spec de 07/09/2026).
///
/// A VISÃO DO COLABORADOR: os mesmos números que a pessoa veria sobre si — os quatro baldes, as
/// horas, o índice com a cobertura ao lado, os aplicativos e quantas anotações e contestações
/// dela estão em aberto.
///
/// O QUE ESTE ENDPOINT NÃO É: uma porta para o colaborador. Ele não tem login, e a decisão 10
/// fecha a questão — nada de token pessoal, magic link ou rota pública com dado dele. Quem lê
/// isto é Viewer+ autenticado, dentro do painel, e o que chega às mãos da pessoa chega pelo
/// resumo em PDF que o gestor imprime ou pelo digest pessoal por e-mail. `/t/{token}` e o
/// PublicTransparencyController continuam só com a política de coleta, e é para continuarem.
///
/// POR QUE UM CONTROLLER PRÓPRIO: mesma razão do PersonNotesController — PeopleController já
/// passa de 400 linhas e junta listagem, série diária e edição de identidade. Esta leitura tem
/// SQL próprio e um motivo próprio para mudar.
///
/// IDENTIDADE: o mesmo aperto do PersonNotesController. O segmento {sid} aceita DUAS formas de
/// propósito — o próprio windows_sid OU o uuid de um device_users.id —, porque a página da pessoa
/// navega por device_user_id e GET /device-users/{id} não devolve o SID. Adivinhar por nome no
/// cliente arriscaria mostrar A PESSOA ERRADA os próprios números; a resolução acontece aqui, pelo
/// par (tenant, id) que é chave real, e a resposta devolve o SID canônico resolvido.
///
/// AUDITORIA: view_report. É leitura de dado pessoal identificado (decisão 2), e deixa rastro
/// como a lista de colaboradores. O 404 não audita: não houve dado pessoal a ler.
/// </summary>
[Route("api/v1/people/{sid}/self-view")]
[Authorize] // Viewer+ — o colaborador NÃO tem acesso; ver decisão 10 do spec
public class PersonSelfViewController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    /// <summary>Lane sintética da máquina: não é pessoa e não tem visão de colaborador.</summary>
    private static readonly Guid MachineLane = Guid.Empty;

    /// <summary>Quantos aplicativos a visão traz. O bastante para a pessoa se reconhecer na lista.</summary>
    public const int TopAppsLimit = 10;

    [HttpGet]
    [AuditRead] // leitura de dado pessoal: view_report via AuditReadFilter, só em 2xx
    public async Task<IActionResult> Get(
        string sid,
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromServices] AuditReadContext readAudit = null!,
        CancellationToken ct = default)
    {
        var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
        if (invalid is not null)
        {
            return invalid;
        }

        var tenantId = CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var resolved = await ResolveSidAsync(connection, tenantId, sid, ct);
        if (resolved is null)
        {
            return NotFoundProblem();
        }

        var args = new
        {
            TenantId = tenantId,
            Sid = resolved,
            From = fromDay.ToString("yyyy-MM-dd"),
            To = toDay.ToString("yyyy-MM-dd"),
            Limit = TopAppsLimit,
        };

        var totals = await connection.QuerySingleAsync<TotalsRow>(
            new CommandDefinition(TotalsSql, args, cancellationToken: ct));
        var identity = await connection.QuerySingleOrDefaultAsync<IdentityRow>(
            new CommandDefinition(IdentitySql, args, cancellationToken: ct));
        var apps = (await connection.QueryAsync<AppRow>(
            new CommandDefinition(TopAppsSql, args, cancellationToken: ct))).ToList();
        // A JANELA DAS ANOTAÇÕES VAI EM UTC EXPLÍCITO, resolvida no fuso do TENANT — igual ao
        // PersonNotesController. Deixar `@To::date::timestamptz` resolver sozinho usaria o fuso
        // da SESSÃO (UTC), e num tenant em America/Sao_Paulo a janela sairia 3 h deslocada: uma
        // contestação das 21h do dia 07 (00h UTC do dia 08) não contaria aqui e apareceria na
        // lista logo abaixo, na mesma página.
        var tz = await TenantTimeZoneAsync(connection, tenantId, ct);
        var notes = await connection.QuerySingleAsync<NotesRow>(new CommandDefinition(
            OpenNotesSql,
            new
            {
                TenantId = tenantId,
                Sid = resolved,
                WindowStart = LocalMidnightUtc(fromDay, tz),
                WindowEnd = LocalMidnightUtc(toDay.AddDays(1), tz),
            },
            cancellationToken: ct));

        // FÓRMULA ÚNICA (decisão 4), a mesma do /dashboard/overview e do /people: índice é
        // produtivo ÷ classificado, cobertura é (ativo − sem classificação) ÷ ativo. Sem
        // denominador vem null — nunca zero, que leria como "índice péssimo" e não "sem dado".
        var classified = totals.SecondsWorkRelated + totals.SecondsNeutral + totals.SecondsNotWorkRelated;
        double? index = classified > 0
            ? Math.Round((double)totals.SecondsWorkRelated / classified, 4)
            : null;
        double? coverage = totals.SecondsActive > 0
            ? Math.Round((double)(totals.SecondsActive - totals.SecondsUnclassified) / totals.SecondsActive, 4)
            : null;

        readAudit.Record(tenantId, AuditActions.ViewReport,
            CurrentUser.UserId(User),
            targetType: "people", targetId: null,
            detailJson: JsonSerializer.Serialize(new
            {
                windows_sid = resolved,
                from = args.From,
                to = args.To,
                view = "self_view",
            }));

        return Ok(new PersonSelfViewResponse(
            resolved,
            identity?.DisplayName,
            identity?.TeamName,
            new OverviewPeriodResponse(args.From, args.To, toDay.DayNumber - fromDay.DayNumber + 1),
            totals.SecondsOn, totals.SecondsActive, totals.SecondsIdle, totals.SecondsLocked,
            totals.SecondsWorkRelated, totals.SecondsNeutral, totals.SecondsNotWorkRelated,
            totals.SecondsUnclassified,
            index, coverage,
            totals.DaysWithData, totals.DeviceCount,
            apps.Select(a => new PersonSelfViewAppRow(
                a.ProcessName, a.DisplayName ?? a.ProcessName, a.Classification, a.SecondsActive)).ToList(),
            notes.OpenNotes, notes.OpenDisputes));
    }

    // ------------------------------------------------------------------------------ identidade

    /// <summary>
    /// {sid} → windows_sid CANÔNICO do tenant, com a mesclagem de people aplicada; null (→ 404)
    /// quando não existe pessoa por trás dele. Cópia deliberada da resolução do
    /// PersonNotesController: as duas telas navegam pelos mesmos identificadores, e divergir aqui
    /// significaria uma das duas mostrar a pessoa errada.
    /// </summary>
    private static async Task<string?> ResolveSidAsync(
        NpgsqlConnection connection, Guid tenantId, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        key = key.Trim();

        if (Guid.TryParse(key, out var deviceUserId))
        {
            if (deviceUserId == MachineLane)
            {
                return null; // lane-máquina não é pessoa (decisão 8)
            }

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

    // ------------------------------------------------------------------------------------- SQL

    /// <summary>Apelido da pessoa e a equipe a que ela pertence (people + team_members).</summary>
    private const string IdentitySql = """
        SELECT p.display_name AS DisplayName, t.name AS TeamName
        FROM (SELECT @Sid::text AS windows_sid) k
        LEFT JOIN people p ON p.tenant_id = @TenantId AND p.windows_sid = k.windows_sid
        LEFT JOIN team_members tm ON tm.tenant_id = @TenantId AND tm.windows_sid = k.windows_sid
        LEFT JOIN teams t ON t.tenant_id = @TenantId AND t.id = tm.team_id
        """;

    /// <summary>
    /// Os baldes da pessoa no período. As lanes da pessoa são todas as (device, sid) que resolvem
    /// para o SID canônico — a MESMA pessoa em duas máquinas é uma linha só, que é a promessa de
    /// people e o que device_users, chaveado por (device, sid), não consegue dar.
    ///
    /// days_with_data conta DIAS DISTINTOS com tempo ligado, não linhas: quem trabalhou o mesmo
    /// dia em duas máquinas viveu um dia, não dois.
    /// </summary>
    private const string TotalsSql = """
        SELECT COALESCE(sum(s.seconds_on), 0)::bigint AS SecondsOn,
               COALESCE(sum(s.seconds_active), 0)::bigint AS SecondsActive,
               COALESCE(sum(s.seconds_idle), 0)::bigint AS SecondsIdle,
               COALESCE(sum(s.seconds_locked), 0)::bigint AS SecondsLocked,
               COALESCE(sum(s.seconds_work_related), 0)::bigint AS SecondsWorkRelated,
               COALESCE(sum(s.seconds_neutral), 0)::bigint AS SecondsNeutral,
               COALESCE(sum(s.seconds_not_work_related), 0)::bigint AS SecondsNotWorkRelated,
               COALESCE(sum(s.seconds_unclassified), 0)::bigint AS SecondsUnclassified,
               count(DISTINCT s.summary_date) FILTER (WHERE s.seconds_on > 0)::int AS DaysWithData,
               count(DISTINCT s.device_id)::int AS DeviceCount
        FROM daily_device_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        JOIN device_users du ON du.id = s.device_user_id AND du.tenant_id = s.tenant_id
        LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND COALESCE(p.merged_into_sid, du.windows_sid) = @Sid
          AND s.summary_date BETWEEN @From::date AND @To::date
        """;

    /// <summary>
    /// Os aplicativos da pessoa, com a classificação vigente pela precedência da F5 (regra da
    /// EQUIPE vence a da ORGANIZAÇÃO). Classificação NULL quando ninguém classificou: null não é
    /// zero, e zero significaria "a empresa decidiu que isto é neutro".
    /// </summary>
    private const string TopAppsSql = """
        WITH lane AS (
            -- EQUIPE DA LANE, com a MESMA precedência do DailyAggregationService (F7):
            --   1. equipe da PESSOA, pelo SID canônico;
            --   2. etiqueta legada do dispositivo que alguma equipe declarou em teams.tag;
            --   3. NULL — cai na regra da organização.
            -- Resolver só por team_members mostraria à pessoa a regra da ORGANIZAÇÃO enquanto
            -- os números dela foram somados pela regra da EQUIPE: dois rótulos para o mesmo
            -- tempo, na mesma tela. O LATERAL com LIMIT 1 é obrigatório — sem ele, device com
            -- duas etiquetas reivindicadas por duas equipes duplicaria a lane.
            SELECT du.id AS device_user_id,
                   COALESCE(tm.team_id, tag_team.team_id) AS team_id
            FROM device_users du
            LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
            LEFT JOIN team_members tm
                   ON tm.tenant_id = du.tenant_id
                  AND tm.windows_sid = COALESCE(p.merged_into_sid, du.windows_sid)
            LEFT JOIN devices dev
                   ON dev.tenant_id = du.tenant_id AND dev.id = du.device_id
            LEFT JOIN LATERAL (
                SELECT t2.id AS team_id
                FROM teams t2
                WHERE t2.tenant_id = du.tenant_id
                  AND t2.tag IS NOT NULL AND t2.tag = ANY(dev.tags)
                ORDER BY t2.name
                LIMIT 1
            ) tag_team ON true
            WHERE du.tenant_id = @TenantId
              AND COALESCE(p.merged_into_sid, du.windows_sid) = @Sid
        )
        SELECT ac.process_name AS ProcessName,
               NULLIF(ac.display_name, '') AS DisplayName,
               c.classification::int AS Classification,
               COALESCE(sum(a.seconds_active), 0)::bigint AS SecondsActive
        FROM daily_app_usage a
        JOIN lane l ON l.device_user_id = a.device_user_id
        JOIN devices d ON d.id = a.device_id AND d.tenant_id = a.tenant_id
        JOIN app_catalog ac ON ac.id = a.app_id
        LEFT JOIN tenant_app_team_categories tatc
               ON tatc.tenant_id = a.tenant_id AND tatc.app_id = a.app_id
              AND tatc.team_id = l.team_id
        LEFT JOIN tenant_app_categories tac
               ON tac.tenant_id = a.tenant_id AND tac.app_id = a.app_id
        LEFT JOIN categories c
               ON c.tenant_id = a.tenant_id
              AND c.id = COALESCE(tatc.category_id, tac.category_id)
        WHERE a.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND a.summary_date BETWEEN @From::date AND @To::date
        GROUP BY ac.process_name, ac.display_name, c.classification
        ORDER BY 4 DESC, 1 ASC
        LIMIT @Limit
        """;

    /// <summary>
    /// Anotações e contestações da pessoa que CRUZAM o recorte e seguem em aberto. "Cruzam", e
    /// não "começam nele": uma anotação de segunda a sexta tem de contar quando a tela mostra a
    /// quarta — a mesma régua da listagem em PersonNotesController.
    /// </summary>
    private const string OpenNotesSql = """
        SELECT count(*) FILTER (WHERE n.kind = 'anotacao')::int AS OpenNotes,
               count(*) FILTER (WHERE n.kind = 'contestacao')::int AS OpenDisputes
        FROM person_notes n
        WHERE n.tenant_id = @TenantId
          AND n.windows_sid = @Sid
          AND n.status = 'aberta'
          AND n.started_at < @WindowEnd
          AND n.ended_at >= @WindowStart
        """;

    /// <summary>Fuso do tenant; UTC quando o id do fuso não é reconhecido pela máquina.</summary>
    private static async Task<TimeZoneInfo> TenantTimeZoneAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        var id = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));

        try
        {
            return id is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Meia-noite local do dia, em UTC — a borda real da janela de um relatório.</summary>
    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), tz), TimeSpan.Zero);

    private sealed record TotalsRow(
        long SecondsOn,
        long SecondsActive,
        long SecondsIdle,
        long SecondsLocked,
        long SecondsWorkRelated,
        long SecondsNeutral,
        long SecondsNotWorkRelated,
        long SecondsUnclassified,
        int DaysWithData,
        int DeviceCount);

    private sealed record IdentityRow(string? DisplayName, string? TeamName);

    private sealed record AppRow(string ProcessName, string? DisplayName, int? Classification, long SecondsActive);

    private sealed record NotesRow(int OpenNotes, int OpenDisputes);
}
