namespace M351.Infrastructure.Reports;

/// <summary>
/// SQL canônico do relatório de jornada (F3.5, Seção 8.6) — FONTE ÚNICA compartilhada pelo
/// endpoint GET /reports/jornada (M351.Api) e pelo CSV assíncrono (ExportService): os números
/// do arquivo exportado são, por construção, os mesmos da tela (DoD 11.3).
///
/// Linha por device × dia do RANGE INTEIRO (dias sem dados também viram linha — spec linha
/// 947). Fonte: daily_device_summaries somando as lanes por device-dia (seconds_* somados;
/// first_event_at = MIN e last_event_at = MAX das lanes de usuário — a agregação F3.1 já
/// deixa NULL nas lanes sem intervalo de usuário) + UMA query agregada em activity_intervals
/// por (device, source_day) só para distinguir "sem comunicação" de "sem dados".
///
/// Decisões documentadas (silêncios da spec):
///  - devices: por default só não-archived (mesma regra de dashboards/relatórios — spec linha
///    954); device_ids EXPLÍCITO inclui archived (o gestor pediu aquele histórico pelo toggle
///    "incluir arquivados" do portal; o gate de tenant continua: id de outro tenant → 404);
///  - users: nomes das lanes de USUÁRIO com tempo no dia (seconds_on > 0), separados por
///    ", "; lane-máquina (UUID zero) fora; titular removido por DSR → "Usuário desconhecido";
///  - note: data_incomplete do dia → 'dados_incompletos'; senão seconds_on = 0 com intervalo
///    no_data no dia → 'sem_comunicacao'; senão seconds_on = 0 → 'sem_dados'; senão NULL;
///  - ordenação: device_name (case-insensitive, desempate por id) e data — estável para a
///    paginação do endpoint e para o streaming do CSV.
/// </summary>
public static class JornadaReportSql
{
    /// <summary>
    /// Recorte de devices do relatório. Parâmetros: @TenantId, @FilterDevices (bool),
    /// @DeviceIds (uuid[]; vazio quando sem filtro — o Npgsql não infere uuid[] nulo).
    /// </summary>
    public const string DevicesCte = """
        devs AS (
            SELECT d.id, COALESCE(d.display_name, d.hostname) AS device_name
            FROM devices d
            WHERE d.tenant_id = @TenantId
              AND ((@FilterDevices AND d.id = ANY(@DeviceIds))
                   OR (NOT @FilterDevices AND d.status <> 'archived'))
        )
        """;

