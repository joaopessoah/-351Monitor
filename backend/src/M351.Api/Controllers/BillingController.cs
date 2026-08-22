using System.Globalization;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// GET /api/v1/billing/billable-devices?month=YYYY-MM (F3.7, Seção 7.4 linha 816, papel
/// OWNER): relatório interno mensal de devices cobráveis ("device com ≥ 1 batch no mês,
/// excluindo archived"), insumo do billing manual.
///
/// REGRA (decisão para o silêncio da spec, espelhada no campo criteria): cobrável = device
/// NÃO-archived (status ATUAL) com pelo menos um sinal de uso no mês, no FUSO DO TENANT:
///   1. eventos em raw_events no mês (evidence "events");
///   2. registro (enroll) no mês (evidence "enrolled") — cobre o device recém-enrolado e
///      ainda silencioso; o instante do enroll vem do timestamp do UUIDv7 do id (não há
///      coluna enrolled_at e re-enroll por fingerprint preserva o id original);
///   3. last_seen_at no mês (evidence "keep_alive") — cobre o device que só mandou lote
///      VAZIO no mês (keep-alive não gera raw_events, só atualiza last_seen_at).
/// A união das três regras materializa o "≥ 1 batch no mês"; evidence reporta a PRIMEIRA
/// que casou (events &gt; enrolled &gt; keep_alive). Devices revoked NÃO são excluídos: se
/// usaram o serviço no mês, contam (a spec só exclui archived).
///
/// JANELA DE VALIDADE (decisão MVP, achado da revisão da F3.7): o relatório NÃO é um
/// snapshot estável de mês fechado — d.status e last_seen_at são lidos no instante da
/// execução. Dois efeitos: (a) device cujo único sinal do mês seria keep_alive (só lotes
/// vazios) some do relatório assim que contacta de novo num mês seguinte, porque
/// last_seen_at é coluna única e mutável; (b) arquivar um device hoje o remove
/// retroativamente de relatórios de meses passados. Para o billing manual do MVP isso é
/// aceitável DESDE QUE o relatório seja gerado e arquivado logo após o fechamento do mês —
/// o campo criteria avisa o Owner explicitamente. Solução robusta fica de follow-up por
/// exigir migration: persistir o sinal por mês (ex.: tabela device_billing_months
/// alimentada no ingest/keep-alive) e congelar o status vigente no fim do mês cobrado.
///
/// SEM audit (decisão documentada): é relatório de infraestrutura de cobrança (contagem de
/// devices do próprio tenant), não visualização de dado pessoal de titular — o DoD 11.3
/// audita timeline/relatórios/exports/DSR, que expõem comportamento de pessoas.
/// </summary>
[Route("api/v1/billing")]
[Authorize(Policy = AuthConstants.PolicyOwnerOnly)] // Admin recebe 403 (papel da tabela 7.4: Owner)
public class BillingController(NpgsqlDataSource dataSource, TimeProvider clock) : ApiControllerBase
{
    public const string EvidenceEvents = "events";
    public const string EvidenceEnrolled = "enrolled";
    public const string EvidenceKeepAlive = "keep_alive";

