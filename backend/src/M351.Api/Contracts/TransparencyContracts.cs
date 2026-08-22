using System.Text.Json;

namespace M351.Api.Contracts;

// ====================================================================== F4.8 — Transparência
//
// Contrato da página PÚBLICA por slug (Seção 8.8) e dos endpoints autenticados de leitura/edição
// da config de transparência da org. snake_case na serialização (PropertyNamingPolicy global).
//
// O endpoint público NUNCA expõe dado pessoal, window_title cru nem masked_patterns crus (regex
// internos): só a POLÍTICA vigente derivada das configs do tenant + as retenções fixas (Seção 9.6)
// + os campos editáveis (finalidade/DPO/vigência) + a data da última purga.

/// <summary>
/// Política de títulos de janela (window_title_policy) exposta na transparência: o modo cru
/// (FULL/MASKED_PATTERNS/APP_ONLY) + uma descrição amigável em pt-BR. JAMAIS os masked_patterns.
/// </summary>
public record WindowTitlePolicyPublic(string Mode, string Descricao);

/// <summary>
/// Janela de coleta (collection_window) exposta na transparência: modo (ALWAYS|BUSINESS_HOURS),
/// dias/início/fim quando BUSINESS_HOURS, e a descrição em pt-BR. Sem dado pessoal.
/// </summary>
public record CollectionWindowPublic(string Mode, int[]? Days, string? Start, string? End, string Descricao);

/// <summary>Retenções fixas no MVP (Seção 9.6, N10–N13) — números canônicos, não configuráveis.</summary>
public record RetencoesPublic(int EventosDias, int IntervalosMeses, int AgregadosMeses, int AuditoriaMeses);

/// <summary>
/// Bloco "Este dispositivo" da página por TOKEN (GET /api/v1/public/t/{token}). Só o estado da
/// INSTALAÇÃO: hostname (que o funcionário lê na própria máquina), quando a ciência do aviso foi
/// registrada, o último contato do agente e o status.
///
/// O que NÃO entra, por decisão: nenhum dado pessoal do dia — horas ativas, ociosas, aplicativo
/// em foco. A URL é uma capability sem autenticação; quem tiver o link veria o comportamento de
/// quem usa a máquina. Transparência sobre a POLÍTICA e sobre a INSTALAÇÃO, nunca sobre a pessoa.
/// </summary>
public record PublicDeviceBlock(
    string Hostname,
    DateTimeOffset? NoticeAckedAt,
    DateTimeOffset? LastSeenAt,
    string Status);

/// <summary>
/// Resposta de GET /api/v1/public/transparencia/{slug} e de GET /api/v1/public/t/{token}
/// (AllowAnonymous). Renderizada do estado REAL das configs do tenant. ZERO dado pessoal: sem
/// window_title, sem masked_patterns, sem nome de usuário. Device só é preenchido na rota por
/// token (o link do tray da própria máquina) e mesmo ali carrega apenas o estado da instalação.
/// </summary>
public record PublicTransparencyResponse(
    string OrganizationName,
    WindowTitlePolicyPublic WindowTitlePolicy,
    CollectionWindowPublic CollectionWindow,
    RetencoesPublic Retencoes,
    string? FinalidadeDeclarada,
    string? ContatoDpo,
    DateOnly? Vigencia,
    DateTimeOffset? UltimaPurga,
    IReadOnlyList<string> Coletado,
    IReadOnlyList<string> NuncaColetado,
    PublicDeviceBlock? Device = null);

// ---------------------------------------------------------------------- org autenticado (GET/PATCH)

/// <summary>
/// Resposta de GET /api/v1/organization (PolicyAccess — qualquer papel autenticado). Espelha os
/// campos editáveis na tela de Configurações + os já existentes (name/slug/timezone/business_hours).
/// business_hours é o jsonb CRU da org (ou null), igual ao /me.
/// </summary>
public record OrganizationResponse(
    string Name,
    string Slug,
    string Timezone,
    JsonElement? BusinessHours,
    string? FinalidadeDeclarada,
    string? ContatoDpo,
    DateOnly? DataVigencia,
    /// <summary>F5 — metas semanais AGREGADAS da org (null = sem meta definida).</summary>
    int? GoalWeeklyActiveHours = null,
    int? GoalWorkRelatedPct = null);
