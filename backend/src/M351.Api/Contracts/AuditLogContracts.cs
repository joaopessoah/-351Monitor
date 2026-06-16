namespace M351.Api.Contracts;

// ----- GET /api/v1/audit-logs (F4.7, Seção 9.5 — auditoria de acesso; PolicyAdminPlus) -----

/// <summary>
/// Página da trilha de auditoria do tenant. items ordenados por occurred_at desc; total é a
/// contagem do filtro inteiro (não da página). Tudo em snake_case (contrato fixo F4.7).
/// </summary>
public sealed record AuditLogListResponse(
    IReadOnlyList<AuditLogItemResponse> Items,
    long Total,
    int Page,
    int PageSize);

/// <summary>
/// Uma linha da trilha. actor_name vem do join com users (display_name/email); null em ações de
/// SISTEMA/CLI (publish/rollback de release sob tenant-sentinela, ou ator já removido). actor_ip
/// é a representação textual do inet (ex.: "203.0.113.7"), null quando não capturado. detail é o
/// jsonb cru (já serializado) — o portal interpreta por ação.
/// </summary>
public sealed record AuditLogItemResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? ActorName,
    string? ActorIp,
    string Action,
    string? TargetType,
    Guid? TargetId,
    object? Detail);
