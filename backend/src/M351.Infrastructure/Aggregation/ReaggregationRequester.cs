using Npgsql;

namespace M351.Infrastructure.Aggregation;

/// <summary>
/// Reagregação dos últimos 30 dias do TENANT (spec linha 777): mudança de categorização
/// (PATCH de categoria com troca de classification, DELETE de categoria, PUT de mapeamento
/// app→categoria) re-enfileira em dirty_days todos os (device, dia) com intervalos na janela —
/// o próximo ciclo do DailyAggregationService recomputa os baldes seconds_work_related/
/// seconds_neutral/seconds_not_work_related com o mapeamento novo.
///
/// Decisões documentadas:
///  - histórico ANTERIOR à janela mantém a classificação antiga (documentado pela spec). A F6
///    acrescenta a janela SOB DEMANDA (decisão 7): POST /api/v1/reaggregation aceita até 366
///    dias, e a folga de partition pruning acompanha a janela pedida (days + 3);
///  - o ON CONFLICT DO UPDATE no-op é OBRIGATÓRIO: trava a linha viva de dirty_days e fecha a
///    mesma corrida intervalização/agregação já documentada no DailyAggregationService (com
///    DO NOTHING a agregação poderia consumir a marca lendo intervalos pré-commit);
///  - "hoje" é o dia local no FUSO DO TENANT (organizations.timezone), resolvido no próprio SQL;
///  - filtro extra por started_at (janela + folga) só para partition pruning: source_day não é
///    a chave de partição de activity_intervals e sozinho varreria os 12 meses. Intervalos são
///    divididos na meia-noite do tenant, então source_day >= hoje-30 implica started_at dentro
///    de ~31 dias + fuso; 33 dias de folga cobre inclusive troca de fuso da org.
/// </summary>
public sealed class ReaggregationRequester(NpgsqlDataSource dataSource)
{
    /// <summary>Janela DEFAULT da reagregação automática da curadoria, em dias (spec linha 777).</summary>
    public const int WindowDays = 30;

    /// <summary>
    /// Teto da reagregação SOB DEMANDA (F6, decisão 7 do spec de 07/09/2026): 12 meses, que é a
    /// retenção de activity_intervals (N11) — pedir mais que isso não teria intervalo para reler.
    /// </summary>
    public const int MaxWindowDays = 366;

    private const string Sql = """
        INSERT INTO dirty_days (tenant_id, device_id, day)
        SELECT DISTINCT i.tenant_id, i.device_id, i.source_day
        FROM activity_intervals i
        WHERE i.tenant_id = @t
          AND i.started_at >= now() - make_interval(days => @days + 3)
          AND i.source_day >=
              ((now() AT TIME ZONE (SELECT timezone FROM organizations WHERE id = @t))::date - @days)
        ON CONFLICT (tenant_id, device_id, day) DO UPDATE SET day = EXCLUDED.day
        """;

    /// <summary>
    /// Conexão própria (chamadores sem transação em aberto). `days` é limitado a
    /// [1, MaxWindowDays]. Retorna linhas enfileiradas.
    /// </summary>
    public async Task<int> RequestAsync(Guid tenantId, int days = WindowDays, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await RequestAsync(connection, null, tenantId, days, ct);
    }

    /// <summary>
    /// Variante para participar da transação do chamador (a escrita do mapeamento e o
    /// enfileiramento da reagregação saem ou ficam JUNTOS). Retorna linhas enfileiradas.
    /// </summary>
    public static async Task<int> RequestAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenantId,
        int days = WindowDays, CancellationToken ct = default)
    {
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("days", Math.Clamp(days, 1, MaxWindowDays));
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Atalho da janela default — é o que a curadoria de categorias usa.</summary>
    public Task<int> RequestLast30DaysAsync(Guid tenantId, CancellationToken ct = default) =>
        RequestAsync(tenantId, WindowDays, ct);

    /// <summary>Atalho transacional da janela default.</summary>
    public static Task<int> RequestLast30DaysAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenantId,
        CancellationToken ct = default) =>
        RequestAsync(connection, transaction, tenantId, WindowDays, ct);
}
