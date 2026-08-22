namespace M351.Infrastructure.Reports;

/// <summary>
/// SQL canônico do painel de ATIVIDADE FORA DO HORÁRIO DE TRABALHO, FONTE ÚNICA
/// compartilhada pelo endpoint GET /reports/fora-do-horario (M351.Api), pelo card da Visão
/// Geral (mesmo endpoint, só os totais) e pelo CSV assíncrono (ExportService): os números do
/// arquivo são, por construção, os mesmos da tela (DoD 11.3).
///
/// LINHA VERMELHA DO PRODUTO: isto é um indicador de EQUILÍBRIO, não de jornada. O relatório
/// soma tempo ATIVO fora da janela declarada e nada mais. Não existe aqui, e não pode passar a
/// existir, cálculo de hora extra, banco de horas, adicional noturno ou qualquer vocabulário de
/// controle de ponto (CLT / Portaria 671).
///
/// Fonte: activity_intervals, JAMAIS os agregados diários, daily_device_summaries tem
/// granularidade de DIA e não sabe dizer se o tempo ativo caiu antes, dentro ou depois da
/// janela. O worker já divide os intervalos na meia-noite do tenant, então cada intervalo
/// pertence a UM source_day local e a comparação com a janela é feita em relógio de parede
/// local (started_at AT TIME ZONE fuso da org), sem matemática de fuso na aplicação.
///
/// Decisões documentadas (silêncios da spec):
///  - só o estado 'active' entra: ocioso/bloqueado fora do horário é máquina ligada, não
///    pessoa trabalhando, contá-los inflaria o indicador e empurraria para leitura de jornada;
///  - a janela é a business_hours da ORG (BusinessHoursWindow.TryParse). Sem ela configurada o
///    endpoint NÃO consulta nada: devolve o estado vazio explicativo (zero seria mentira);
///  - três baldes disjuntos e somáveis: antes do início, depois do fim e dia inteiro fora dos
///    dias declarados (fim de semana / folga). A soma dos três é o "fora do horário";
///  - seconds_active é o tempo ativo TOTAL do mesmo recorte e da MESMA fonte, para que o
///    percentual da tela use numerador e denominador consistentes (o relatório de Uso, que sai
///    dos agregados diários, pode divergir por arredondamento, nunca misturar as duas fontes);
///  - arredondamento: floor da soma em segundos por dispositivo (mesma direção conservadora do
///    gate 11.3, jamais arredondar para cima um indicador de equilíbrio);
///  - devices: por default só não-archived; device_ids EXPLÍCITO inclui archived, mesmo
///    recorte do relatório de jornada, reusando o DevicesCte de <see cref="JornadaReportSql"/>,
///    e com ele o recorte por etiqueta de equipe (@Tag, F5).
/// </summary>
public static class ForaDoHorarioReportSql
{
    /// <summary>
    /// Blocos ativos do recorte já fatiados nos três baldes. Parâmetros: os do
    /// <see cref="JornadaReportSql.DevicesCte"/> (@TenantId, @FilterDevices, @DeviceIds, @Tag) +
    /// @From/@To (yyyy-MM-dd, dias locais do tenant), @Timezone (fuso IANA da org) e
    /// @BusinessDays (int[] ISO 1..7) / @BusinessStart / @BusinessEnd ("HH:mm" locais).
    /// </summary>
    private const string Partes = $"""
        WITH {JornadaReportSql.DevicesCte},
        blocos AS (
            -- piso/teto em started_at (chave de particionamento, InitialCreate): source_day
            -- NÃO poda partições; sem isto a CTE varreria TODAS as partições mensais (12
            -- meses, N11). Folga de 48 h pela mesma justificativa do TimelineController
            -- (split na meia-noite local + troca de fuso da org).
            SELECT i.device_id,
                   i.source_day,
                   (i.started_at AT TIME ZONE @Timezone) AS ls,
                   (i.ended_at   AT TIME ZONE @Timezone) AS le,
                   (EXTRACT(ISODOW FROM i.source_day)::int = ANY(@BusinessDays)) AS dia_util,
                   i.source_day + @BusinessStart::time AS janela_inicio,
                   i.source_day + @BusinessEnd::time   AS janela_fim
            FROM activity_intervals i
            WHERE i.tenant_id = @TenantId
              AND i.started_at >= @From::date - interval '48 hours'
              AND i.started_at <  @To::date + interval '48 hours'
              AND i.source_day BETWEEN @From::date AND @To::date
              AND i.state = 'active'
              AND i.device_id IN (SELECT id FROM devs)
        ),
        partes AS (
            SELECT b.device_id,
                   b.source_day,
                   EXTRACT(EPOCH FROM (b.le - b.ls)) AS total,
                   CASE WHEN b.dia_util
                        THEN GREATEST(0, EXTRACT(EPOCH FROM (LEAST(b.le, b.janela_inicio) - b.ls)))
                        ELSE 0 END AS antes,
                   CASE WHEN b.dia_util
                        THEN GREATEST(0, EXTRACT(EPOCH FROM (b.le - GREATEST(b.ls, b.janela_fim))))
                        ELSE 0 END AS depois,
                   CASE WHEN b.dia_util
                        THEN 0
                        ELSE EXTRACT(EPOCH FROM (b.le - b.ls)) END AS dia_nao_util
            FROM blocos b
        )
        """;

