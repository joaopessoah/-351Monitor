namespace M351.Api.Contracts;

public record DeviceResponse(
    Guid Id,
    string Hostname,
    string? DisplayName,
    string OsType,
    string? OsVersion,
    string? AgentVersion,
    string Status,
    string[]? Tags,
    DateTimeOffset? LastSeenAt,
    int? TzOffsetMin,
    long ClockOffsetMs,
    DateTimeOffset? NoticeAckedAt,
    DateTimeOffset? LastTamperAt,
    string? LastTamperReason,
    bool AgentOutdated);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// GET /devices/health-summary (F5): contagens de saúde sobre a FROTA INTEIRA (devices
/// active), com os MESMOS limiares do deviceHealth.ts. within_business_hours diz se o
/// realce de "sem comunicação há mais de 30 min" está valendo agora no fuso da org.
/// </summary>
public record DeviceHealthSummaryResponse(
    int ActiveDevices,
    int Offline,
    int OfflineSevere,
    int ClockSkewed,
    int Outdated,
    int Tampered,
    int NoticePending,
    int WithAlert,
    bool WithinBusinessHours,
    DateTimeOffset ServerTime);

/// <summary>
/// Uma versão do agente presente na frota. Version null é a máquina que ainda não reportou versão
/// alguma (enrolada e sem primeiro lote); ela aparece na lista para o total bater com
/// active_devices, em vez de sumir e deixar a soma inexplicável.
/// </summary>
public record FleetVersionRow(string? Version, int Count, bool Outdated);

/// <summary>
/// Falha RECENTE de auto-update num device (materializada do UPDATE_FAILED). Reason é a etapa
/// canônica que reprovou: download | hash | signature | install. Nunca há texto livre aqui.
/// </summary>
public record UpdateFailureRow(
    Guid DeviceId,
    string Hostname,
    string? DisplayName,
    string Reason,
    string? TargetVersion,
    DateTimeOffset OccurredAt);

/// <summary>
/// GET /devices/version-summary: distribuição de versões do agente na FROTA INTEIRA (devices
/// active) mais as falhas de atualização recentes, ambas computadas no servidor, no mesmo padrão
/// do health-summary. É a vigilância de rollout: current_version/min_version dizem para onde a
/// frota deveria estar indo, versions diz onde ela está, e recent_failures diz em que etapa quem
/// não chegou lá emperrou.
/// </summary>
public record DeviceVersionSummaryResponse(
    int ActiveDevices,
    string? CurrentVersion,
    string? MinVersion,
    IReadOnlyList<FleetVersionRow> Versions,
    int UpdateFailures,
    IReadOnlyList<UpdateFailureRow> RecentFailures,
    int UpdateFailureWindowDays,
    DateTimeOffset ServerTime);
