using Npgsql;

namespace M351.Infrastructure.Digest;

/// <summary>
/// Números da semana fechada de uma organização — FONTE ÚNICA do digest do gestor
/// (e-mail), do digest pessoal do colaborador e do PDF do resumo (kind resumo_pdf).
/// Existe para que os três artefatos NUNCA divirjam: um gestor que compara o e-mail com o
/// PDF tem de ver o mesmo número.
///
/// As fórmulas são as MESMAS do <c>DashboardController.OverviewTotalsAsync</c>
/// (backend/src/M351.Api/Controllers/DashboardController.cs), repetidas aqui porque a
/// Infrastructure não referencia a Api:
///   índice    = produtivo ÷ (produtivo + neutro + improdutivo)   — "sem classificação"
///               fica FORA do denominador para não punir curadoria incompleta;
///   cobertura = (ativo − sem classificação) ÷ ativo.
/// Índice sem cobertura ao lado é proibido (decisão 4 do spec de 07/09/2026): quem lê os
/// dois sabe de quanto tempo o índice está falando.
///
/// Vocabulário: ocioso NUNCA entra em improdutivo — ocioso é ausência de teclado e mouse,
/// não julgamento de trabalho. Nada aqui produz lista de pessoas ordenada por métrica.
/// </summary>
public static class WeeklySummary
{
    /// <summary>A lane-máquina (UUID zero) não é pessoa — decisão 8 do spec.</summary>
    public static readonly Guid MachineLane = Guid.Empty;

    /// <summary>Enquadramento obrigatório da classificação (idêntico ao portal: lib/classification.ts).</summary>
    public const string Framing = "classificação definida pela sua empresa";

    /// <summary>
    /// Frase pedagógica do ocioso, repetida em TODO artefato que mostre ociosidade
    /// (mesmo conteúdo do IDLE_HINT do portal: components/dashboard/overviewKit.tsx).
    /// </summary>
    public const string IdleLesson =
        "Ocioso significa sem uso de teclado e mouse: reunião, chamada, leitura e conversa "
        + "presencial aparecem como ociosidade. Tempo ocioso não é tempo improdutivo.";

    /// <summary>Rótulos dos quatro baldes por vocabulário da organização (mesma tabela do portal).</summary>
    public static (string Work, string Neutral, string NotWork, string Unclassified) Labels(string? vocabulary) =>
        vocabulary == "trabalho"
            ? ("Relacionado ao trabalho", "Neutro", "Não relacionado ao trabalho", "Não categorizado")
            : ("Produtivo", "Neutro", "Improdutivo", "Sem classificação");

    /// <summary>Rótulo de um app pela classificação da categoria do tenant (+1/0/−1/sem categoria).</summary>
    public static string AppLabel(short? classification, string? vocabulary)
    {
        var (work, neutral, notWork, unclassified) = Labels(vocabulary);
        return classification switch
        {
            1 => work.ToLowerInvariant(),
            0 => neutral.ToLowerInvariant(),
            -1 => notWork.ToLowerInvariant(),
            _ => unclassified.ToLowerInvariant(),
        };
    }

    // ------------------------------------------------------------------ consultas

