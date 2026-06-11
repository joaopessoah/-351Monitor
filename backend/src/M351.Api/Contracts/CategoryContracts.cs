namespace M351.Api.Contracts;

// ----- /api/v1/categories (F3.3, Seção 7.4) -----
// Vocabulário fixo (Princípio 8): classification 1 = Relacionado ao trabalho,
// 0 = Neutro, -1 = Não relacionado ao trabalho. JAMAIS "produtivo/improdutivo".

public sealed record CategoryListResponse(IReadOnlyList<CategoryResponse> Items);

/// <summary>app_count = mapeamentos do TENANT (tenant_app_categories) apontando para a categoria.</summary>
public sealed record CategoryResponse(
    Guid Id,
    string Name,
    int Classification,
    string? Color,
    int AppCount);

public sealed record CreateCategoryRequest(string? Name, int? Classification, string? Color);

/// <summary>
/// PATCH parcial: só os campos presentes (não-null) são alterados. Limpar color via
/// PATCH não é suportado (decisão documentada: o portal sempre envia uma cor).
/// </summary>
public sealed record UpdateCategoryRequest(string? Name, int? Classification, string? Color);
