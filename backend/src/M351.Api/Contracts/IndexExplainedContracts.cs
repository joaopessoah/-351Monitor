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
    IndexDimensionResponse ByApp,
    IndexDimensionResponse ByTeam,
    IndexDimensionResponse ByDay);

/// <summary>
/// Uma dimensão da decomposição, com o delta que ELA explica.
///
/// POR QUE CADA DIMENSÃO CARREGA O PRÓPRIO DELTA: <c>ByTeam</c> e <c>ByDay</c> particionam
/// exatamente <c>daily_device_summaries</c>, então o delta delas é o do cabeçalho. <c>ByApp</c>
/// lê <c>daily_app_usage</c> e reaplica a classificação NA LEITURA — se alguém mudou a
/// classificação e não recalculou o histórico, os baldes agregados guardam a regra ANTIGA e a
/// leitura usa a NOVA, e os dois lados passam a ter denominadores diferentes. Nesse caso o
/// delta da dimensão difere do cabeçalho, e é ele que as parcelas fecham.
///
/// A promessa "as parcelas somam" continua verdadeira SEMPRE — só passa a ser sobre o delta da
/// própria dimensão. Quando ele diverge do cabeçalho, <paramref name="Divergence"/> diz o motivo
/// e o caminho para resolver, em vez de a tela inventar uma explicação.
/// </summary>
public sealed record IndexDimensionResponse(
    double? DeltaPoints,
    string? Divergence,
    IReadOnlyList<IndexContributionResponse> Items);

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
