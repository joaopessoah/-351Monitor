using System.Text.Json;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Aggregation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// CRUD de categorias do TENANT (F3.3, Seção 7.4). Vocabulário fixo (Princípio 8):
/// classification 1 = Relacionado ao trabalho, 0 = Neutro, -1 = Não relacionado ao trabalho;
/// app sem mapeamento = Não categorizado. JAMAIS "produtivo/improdutivo".
///
/// Mudança de classification (PATCH) e DELETE disparam a reagregação dos últimos 30 dias
/// (ReaggregationRequester) + audit update_category — reagregação E audit na MESMA transação
/// da mutação (padrão atômico dos writes do produto). O DELETE também REMOVE os mapeamentos
/// tenant_app_categories que referenciam a categoria — os apps voltam a "Não categorizados"
/// (decisão documentada para o silêncio da spec; a alternativa de bloquear o delete com 409
/// pioraria o fluxo da tela de configurações).
/// </summary>
[Route("api/v1/categories")]
[Authorize] // Viewer+ no GET; rotas de escrita exigem AdminPlus
public class CategoriesController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    private const int MaxNameLength = 100;
    private const int MaxColorLength = 32;
    private static readonly int[] ValidClassifications = [1, 0, -1];

    /// <summary>GET /api/v1/categories (Viewer): ordenado por classification desc, name asc.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<CategoryRow>(new CommandDefinition(
            """
            SELECT c.id, c.name, c.classification, c.color,
                   (SELECT count(*)::int FROM tenant_app_categories tac
                     WHERE tac.tenant_id = c.tenant_id AND tac.category_id = c.id) AS app_count
            FROM categories c
            WHERE c.tenant_id = @TenantId
            ORDER BY c.classification DESC, c.name
            """,
            new { TenantId = tenantId }, cancellationToken: ct));

        return Ok(new CategoryListResponse(
            rows.Select(r => new CategoryResponse(r.Id, r.Name, r.Classification, r.Color, r.AppCount)).ToList()));
    }

    /// <summary>
    /// POST /api/v1/categories (Admin): 201 com o objeto; nome duplicado no tenant responde 409.
    /// Criar categoria não reagrega nem audita (nenhum app aponta para ela ainda).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Create([FromBody] CreateCategoryRequest request, CancellationToken ct)
    {
        var invalid = ValidatePayload(request.Name, request.Classification, request.Color, requireAll: true);
        if (invalid is not null) return invalid;

        var tenantId = Auth.CurrentUser.TenantId(User);
        var id = Uuid7.NewUuid7();
        var name = request.Name!.Trim();
        var color = NormalizeColor(request.Color);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // ON CONFLICT DO NOTHING + contagem: detecção de duplicado sem corrida com outro POST
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO categories (id, tenant_id, name, classification, color)
            VALUES (@Id, @TenantId, @Name, @Classification, @Color)
            ON CONFLICT (tenant_id, name) DO NOTHING
            """,
            new { Id = id, TenantId = tenantId, Name = name, Classification = (short)request.Classification!.Value, Color = color },
            cancellationToken: ct));
        if (inserted == 0)
        {
            return ProblemResponse(StatusCodes.Status409Conflict, "Já existe uma categoria com esse nome.");
        }

        return Created($"/api/v1/categories/{id}",
            new CategoryResponse(id, name, request.Classification.Value, color, 0));
    }

    /// <summary>
    /// PATCH /api/v1/categories/{id} (Admin): atualização parcial. SE classification mudou,
    /// reagrega os últimos 30 dias e audita update_category. 404 para inexistente/cross-tenant.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCategoryRequest request, CancellationToken ct)
    {
        var invalid = ValidatePayload(request.Name, request.Classification, request.Color, requireAll: false);
        if (invalid is not null) return invalid;

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var current = await connection.QuerySingleOrDefaultAsync<CategoryRow>(new CommandDefinition(
            """
            SELECT c.id, c.name, c.classification, c.color,
                   (SELECT count(*)::int FROM tenant_app_categories tac
                     WHERE tac.tenant_id = c.tenant_id AND tac.category_id = c.id) AS app_count
            FROM categories c
            WHERE c.tenant_id = @TenantId AND c.id = @Id
            """,
            new { TenantId = tenantId, Id = id }, cancellationToken: ct));
        if (current is null) return NotFoundProblem(); // inexistente OU de outro tenant

        var newName = request.Name?.Trim() ?? current.Name;
        var newClassification = request.Classification ?? current.Classification;
        var newColor = request.Color is null ? current.Color : NormalizeColor(request.Color);
        var classificationChanged = newClassification != current.Classification;

        await using var tx = await connection.BeginTransactionAsync(ct);

        // rename para nome já usado por OUTRA categoria do tenant: mesma regra do POST (409)
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE categories
            SET name = @Name, classification = @Classification, color = @Color
            WHERE tenant_id = @TenantId AND id = @Id
              AND NOT EXISTS (
                  SELECT 1 FROM categories d
                  WHERE d.tenant_id = @TenantId AND d.name = @Name AND d.id <> @Id)
            """,
            new { TenantId = tenantId, Id = id, Name = newName, Classification = (short)newClassification, Color = newColor },
            transaction: tx, cancellationToken: ct));
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return ProblemResponse(StatusCodes.Status409Conflict, "Já existe uma categoria com esse nome.");
        }

        if (classificationChanged)
        {
            // a troca de classification muda os baldes dos agregados: reagrega 30 dias
            await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);

            // trilha na MESMA transação da mutação: a mudança jamais persiste sem audit
            await AuditWriter.AddInTransactionAsync(connection, tx, tenantId, AuditActions.UpdateCategory,
                actorUserId: Auth.CurrentUser.UserId(User),
                targetType: "category", targetId: id,
                detailJson: JsonSerializer.Serialize(new
                {
                    name = newName,
                    from_classification = current.Classification,
                    to_classification = newClassification,
                }), ct: ct);
        }

        await tx.CommitAsync(ct);

        return Ok(new CategoryResponse(id, newName, newClassification, newColor, current.AppCount));
    }

    /// <summary>
    /// DELETE /api/v1/categories/{id} (Admin): 204. Remove os mapeamentos que referenciam a
    /// categoria (apps viram "Não categorizados") + reagrega 30 dias + audit update_category.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var current = await connection.QuerySingleOrDefaultAsync<CategoryRow>(new CommandDefinition(
            "SELECT c.id, c.name, c.classification, c.color, 0 AS app_count FROM categories c WHERE c.tenant_id = @TenantId AND c.id = @Id",
            new { TenantId = tenantId, Id = id }, cancellationToken: ct));
        if (current is null) return NotFoundProblem(); // inexistente OU de outro tenant

        await using var tx = await connection.BeginTransactionAsync(ct);

        // mapeamentos PRIMEIRO (FK tenant_app_categories.category_id → categories.id)
        var unmapped = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tenant_app_categories WHERE tenant_id = @TenantId AND category_id = @Id",
            new { TenantId = tenantId, Id = id }, transaction: tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM categories WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = id }, transaction: tx, cancellationToken: ct));

        // os apps desmapeados voltam ao balde neutro ("Não categorizado") nos últimos 30 dias
        await ReaggregationRequester.RequestLast30DaysAsync(connection, tx, tenantId, ct);

        // trilha na MESMA transação da mutação: a exclusão jamais persiste sem audit
        await AuditWriter.AddInTransactionAsync(connection, tx, tenantId, AuditActions.UpdateCategory,
            actorUserId: Auth.CurrentUser.UserId(User),
            targetType: "category", targetId: id,
            detailJson: JsonSerializer.Serialize(new
            {
                name = current.Name,
                deleted = true,
                unmapped_apps = unmapped,
            }), ct: ct);

        await tx.CommitAsync(ct);

        return NoContent();
    }

    // ------------------------------------------------------------ helpers
    /// <summary>Validações comuns POST/PATCH (no PATCH só os campos presentes são validados).</summary>
    private ObjectResult? ValidatePayload(string? name, int? classification, string? color, bool requireAll)
    {
        if (requireAll && string.IsNullOrWhiteSpace(name))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Informe o nome da categoria.");
        if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength))
            return ProblemResponse(StatusCodes.Status400BadRequest, $"Nome inválido (1 a {MaxNameLength} caracteres).");

        if (requireAll && classification is null)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Informe a classificação.");
        if (classification is not null && !ValidClassifications.Contains(classification.Value))
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Classificação inválida. Use 1 (Relacionado ao trabalho), 0 (Neutro) ou -1 (Não relacionado ao trabalho).");

        if (color is not null && color.Trim().Length > MaxColorLength)
            return ProblemResponse(StatusCodes.Status400BadRequest, $"Cor inválida (máximo {MaxColorLength} caracteres).");

        return null;
    }

    private static string? NormalizeColor(string? color) =>
        string.IsNullOrWhiteSpace(color) ? null : color.Trim();

    private sealed record CategoryRow(Guid Id, string Name, short Classification, string? Color, int AppCount);
}
