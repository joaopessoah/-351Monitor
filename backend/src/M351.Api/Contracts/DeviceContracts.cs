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
