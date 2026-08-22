using System.Globalization;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// GET /api/v1/compliance/summary (F5, AdminPlus, READ-ONLY) — a Central de Conformidade.
///
/// Para que serve: quando o jurídico, um cliente ou a própria ANPD pergunta "como vocês cumprem
/// a LGPD aqui?", a controladora precisa de EVIDÊNCIA reunida, não de uma varredura manual em
/// quatro telas. Este endpoint junta o que já existe no banco: as últimas execuções dos jobs de
/// manutenção (a purga de retenção está rodando?), a cobertura de ciência do aviso na frota
/// (todo mundo foi avisado?), a atividade da trilha no mês (quem olhou dado pessoal?) e os
/// pacotes DSR por status (os pedidos de titular foram atendidos?).
///
/// SEM migration e SEM tabela nova: tudo é agregação do que já é gravado.
///
/// PRIVACIDADE E ISOLAMENTO, os dois cuidados que este endpoint exige:
///  1. maintenance_runs é GLOBAL (sem tenant_id) e o detail jsonb carrega contagens de TODOS os
///     tenants. Só job_name, finished_at e status saem daqui — JAMAIS o detail.
///  2. Todo o resto tem tenant_id manuscrito no WHERE. Nenhuma contagem é de dado pessoal
///     individual: são números do tenant, sem nome de pessoa alguma.
///
/// Read-only por natureza: não audita a própria leitura (é agregado de conformidade da
/// organização, não visualização de comportamento de titular — mesmo critério do
/// BillingController).
/// </summary>
[Route("api/v1/compliance")]
[Authorize(Policy = AuthConstants.PolicyAdminPlus)] // Viewer recebe 403
public class ComplianceController(NpgsqlDataSource dataSource, TimeProvider clock) : ApiControllerBase
{
    /// <summary>Jobs de manutenção do worker cuja última execução interessa ao dossiê.</summary>
    private static readonly string[] MaintenanceJobs = ["RetentionPurge", "PartitionMaintenance", "Housekeeping"];

    /// <summary>Ações da trilha contadas no mês corrente (leituras de dado pessoal + atos de titular).</summary>
    private static readonly string[] AuditedActions =
    [
        AuditActions.ViewTimeline, AuditActions.ViewReport, AuditActions.ExportCsv,
        AuditActions.DsrExport, AuditActions.DsrDelete,
    ];

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var tenantId = CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var org = await connection.QuerySingleOrDefaultAsync<OrgRow>(new CommandDefinition(
            "SELECT name, timezone FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, cancellationToken: ct));
        if (org is null) return NotFoundProblem();

        var now = clock.GetUtcNow();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(org.Timezone);

        // início do mês corrente no fuso do TENANT, convertido para UTC (mesmo helper do
        // BillingController): o "mês" do dossiê é o mês de quem lê, não o de UTC
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);
        var monthStart = new DateOnly(todayLocal.Year, todayLocal.Month, 1);
        var monthStartUtc = LocalMidnightUtc(monthStart, tz);

        // ---- 1. manutenção: a última execução de cada job. DISTINCT ON usa o índice
        // (job_name, started_at DESC). SEM o detail jsonb (tabela global — ver docstring).
        var maintenance = (await connection.QueryAsync<MaintenanceRow>(new CommandDefinition(
            """
            SELECT DISTINCT ON (job_name) job_name, finished_at, status
            FROM maintenance_runs
            WHERE job_name = ANY(@Jobs)
            ORDER BY job_name, started_at DESC
            """,
            new { Jobs = MaintenanceJobs }, cancellationToken: ct))).ToList();

        // job que nunca rodou aparece como "sem execução registrada" — a AUSÊNCIA de purga é
        // exatamente o que o dossiê precisa mostrar, então não pode sumir da lista
        var runs = MaintenanceJobs
            .Select(job =>
            {
                var row = maintenance.FirstOrDefault(m => m.JobName == job);
                return new MaintenanceRunSummary(job, row?.FinishedAt, row?.Status ?? "never_run");
            })
            .ToList();

        // ---- 2. cobertura de ciência: contagem SERVER-SIDE na frota ativa
        var coverage = await connection.QuerySingleAsync<CoverageRow>(new CommandDefinition(
            """
            SELECT count(*)::int AS active_devices,
                   count(notice_acked_at)::int AS acknowledged
            FROM devices
            WHERE tenant_id = @TenantId AND status = 'active'
            """,
            new { TenantId = tenantId }, cancellationToken: ct));

        // ---- 3. trilha do mês corrente, por ação (audit_log é particionada por mês: o filtro
        // em occurred_at habilita o pruning)
        var auditRows = (await connection.QueryAsync<ActionCountRow>(new CommandDefinition(
            """
            SELECT action, count(*)::int AS count
            FROM audit_log
            WHERE tenant_id = @TenantId
              AND occurred_at >= @MonthStart
              AND action = ANY(@Actions)
            GROUP BY action
            """,
            new { TenantId = tenantId, MonthStart = monthStartUtc, Actions = AuditedActions },
            cancellationToken: ct))).ToList();

        int CountOf(string action) => auditRows.FirstOrDefault(r => r.Action == action)?.Count ?? 0;

        // ---- 4. pacotes DSR por status. kind LIKE 'dsr_%' cobre dsr_subject e dsr_device (os
        // pedidos de TITULAR); tenant_full fica fora de propósito — é offboarding da
        // organização, não atendimento a um direito de titular.
        var dsrExports = (await connection.QueryAsync<StatusCountRow>(new CommandDefinition(
            """
            SELECT status, count(*)::int AS count
            FROM export_jobs
            WHERE tenant_id = @TenantId AND kind LIKE 'dsr\_%'
            GROUP BY status
            ORDER BY status
            """,
            new { TenantId = tenantId }, cancellationToken: ct))).ToList();

        return Ok(new ComplianceSummaryResponse(
            OrganizationName: org.Name,
            GeneratedAt: now,
            MaintenanceRuns: runs,
            NoticeCoverage: new NoticeCoverageSummary(
                coverage.ActiveDevices, coverage.Acknowledged, coverage.ActiveDevices - coverage.Acknowledged),
            AuditActivity: new AuditActivitySummary(
                monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                CountOf(AuditActions.ViewTimeline),
                CountOf(AuditActions.ViewReport),
                CountOf(AuditActions.ExportCsv),
                CountOf(AuditActions.DsrExport),
                CountOf(AuditActions.DsrDelete)),
            DsrExports: dsrExports.Select(r => new DsrExportStatusSummary(r.Status, r.Count)).ToList()));
    }

    /// <summary>Meia-noite local do tenant convertida para UTC (mesmo helper do billing/timeline).</summary>
    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed record OrgRow(string Name, string Timezone);

    private sealed record MaintenanceRow(string JobName, DateTimeOffset? FinishedAt, string Status);

    private sealed record CoverageRow(int ActiveDevices, int Acknowledged);

    private sealed record ActionCountRow(string Action, int Count);

    private sealed record StatusCountRow(string Status, int Count);
}
