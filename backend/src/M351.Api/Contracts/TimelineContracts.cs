namespace M351.Api.Contracts;

/// <summary>GET /api/v1/timeline/device (Seção 7.4) — resolução fixa 1 min, cap N21.</summary>
public sealed record TimelineResponse(
    Guid DeviceId,
    string DeviceName,
    string Date,
    string Timezone,
    int? DeviceTzOffsetMin,
    int ResolutionSec,
    bool DataIncomplete,
    DateTimeOffset ServerTime,
    IReadOnlyList<TimelineIntervalResponse> Intervals,
    TimelineSummaryResponse Summary);

public sealed record TimelineIntervalResponse(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string State,
    TimelineAppResponse? App,
    string? WindowTitle,
    bool DataIncomplete);

/// <summary>Category é null na F2 (categorias/catálogo curado chegam na F3).</summary>
public sealed record TimelineAppResponse(Guid AppId, string ProcessName, string DisplayName, string? Category);

/// <summary>
/// GET /api/v1/timeline/team (Seção 7.4/8.5, F3.4) — uma lane por device NÃO-archived
/// do tenant (lanes vazias incluídas: o gestor varre a equipe inteira), ordenadas por
/// nome de exibição. Mesma agregação do modo device; truncated = cap N21 atingido
/// (lanes INTEIRAS deixadas de fora, nunca lane cortada no meio).
/// </summary>
public sealed record TeamTimelineResponse(
    string Date,
    int ResolutionSec,
    DateTimeOffset ServerTime,
    bool Truncated,
    IReadOnlyList<TeamTimelineLaneResponse> Lanes);

/// <summary>Lane de 28 px do modo equipe — intervals com o MESMO shape do modo device.</summary>
public sealed record TeamTimelineLaneResponse(
    Guid DeviceId,
    string DeviceName,
    int? DeviceTzOffsetMin,
    bool DataIncomplete,
    IReadOnlyList<TimelineIntervalResponse> Intervals);

/// <summary>
/// Rodapé do modo device (Seção 8.5): MESMOS números do relatório de jornada (11.3).
/// seconds_on = active + idle + locked; first/last_event_at = bordas dos intervalos de
/// usuário (off_clean/no_data não contam como "evento" — a noite desligada não é jornada).
/// Definição compartilhada com a agregação diária da F3.
/// </summary>
public sealed record TimelineSummaryResponse(
    DateTimeOffset? FirstEventAt,
    DateTimeOffset? LastEventAt,
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked);
