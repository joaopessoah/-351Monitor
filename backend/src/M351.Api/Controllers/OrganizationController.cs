using System.Globalization;
using System.Text.Json;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Controllers;

/// <summary>
/// /api/v1/organization (F4.8, Seção 8.8) — leitura e edição da config de transparência da org.
///
/// GET (PolicyAccess — qualquer papel autenticado, base [Authorize]): a org do token (filtro global
/// por tenant garante que é a própria — Princípio 4, igual ao /me) com name/slug/timezone/
/// business_hours + os campos editáveis (finalidade_declarada, contato_dpo, data_vigencia).
///
/// PATCH (PolicyAdminPlus — Owner/Admin; Viewer → 403): atualização PARCIAL dos campos de
/// transparência (e business_hours). Corpo cru (JsonElement) para distinguir "campo ausente" (não
/// muda) de "campo: null" (limpa). Mutação EF + trilha update_privacy_config (detail de→para por
/// campo) no MESMO SaveChanges — padrão do PATCH /devices: a mudança jamais persiste sem a trilha
/// (Seção 9.5: mudança de config de privacidade exige a trilha de→para).
/// </summary>
[ApiController]
[Route("api/v1/organization")]
[Authorize] // GET: Viewer+; PATCH refina para AdminPlus
public class OrganizationController(M351DbContext db, AuditWriter audit) : ApiControllerBase
{
    private const int MaxTextLength = 1000;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        // filtro global por tenant garante a org do token (FirstAsync — sempre existe)
        var org = await db.Organizations.FirstAsync(ct);

        return Ok(new OrganizationResponse(
            org.Name, org.Slug, org.Timezone, ParseBusinessHours(org.BusinessHours),
            org.FinalidadeDeclarada, org.ContatoDpo, org.DataVigencia));
    }

    [HttpPatch]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)] // Viewer → 403
    public async Task<IActionResult> Patch([FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Corpo inválido: envie um objeto JSON.");
        }

        // ----- texto: ausente = não muda; null = limpa; string = define (trim, limite) -----
        var finalidade = ParseOptionalText(body, "finalidade_declarada", out var hasFinalidade, out var finalidadeError);
        if (finalidadeError is not null) return finalidadeError;

        var dpo = ParseOptionalText(body, "contato_dpo", out var hasDpo, out var dpoError);
        if (dpoError is not null) return dpoError;

        // ----- data_vigencia: ausente = não muda; null = limpa; "yyyy-MM-dd" = define -----
        var hasVigencia = body.TryGetProperty("data_vigencia", out var vigenciaEl);
        DateOnly? vigencia = null;
        if (hasVigencia && vigenciaEl.ValueKind != JsonValueKind.Null)
        {
            if (vigenciaEl.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(vigenciaEl.GetString(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "data_vigencia deve ser uma data no formato yyyy-MM-dd ou null.");
            }

            vigencia = parsed;
        }

        // ----- business_hours: ausente = não muda; null = limpa; objeto = substitui (jsonb cru) -----
        var hasBusinessHours = body.TryGetProperty("business_hours", out var businessHoursEl);
        string? businessHours = null;
        if (hasBusinessHours && businessHoursEl.ValueKind != JsonValueKind.Null)
        {
            if (businessHoursEl.ValueKind != JsonValueKind.Object)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "business_hours deve ser um objeto JSON ou null.");
            }

            businessHours = businessHoursEl.GetRawText();
        }

        var org = await db.Organizations.FirstAsync(ct);

        // aplica somente o que mudou e registra o de→para por campo (detail do audit)
        var changes = new Dictionary<string, object?>();
        if (hasFinalidade && org.FinalidadeDeclarada != finalidade)
        {
            changes["finalidade_declarada"] = new { from = org.FinalidadeDeclarada, to = finalidade };
            org.FinalidadeDeclarada = finalidade;
        }

        if (hasDpo && org.ContatoDpo != dpo)
        {
            changes["contato_dpo"] = new { from = org.ContatoDpo, to = dpo };
            org.ContatoDpo = dpo;
        }

        if (hasVigencia && org.DataVigencia != vigencia)
        {
            changes["data_vigencia"] = new
            {
                from = org.DataVigencia?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to = vigencia?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            org.DataVigencia = vigencia;
        }

        if (hasBusinessHours && !JsonEqual(org.BusinessHours, businessHours))
        {
            changes["business_hours"] = new { from = org.BusinessHours, to = businessHours };
            org.BusinessHours = businessHours;
        }

        if (changes.Count > 0)
        {
            audit.Add(CurrentUser.TenantId(User), AuditActions.UpdatePrivacyConfig, CurrentUser.UserId(User),
                HttpContext.Connection.RemoteIpAddress, targetType: "organization", targetId: org.Id,
                detailJson: JsonSerializer.Serialize(changes));

            await db.SaveChangesAsync(ct);
        }

        return Ok(new OrganizationResponse(
            org.Name, org.Slug, org.Timezone, ParseBusinessHours(org.BusinessHours),
            org.FinalidadeDeclarada, org.ContatoDpo, org.DataVigencia));
    }

    /// <summary>
    /// F5 — Seção 8.3 passo 4: o checklist de primeiros passos é dispensável e o estado é da
    /// ORGANIZAÇÃO (persistido no servidor, não em localStorage). Idempotente. Estado de UI
    /// puro, sem dado pessoal: deliberadamente fora da trilha de auditoria.
    /// </summary>
    [HttpPost("onboarding-checklist/dismiss")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> DismissOnboardingChecklist(CancellationToken ct)
    {
        var org = await db.Organizations.FirstAsync(ct);
        if (org.OnboardingChecklistDismissedAt is null)
        {
            org.OnboardingChecklistDismissedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    /// <summary>Reabre o checklist de primeiros passos (link em Configurações).</summary>
    [HttpDelete("onboarding-checklist/dismiss")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> RestoreOnboardingChecklist(CancellationToken ct)
    {
        var org = await db.Organizations.FirstAsync(ct);
        if (org.OnboardingChecklistDismissedAt is not null)
        {
            org.OnboardingChecklistDismissedAt = null;
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Lê um campo de texto opcional do corpo: ausente (hasField=false, não muda); null (define
    /// null, limpa); string (trim + limite de tamanho). Retorna o valor; o erro 400 vai por out.
    /// </summary>
    private string? ParseOptionalText(JsonElement body, string field, out bool hasField, out ObjectResult? error)
    {
        error = null;
        hasField = body.TryGetProperty(field, out var el);
        if (!hasField || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            error = ProblemResponse(StatusCodes.Status400BadRequest, $"{field} deve ser uma string ou null.");
            return null;
        }

        var value = el.GetString()!.Trim();
        if (value.Length == 0)
        {
            // string vazia equivale a limpar (null) — evita persistir "" sem sentido
            return null;
        }

        if (value.Length > MaxTextLength)
        {
            error = ProblemResponse(StatusCodes.Status400BadRequest,
                $"{field} excede o limite de {MaxTextLength} caracteres.");
            return null;
        }

        return value;
    }

    private static JsonElement? ParseBusinessHours(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Compara dois jsonb por forma canônica (null e ambos ausentes contam como iguais). Reserializa
    /// cada lado para um texto canônico — assim diferenças só de espaço/ordem-de-chaves não contam
    /// como mudança (evita uma trilha update_privacy_config espúria e um bump de config inútil).
    /// </summary>
    private static bool JsonEqual(string? a, string? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return Canonical(a) == Canonical(b);
    }

    private static string Canonical(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }
}
