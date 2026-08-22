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

// ----- GET /api/v1/reports/jornada (F3.5, Seções 7.4/8.6) -----

/// <summary>
/// items = página corrente (ordem device_name, date); total = devices × dias do range
/// INTEIRO; device_totals SEMPRE do range inteiro, independente da página.
/// </summary>
public sealed record JornadaReportResponse(
    IReadOnlyList<JornadaRowResponse> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<JornadaDeviceTotalsResponse> DeviceTotals);

/// <summary>
/// Uma linha por device × dia — dias sem dados TAMBÉM viram linha (spec linha 947).
/// first/last_event_at = bordas dos intervalos de USUÁRIO (mesma definição do rodapé da
/// timeline — consistência 11.3). note: dados_incompletos | sem_comunicacao | sem_dados |
/// null. JAMAIS nomenclatura de ponto eletrônico nem cálculo de horas extras.
/// </summary>
public sealed record JornadaRowResponse(
    string Date,
    Guid DeviceId,
    string DeviceName,
    string? Users,
    DateTimeOffset? FirstEventAt,
    DateTimeOffset? LastEventAt,
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    string? Note);

/// <summary>Totais por device do range inteiro; days_with_data = dias com seconds_on &gt; 0.</summary>
public sealed record JornadaDeviceTotalsResponse(
    Guid DeviceId,
    string DeviceName,
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    int DaysWithData);

// ----- GET /api/v1/reports/fora-do-horario (atividade fora do horário de trabalho) -----

/// <summary>Situações em que o painel NÃO pode mostrar número (ver o status da resposta).</summary>
public static class ForaDoHorarioStatus
{
    /// <summary>Janela configurada e coleta contínua: os números valem.</summary>
    public const string Ok = "ok";

    /// <summary>business_hours ausente/malformada: sem janela declarada não há "fora dela".</summary>
    public const string HorarioNaoConfigurado = "horario_nao_configurado";

    /// <summary>
    /// collection_window = BUSINESS_HOURS: fora da janela o agente NÃO coleta, por decisão da
    /// própria organização. Zero aqui seria um número falso, então a tela explica em vez de exibir.
    /// </summary>
    public const string ColetaRestritaAoHorario = "coleta_restrita_ao_horario";
}

/// <summary>Janela declarada da organização, ecoada para a tela desenhar a explicação.</summary>
public sealed record ForaDoHorarioWindowResponse(int[] Days, string Start, string End);

/// <summary>
/// Totais do período INTEIRO. seconds_outside = before + after + non_business_day;
/// seconds_active é o tempo ativo total do mesmo recorte e da MESMA fonte (activity_intervals),
/// para que o percentual da tela seja internamente consistente.
/// </summary>
public sealed record ForaDoHorarioTotalsResponse(
    long SecondsActive,
    long SecondsOutside,
    long SecondsBefore,
    long SecondsAfter,
    long SecondsNonBusinessDay,
    int DevicesWithActivityOutside);

/// <summary>Uma linha por dispositivo COM atividade fora do horário no período.</summary>
public sealed record ForaDoHorarioItemResponse(
    Guid DeviceId,
    string DeviceName,
    long SecondsActive,
    long SecondsOutside,
    long SecondsBefore,
    long SecondsAfter,
    long SecondsNonBusinessDay,
    int DaysWithActivityOutside);

/// <summary>
/// Painel de atividade fora do horário de trabalho. Indicador de EQUILÍBRIO da equipe: nunca
/// hora extra, banco de horas ou qualquer leitura de controle de ponto.
///
/// status != "ok" traz totals null e items vazio DE PROPÓSITO: sem janela declarada, ou com a
/// coleta restrita ao próprio horário de trabalho, qualquer número seria enganoso — a tela
/// explica o motivo em vez de exibir zero.
///
/// items só vem preenchido com include_devices=true (ou device_ids); total é o número de
/// dispositivos com atividade fora do horário no período inteiro, não o da página.
/// </summary>
public sealed record ForaDoHorarioResponse(
    string Status,
    string Timezone,
    ForaDoHorarioWindowResponse? BusinessHours,
    string CollectionWindowMode,
    ForaDoHorarioTotalsResponse? Totals,
    IReadOnlyList<ForaDoHorarioItemResponse> Items,
    int Total,
    int Page,
    int PageSize);