    /// <summary>
    /// Linhas device × dia (sem LIMIT/OFFSET — o endpoint pagina, o CSV faz streaming).
    /// Parâmetros: os do DevicesCte + @From/@To (yyyy-MM-dd, dias locais do tenant — a
    /// summary_date da agregação F3.1 JÁ é o dia local; zero matemática de fuso aqui).
    /// </summary>
    public const string Rows = $"""
        WITH {DevicesCte},
        days AS (
            SELECT generate_series(@From::date, @To::date, interval '1 day')::date AS day
        ),
        sums AS (
            SELECT s.device_id, s.summary_date AS day,
                   sum(s.seconds_on)::bigint AS seconds_on,
                   sum(s.seconds_active)::bigint AS seconds_active,
                   sum(s.seconds_idle)::bigint AS seconds_idle,
                   sum(s.seconds_locked)::bigint AS seconds_locked,
                   min(s.first_event_at) AS first_event_at,
                   max(s.last_event_at) AS last_event_at,
                   bool_or(s.data_incomplete) AS data_incomplete
            FROM daily_device_summaries s
            WHERE s.tenant_id = @TenantId
              AND s.summary_date BETWEEN @From::date AND @To::date
              AND s.device_id IN (SELECT id FROM devs)
            GROUP BY s.device_id, s.summary_date
        ),
        lane_users AS (
            SELECT s.device_id, s.summary_date AS day,
                   string_agg(DISTINCT COALESCE(du.display_name, du.windows_username, 'Usuário desconhecido'), ', '
                       ORDER BY COALESCE(du.display_name, du.windows_username, 'Usuário desconhecido')) AS users
            FROM daily_device_summaries s
            LEFT JOIN device_users du ON du.tenant_id = s.tenant_id AND du.id = s.device_user_id
            WHERE s.tenant_id = @TenantId
              AND s.summary_date BETWEEN @From::date AND @To::date
              AND s.device_id IN (SELECT id FROM devs)
              AND s.device_user_id <> '00000000-0000-0000-0000-000000000000'::uuid
              AND s.seconds_on > 0
            GROUP BY s.device_id, s.summary_date
        ),
        gaps AS (
            -- piso/teto em started_at (chave de particionamento — InitialCreate): source_day
            -- NÃO poda partições; sem isto a CTE varreria TODAS as partições mensais (12
            -- meses, N11) a cada página do endpoint e a cada CSV. Folga de 48 h pela mesma
            -- justificativa do TimelineController (split na meia-noite local + troca de fuso).
            SELECT i.device_id, i.source_day AS day,
                   bool_or(i.state = 'no_data') AS had_no_data
            FROM activity_intervals i
            WHERE i.tenant_id = @TenantId
              AND i.started_at >= @From::date - interval '48 hours'
              AND i.started_at < @To::date + interval '48 hours'
              AND i.source_day BETWEEN @From::date AND @To::date
              AND i.device_id IN (SELECT id FROM devs)
            GROUP BY i.device_id, i.source_day
        )
        SELECT dy.day::text AS date,
               dv.id AS device_id,
               dv.device_name,
               u.users,
               s.first_event_at,
               s.last_event_at,
               COALESCE(s.seconds_on, 0) AS seconds_on,
               COALESCE(s.seconds_active, 0) AS seconds_active,
               COALESCE(s.seconds_idle, 0) AS seconds_idle,
               COALESCE(s.seconds_locked, 0) AS seconds_locked,
               CASE
                   WHEN COALESCE(s.data_incomplete, false) THEN 'dados_incompletos'
                   WHEN COALESCE(s.seconds_on, 0) = 0 AND COALESCE(g.had_no_data, false) THEN 'sem_comunicacao'
                   WHEN COALESCE(s.seconds_on, 0) = 0 THEN 'sem_dados'
                   ELSE NULL
               END AS note
        FROM devs dv
        CROSS JOIN days dy
        LEFT JOIN sums s ON s.device_id = dv.id AND s.day = dy.day
        LEFT JOIN lane_users u ON u.device_id = dv.id AND u.day = dy.day
        LEFT JOIN gaps g ON g.device_id = dv.id AND g.day = dy.day
        ORDER BY lower(dv.device_name), dv.id, dy.day
        """;

    /// <summary>
    /// Nº de devices do recorte — total da paginação do endpoint = (este count) × (nº de
    /// dias do range), já que TODO par device × dia vira linha. Mesmos parâmetros do DevicesCte.
    /// </summary>
    public const string DeviceCount = $"""
        WITH {DevicesCte}
        SELECT count(*)::int FROM devs
        """;

    /// <summary>
    /// Totais por device do RANGE INTEIRO (independente da página). days_with_data = dias
    /// em que alguma lane teve tempo (seconds_on > 0) — dia só com off_clean/no_data não
    /// conta como "dia com dados". Mesmos parâmetros do Rows.
    /// </summary>
    public const string DeviceTotals = $"""
        WITH {DevicesCte}
        SELECT dv.id AS device_id,
               dv.device_name,
               COALESCE(sum(s.seconds_on), 0)::bigint AS seconds_on,
               COALESCE(sum(s.seconds_active), 0)::bigint AS seconds_active,
               COALESCE(sum(s.seconds_idle), 0)::bigint AS seconds_idle,
               COALESCE(sum(s.seconds_locked), 0)::bigint AS seconds_locked,
               COALESCE(count(DISTINCT s.summary_date) FILTER (WHERE s.seconds_on > 0), 0)::int AS days_with_data
        FROM devs dv
        LEFT JOIN daily_device_summaries s
            ON s.tenant_id = @TenantId AND s.device_id = dv.id
           AND s.summary_date BETWEEN @From::date AND @To::date
        GROUP BY dv.id, dv.device_name
        ORDER BY lower(dv.device_name), dv.id
        """;
}
