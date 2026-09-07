using System.Text.Json.Serialization;

namespace M351.Api.Contracts;

// ----- /api/v1/app-catalog (F3.3, Seção 7.4) -----

/// <summary>
/// Listagem do RECORTE DO TENANT sobre o catálogo global (decisão documentada): união dos
/// apps com uso em daily_app_usage do tenant com os apps mapeados em tenant_app_categories
/// do tenant. Máximo de 500 itens, ordenados por seconds_active_30d desc.
/// uncategorized_count é do recorte INTEIRO (badge "N apps sem categoria"), ignorando q
/// e o filtro uncategorized.
///
/// F6 — COBERTURA DA CLASSIFICAÇÃO como KPI da curadoria (decisão 4 do spec de 07/09/2026):
/// contar apps sem categoria não diz nada sobre o tamanho do buraco (100 apps sem categoria
/// podem valer 2 minutos ou 200 horas). Os dois campos de segundos dão o buraco em TEMPO, que
/// é o denominador do indicador exibido no portal:
///   cobertura = (total_seconds_active − uncategorized_seconds_active) ÷ total_seconds_active.
///
/// JANELA DE 30 DIAS, e por quê: é a mesma janela das métricas por item (seconds_active_30d)
/// e a mesma da reagregação automática que a curadoria dispara — então o número do cabeçalho
/// fecha com a soma das linhas da tela, e a fila mede o efeito que aplicar a categoria terá de
/// fato nos dados recalculados. Janela maior mediria um passado que a curadoria de hoje não
/// corrige sem a reagregação sob demanda (POST /reaggregation).
/// </summary>
public sealed record AppCatalogListResponse(
    IReadOnlyList<AppCatalogItemResponse> Items,
    int UncategorizedCount,
    /// <summary>Tempo ativo dos últimos 30 dias em apps SEM categoria do tenant (o buraco).</summary>
    [property: JsonPropertyName("uncategorized_seconds_active")] long UncategorizedSecondsActive = 0,
    /// <summary>Tempo ativo dos últimos 30 dias em TODOS os apps do tenant (o denominador).</summary>
    [property: JsonPropertyName("total_seconds_active")] long TotalSecondsActive = 0);

/// <summary>
/// Métricas da janela dos últimos 30 dias no fuso do tenant; category null = não categorizado.
/// JsonPropertyName explícito nos campos _30d: a SnakeCaseLower do .NET não separa dígito de
/// letra ("SecondsActive30d" viraria "seconds_active30d", fora do contrato).
///
/// default_category (F1.1) é a SUGESTÃO do dicionário brasileiro (app_catalog.default_category,
/// nome canônico de categoria), null para app sem curadoria. É só sugestão: quem decide é o
/// tenant em category. O portal usa os dois para oferecer "aplicar sugestões" em lote
/// (PUT /app-catalog/categories/batch).
/// </summary>
public sealed record AppCatalogItemResponse(
    Guid AppId,
    string ProcessName,
    string DisplayName,
    string? CustomDisplayName,
    AppCategoryResponse? Category,
    string? DefaultCategory,
    [property: JsonPropertyName("seconds_active_30d")] long SecondsActive30d,
    [property: JsonPropertyName("device_count_30d")] int DeviceCount30d,
    /// <summary>
    /// F5 — de ONDE veio a categoria acima: "team" (regra própria da equipe consultada),
    /// "organization" (regra geral, HERDADA quando se consulta uma equipe) ou null (nenhuma
    /// regra: o app está sem classificação). Sem <c>?team_id</c> só existem "organization" e
    /// null. É o que permite à tela dizer "herdado da organização" em vez de fingir que a
    /// equipe declarou algo que não declarou.
    /// </summary>
    string? CategoryScope = null);

/// <summary>Categoria do TENANT referenciada por um app (shape compartilhado catálogo/relatórios).</summary>
public sealed record AppCategoryResponse(Guid Id, string Name, int Classification, string? Color);

/// <summary>
/// PUT declarativo do mapeamento: category_id null = desmapear (a linha inteira sai,
/// inclusive custom_display_name); custom_display_name ausente ou null = sem nome custom.
///
/// F5 — <c>team_id</c> escolhe o ESCOPO da regra: ausente ou null grava a regra da
/// ORGANIZAÇÃO (comportamento de sempre, nada mudou para quem não manda o campo); com um
/// team_id, grava a regra específica DAQUELA EQUIPE, que vence a geral para as pessoas dela.
/// Desmapear no escopo de equipe (category_id null) remove só a regra da equipe — o app volta
/// a HERDAR a regra da organização, não vira "sem classificação".
/// <c>custom_display_name</c> é da organização e é ignorado no escopo de equipe: o nome
/// exibido de um app não muda de time para time.
/// </summary>
public sealed record SetAppCategoryRequest(Guid? CategoryId, string? CustomDisplayName, Guid? TeamId = null);

/// <summary>Resposta do PUT (estado do mapeamento após a escrita, sem métricas de uso).</summary>
public sealed record AppCategoryMappingResponse(
    Guid AppId,
    string ProcessName,
    string DisplayName,
    string? CustomDisplayName,
    AppCategoryResponse? Category,
    /// <summary>F5 — escopo em que a regra foi escrita: null (organização) ou o id da equipe.</summary>
    Guid? TeamId = null);

// ----- PUT /api/v1/app-catalog/categories/batch (F1.1, aplicação em LOTE das sugestões) -----

/// <summary>
/// Um mapeamento do lote: app_id do catálogo global + category_id do TENANT
/// (null = desmapear, mesma semântica do PUT individual).
/// </summary>
public sealed record BatchAppCategoryItem(Guid AppId, Guid? CategoryId);

/// <summary>
/// Corpo do PUT em lote: a lista de mapeamentos a aplicar de uma vez. <c>team_id</c> (F5)
/// aplica o lote inteiro no escopo de UMA equipe; ausente = escopo da organização, como sempre.
/// </summary>
public sealed record BatchAppCategoryRequest(
    IReadOnlyList<BatchAppCategoryItem>? Items, Guid? TeamId = null);

/// <summary>
/// Resultado do lote: applied = quantidade de mapeamentos escritos; items = estado final de
/// cada um (mesmo shape do PUT individual). reaggregation_days = linhas enfileiradas em
/// dirty_days pela ÚNICA reagregação do lote (contra N reagregações de N PUTs individuais).
/// </summary>
public sealed record BatchAppCategoryResponse(
    int Applied,
    IReadOnlyList<AppCategoryMappingResponse> Items,
    [property: JsonPropertyName("reaggregation_days")] int ReaggregationDays);

// ----- GET /api/v1/app-catalog/{appId}/titles (drill-down de títulos, F3.3) -----

/// <summary>
/// Top 20 títulos por tempo ativo do app no período; masked_seconds = soma dos intervalos
/// active com window_title NULL (mascarado pela política de privacidade ou não capturado);
/// total_seconds = todos os intervalos active do app no período.
/// </summary>
public sealed record AppTitlesResponse(
    IReadOnlyList<AppTitleResponse> Items,
    long MaskedSeconds,
    long TotalSeconds);

public sealed record AppTitleResponse(string WindowTitle, long SecondsActive);
