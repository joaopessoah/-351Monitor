namespace M351.Api.Contracts;

// ----- GET /api/v1/reports/usage (F3.3, Seção 7.4 — tabular paginado) -----

/// <summary>
/// total = número de grupos do período inteiro (não da página);
/// total_seconds_active = soma de seconds_active de TODOS os grupos (denominador de %).
/// </summary>
public sealed record UsageReportResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize,
    long TotalSecondsActive);

/// <summary>group_by=app (fonte daily_app_usage; categoria resolvida na leitura).</summary>
public sealed record UsageByAppItemResponse(
    Guid AppId,
    string ProcessName,
    string DisplayName,
    string? CustomDisplayName,
    AppCategoryResponse? Category,
    long SecondsActive,
    int DeviceCount);

/// <summary>group_by=category; campos null = balde "Não categorizado" (apps sem mapeamento).</summary>
public sealed record UsageByCategoryItemResponse(
    Guid? CategoryId,
    string? Name,
    int? Classification,
    string? Color,
    long SecondsActive,
    int AppCount);

/// <summary>group_by=device (fonte daily_device_summaries, baldes de classificação inclusos).</summary>
public sealed record UsageByDeviceItemResponse(
    Guid DeviceId,
    string DeviceName,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsOn,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated);

/// <summary>
/// group_by=device_user: UUID zero é a lane-máquina ("Máquina (sem usuário)");
/// windows_user/display_name resolvidos via device_users quando possível.
/// </summary>
public sealed record UsageByDeviceUserItemResponse(
    Guid DeviceUserId,
    Guid DeviceId,
    string DeviceName,
    string? WindowsUser,
    string DisplayName,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsOn,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated);
