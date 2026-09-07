using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6, decisão 1 do spec de 07/09/2026 — organizations.classification_vocabulary: a
    /// organização escolhe COMO os rótulos de classificação aparecem no portal.
    ///
    /// DEFAULT 'produtividade' (Produtivo / Neutro / Improdutivo / Sem classificação): é o
    /// padrão da categoria e o que o site já promete. 'trabalho' mantém o conjunto neutro
    /// (Relacionado ao trabalho / Neutro / Não relacionado ao trabalho / Não categorizado)
    /// para quem prefere não usar adjetivo.
    ///
    /// Só ROTULAGEM: nem a coluna categories.classification (+1/0/−1) nem os baldes de
    /// daily_device_summaries mudam de semântica, então trocar o vocabulário NÃO enfileira
    /// reagregação — diferente da troca de classificação de uma categoria. O CHECK existe
    /// porque o valor é contrato de produto (o portal resolve rótulo por ele), não texto livre.
    ///
    /// Sem backfill: toda org existente cai no default, que é o rótulo aprovado como padrão.
    /// </summary>
    public partial class VocabularioClassificacaoF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "classification_vocabulary",
                table: "organizations",
                type: "text",
                nullable: false,
                defaultValue: "produtividade");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organizations_classification_vocabulary",
                table: "organizations",
                sql: "classification_vocabulary IN ('produtividade','trabalho')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_organizations_classification_vocabulary",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "classification_vocabulary",
                table: "organizations");
        }
    }
}
