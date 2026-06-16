using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F4.6 — jobs de retencao/purga (Secao 7.6, tabela 7.2). Cria maintenance_runs: trilha
    /// GLOBAL (SEM tenant_id) de cada execucao de PartitionMaintenance/RetentionPurge/Housekeeping.
    ///
    /// POR QUE UMA TABELA NOVA, E NAO audit_log (que a spec linha 836 menciona): a purga e GLOBAL
    /// — DROP/DELETE de particao varre TODOS os tenants de uma vez —, mas audit_log e por-tenant
    /// (tenant_id NOT NULL, PK (id, occurred_at)) e seria semanticamente errado atribuir um
    /// evento global a um tenant qualquer. Desvio consciente registrado. A F4.8 (Transparencia)
    /// le "data da ultima purga" daqui (ultimo RetentionPurge/PartitionMaintenance com status='ok').
    ///  - job_name: 'PartitionMaintenance' | 'RetentionPurge' | 'Housekeeping';
    ///  - status: 'ok' | 'error' (gravado mesmo em falha, com a causa em detail);
    ///  - detail jsonb: contagens da execucao (particoes criadas/dropadas, linhas deletadas etc.)
    ///    — a fonte estruturada que as telas de Privacidade/Transparencia exibem.
    /// maintenance_runs NAO e entidade EF (acesso so por Npgsql), entao a migration e SQL cru e o
    /// ModelSnapshot nao muda (o Designer apenas copia o modelo vigente, como em AgentReleasesF4).
    /// </summary>
    public partial class MaintenanceRunsF4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE maintenance_runs (
                  id uuid PRIMARY KEY,
                  job_name text NOT NULL,
                  started_at timestamptz NOT NULL DEFAULT now(),
                  finished_at timestamptz,
                  status text NOT NULL DEFAULT 'ok',
                  detail jsonb NOT NULL DEFAULT '{}'
                );

                -- "ultima execucao do job X" (Transparencia/Privacidade): lookup por job + recencia
                CREATE INDEX ix_maintenance_runs_job_started
                  ON maintenance_runs (job_name, started_at DESC);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS maintenance_runs;");
        }
    }
}
