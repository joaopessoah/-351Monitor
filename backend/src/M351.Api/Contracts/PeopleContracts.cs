namespace M351.Api.Contracts;

// ----- GET /api/v1/people (F6 — a lista de colaboradores) -----

/// <summary>
/// Uma linha por PESSOA do tenant no período. Identidade = windows_sid resolvido pela
/// mesclagem de people, então a mesma pessoa em duas máquinas é UMA linha (o que
/// /device-users, chaveado por (device, sid), não conseguia prometer).
///
/// Paginado no padrão dos relatórios; total é a contagem de pessoas do período inteiro,
/// independente da página.
/// </summary>
public sealed record PeopleReportResponse(
    IReadOnlyList<PersonRowResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// display_name JÁ resolvido pelo servidor: apelido de people, senão o nome amigável da lane
/// mais recente, senão o windows_username mais recente, senão o próprio SID. Renderize este
/// campo — nunca reimplemente a regra no cliente.
///
/// productivity_index e classification_coverage seguem a fórmula ÚNICA do overview
/// (decisão 4 do spec): índice = produtivo ÷ classificado; cobertura = (ativo − sem
/// classificação) ÷ ativo. null quando o denominador é zero, nunca 0.
/// </summary>
public sealed record PersonRowResponse(
    string WindowsSid,
    string DisplayName,
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated,
    long SecondsUnclassified,
    double? ProductivityIndex,
    double? ClassificationCoverage,
    int DeviceCount,
    int DaysWithData,
    IReadOnlyList<string> Teams);

/// <summary>
/// PATCH /api/v1/people/{sid}: apelido e mesclagem. Campo ausente no corpo não muda nada;
/// display_name vazio limpa o apelido; merged_into_sid vazio desfaz a mesclagem.
/// </summary>
public sealed record PersonPatchRequest(string? DisplayName, string? MergedIntoSid);

/// <summary>Estado corrente da pessoa depois do PATCH.</summary>
public sealed record PersonPatchResponse(string WindowsSid, string? DisplayName, string? MergedIntoSid);

// ----- GET /api/v1/people/daily (mapa do mês DIÁRIO da Linha do Tempo) -----

/// <summary>
/// Uma linha por (PESSOA, DIA) no período. Existe porque o mapa do mês desenha pessoa × DIA e
/// a listagem de /people só sabe agregar o período INTEIRO: pintar 31 colunas com ela custaria
/// uma requisição por dia (ordem de 150 chamadas numa tela). Aqui é UMA consulta.
///
/// Sem paginação de propósito: o teto é o produto do recorte (92 dias × pessoas do tenant) e a
/// régua de janela já é a mesma dos outros endpoints históricos — paginar por página cortaria
/// pessoa no meio do mês e o cliente teria de recolar linhas.
/// </summary>
public sealed record PeopleDailyResponse(IReadOnlyList<PersonDayResponse> Items);

/// <summary>
/// O dia de UMA pessoa. Identidade e nome seguem exatamente a régua da listagem: windows_sid
/// resolvido por people.merged_into_sid (a mesma pessoa em duas máquinas soma numa linha só),
/// lane-máquina fora, display_name JÁ resolvido pelo servidor (apelido &gt; nome da lane &gt;
/// usuário do Windows &gt; SID). Renderize display_name — não reimplemente a regra no cliente.
///
/// productivity_index usa a fórmula ÚNICA do overview: produtivo ÷ classificado. É null quando
/// não houve NENHUM tempo classificado no dia — null é "não sei", nunca 0%. Só os quatro baldes
/// que o mapa do mês desenha vêm aqui; quem precisa dos seis (produtivo/neutro/improdutivo/
/// bloqueado) continua na listagem agregada de /people.
/// </summary>
public sealed record PersonDayResponse(
    string WindowsSid,
    string DisplayName,
    // Date: dia no fuso do tenant em yyyy-MM-dd (summary_date JÁ é o dia local do tenant).
    string Date,
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsUnclassified,
    double? ProductivityIndex);
