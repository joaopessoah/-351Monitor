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
