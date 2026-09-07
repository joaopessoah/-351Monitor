using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6 — quarto balde do tempo ativo: seconds_unclassified separa "app sem categoria no tenant"
    /// de "app em categoria neutra". Até aqui os dois caíam em seconds_neutral (que é resto), o que
    /// impedia calcular o índice de produtividade (produtivo ÷ classificado) e a cobertura da
    /// classificação sem mentir sobre a curadoria incompleta.
    ///
    /// DDL cru porque daily_device_summaries não é entidade do EF (nasceu em SQL na InitialCreate):
    /// AddColumn geraria o mesmo comando, mas o ModelSnapshot não conhece a tabela — por isso a
    /// migration sai vazia do scaffolding e o corpo é escrito aqui.
    ///
    /// DEFAULT 0 e SEM backfill: a linha antiga fica com o neutro inflado, que é exatamente o que
    /// ela media. Corrigir o histórico é ação explícita do admin pela reagregação sob demanda
    /// (POST /api/v1/reaggregation, até 12 meses) — nunca um UPDATE cego na migration.
    /// </summary>
    public partial class BaldeSemClassificacaoF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "ALTER TABLE daily_device_summaries ADD COLUMN seconds_unclassified int NOT NULL DEFAULT 0;");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "ALTER TABLE daily_device_summaries DROP COLUMN seconds_unclassified;");
    }
}
