using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F4.8 — transparência editável (Seção 8.8). organizations ganha 3 colunas NULLABLE preenchidas
    /// pelo admin em Configurações e exibidas na página pública /transparencia/:slug:
    ///  - finalidade_declarada (text): a finalidade do monitoramento declarada pela controladora;
    ///  - contato_dpo (text): contato do encarregado/DPO da controladora;
    ///  - data_vigencia (date): data de vigência da política declarada.
    /// Texto livre/data — JAMAIS dado pessoal de titular. Mapeadas na entidade Organization (EF),
    /// então o ModelSnapshot e o Designer acompanham. ADD COLUMN nullable sem default não reescreve
    /// a tabela (barato), mesmo padrão do precedente DeviceHealthF4.
    /// </summary>
    public partial class OrganizationTransparencyF4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contato_dpo",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "data_vigencia",
                table: "organizations",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finalidade_declarada",
                table: "organizations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "contato_dpo",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "data_vigencia",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "finalidade_declarada",
                table: "organizations");
        }
    }
}
