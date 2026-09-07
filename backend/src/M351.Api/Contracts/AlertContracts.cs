namespace M351.Api.Contracts;

/// <summary>
/// Um alerta de gestão VIVO (F6, seção 4 do spec de 07/09/2026), já traduzido para a tela.
///
/// O motor grava números em <c>management_alerts.detail</c>; o controller monta aqui o
/// <see cref="Title"/> e o <see cref="Context"/> em português, para o VOCABULÁRIO ter uma
/// fonte única: sempre VARIAÇÃO e CONTEXTO, nunca julgamento ("Equipe Comercial com 18%
/// menos tempo ativo que a média das 4 semanas anteriores"), e jamais um ranking.
///
/// <see cref="Detail"/> vai junto como objeto cru para a tela poder exibir número exato sem
/// reparsear a frase — e para nenhum consumidor precisar recalcular índice ou cobertura, que
/// vêm sempre do servidor.
/// </summary>
/// <param name="Kind">Regra que disparou (constantes de <c>ManagementAlertService</c>).</param>
/// <param name="ScopeType">organization, team, person ou device.</param>
/// <param name="ScopeKey">Etiqueta da equipe, windows_sid, uuid do dispositivo ou "-".</param>
/// <param name="ScopeLabel">O escopo como se lê na tela (nome da pessoa, nome do device).</param>
/// <param name="Severity">"atencao" ou "informativo" — faixa de cor do cartão, sem juízo.</param>
/// <param name="Title">Frase curta do alerta, com o número que o gerou.</param>
/// <param name="Context">O porquê e a régua usada, em uma frase.</param>
/// <param name="Action">Rótulo do link sugerido ("Classificar aplicativos").</param>
/// <param name="Link">Rota do portal que resolve o alerta (nunca URL absoluta).</param>
/// <param name="FirstSeenAt">Quando o alerta nasceu — a idade que a tela mostra.</param>
/// <param name="LastSeenAt">Última avaliação em que ainda estava valendo.</param>
/// <param name="Detail">Os números crus da regra (jsonb do motor).</param>
public record AlertItemResponse(
    string Kind,
    string ScopeType,
    string ScopeKey,
    string? ScopeLabel,
    string Severity,
    string Title,
    string Context,
    string Action,
    string Link,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    Dictionary<string, object?> Detail);

/// <summary>
/// Resposta de <c>GET /api/v1/alerts</c>.
///
/// <paramref name="PersonAlertsEnabled"/> é o opt-in da decisão 5: com <c>false</c> as duas
/// regras de escopo pessoa (dias longos e atividade fora do horário) NÃO são avaliadas, e a
/// tela precisa dizer isso — omitir a informação faria o gestor concluir que ninguém trabalha
/// fora do horário, quando na verdade ninguém está medindo.
///
/// <paramref name="PlanIncludesAlerts"/> separa "nenhum alerta porque está tudo bem" de
/// "nenhum alerta porque o plano não inclui a feature": a diferença entre um elogio e um
/// upgrade, e o card não pode confundir os dois.
/// </summary>
public record AlertsResponse(
    List<AlertItemResponse> Items,
    bool PersonAlertsEnabled,
    bool PlanIncludesAlerts,
    DateTimeOffset? EvaluatedAt);
