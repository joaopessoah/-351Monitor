using System.Text.Json.Serialization;

namespace M351.Api.Contracts;

// ----- /api/v1/site-catalog -----
// Espelho deliberado de AppCatalogContracts: o cliente classifica SITE e APP no mesmo
// vocabulário, com as mesmas categorias (tabela `categories`) e a mesma noção de cobertura.
// Um shape diferente obrigaria o portal a ter duas telas em vez de uma tela parametrizada.

/// <summary>
/// Listagem do RECORTE DO TENANT sobre o catálogo global de sites: união dos sites com uso em
/// daily_site_usage do tenant com os sites mapeados por ele. Máximo de 500 itens, ordenados por
/// seconds_active_30d desc.
///
/// COBERTURA: mesma fração da tela de apps, medida em TEMPO e na MESMA janela de 30 dias da
/// reagregação sob demanda —
///   cobertura = (total_seconds_active − uncategorized_seconds_active) ÷ total_seconds_active.
/// </summary>
public sealed record SiteCatalogListResponse(
    IReadOnlyList<SiteCatalogItemResponse> Items,
    int UncategorizedCount,
    /// <summary>Tempo ativo dos últimos 30 dias em sites SEM categoria do tenant (o buraco).</summary>
    [property: JsonPropertyName("uncategorized_seconds_active")] long UncategorizedSecondsActive = 0,
    /// <summary>Tempo ativo dos últimos 30 dias em TODOS os sites do tenant (o denominador).</summary>
    [property: JsonPropertyName("total_seconds_active")] long TotalSecondsActive = 0);

/// <summary>
/// Um site do recorte do tenant. <c>domain</c> é o DOMÍNIO REGISTRÁVEL, a chave do catálogo
/// global — nunca URL, nunca caminho. <c>default_category</c> é a sugestão do dicionário
/// brasileiro (sites-br.csv); quem decide é o tenant, em <c>category</c>.
/// </summary>
public sealed record SiteCatalogItemResponse(
    Guid SiteId,
    string Domain,
    string DisplayName,
    string? CustomDisplayName,
    AppCategoryResponse? Category,
    string? DefaultCategory,
    [property: JsonPropertyName("seconds_active_30d")] long SecondsActive30d,
    [property: JsonPropertyName("device_count_30d")] int DeviceCount30d,
    /// <summary>"team", "organization" ou null — de onde veio a categoria acima (mesma régua dos apps).</summary>
    string? CategoryScope = null);

/// <summary>PUT declarativo do mapeamento de um site (mesma semântica do de apps).</summary>
public sealed record SetSiteCategoryRequest(Guid? CategoryId, string? CustomDisplayName, Guid? TeamId = null);

/// <summary>Resposta do PUT (estado do mapeamento após a escrita, sem métricas de uso).</summary>
public sealed record SiteCategoryMappingResponse(
    Guid SiteId,
    string Domain,
    string DisplayName,
    string? CustomDisplayName,
    AppCategoryResponse? Category,
    Guid? TeamId = null);

/// <summary>Um mapeamento do lote: site_id do catálogo global + category_id do TENANT.</summary>
public sealed record BatchSiteCategoryItem(Guid SiteId, Guid? CategoryId);

/// <summary>Corpo do PUT em lote; <c>team_id</c> aplica o lote inteiro no escopo de uma equipe.</summary>
public sealed record BatchSiteCategoryRequest(
    IReadOnlyList<BatchSiteCategoryItem>? Items, Guid? TeamId = null);

/// <summary>Resultado do lote (mesmo shape do de apps).</summary>
public sealed record BatchSiteCategoryResponse(
    int Applied,
    IReadOnlyList<SiteCategoryMappingResponse> Items,
    [property: JsonPropertyName("reaggregation_days")] int ReaggregationDays);
