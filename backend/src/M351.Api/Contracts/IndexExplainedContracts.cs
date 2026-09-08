namespace M351.Api.Contracts;

/// <summary>
/// "Por que o índice mudou" (item 1 do estudo): a variação do índice entre o período pedido e o
/// imediatamente anterior, de mesma duração, REPARTIDA entre aplicativos, equipes e dias.
///
/// <paramref name="DeltaPoints"/> vem em PONTOS do índice (escala 0–100), com sinal. As três
/// listas trazem as maiores contribuições em módulo, com o sinal preservado — a lista é "onde a
/// variação está", jamais um pódio.
///
/// <paramref name="Unavailable"/> preenchido significa que não há variação a explicar (falta
/// denominador num dos períodos): nesse caso os indicadores vêm null e as três listas vazias.
/// Zero nunca substitui "não sei" — a mesma régua do índice e da cobertura.
/// </summary>
public sealed record IndexExplainedResponse(
    OverviewPeriodResponse Period,
    OverviewPeriodResponse PreviousPeriod,
    double? Index,
    double? PreviousIndex,
    double? DeltaPoints,
    string? Unavailable,
    IReadOnlyList<IndexContributionResponse> ByApp,
    IReadOnlyList<IndexContributionResponse> ByTeam,
    IReadOnlyList<IndexContributionResponse> ByDay);

/// <summary>
/// A contribuição de UM membro (aplicativo, equipe ou dia) para a variação do índice.
///
/// <paramref name="Points"/> é a parcela em pontos, com sinal. NÃO é "o índice deste membro":
/// um aplicativo improdutivo que cresceu derruba o índice sem ter um único segundo produtivo,
/// porque engorda o denominador. Os segundos dos dois períodos viajam junto para a tela dizer
/// "mais 6 h" ao lado de "−4 pontos" sem uma segunda requisição.
///
/// "Classified" é produtivo + neutro + improdutivo. Tempo SEM classificação fica fora dos dois
/// lados, porque também está fora da fórmula do índice (decisão 4 do spec).
/// </summary>
public sealed record IndexContributionResponse(
    string Key,
    string Label,
    double Points,
    long SecondsWorkRelated,
    long SecondsWorkRelatedPrevious,
    long SecondsClassified,
    long SecondsClassifiedPrevious);
