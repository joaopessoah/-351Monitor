using System.Globalization;
using System.Text.Json;
using Dapper;
using M351.Api.Contracts;
using M351.Infrastructure.Alerts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// GET /api/v1/alerts (F6, seção 4 do spec de 07/09/2026): os alertas de gestão VIVOS da
/// organização, avaliados pelo worker sobre os agregados diários (ManagementAlertService).
///
/// LEITURA PURA de estado: nenhuma regra é avaliada aqui. O portal chama isto a cada abertura
/// da Visão Geral e o custo é um SELECT num índice parcial — se a avaliação vivesse no
/// controller, cada F5 do gestor recomputaria quatro semanas de agregado.
///
/// Viewer+ ([Authorize] simples, como o DashboardController): alerta de gestão é AGREGADO de
/// equipe e de organização, o mesmo material que a Visão Geral já mostra a qualquer papel.
/// Por isso também NÃO grava <c>view_report</c> — a trilha de leitura existe para consulta
/// FILTRADA por titular, e aqui não há filtro por pessoa... com uma exceção: os alertas de
/// escopo pessoa, que só existem sob o opt-in da organização (decisão 5) e cuja autorização
/// fica registrada no <c>update_alert_prefs</c> do momento em que o toggle foi ligado.
///
/// TODO(F6/Configurações › Alertas): o TOGGLE de interface de <c>person_alerts_enabled</c>
/// entra junto com a tela Configurações › Alertas, no PATCH /organization (que pertence ao
/// OrganizationController). A coluna e a constante de auditoria
/// (<c>AuditActions.UpdateAlertPrefs</c>) já existem; até a tela chegar, o campo
/// <c>person_alerts_enabled</c> desta resposta é o único lugar que informa se as regras de
/// pessoa estão ligadas.
/// </summary>
[Route("api/v1/alerts")]
[Authorize] // Viewer+
public class AlertsController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    /// <summary>Severidade "precisa de decisão" — faixa de destaque no cartão.</summary>
    public const string SeverityAttention = "atencao";

    /// <summary>Severidade "vale saber" — faixa discreta.</summary>
    public const string SeverityInfo = "informativo";

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // tenant_id manuscrito em todo SQL: o filtro global do EF não alcança Dapper
        var org = await connection.QuerySingleAsync<OrgRow>(new CommandDefinition(
            """
            SELECT plan, person_alerts_enabled AS PersonAlertsEnabled
            FROM organizations
            WHERE id = @TenantId
            """,
            new { TenantId = tenantId }, cancellationToken: ct));

        var rows = (await connection.QueryAsync<AlertRow>(new CommandDefinition(
            """
            SELECT kind, scope_type AS ScopeType, scope_key AS ScopeKey,
                   first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt,
                   detail::text AS Detail
            FROM management_alerts
            WHERE tenant_id = @TenantId AND resolved_at IS NULL
            ORDER BY first_seen_at DESC, kind, scope_key
            """,
            new { TenantId = tenantId }, cancellationToken: ct))).ToList();

        // rótulo das pessoas numa consulta só (nunca uma por alerta): mesma resolução do
        // GET /people — apelido > nome da lane > usuário do Windows > o próprio SID.
        var personKeys = rows
            .Where(r => r.ScopeType == ManagementAlertService.ScopePerson)
            .Select(r => r.ScopeKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var personLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (personKeys.Length > 0)
        {
            var labels = await connection.QueryAsync<PersonLabelRow>(new CommandDefinition(
                """
                SELECT COALESCE(p.merged_into_sid, du.windows_sid) AS Sid,
                       COALESCE(max(pp.display_name), max(du.display_name),
                                max(du.windows_username), COALESCE(p.merged_into_sid, du.windows_sid)) AS Label
                FROM device_users du
                LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
                LEFT JOIN people pp ON pp.tenant_id = du.tenant_id
                                   AND pp.windows_sid = COALESCE(p.merged_into_sid, du.windows_sid)
                WHERE du.tenant_id = @TenantId
                  AND COALESCE(p.merged_into_sid, du.windows_sid) = ANY(@Sids)
                GROUP BY COALESCE(p.merged_into_sid, du.windows_sid)
                """,
                new { TenantId = tenantId, Sids = personKeys }, cancellationToken: ct));

            foreach (var row in labels)
            {
                personLabels[row.Sid] = row.Label;
            }
        }

        // evidência de que o motor rodou (a trilha global de maintenance_runs), para a tela
        // poder dizer "avaliado há X min" em vez de deixar o gestor no escuro num ciclo travado
        var evaluatedAt = await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            """
            SELECT max(finished_at) FROM maintenance_runs
            WHERE job_name = @Job AND status = 'ok'
            """,
            new { Job = ManagementAlertService.JobName }, cancellationToken: ct));

        var items = rows.Select(row => Present(row, personLabels)).ToList();

        return Ok(new AlertsResponse(
            items,
            org.PersonAlertsEnabled,
            org.Plan == ManagementAlertService.RequiredPlan,
            evaluatedAt));
    }

    // ------------------------------------------------------------------ vocabulário

    /// <summary>
    /// Traduz uma linha de estado na frase exibida. REGRA DE REDAÇÃO: variação e contexto,
    /// nunca julgamento. Nada aqui diz "equipe ruim" nem ordena equipes; o alerta descreve a
    /// diferença e a régua, e o gestor decide. Ocioso nunca é chamado de improdutivo.
    /// </summary>
    private static AlertItemResponse Present(AlertRow row, Dictionary<string, string> personLabels)
    {
        var detail = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.Detail ?? "{}")
                     ?? new Dictionary<string, object?>();

        var scopeLabel = row.ScopeType switch
        {
            ManagementAlertService.ScopeTeam => row.ScopeKey,
            ManagementAlertService.ScopePerson => personLabels.GetValueOrDefault(row.ScopeKey, row.ScopeKey),
            ManagementAlertService.ScopeDevice => Text(detail, "device_name") ?? row.ScopeKey,
            _ => null,
        };

        var (severity, title, context, action, link) = row.Kind switch
        {
            ManagementAlertService.KindCoverageBelow => (
                SeverityAttention,
                $"Cobertura da classificação em {Pct(Num(detail, "coverage"))} nos últimos 7 dias",
                $"A régua é {Pct(ManagementAlertService.CoverageFloor)}. "
                + $"{Hours(Num(detail, "seconds_unclassified"))} de tempo ativo estão em aplicativos sem categoria, "
                + "e o índice não considera esse tempo — classificar muda o número.",
                "Classificar aplicativos",
                "/configuracoes/categorias"),

            ManagementAlertService.KindTeamActiveDrop => (
                SeverityAttention,
                $"Equipe {scopeLabel} com {Dec(Num(detail, "drop_pct"))}% menos tempo ativo por pessoa "
                + $"que a média das {Int(detail, "baseline_weeks")} semanas anteriores",
                $"{Hours(Num(detail, "seconds_active_per_person_day"))} por pessoa por dia nesta semana, "
                + $"contra {Hours(Num(detail, "baseline_seconds_active_per_person_day"))} na base. "
                + $"{Int(detail, "people")} pessoas na etiqueta. Feriado, férias e trabalho fora da máquina "
                + "explicam parte da diferença.",
                "Ver a equipe",
                LinkComTag("/visao-geral", row.ScopeKey)),

            ManagementAlertService.KindTeamUnproductiveHigh => (
                SeverityInfo,
                $"Equipe {scopeLabel} com {Dec(Num(detail, "unproductive_pct"))}% do tempo ativo "
                + "em aplicativos classificados como improdutivos",
                $"A régua é {Dec(Num(detail, "ceiling_pct"))}%. O denominador é o tempo ATIVO — ocioso e "
                + "bloqueado ficam fora. A classificação é definida pela sua empresa e vale para "
                + "aplicativos, não para pessoas.",
                "Rever a classificação",
                "/configuracoes/categorias"),

            ManagementAlertService.KindPersonLongDays => (
                SeverityInfo,
                $"{scopeLabel} com {Int(detail, "days")} dias de máquina ligada acima de "
                + $"{Int(detail, "threshold_hours")} h nesta semana",
                "Sinal de EQUILÍBRIO, não de desempenho: máquina ligada inclui tempo ocioso e "
                + "bloqueado. Vale conversar sobre carga, não sobre produtividade.",
                "Ver a linha do tempo",
                "/linha-do-tempo"),

            ManagementAlertService.KindPersonAfterHours => (
                SeverityInfo,
                $"{scopeLabel} com {Hours(Num(detail, "seconds_active"))} de atividade fora do "
                + "horário nesta semana",
                $"A régua é {Int(detail, "threshold_hours")} h na semana, medida contra o horário de "
                + "trabalho configurado pela organização. Sinal de equilíbrio; plantão e fuso "
                + "diferente explicam parte.",
                "Ver fora do horário",
                "/relatorios/fora-do-horario"),

            ManagementAlertService.KindGoalAtRisk => (
                SeverityAttention,
                $"Meta da semana projetada em {Dec(Num(detail, "projected_hours"))} h "
                + $"contra {Int(detail, "goal_hours")} h combinadas",
                $"Projeção pelo ritmo dos {Int(detail, "days_with_data")} dias já registrados, "
                + $"estendida aos {Int(detail, "business_days")} dias úteis da semana. "
                + "Projeção não é resultado: um dia forte muda o número.",
                "Ver a meta",
                "/visao-geral"),

            ManagementAlertService.KindBusinessDayNoData => (
                SeverityAttention,
                $"{scopeLabel} sem nenhum dado em {Dia(detail)}, um dia útil",
                "Dispositivo ativo e dia útil, mas nenhum segundo registrado: agente parado, "
                + "máquina desligada o dia inteiro ou pessoa afastada. Sem dado, o período fica "
                + "subestimado para a equipe inteira.",
                "Ver o dispositivo",
                "/dispositivos?filtro=alerta"),

            // regra nova sem redação ainda: mostra o kind cru em vez de sumir da tela
            _ => (SeverityInfo, row.Kind, "Regra sem descrição cadastrada.", "Ver a Visão Geral", "/visao-geral"),
        };

        return new AlertItemResponse(
            row.Kind, row.ScopeType, row.ScopeKey, scopeLabel,
            severity, title, context, action, link,
            row.FirstSeenAt, row.LastSeenAt, detail);
    }

    /// <summary>Link do portal com a etiqueta de equipe no mesmo parâmetro que a tela usa.</summary>
    private static string LinkComTag(string path, string tag) => $"{path}?tag={Uri.EscapeDataString(tag)}";

    // ------------------------------------------------------------------ formatação pt-BR

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Número do jsonb; null quando ausente (jamais 0, que mentiria na frase).</summary>
    private static double? Num(Dictionary<string, object?> detail, string key) =>
        detail.TryGetValue(key, out var value) && value is JsonElement el
        && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)
            ? d
            : null;

    private static string? Text(Dictionary<string, object?> detail, string key) =>
        detail.TryGetValue(key, out var value) && value is JsonElement el
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>Inteiro para a frase; "–" quando falta o dado (nunca "0").</summary>
    private static string Int(Dictionary<string, object?> detail, string key)
    {
        var value = Num(detail, key);
        return value is null ? "–" : ((long)Math.Round(value.Value)).ToString("N0", PtBr);
    }

    /// <summary>Uma casa decimal, vírgula pt-BR; "–" quando falta o dado.</summary>
    private static string Dec(double? value) =>
        value is null ? "–" : value.Value.ToString("N1", PtBr);

    /// <summary>Fração de 0 a 1 como percentual inteiro; "–" quando null (nunca "0%").</summary>
    private static string Pct(double? value) =>
        value is null ? "–" : $"{Math.Round(value.Value * 100)}%";

    /// <summary>Segundos em horas legíveis: "8,1 h" abaixo de 10 h, "908 h" acima.</summary>
    private static string Hours(double? seconds)
    {
        if (seconds is null)
        {
            return "–";
        }

        var hours = seconds.Value / 3600.0;
        return hours > 0 && hours < 10
            ? $"{hours.ToString("N1", PtBr)} h"
            : $"{Math.Round(hours).ToString("N0", PtBr)} h";
    }

    private static string Dia(Dictionary<string, object?> detail)
    {
        var raw = Text(detail, "day");
        return raw is not null && DateOnly.TryParseExact(raw, "yyyy-MM-dd", out var day)
            ? day.ToString("dd/MM", PtBr)
            : "–";
    }

    private sealed record OrgRow(string Plan, bool PersonAlertsEnabled);

    private sealed record AlertRow(
        string Kind,
        string ScopeType,
        string ScopeKey,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt,
        string? Detail);

    private sealed record PersonLabelRow(string Sid, string Label);
}
