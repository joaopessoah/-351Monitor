using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F4.4 — painel de saude de agentes (entrega F4, objetivo "quais maquinas pararam de reportar").
    /// devices ganha duas colunas materializadas na ingestao a partir do AGENT_TAMPER (N19) — que ate
    /// agora so existia em raw_events (inerte no pipeline, expira em 90 dias N10):
    ///  - last_tamper_at: occurred_at do tamper mais recente ja visto (monotonico via GREATEST, igual
    ///    ao padrao do notice_acked_at).
    ///  - last_tamper_reason: motivo do tamper mais recente (helper_killed | helper_killed_repeatedly |
    ///    pipe_denied).
    /// Ambas NULL ate o primeiro tamper. Colunas mapeadas na entidade Device (EF), entao o ModelSnapshot
    /// e o Designer acompanham. SQL cru (mesmo estilo da InitialCreate) — ADD COLUMN e barato (nullable,
    /// sem default), nao reescreve a tabela.
    /// </summary>
    public partial class DeviceHealthF4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE devices ADD COLUMN last_tamper_at timestamptz;
                ALTER TABLE devices ADD COLUMN last_tamper_reason text;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE devices DROP COLUMN IF EXISTS last_tamper_reason;
                ALTER TABLE devices DROP COLUMN IF EXISTS last_tamper_at;
                """);
        }
    }
}
