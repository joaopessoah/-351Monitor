using M351.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Billing;

/// <summary>
/// Congelamento MENSAL do sinal de cobrança (F5), fechando o caveat documentado no próprio
/// BillingController: o relatório de cobráveis lê d.status e last_seen_at NO INSTANTE da
/// execução, então arquivar um device hoje o removia RETROATIVAMENTE de meses passados e um
/// device cujo único sinal era keep-alive desaparecia ao contactar num mês seguinte. Isso é
/// risco real de subfaturamento e de disputa com o cliente no billing manual.
///
/// Este serviço materializa device_billing_months no fechamento: um job roda no dia 1 (no fuso
/// de cada tenant) e congela o mês ANTERIOR com as 3 evidências e o nome do device na época.
/// Mês fechado passa a ser lido da tabela; o mês corrente continua sendo calculado ao vivo.
/// Idempotente: reprocessar o mesmo mês não duplica nem reescreve o que já foi congelado.
/// </summary>
public class BillingSnapshotService(NpgsqlDataSource dataSource, ILogger<BillingSnapshotService> logger)
{
    public const string EvidenceEvents = "events";
    public const string EvidenceEnrolled = "enrolled";
    public const string EvidenceKeepAlive = "keep_alive";

    private sealed record OrgRow(Guid Id, string Timezone);

    /// <summary>
    /// Congela, para cada org, os meses fechados ainda sem snapshot (até 3 meses para trás,
    /// o suficiente para cobrir uma janela de worker parado sem varrer a história toda).
    /// Retorna quantos (org, mês) foram congelados.
    /// </summary>
    public async Task<int> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var frozen = 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var orgs = new List<OrgRow>();
        await using (var cmd = new NpgsqlCommand("SELECT id, timezone FROM organizations", connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                orgs.Add(new OrgRow(reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var org in orgs)
        {
            TimeZoneInfo tz;
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(org.Timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                logger.LogWarning("Congelamento de cobrança: fuso desconhecido {Timezone} na org {OrgId}", org.Timezone, org.Id);
                continue;
            }

            var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).Date);
            var currentMonth = new DateOnly(todayLocal.Year, todayLocal.Month, 1);

            for (var back = 1; back <= 3; back++)
            {
                var month = currentMonth.AddMonths(-back);
                if (await IsFrozenAsync(connection, org.Id, month, ct))
                {
                    continue;
                }

                var rows = await FreezeMonthAsync(connection, org.Id, month, tz, nowUtc, ct);
                if (rows > 0)
                {
                    frozen++;
                    logger.LogInformation(
                        "Cobrança congelada: org {OrgId}, mês {Month}, {Devices} dispositivo(s)",
                        org.Id, month.ToString("yyyy-MM"), rows);
                }
            }
        }

        return frozen;
    }

    private static async Task<bool> IsFrozenAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly month, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM device_billing_months WHERE tenant_id = @t AND month = @m)", connection);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("m", month);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private sealed record CandidateRow(
        Guid Id, string Hostname, string? DisplayName, DateTimeOffset? LastSeenAt, bool HasEvents);

    /// <summary>
    /// Congela um mês. As MESMAS três evidências do BillingController (events, enrolled,
    /// keep_alive) na janela [início, fim) do mês LOCAL do tenant convertida para UTC. A
    /// evidência "enrolled" sai do timestamp do UUIDv7 via Uuid7.TimestampOf, exatamente como
    /// no controller (mesma lógica já coberta por testes, em vez de reimplementá-la em SQL).
    /// </summary>
    private static async Task<int> FreezeMonthAsync(
        NpgsqlConnection connection, Guid tenantId, DateOnly month, TimeZoneInfo tz,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        var fromUtc = LocalMidnightUtc(month, tz);
        var toUtc = LocalMidnightUtc(month.AddMonths(1), tz);

        var candidates = new List<CandidateRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT d.id, d.hostname, d.display_name, d.last_seen_at,
                   EXISTS (
                       SELECT 1 FROM raw_events e
                       WHERE e.tenant_id = d.tenant_id AND e.device_id = d.id
                         AND e.occurred_at >= @from AND e.occurred_at < @to
                   ) AS has_events
            FROM devices d
            WHERE d.tenant_id = @t AND d.status <> 'archived'
            """, connection))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("from", fromUtc);
            cmd.Parameters.AddWithValue("to", toUtc);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                candidates.Add(new CandidateRow(
                    reader.GetGuid(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetBoolean(4)));
            }
        }

        var inserted = 0;
        foreach (var candidate in candidates)
        {
            var enrolledAt = Uuid7.TimestampOf(candidate.Id);
            var wasEnrolled = enrolledAt >= fromUtc && enrolledAt < toUtc;
            var keepAlive = candidate.LastSeenAt >= fromUtc && candidate.LastSeenAt < toUtc;
            if (!candidate.HasEvents && !wasEnrolled && !keepAlive)
            {
                continue; // nenhum sinal de uso no mês: não cobrável
            }

            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO device_billing_months (
                    tenant_id, month, device_id, hostname, display_name,
                    had_events, was_enrolled, keep_alive, frozen_at)
                VALUES (@t, @month, @d, @hostname, @display, @events, @enrolled, @keepalive, @now)
                ON CONFLICT (tenant_id, month, device_id) DO NOTHING
                """, connection);
            insert.Parameters.AddWithValue("t", tenantId);
            insert.Parameters.AddWithValue("month", month);
            insert.Parameters.AddWithValue("d", candidate.Id);
            insert.Parameters.AddWithValue("hostname", candidate.Hostname);
            insert.Parameters.AddWithValue("display", (object?)candidate.DisplayName ?? DBNull.Value);
            insert.Parameters.AddWithValue("events", candidate.HasEvents);
            insert.Parameters.AddWithValue("enrolled", wasEnrolled);
            insert.Parameters.AddWithValue("keepalive", keepAlive);
            insert.Parameters.AddWithValue("now", nowUtc);
            inserted += await insert.ExecuteNonQueryAsync(ct);
        }

        return inserted;
    }

    /// <summary>Meia-noite local do tenant em UTC (mesmo helper do BillingController/timeline).</summary>
    public static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Evidência canônica a partir das 3 flags (events &gt; enrolled &gt; keep_alive).</summary>
    public static string EvidenceOf(bool hadEvents, bool wasEnrolled, bool keepAlive) =>
        hadEvents ? EvidenceEvents : wasEnrolled ? EvidenceEnrolled : keepAlive ? EvidenceKeepAlive : EvidenceKeepAlive;
}
