namespace M351.Api.Contracts;

// ====================================================================== Central de Conformidade
//
// Contrato de GET /api/v1/compliance/summary (AdminPlus, read-only): as EVIDÊNCIAS que a
// controladora precisa reunir quando alguém pergunta "como vocês cumprem a LGPD aqui?" —
// manutenção rodando, ciência registrada pela frota, atividade de auditoria do mês e pedidos de
// titular atendidos. Nada aqui é dado pessoal: são contagens e carimbos de tempo.

/// <summary>
/// Última execução de um job de manutenção. APENAS job_name/finished_at/status: maintenance_runs
/// é tabela GLOBAL (sem tenant_id) e o detail jsonb carrega contagens de TODOS os tenants — expor
/// o detail num endpoint por tenant vazaria volume alheio.
/// </summary>
public record MaintenanceRunSummary(string JobName, DateTimeOffset? FinishedAt, string Status);

/// <summary>
/// Cobertura de ciência do aviso na frota ATIVA (devices status='active'), contada no servidor.
/// Pendente = device ativo sem notice_acked_at: o agente ainda não confirmou que o aviso foi
/// exibido naquela máquina.
/// </summary>
public record NoticeCoverageSummary(int ActiveDevices, int Acknowledged, int Pending);

/// <summary>
/// Contagens do MÊS CORRENTE (no fuso da organização) na trilha do tenant. Leituras de dado
/// pessoal (view_timeline/view_report), exports de relatório (export_csv) e atos de titular
/// (dsr_export/dsr_delete) — o retrato de quem olhou o quê no mês.
/// </summary>
public record AuditActivitySummary(
    string Month,
    int ViewTimeline,
    int ViewReport,
    int ExportCsv,
    int DsrExport,
    int DsrDelete);

/// <summary>Pacotes DSR de titular/dispositivo por status (queued|running|done|failed).</summary>
public record DsrExportStatusSummary(string Status, int Count);

/// <summary>
/// Resposta de GET /api/v1/compliance/summary. generated_at é o carimbo do dossiê impresso
/// (a página imprime a si mesma, então a data precisa vir do servidor).
/// </summary>
public record ComplianceSummaryResponse(
    string OrganizationName,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<MaintenanceRunSummary> MaintenanceRuns,
    NoticeCoverageSummary NoticeCoverage,
    AuditActivitySummary AuditActivity,
    IReadOnlyList<DsrExportStatusSummary> DsrExports);