    [HttpGet("billable-devices")]
    public async Task<IActionResult> BillableDevices([FromQuery(Name = "month")] string? month, CancellationToken ct)
    {
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "Parâmetro month é obrigatório no formato YYYY-MM.");
        }

        var monthStart = DateOnly.FromDateTime(parsed); // dia 1 do mês pedido
        var monthText = monthStart.ToString("yyyy-MM");

        var tenantId = Auth.CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var timezone = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        if (timezone is null) return NotFoundProblem();

        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);

        // mês FUTURO no fuso do tenant é inválido; o corrente (ainda parcial) é permitido
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).DateTime);
        if (monthStart > new DateOnly(todayLocal.Year, todayLocal.Month, 1))
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                "month não pode ser um mês futuro no fuso do tenant.");
        }

        // F5 — mês CONGELADO (device_billing_months, preenchida pelo job diário de
        // congelamento): passa a ser a fonte da verdade para meses fechados, o que elimina o
        // efeito retroativo descrito acima. O mês corrente segue calculado ao vivo.
        var frozen = (await connection.QueryAsync<FrozenRow>(new CommandDefinition(
            """
            SELECT device_id, hostname, display_name, had_events, was_enrolled, keep_alive, frozen_at
            FROM device_billing_months
            WHERE tenant_id = @TenantId AND month = @Month
            ORDER BY lower(COALESCE(display_name, hostname)), device_id
            """,
            new { TenantId = tenantId, Month = monthStart }, cancellationToken: ct))).ToList();

        if (frozen.Count > 0)
        {
            var frozenItems = frozen.Select(row => new BillableDeviceResponse(
                row.DeviceId, row.DisplayName, row.Hostname,
                Status: "frozen",
                EnrolledAt: Uuid7.TimestampOf(row.DeviceId),
                LastSeenAt: null,
                Evidence: row.HadEvents ? EvidenceEvents
                    : row.WasEnrolled ? EvidenceEnrolled
                    : EvidenceKeepAlive)).ToList();

            var frozenCriteria =
                $"Mês {monthText} FECHADO e congelado em {frozen[0].FrozenAt:dd/MM/yyyy HH:mm} (UTC). " +
                "A contagem reflete o estado no fechamento do mês no fuso do tenant " +
                $"({timezone}) e não muda mais: arquivar ou renomear um dispositivo depois disso " +
                "não altera este extrato. Cobrável: dispositivo não arquivado com pelo menos 1 lote " +
                "no mês (eventos recebidos, registro no mês ou último contato no mês).";

            return Ok(new BillableDevicesResponse(
                monthText, frozenItems.Count, frozenCriteria, frozenItems,
                Frozen: true, FrozenAt: frozen[0].FrozenAt));
        }

        // janela [início, fim) do mês LOCAL do tenant, em instantes UTC — é essa conversão
        // que faz o evento de 23:30 local do dia 31 (já dia 1 seguinte em UTC) contar no mês certo
        var fromUtc = LocalMidnightUtc(monthStart, tz);
        var toUtc = LocalMidnightUtc(monthStart.AddMonths(1), tz);

        // raw_events é particionada por RANGE (occurred_at) em partições DIÁRIAS: o range
        // fechado em occurred_at habilita partition pruning, e o EXISTS por device usa o
        // índice ix_raw_tenant_dev_time (tenant_id, device_id, occurred_at).
        var rows = (await connection.QueryAsync<BillableRow>(new CommandDefinition(
            """
            SELECT d.id, d.display_name, d.hostname, d.status, d.last_seen_at,
                   EXISTS (
                       SELECT 1 FROM raw_events e
                       WHERE e.tenant_id = d.tenant_id AND e.device_id = d.id
                         AND e.occurred_at >= @FromUtc AND e.occurred_at < @ToUtc
                   ) AS has_events
            FROM devices d
            WHERE d.tenant_id = @TenantId AND d.status <> 'archived'
            ORDER BY lower(COALESCE(d.display_name, d.hostname)), d.id
            """,
            new { TenantId = tenantId, FromUtc = fromUtc, ToUtc = toUtc },
            cancellationToken: ct))).ToList();

        var items = new List<BillableDeviceResponse>();
        foreach (var row in rows)
        {
            var enrolledAt = Uuid7.TimestampOf(row.Id);
            var evidence =
                row.HasEvents ? EvidenceEvents
                : enrolledAt >= fromUtc && enrolledAt < toUtc ? EvidenceEnrolled
                : row.LastSeenAt >= fromUtc && row.LastSeenAt < toUtc ? EvidenceKeepAlive
                : null;
            if (evidence is null) continue; // nenhum sinal de uso no mês: não cobrável

            items.Add(new BillableDeviceResponse(
                row.Id, row.DisplayName, row.Hostname, row.Status, enrolledAt, row.LastSeenAt, evidence));
        }

        var criteria =
            $"Cobrável: device não arquivado com pelo menos 1 lote no mês {monthText} no fuso do tenant ({timezone}). " +
            "Conta quem tem eventos recebidos no mês (events), ou foi registrado no mês (enrolled), " +
            "ou teve último contato no mês (keep_alive, lote vazio que só atualiza o last_seen_at). " +
            "Atenção: gere e arquive este relatório logo após o fechamento do mês. O filtro de arquivados " +
            "usa o status atual e o sinal keep_alive usa o último contato atual do device, então executar " +
            "meses depois pode omitir devices arquivados desde então ou que voltaram a contactar.";

        return Ok(new BillableDevicesResponse(monthText, items.Count, criteria, items));
    }

    /// <summary>Meia-noite local do tenant convertida para UTC (mesmo helper da timeline F3.4).</summary>
    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed record BillableRow(
        Guid Id,
        string? DisplayName,
        string Hostname,
        string Status,
        DateTimeOffset? LastSeenAt,
        bool HasEvents);

    /// <summary>Linha congelada de device_billing_months (F5): o estado no fechamento do mês.</summary>
    private sealed record FrozenRow(
        Guid DeviceId,
        string Hostname,
        string? DisplayName,
        bool HadEvents,
        bool WasEnrolled,
        bool KeepAlive,
        DateTimeOffset FrozenAt);
}
