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
