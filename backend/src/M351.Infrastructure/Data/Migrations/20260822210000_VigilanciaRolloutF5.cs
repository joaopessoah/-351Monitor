using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Vigilância de rollout: materialização do UPDATE_FAILED (19º tipo da tabela 5.3) em devices,
    /// no mesmo desenho do AGENT_TAMPER da F4.4, três colunas que o ingest avança monotonicamente.
    ///
    ///  - last_update_failure_at: instante da última tentativa de auto-update que NÃO instalou;
    ///  - last_update_failure_reason: a ETAPA que reprovou (download | hash | signature | install),
    ///    categorizada no agente — jamais a mensagem crua da exceção;
    ///  - last_update_target_version: a versão que a tentativa mirava, para separar "a frota inteira
    ///    trava neste release" de "esta máquina tem problema".
    ///
    /// Sem backfill: quem nunca falhou fica NULL, que é a leitura correta. Sem índice novo, também
    /// por decisão: a leitura da distribuição de versões varre os devices ativos do tenant numa
    /// passada só, exatamente como o health-summary já faz.
    /// </summary>
    public partial class VigilanciaRolloutF5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_update_failure_at",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_update_failure_reason",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_update_target_version",
                table: "devices",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_update_failure_at",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "last_update_failure_reason",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "last_update_target_version",
                table: "devices");
        }
    }
}
