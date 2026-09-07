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
    long SecondsUnclassified,
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
    long SecondsUnclassified,
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

// ----- GET /api/v1/dashboard/activity-by-hour (F6 — de hourly_activity) -----

/// <summary>
/// As 24 horas locais do tenant, SEMPRE presentes (hora sem dado vem com zeros — o gráfico
/// não pode ter buraco). days_with_data é o denominador de avg_people_active.
/// </summary>
public sealed record ActivityByHourResponse(
    IReadOnlyList<ActivityByHourItemResponse> Hours,
    int DaysWithData);

/// <summary>
/// avg_people_active = seconds_active ÷ 3600 ÷ days_with_data: a média de pessoas ativas
/// SIMULTÂNEAS naquela hora ao longo do período. null quando o período não tem nenhum dia
/// com dado — dividir por zero seria erro, e devolver zero leria como "ninguém trabalhou".
/// </summary>
public sealed record ActivityByHourItemResponse(
    int Hour,
    long SecondsActive,
    long SecondsIdle,
    double? AvgPeopleActive);

// ----- GET /api/v1/dashboard/overview (F6 — KPIs da Visão Geral numa chamada) -----

/// <summary>
/// Tudo o que a Visão Geral precisa numa chamada: período resolvido, totais com índice e
/// cobertura, o período anterior de mesma duração (quando compare=true) e a série por dia.
/// </summary>
public sealed record OverviewResponse(
    OverviewPeriodResponse Period,
    OverviewTotalsResponse Totals,
    OverviewTotalsResponse? Previous,
    IReadOnlyList<DashboardSummaryDayResponse> Days,
    OverviewGoalsResponse Goals);

/// <summary>Período INCLUSIVO no fuso do tenant; days é a contagem de dias do intervalo.</summary>
public sealed record OverviewPeriodResponse(string From, string To, int Days);

/// <summary>
/// Baldes somados do período + os dois indicadores derivados (decisão 4 do spec de 07/09/2026):
/// productivity_index = work_related ÷ (work_related + neutral + not_work_related);
/// classification_coverage = (active − unclassified) ÷ active. Ambos null quando o denominador
/// é zero — nunca 0, que leria como "índice péssimo" em vez de "sem dado".
/// person_days = pares (lane de usuário, dia) com tempo ligado; person_count = lanes distintas.
/// A lane-máquina (UUID zero) NÃO conta como pessoa (decisão 8 do spec).
/// </summary>
public sealed record OverviewTotalsResponse(
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated,
    long SecondsUnclassified,
    double? ProductivityIndex,
    double? ClassificationCoverage,
    int DeviceCount,
    int PersonCount,
    int PersonDays,
    bool DataIncomplete);

/// <summary>Metas semanais da organização, repetidas aqui para a tela não precisar do /me.</summary>
public sealed record OverviewGoalsResponse(int? WeeklyActiveHours, int? WorkRelatedPct);
