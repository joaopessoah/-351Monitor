using System.Text.Json.Serialization;

namespace M351.Api.Contracts;

// ----- /api/v1/app-catalog (F3.3, Seção 7.4) -----

/// <summary>
/// Listagem do RECORTE DO TENANT sobre o catálogo global (decisão documentada): união dos
/// apps com uso em daily_app_usage do tenant com os apps mapeados em tenant_app_categories
/// do tenant. Máximo de 500 itens, ordenados por seconds_active_30d desc.
/// uncategorized_count é do recorte INTEIRO (badge "N apps sem categoria"), ignorando q
/// e o filtro uncategorized.
/// </summary>
public sealed record AppCatalogListResponse(
    IReadOnlyList<AppCatalogItemResponse> Items,
    int UncategorizedCount);

/// <summary>
/// Métricas da janela dos últimos 30 dias no fuso do tenant; category null = não categorizado.
/// JsonPropertyName explícito nos campos _30d: a SnakeCaseLower do .NET não separa dígito de
/// letra ("SecondsActive30d" viraria "seconds_active30d", fora do contrato).
/// </summary>
public sealed record AppCatalogItemResponse(
    Guid AppId,
    string ProcessName,
    string DisplayName,
    string? CustomDisplayName,
    AppCategoryResponse? Category,
    [property: JsonPropertyName("seconds_active_30d")] long SecondsActive30d,
    [property: JsonPropertyName("device_count_30d")] int DeviceCount30d);

/// <summary>Categoria do TENANT referenciada por um app (shape compartilhado catálogo/relatórios).</summary>
public sealed record AppCategoryResponse(Guid Id, string Name, int Classification, string? Color);

/// <summary>
/// PUT declarativo do mapeamento: category_id null = desmapear (a linha inteira sai,
/// inclusive custom_display_name); custom_display_name ausente ou null = sem nome custom.
/// </summary>
public sealed record SetAppCategoryRequest(Guid? CategoryId, string? CustomDisplayName);

/// <summary>Resposta do PUT (estado do mapeamento após a escrita, sem métricas de uso).</summary>
public sealed record AppCategoryMappingResponse(
    Guid AppId,
    string ProcessName,
    string DisplayName,
    string? CustomDisplayName,
    AppCategoryResponse? Category);

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