    /// <summary>
    /// Uma linha por dispositivo COM atividade fora do horário no período (dispositivos sem
    /// nada fora ficam de fora: a tabela é um recorte de atenção, não um censo da frota),
    /// ordenada por tempo fora desc. Sem LIMIT/OFFSET, o endpoint pagina, o CSV faz streaming.
    /// </summary>
    public const string Rows = $"""
        {Partes}
        SELECT dv.id AS device_id,
               dv.device_name,
               floor(sum(p.total))::bigint AS seconds_active,
               floor(sum(p.antes + p.depois + p.dia_nao_util))::bigint AS seconds_outside,
               floor(sum(p.antes))::bigint AS seconds_before,
               floor(sum(p.depois))::bigint AS seconds_after,
               floor(sum(p.dia_nao_util))::bigint AS seconds_non_business_day,
               COALESCE(count(DISTINCT p.source_day)
                   FILTER (WHERE p.antes + p.depois + p.dia_nao_util > 0), 0)::int AS days_with_activity_outside
        FROM devs dv
        JOIN partes p ON p.device_id = dv.id
        GROUP BY dv.id, dv.device_name
        HAVING floor(sum(p.antes + p.depois + p.dia_nao_util)) >= 1
        ORDER BY seconds_outside DESC, lower(dv.device_name), dv.id
        """;

    /// <summary>
    /// Totais do PERÍODO INTEIRO (independentes da página), é o que alimenta o card da Visão
    /// Geral. devices_with_activity_outside é também o total da paginação das
    /// <see cref="Rows"/>: mesmo predicado (floor ≥ 1 s), calculado na mesma varredura.
    /// </summary>
    public const string Totals = $"""
        {Partes}
        SELECT floor(COALESCE(sum(p.total), 0))::bigint AS seconds_active,
               floor(COALESCE(sum(p.antes + p.depois + p.dia_nao_util), 0))::bigint AS seconds_outside,
               floor(COALESCE(sum(p.antes), 0))::bigint AS seconds_before,
               floor(COALESCE(sum(p.depois), 0))::bigint AS seconds_after,
               floor(COALESCE(sum(p.dia_nao_util), 0))::bigint AS seconds_non_business_day,
               (SELECT count(*)::int FROM (
                    SELECT p2.device_id
                    FROM partes p2
                    GROUP BY p2.device_id
                    HAVING floor(sum(p2.antes + p2.depois + p2.dia_nao_util)) >= 1
                ) g) AS devices_with_activity_outside
        FROM partes p
        """;
}
