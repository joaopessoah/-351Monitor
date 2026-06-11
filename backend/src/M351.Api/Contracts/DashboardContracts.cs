namespace M351.Api.Contracts;

// ----- GET /api/v1/dashboard/summary (Seção 7.4 — KPIs de daily_device_summaries) -----

/// <summary>
/// Dias sem linhas no agregado NÃO aparecem em days (o portal preenche zeros).
/// Datas no fuso do TENANT, inclusivas.
/// </summary>
public sealed record DashboardSummaryResponse(
    IReadOnlyList<DashboardSummaryDayResponse> Days,
    DashboardSummaryTotalsResponse Totals);

public sealed record DashboardSummaryDayResponse(
    string Date,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsOn,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated,
    bool DataIncomplete,
    int DeviceCount);

/// <summary>Mesmos campos dos dias somados (data_incomplete = OR); device_count é o DISTINCT do período inteiro.</summary>
public sealed record DashboardSummaryTotalsResponse(
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsOn,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated,
    bool DataIncomplete,
    int DeviceCount);

// ----- GET /api/v1/dashboard/top-apps (Seção 7.4 — de daily_app_usage) -----

/// <summary>total_seconds_active soma TODOS os apps do período (não só o top) — denominador de %.</summary>
public sealed record DashboardTopAppsResponse(
    IReadOnlyList<DashboardTopAppResponse> Items,
    long TotalSecondsActive);

public sealed record DashboardTopAppResponse(
    Guid AppId,
    string ProcessName,
    string DisplayName,
    string? CustomDisplayName,
    DashboardAppCategoryResponse? Category,
    long SecondsActive,
    int DeviceCount);

/// <summary>Categoria do TENANT (tenant_app_categories → categories); null = não categorizado.</summary>
public sealed record DashboardAppCategoryResponse(Guid Id, string Name, int Classification, string? Color);
