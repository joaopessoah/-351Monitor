using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F4.7 — APPEND-ONLY REAL de audit_log (contrato 4; Seção 9.5; DoD 11.3). A InitialCreate
    /// declarou audit_log como append-only apenas em COMENTÁRIO. Esta migration impõe a regra:
    ///
    ///  1. FUNCTION + TRIGGER (defesa PRINCIPAL, independente de role): um trigger BEFORE
    ///     UPDATE OR DELETE de LINHA na tabela particionada audit_log levanta exceção. Em
    ///     PostgreSQL 13+ o trigger ROW-LEVEL definido no PARENT particionado é PROPAGADO
    ///     automaticamente a TODAS as partições (atuais e futuras) — não é preciso recriá-lo por
    ///     partição. Qualquer UPDATE/DELETE de linha (por qualquer role, inclusive o owner) aborta
    ///     com SQLSTATE da exceção. INSERT continua livre (a trilha cresce normalmente).
    ///
    ///     POR QUE O TRIGGER NÃO ATRAPALHA A RETENÇÃO (N13): a purga do PartitionMaintenance
    ///     (F4.6) faz DROP TABLE da partição filha — isso é DDL (remoção da relação inteira), NÃO
    ///     um DELETE de linha, então o trigger row-level NÃO dispara. A partição expirada continua
    ///     sendo dropada normalmente com o trigger ativo (garantido por teste de regressão em
    ///     RetentionJobsTests/AuditLogTests).
    ///
    ///  2. REVOKE UPDATE, DELETE defensivo (defesa em PROFUNDIDADE): revoga de PUBLIC os privilégios
    ///     de UPDATE/DELETE sobre audit_log. A eficácia PLENA exige que a aplicação conecte com uma
    ///     role NÃO-owner (o owner da tabela ignora GRANT/REVOKE e só é barrado pelo trigger) — isso
    ///     é item de INFRA/RUNBOOK (provisionar uma role de app sem ownership do schema), FORA do
    ///     alcance desta migration. O trigger é a garantia que não depende de role.
    ///
    /// SQL cru (a regra não é modelável em EF); o ModelSnapshot/Designer não muda (nenhuma coluna
    /// nova) — mesmo precedente de MaintenanceRunsF4/AgentReleasesF4.
    /// </summary>
    public partial class AuditLogAppendOnlyF4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- function append-only: barra qualquer UPDATE/DELETE de linha em audit_log
                CREATE OR REPLACE FUNCTION audit_log_append_only()
                RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'audit_log e append-only: % nao e permitido (trilha imutavel - F4.7)', TG_OP
                    USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                -- trigger ROW-LEVEL no PARENT particionado: PG13+ propaga a TODAS as particoes
                -- (atuais e futuras). INSERT fica livre; UPDATE/DELETE de linha abortam.
                DROP TRIGGER IF EXISTS trg_audit_log_append_only ON audit_log;
                CREATE TRIGGER trg_audit_log_append_only
                  BEFORE UPDATE OR DELETE ON audit_log
                  FOR EACH ROW EXECUTE FUNCTION audit_log_append_only();

                -- defesa em profundidade: revoga UPDATE/DELETE de PUBLIC (eficacia plena exige role
                -- de app nao-owner - runbook de infra). O trigger acima e a garantia role-independente.
                REVOKE UPDATE, DELETE ON audit_log FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_log_append_only ON audit_log;
                DROP FUNCTION IF EXISTS audit_log_append_only();
                GRANT UPDATE, DELETE ON audit_log TO PUBLIC;
                """);
        }
    }
}
