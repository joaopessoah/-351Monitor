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
///  - histórico ANTERIOR aos 30 dias mantém a classificação antiga (documentado pela spec);
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
    /// <summary>Janela de reagregação retroativa, em dias (spec linha 777).</summary>
    public const int WindowDays = 30;

    private const string Sql = """
        INSERT INTO dirty_days (tenant_id, device_id, day)
        SELECT DISTINCT i.tenant_id, i.device_id, i.source_day
        FROM activity_intervals i
        WHERE i.tenant_id = @t
          AND i.started_at >= now() - interval '33 days'
          AND i.source_day >=
              ((now() AT TIME ZONE (SELECT timezone FROM organizations WHERE id = @t))::date - 30)
        ON CONFLICT (tenant_id, device_id, day) DO UPDATE SET day = EXCLUDED.day
        """;

    /// <summary>Conexão própria (chamadores sem transação em aberto). Retorna linhas enfileiradas.</summary>
    public async Task<int> RequestLast30DaysAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await RequestLast30DaysAsync(connection, null, tenantId, ct);
    }

    /// <summary>
    /// Variante para participar da transação do chamador (a escrita do mapeamento e o
    /// enfileiramento da reagregação saem ou ficam JUNTOS). Retorna linhas enfileiradas.
    /// </summary>
    public static async Task<int> RequestLast30DaysAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenantId, CancellationToken ct = default)
    {
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