    /// <summary>
    /// Totais AGREGADOS do tenant no intervalo (datas locais do tenant — summary_date já é o
    /// dia local). Mesma forma do dashboard: junta devices para excluir arquivado, soma TODAS
    /// as lanes (a lane-máquina tem seconds_active estruturalmente 0, então não conta em
    /// dobro) e conta pessoa só fora da lane-máquina. tenant_id manuscrito no WHERE.
    /// </summary>
    public static async Task<WeekTotals> TotalsAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(sum(s.seconds_on), 0)::bigint,
                   COALESCE(sum(s.seconds_active), 0)::bigint,
                   COALESCE(sum(s.seconds_idle), 0)::bigint,
                   COALESCE(sum(s.seconds_locked), 0)::bigint,
                   COALESCE(sum(s.seconds_work_related), 0)::bigint,
                   COALESCE(sum(s.seconds_neutral), 0)::bigint,
                   COALESCE(sum(s.seconds_not_work_related), 0)::bigint,
                   COALESCE(sum(s.seconds_unclassified), 0)::bigint,
                   count(DISTINCT s.device_id)::int,
                   count(DISTINCT s.device_user_id) FILTER (
                       WHERE s.device_user_id <> @machine AND s.seconds_on > 0)::int
            FROM daily_device_summaries s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @t
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @from AND @to
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("machine", MachineLane);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new WeekTotals(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt32(8), reader.GetInt32(9));
    }

    /// <summary>Cinco aplicativos com mais tempo ativo no intervalo (nunca por pessoa).</summary>
    public static async Task<List<TopApp>> TopAppsAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(tac.custom_display_name, ac.display_name) AS display_name,
                   c.classification,
                   sum(dau.seconds_active)::bigint AS seconds_active
            FROM daily_app_usage dau
            JOIN app_catalog ac ON ac.id = dau.app_id
            LEFT JOIN tenant_app_categories tac ON tac.tenant_id = dau.tenant_id AND tac.app_id = dau.app_id
            LEFT JOIN categories c ON c.id = tac.category_id
            WHERE dau.tenant_id = @t AND dau.summary_date BETWEEN @from AND @to
            GROUP BY 1, 2
            ORDER BY seconds_active DESC
            LIMIT 5
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        var apps = new List<TopApp>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            apps.Add(new TopApp(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt16(1),
                reader.GetInt64(2)));
        }

        return apps;
    }

    /// <summary>
    /// Alertas de gestão VIVOS (resolved_at IS NULL) agrupados por tipo e escopo, com
    /// CONTAGEM. Agrupar é decisão de produto, não economia: o digest do gestor não lista
    /// pessoa por pessoa, então alerta de escopo "person" viaja só como número — quem
    /// precisa do nome abre a central de alertas, que é auditada.
    /// </summary>
    public static async Task<List<AlertGroup>> LiveAlertsAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT kind, scope_type, count(*)::int,
                   CASE WHEN scope_type = 'team' AND count(*) = 1 THEN min(scope_key) END AS single_scope
            FROM management_alerts
            WHERE tenant_id = @t AND resolved_at IS NULL
            GROUP BY kind, scope_type
            ORDER BY 3 DESC, kind
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);

        var groups = new List<AlertGroup>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            groups.Add(new AlertGroup(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return groups;
    }

    /// <summary>
    /// Totais da semana POR PESSOA (SID canônico: people.merged_into_sid quando houve
    /// mesclagem). Só alimenta o digest PESSOAL, que vai para o próprio titular — nunca
    /// entra em e-mail de gestor. A ordem é por rótulo (alfabética), jamais por métrica:
    /// ordenar pessoas por desempenho é exatamente o ranking que o produto proíbe.
    /// </summary>
    public static async Task<List<PersonWeek>> PeopleTotalsAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(p.merged_into_sid, du.windows_sid) AS sid,
                   COALESCE(max(pp.display_name), max(du.display_name), max(du.windows_username),
                            COALESCE(p.merged_into_sid, du.windows_sid)) AS label,
                   COALESCE(sum(s.seconds_on), 0)::bigint,
                   COALESCE(sum(s.seconds_active), 0)::bigint,
                   COALESCE(sum(s.seconds_idle), 0)::bigint,
                   COALESCE(sum(s.seconds_locked), 0)::bigint,
                   COALESCE(sum(s.seconds_work_related), 0)::bigint,
                   COALESCE(sum(s.seconds_neutral), 0)::bigint,
                   COALESCE(sum(s.seconds_not_work_related), 0)::bigint,
                   COALESCE(sum(s.seconds_unclassified), 0)::bigint,
                   count(DISTINCT s.device_id)::int
            FROM daily_device_summaries s
            JOIN device_users du ON du.id = s.device_user_id AND du.tenant_id = s.tenant_id
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
            LEFT JOIN people pp ON pp.tenant_id = du.tenant_id
                               AND pp.windows_sid = COALESCE(p.merged_into_sid, du.windows_sid)
            WHERE s.tenant_id = @t
              AND d.status <> 'archived'
              AND s.summary_date BETWEEN @from AND @to
              AND s.device_user_id <> @machine
            GROUP BY COALESCE(p.merged_into_sid, du.windows_sid)
            HAVING COALESCE(sum(s.seconds_on), 0) > 0
            ORDER BY 2
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("machine", MachineLane);

        var people = new List<PersonWeek>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            people.Add(new PersonWeek(
                reader.GetString(0), reader.GetString(1),
                new WeekTotals(
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                    reader.GetInt32(10), 1)));
        }

        return people;
    }

    /// <summary>
    /// Página de transparência DO DISPOSITIVO mais recente da pessoa (/t/{token}) — é a
    /// página que o próprio colaborador vê, com os dados daquela máquina. Sem dispositivo
    /// com token, devolve null e o e-mail cai na página da organização.
    /// </summary>
    public static async Task<Guid?> LatestTransparencyTokenAsync(
        NpgsqlConnection connection, Guid tenantId, string personSid, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT d.transparency_token
            FROM device_users du
            JOIN devices d ON d.id = du.device_id AND d.tenant_id = du.tenant_id
            LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
            WHERE du.tenant_id = @t
              AND COALESCE(p.merged_into_sid, du.windows_sid) = @sid
              AND d.status <> 'archived'
              AND d.transparency_token IS NOT NULL
            ORDER BY du.last_seen_at DESC
            LIMIT 1
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("sid", personSid);

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is Guid token ? token : null;
    }

    /// <summary>
    /// SID canônico → e-mails de usuários ATIVOS do portal que casam com o titular, com a
    /// preferência de e-mail de cada um.
    ///
    /// COMO O DESTINATÁRIO DO DIGEST PESSOAL É RESOLVIDO (não existe coluna de e-mail em
    /// people nem em device_users, e INVENTAR endereço de dado pessoal é inaceitável):
    ///  1. windows_username no formato UPN casando com users.email inteiro
    ///     ("ana@acme.com.br" = "ana@acme.com.br"); ou
    ///  2. a parte local do e-mail casando com o sAMAccountName, isto é, windows_username
    ///     sem o prefixo de domínio ("ACME\ana" → "ana" = parte local de "ana@acme.com.br").
    /// Os dois casamentos são case-insensitive. Quem decide se o e-mail SAI é o chamador:
    /// exige casamento ÚNICO nos dois sentidos (um e-mail para a pessoa, uma pessoa para o
    /// e-mail). Ambíguo ou ausente = nenhum e-mail e um log — jamais um palpite.
    /// </summary>
    public static async Task<List<PersonEmailMatch>> PersonEmailMatchesAsync(
        NpgsqlConnection connection, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(p.merged_into_sid, du.windows_sid) AS sid,
                   lower(u.email) AS email,
                   bool_and(COALESCE(pref.weekly_digest, true)) AS wants
            FROM device_users du
            LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
            JOIN users u ON u.tenant_id = du.tenant_id AND u.status = 'active'
                        AND (lower(u.email) = lower(du.windows_username)
                             OR lower(split_part(u.email, '@', 1))
                                = lower(regexp_replace(du.windows_username, '^.*\\', '')))
            LEFT JOIN user_email_prefs pref ON pref.user_id = u.id
            WHERE du.tenant_id = @t
            GROUP BY 1, 2
            """, connection);
        cmd.Parameters.AddWithValue("t", tenantId);

        var matches = new List<PersonEmailMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            matches.Add(new PersonEmailMatch(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return matches;
    }
}

/// <summary>
/// Totais de um intervalo. Índice e cobertura são PROPRIEDADES calculadas com a fórmula do
/// DashboardController — nunca recalculadas na mão pelo chamador.
/// </summary>
public sealed record WeekTotals(
    long SecondsOn, long SecondsActive, long SecondsIdle, long SecondsLocked,
    long SecondsWork, long SecondsNeutral, long SecondsNotWork, long SecondsUnclassified,
    int DeviceCount, int PersonCount)
{
    public static WeekTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Denominador do índice: tempo ativo COM classificação (sem o balde "sem classificação").</summary>
    public long SecondsClassified => SecondsWork + SecondsNeutral + SecondsNotWork;

    /// <summary>produtivo ÷ classificado (null sem tempo classificado — nunca 0 % falso).</summary>
    public double? Index => SecondsClassified > 0
        ? Math.Round((double)SecondsWork / SecondsClassified, 4)
        : null;

    /// <summary>(ativo − sem classificação) ÷ ativo (null sem tempo ativo).</summary>
    public double? Coverage => SecondsActive > 0
        ? Math.Round((double)(SecondsActive - SecondsUnclassified) / SecondsActive, 4)
        : null;

    /// <summary>ocioso ÷ ligada (null com a máquina nunca ligada).</summary>
    public double? IdleShare => SecondsOn > 0 ? Math.Round((double)SecondsIdle / SecondsOn, 4) : null;
}

public sealed record TopApp(string DisplayName, short? Classification, long SecondsActive);

/// <summary>Alertas vivos de um tipo/escopo. SingleScopeKey só vem preenchido para equipe única.</summary>
public sealed record AlertGroup(string Kind, string ScopeType, int Count, string? SingleScopeKey);

public sealed record PersonWeek(string Sid, string Label, WeekTotals Totals);

public sealed record PersonEmailMatch(string Sid, string Email, bool WantsWeekly);
