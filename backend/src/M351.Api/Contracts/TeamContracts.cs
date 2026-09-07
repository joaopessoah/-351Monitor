using System.Text.Json;

namespace M351.Api.Contracts;

// =============================================================================================
// F7 — EQUIPES, JORNADA POR EQUIPE E FERIADOS (spec seção 2.3 e seção 6).
//
// A "equipe" do produto tem DUAS encarnações que coexistem de propósito:
//  - a ETIQUETA livre de devices.tags, que continua sendo o filtro ?tag de todos os dashboards,
//    relatórios e exports — nada foi removido;
//  - a ENTIDADE teams, vinculada a PESSOA (windows_sid), que é quem tem nome canônico, jornada
//    declarada e regra de classificação própria.
// Onde as duas respondem, a equipe da PESSOA vence; a etiqueta é o atalho de migração, e a
// equipe declara em `tag` qual etiqueta legada ela representa.
// =============================================================================================

/// <summary>
/// Uma equipe na listagem. <c>work_hours</c> é o jsonb CRU da jornada declarada da equipe
/// (mesmo formato do business_hours da organização) ou null; quando null,
/// <c>effective_work_hours</c> traz a jornada da ORGANIZAÇÃO, que é a que vale — a interface
/// mostra o valor efetivo e sinaliza a herança com <c>work_hours_inherited</c>.
/// </summary>
public record TeamResponse(
    Guid Id,
    string Name,
    /// <summary>Etiqueta legada de devices.tags que esta equipe representa; null = só por pessoa.</summary>
    string? Tag,
    JsonElement? WorkHours,
    JsonElement? EffectiveWorkHours,
    bool WorkHoursInherited,
    /// <summary>Pessoas vinculadas (windows_sid) — nunca uma lista de nomes na listagem.</summary>
    int MemberCount,
    /// <summary>Quantos apps têm regra de classificação PRÓPRIA desta equipe (F5).</summary>
    int TeamRuleCount);

/// <summary>GET /api/v1/teams. A jornada da organização vem junto para a tela explicar a herança.</summary>
public record TeamListResponse(
    IReadOnlyList<TeamResponse> Items,
    JsonElement? OrganizationWorkHours);

/// <summary>
/// POST /api/v1/teams. <c>work_hours</c> ausente ou null = a equipe herda a jornada da
/// organização (ausência é HERANÇA, nunca "sem jornada").
/// </summary>
public record CreateTeamRequest(string? Name, string? Tag, JsonElement? WorkHours);

/// <summary>
/// PATCH /api/v1/teams/{id} — parcial. Corpo cru no controller (JsonElement) para distinguir
/// "campo ausente" (não muda) de "campo: null" (limpa: tag some, work_hours volta a herdar).
/// Este record existe só como documentação do contrato.
/// </summary>
public record UpdateTeamRequest(string? Name, string? Tag, JsonElement? WorkHours);

/// <summary>Uma pessoa vinculada à equipe. O nome já vem resolvido pela mesclagem de people.</summary>
public record TeamMemberResponse(string WindowsSid, string DisplayName);

/// <summary>GET /api/v1/teams/{id}/members.</summary>
public record TeamMembersResponse(Guid TeamId, string Name, IReadOnlyList<TeamMemberResponse> Items);

/// <summary>
/// PUT /api/v1/teams/{id}/members — composição DECLARATIVA: a lista enviada passa a ser a
/// composição exata da equipe. Uma pessoa só pode estar em UMA equipe (PK de team_members),
/// então vincular alguém que estava em outra equipe a MOVE, e a resposta diz de onde ela veio.
/// </summary>
public record SetTeamMembersRequest(IReadOnlyList<string>? WindowsSids);

/// <summary>Resultado do PUT de composição: o que entrou, o que saiu e a reagregação enfileirada.</summary>
public record SetTeamMembersResponse(
    Guid TeamId,
    IReadOnlyList<TeamMemberResponse> Items,
    int Added,
    int Removed,
    /// <summary>Pares (dispositivo, dia) reenfileirados — a mudança de equipe muda os baldes.</summary>
    int ReaggregationEnqueued);

// ------------------------------------------------------------------------------- feriados

/// <summary>Um feriado da organização. <c>date</c> é yyyy-MM-dd, como todo dia da API.</summary>
public record HolidayResponse(string Date, string Name);

/// <summary>
/// GET /api/v1/organization/holidays?year=. <c>suggestions</c> são os pontos facultativos
/// federais do mesmo ano (Carnaval e Corpus Christi) que a semeadura NÃO cria: a organização que
/// os observa adiciona com um clique, a que trabalha nesses dias não tem o denominador furado.
/// </summary>
public record HolidayListResponse(
    int Year,
    IReadOnlyList<HolidayResponse> Items,
    IReadOnlyList<HolidayResponse> Suggestions);

/// <summary>POST /api/v1/organization/holidays — data yyyy-MM-dd + nome.</summary>
public record CreateHolidayRequest(string? Date, string? Name);

/// <summary>POST /api/v1/organization/holidays/seed — semeia o calendário nacional de N anos.</summary>
public record SeedHolidaysRequest(IReadOnlyList<int>? Years);

/// <summary>Resultado da semeadura (idempotente: só conta o que realmente entrou).</summary>
public record SeedHolidaysResponse(IReadOnlyList<int> Years, int Inserted);

// ------------------------------------------------------------------------------- capacidade

/// <summary>
/// A base do denominador da CAPACIDADE UTILIZADA de um escopo (a organização ou uma equipe) num
/// período: <c>ativo ÷ (jornada declarada × dias úteis × pessoas)</c>.
///
/// <c>business_days</c> JÁ vem sem os feriados — é essa a correção da F7. <c>holidays_excluded</c>
/// é quantos feriados caíram em dia útil do período, para a interface poder explicar por que o
/// denominador encolheu. <c>capacity_seconds_per_person</c> é o produto pronto
/// (jornada × dias úteis): multiplique pelo número de pessoas da equipe e divida o tempo ativo.
/// </summary>
public record CapacityScopeResponse(
    /// <summary>"organization" ou "team".</summary>
    string ScopeType,
    Guid? TeamId,
    string ScopeLabel,
    /// <summary>Etiqueta legada equivalente, quando a equipe declara uma (permite casar com ?tag).</summary>
    string? Tag,
    double DailyHours,
    int BusinessDays,
    int HolidaysExcluded,
    long CapacitySecondsPerPerson,
    /// <summary>Pessoas vinculadas à equipe (0 na linha da organização, que não tem composição).</summary>
    int MemberCount);

/// <summary>
/// GET /api/v1/teams/capacity?from&amp;to — a linha da organização mais uma por equipe.
///
/// É AQUI que o cartão "Equipes lado a lado" da Visão Geral passa a buscar o denominador da
/// capacidade (hoje ele o deriva sozinho do business_hours, sem feriados e sem jornada por
/// equipe, e o próprio rodapé do cartão avisa que o número está subestimado). Ver o comentário
/// no TeamsController.Capacity para o ponto exato de consumo.
/// </summary>
public record CapacityResponse(
    string From,
    string To,
    IReadOnlyList<CapacityScopeResponse> Scopes);
