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
