using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using M351.Api.Agent;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain.Entities;
using M351.Domain.Privacy;
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
            org.FinalidadeDeclarada, org.ContatoDpo, org.DataVigencia,
            org.GoalWeeklyActiveHours, org.GoalWorkRelatedPct));
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

        // ----- metas semanais AGREGADAS da org (F5): ausente = não muda; null = remove -----
        // Nunca por pessoa e nunca comparando pessoas: o default sugerido pelo portal é a
        // média das últimas semanas da PRÓPRIA org (sem benchmark entre clientes).
        var goalHours = ParseOptionalInt(body, "goal_weekly_active_hours", 1, 10_000,
            out var hasGoalHours, out var goalHoursError);
        if (goalHoursError is not null) return goalHoursError;

        var goalPct = ParseOptionalInt(body, "goal_work_related_pct", 1, 100,
            out var hasGoalPct, out var goalPctError);
        if (goalPctError is not null) return goalPctError;

        var org = await db.Organizations.FirstAsync(ct);

        // aplica somente o que mudou e registra o de→para por campo (detail do audit)
        var changes = new Dictionary<string, object?>();
        if (hasGoalHours && org.GoalWeeklyActiveHours != goalHours)
        {
            changes["goal_weekly_active_hours"] = new { from = org.GoalWeeklyActiveHours, to = goalHours };
            org.GoalWeeklyActiveHours = goalHours;
        }

        if (hasGoalPct && org.GoalWorkRelatedPct != goalPct)
        {
            changes["goal_work_related_pct"] = new { from = org.GoalWorkRelatedPct, to = goalPct };
            org.GoalWorkRelatedPct = goalPct;
        }

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
            org.FinalidadeDeclarada, org.ContatoDpo, org.DataVigencia,
            org.GoalWeeklyActiveHours, org.GoalWorkRelatedPct));
    }

    // =====================================================================================
    // F5 — config de coleta do agente OPERÁVEL (spec §7.4 linha 809 e §8.7): até aqui,
    // tenant_agent_configs só nascia no enroll com defaults de fábrica e config_version
    // jamais era bumpado em produção — o principal diferencial declarado (política de
    // privacidade configurável e transparente) não era operável pelo cliente. A mudança
    // viaja para a frota exclusivamente no ack do batch (decisão: sem endpoint de policy),
    // caminho já validado E2E no aceite da F1 (POLICY_APPLIED).
    // =====================================================================================

    private const int MaxMaskedPatterns = 50;
    private const int MaxPatternLength = 200;
    private const int MaxIgnoredProcesses = 100;
    private const int MaxProcessNameLength = 100;

    /// <summary>Config de coleta vigente (defaults de fábrica se o tenant ainda não tem linha).</summary>
    [HttpGet("agent-config")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> GetAgentConfig(CancellationToken ct)
    {
        var config = await db.TenantAgentConfigs.FirstOrDefaultAsync(ct)
            ?? new TenantAgentConfig { TenantId = CurrentUser.TenantId(User) };

        return Ok(ToAgentConfigResponse(config));
    }

    /// <summary>
    /// Edição PARCIAL da config de coleta — OwnerOnly: mudar a política de coleta é decisão
    /// da CONTROLADORA (kit LGPD itens 3/8). FULL não é aceito por aqui: exige decisão
    /// consciente registrada em DPA e é aplicado via backoffice (kit LGPD item 3). Toda
    /// mudança dá bump transacional de config_version (propaga no próximo ack de cada
    /// device) e grava a trilha de→para; mudar collection_window também registra a ação
    /// collection_window_choice reservada pela spec (linha 726) — quem decide é a controladora.
    ///
    /// notice_text (corpo do aviso de ciência, Seções 6.5/9.4) entra por aqui com a mesma
    /// disciplina, mais a validação do NoticeTextPolicy: tamanho que cabe na janela do aviso
    /// já contando o enquadramento fixo, nada de HTML ou marcação, e nada que imite pedido de
    /// consentimento. Mudar o texto sobe junto notice_version, que é o que reexibe o aviso na
    /// frota. O enquadramento fixo continua sendo concatenado pelo AGENTE e este campo não o
    /// desativa.
    /// </summary>
    [HttpPatch("agent-config")]
    [Authorize(Policy = AuthConstants.PolicyOwnerOnly)]
    public async Task<IActionResult> PatchAgentConfig([FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "Corpo inválido: envie um objeto JSON.");
        }

        // ----- idle_threshold_sec: faixa do protocolo 60–1800 s (N4) -----
        var hasIdle = body.TryGetProperty("idle_threshold_sec", out var idleEl);
        var idle = 0;
        if (hasIdle)
        {
            if (idleEl.ValueKind != JsonValueKind.Number || !idleEl.TryGetInt32(out idle) || idle is < 60 or > 1800)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "idle_threshold_sec deve ser um inteiro entre 60 e 1800 segundos.");
            }
        }

        // ----- window_title_policy: só MASKED_PATTERNS | APP_ONLY via API -----
        var hasPolicy = body.TryGetProperty("window_title_policy", out var policyEl);
        string? policy = null;
        if (hasPolicy)
        {
            policy = policyEl.ValueKind == JsonValueKind.String ? policyEl.GetString() : null;
            if (policy is not ("MASKED_PATTERNS" or "APP_ONLY"))
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "window_title_policy deve ser MASKED_PATTERNS ou APP_ONLY. A política FULL " +
                    "(títulos sem mascaramento) exige decisão registrada em contrato/DPA e é " +
                    "aplicada pela operadora.", code: "full_requires_dpa");
            }
        }

        // ----- masked_patterns: cada item precisa ser uma regex .NET válida — uma regex
        // inválida quebraria o TitleMasker na frota inteira -----
        var hasPatterns = body.TryGetProperty("masked_patterns", out var patternsEl);
        string[]? patterns = null;
        if (hasPatterns)
        {
            if (patternsEl.ValueKind != JsonValueKind.Array)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest, "masked_patterns deve ser uma lista de expressões.");
            }

            var list = new List<string>();
            foreach (var item in patternsEl.EnumerateArray())
            {
                var value = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(value) || value.Length > MaxPatternLength)
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest,
                        $"Cada padrão de mascaramento deve ser um texto de até {MaxPatternLength} caracteres.");
                }

                try
                {
                    _ = Regex.Match(string.Empty, value, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest,
                        $"Padrão de mascaramento inválido (não é uma expressão regular válida): {value}",
                        code: "invalid_pattern");
                }
                catch (RegexMatchTimeoutException)
                {
                    // timeout na string vazia é praticamente impossível; padrão patológico → rejeita
                    return ProblemResponse(StatusCodes.Status400BadRequest,
                        $"Padrão de mascaramento rejeitado por custo excessivo: {value}", code: "invalid_pattern");
                }

                list.Add(value);
            }

            if (list.Count > MaxMaskedPatterns)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    $"Máximo de {MaxMaskedPatterns} padrões de mascaramento.");
            }

            patterns = [.. list];
        }

        // ----- ignored_processes: nomes de executável simples, sem caminho -----
        var hasIgnored = body.TryGetProperty("ignored_processes", out var ignoredEl);
        string[]? ignored = null;
        if (hasIgnored)
        {
            if (ignoredEl.ValueKind != JsonValueKind.Array)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest, "ignored_processes deve ser uma lista de processos.");
            }

            var list = new List<string>();
            foreach (var item in ignoredEl.EnumerateArray())
            {
                var value = item.ValueKind == JsonValueKind.String
                    ? item.GetString()?.Trim().ToLowerInvariant()
                    : null;
                if (string.IsNullOrEmpty(value) || value.Length > MaxProcessNameLength
                    || value.Contains('\\') || value.Contains('/'))
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest,
                        "Cada processo ignorado deve ser um nome de executável simples (ex.: nomedoapp.exe), sem caminho.");
                }

                list.Add(value);
            }

            if (list.Count > MaxIgnoredProcesses)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    $"Máximo de {MaxIgnoredProcesses} processos ignorados.");
            }

            ignored = [.. list.Distinct()];
        }

        // ----- collection_window: ALWAYS ou BUSINESS_HOURS com days/start/end válidos -----
        var hasWindow = body.TryGetProperty("collection_window", out var windowEl);
        string? windowJson = null;
        if (hasWindow)
        {
            var error = ParseCollectionWindow(windowEl, out windowJson);
            if (error is not null) return error;
        }

        // ----- notice_text: corpo do aviso de ciência escrito pela controladora -----
        // ausente = não muda; null (ou só espaço) = volta ao corpo padrão embutido no agente.
        // O enquadramento fixo ("registra a sua ciência, não é pedido de consentimento") é
        // concatenado NO AGENTE e não passa por aqui: este campo não consegue removê-lo.
        var hasNotice = body.TryGetProperty("notice_text", out var noticeEl);
        string? notice = null;
        if (hasNotice && noticeEl.ValueKind != JsonValueKind.Null)
        {
            if (noticeEl.ValueKind != JsonValueKind.String)
            {
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "notice_text deve ser um texto ou null (null volta ao aviso padrão do agente).");
            }

            var candidato = NoticeTextPolicy.Normalize(noticeEl.GetString()!);
            if (candidato.Length > 0)
            {
                var recusa = NoticeTextPolicy.Validate(candidato);
                if (recusa is not null)
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest, recusa.Message, code: recusa.Code);
                }

                notice = candidato;
            }
        }

        if (!hasIdle && !hasPolicy && !hasPatterns && !hasIgnored && !hasWindow && !hasNotice)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Nenhum campo editável informado (idle_threshold_sec, window_title_policy, masked_patterns, ignored_processes, collection_window, notice_text).");
        }

        var tenantId = CurrentUser.TenantId(User);
        var config = await db.TenantAgentConfigs.FirstOrDefaultAsync(ct);
        if (config is null)
        {
            // a linha só nasce no primeiro enroll — tenant recém-criado ainda não tem
            config = new TenantAgentConfig { TenantId = tenantId };
            db.TenantAgentConfigs.Add(config);
        }

        var changes = new Dictionary<string, object?>();
        if (hasIdle && config.IdleThresholdSec != idle)
        {
            changes["idle_threshold_sec"] = new { from = config.IdleThresholdSec, to = idle };
            config.IdleThresholdSec = idle;
        }

        if (hasPolicy && config.WindowTitlePolicy != policy)
        {
            changes["window_title_policy"] = new { from = config.WindowTitlePolicy, to = policy };
            config.WindowTitlePolicy = policy!;
        }

        if (hasPatterns && !config.MaskedPatterns.SequenceEqual(patterns!))
        {
            changes["masked_patterns"] = new { from = config.MaskedPatterns, to = patterns };
            config.MaskedPatterns = patterns!;
        }

        if (hasIgnored && !config.IgnoredProcesses.SequenceEqual(ignored!))
        {
            changes["ignored_processes"] = new { from = config.IgnoredProcesses, to = ignored };
            config.IgnoredProcesses = ignored!;
        }

        var windowChanged = false;
        if (hasWindow && !JsonEqual(config.CollectionWindow, windowJson))
        {
            changes["collection_window"] = new { from = config.CollectionWindow, to = windowJson };
            config.CollectionWindow = windowJson!;
            windowChanged = true;
        }

        if (hasNotice && config.NoticeText != notice)
        {
            changes["notice_text"] = new { from = config.NoticeText, to = notice };
            config.NoticeText = notice;

            // sobe também a versão do aviso: é o bump de notice_version que faz o NoticeForm
            // reaparecer na frota e gerar um NOTICE_ACK novo. Um aviso reescrito que ninguém
            // volta a ver não informa ninguém, e ficaria só como texto no banco.
            changes["notice_version"] = new { from = config.NoticeVersion, to = config.NoticeVersion + 1 };
            config.NoticeVersion++;
        }

        if (changes.Count > 0)
        {
            // bump transacional: a frota recebe a config nova no próximo ack de cada device
            config.ConfigVersion++;
            config.UpdatedAt = DateTimeOffset.UtcNow;

            audit.Add(tenantId, AuditActions.UpdatePrivacyConfig, CurrentUser.UserId(User),
                HttpContext.Connection.RemoteIpAddress, targetType: "agent_config", targetId: tenantId,
                detailJson: JsonSerializer.Serialize(new { changes, config_version = config.ConfigVersion }));

            if (windowChanged)
            {
                // registro EXPLÍCITO da escolha da janela de coleta (spec linha 726):
                // quem decide é a controladora, e a decisão fica evidenciada por si só
                audit.Add(tenantId, AuditActions.CollectionWindowChoice, CurrentUser.UserId(User),
                    HttpContext.Connection.RemoteIpAddress, targetType: "agent_config", targetId: tenantId,
                    detailJson: windowJson);
            }

            await db.SaveChangesAsync(ct);
        }

        return Ok(ToAgentConfigResponse(config));
    }

    private static AgentConfigAdminResponse ToAgentConfigResponse(TenantAgentConfig config) => new(
        config.ConfigVersion,
        config.HeartbeatSec,
        config.ActiveWindowPollSec,
        config.IdleThresholdSec,
        config.WindowTitlePolicy,
        config.MaskedPatterns,
        config.IgnoredProcesses,
        AgentConfigService.ParseCollectionWindow(config.CollectionWindow),
        config.NoticeText,
        config.NoticeVersion,
        NoticeTextPolicy.DefaultBody,
        NoticeTextPolicy.FixedFraming,
        NoticeTextPolicy.MaxBodyLength,
        config.UpdatedAt);

    private ObjectResult? ParseCollectionWindow(JsonElement el, out string? canonicalJson)
    {
        canonicalJson = null;
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty("mode", out var modeEl)
            || modeEl.ValueKind != JsonValueKind.String)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "collection_window deve ser um objeto com mode ALWAYS ou BUSINESS_HOURS.");
        }

        var mode = modeEl.GetString();
        if (mode == "ALWAYS")
        {
            canonicalJson = TenantAgentConfig.FactoryDefaults.CollectionWindowAlways;
            return null;
        }

        if (mode != "BUSINESS_HOURS")
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "collection_window.mode deve ser ALWAYS ou BUSINESS_HOURS.");
        }

        int[]? days = null;
        if (el.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<int>();
            foreach (var d in daysEl.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out var day) || day is < 0 or > 6)
                {
                    return ProblemResponse(StatusCodes.Status400BadRequest,
                        "collection_window.days deve conter dias da semana entre 0 (domingo) e 6 (sábado).");
                }

                list.Add(day);
            }

            days = [.. list.Distinct().Order()];
        }

        if (days is null || days.Length == 0)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "collection_window BUSINESS_HOURS exige a lista de dias (days).");
        }

        var start = el.TryGetProperty("start", out var startEl) && startEl.ValueKind == JsonValueKind.String
            ? startEl.GetString() : null;
        var end = el.TryGetProperty("end", out var endEl) && endEl.ValueKind == JsonValueKind.String
            ? endEl.GetString() : null;
        if (!IsHourMinute(start) || !IsHourMinute(end))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "collection_window BUSINESS_HOURS exige start e end no formato HH:mm.");
        }

        canonicalJson = JsonSerializer.Serialize(new
        {
            mode = "BUSINESS_HOURS",
            days,
            start,
            end,
        });
        return null;
    }

    private static bool IsHourMinute(string? value) =>
        value is not null && Regex.IsMatch(value, "^([01][0-9]|2[0-3]):[0-5][0-9]$");

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

    /// <summary>
    /// Inteiro opcional do corpo: ausente (não muda); null (limpa); número dentro da faixa.
    /// Mesmo contrato do ParseOptionalText, para os campos numéricos das metas.
    /// </summary>
    private int? ParseOptionalInt(
        JsonElement body, string field, int min, int max, out bool hasField, out ObjectResult? error)
    {
        error = null;
        hasField = body.TryGetProperty(field, out var el);
        if (!hasField || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var value) || value < min || value > max)
        {
            error = ProblemResponse(StatusCodes.Status400BadRequest,
                $"{field} deve ser um inteiro entre {min} e {max}, ou null para remover a meta.");
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
