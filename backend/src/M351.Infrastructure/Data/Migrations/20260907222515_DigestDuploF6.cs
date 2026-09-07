using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6, digest duplo — <c>organizations.last_personal_digest_at</c>: carimbo de
    /// idempotência do lote de digests PESSOAIS (mesmo padrão do
    /// <c>last_weekly_digest_at</c>, que segue exclusivo do digest do gestor). Dois
    /// carimbos e não um: falha no lote pessoal não pode reenviar o resumo do gestor na
    /// hora seguinte. Só o worker escreve esta coluna.
    ///
    /// O opt-in do digest pessoal NÃO ganhou coluna: reutiliza
    /// <c>organizations.person_alerts_enabled</c>, a decisão já registrada e auditada da
    /// organização sobre comunicar resultado no nível da pessoa (a justificativa completa
    /// está no cabeçalho de WeeklyDigestService).
    /// </summary>
    public partial class DigestDuploF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_personal_digest_at",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_personal_digest_at",
                table: "organizations");
        }
    }
}
