using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F3.5 — exports CSV assíncronos. A tabela export_jobs (Seção 7.1) existe desde a
    /// InitialCreate; colunas operacionais que a spec não detalha:
    ///  - created_at: "quem gerou, quando" da tela /relatorios/exportacoes (spec linha 949)
    ///    e ordem de drenagem da fila do worker;
    ///  - started_at: carimbo do claim — jobs presos em 'running' (crash do worker sem
    ///    shutdown gracioso) são devolvidos à fila pelo sweep após 15 min;
    ///  - truncated: o teto de 500 k linhas foi atingido — exposto na listagem para o
    ///    usuário SABER que o CSV é parcial (jamais truncamento silencioso).
    /// export_jobs NÃO é entidade EF (acesso só por Dapper/Npgsql), então a migration é SQL
    /// cru e o ModelSnapshot não muda.
    /// </summary>
    public partial class ExportJobsF3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE export_jobs
                  ADD COLUMN created_at timestamptz NOT NULL DEFAULT now(),
                  ADD COLUMN started_at timestamptz,
                  ADD COLUMN truncated boolean NOT NULL DEFAULT false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE export_jobs
                  DROP COLUMN IF EXISTS created_at,
                  DROP COLUMN IF EXISTS started_at,
                  DROP COLUMN IF EXISTS truncated;
                """);
        }
    }
}
