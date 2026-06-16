using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F4.2 — auto-update de canal único (Seção 6.7). Cria agent_releases: catálogo GLOBAL de
    /// releases do agente (sem tenant_id — o manifesto é o mesmo para todos os devices de todos
    /// os tenants; a única dimensão é o canal, default 'stable'). É a FONTE DA VERDADE do
    /// manifesto: o endpoint lê WHERE channel='stable' AND is_current.
    ///  - is_current marca o release vigente do canal; o índice parcial único garante NO MÁXIMO
    ///    um current por canal (publish/rollback só trocam essa flag — sem redeploy, sem tocar
    ///    nas máquinas, cumprindo o "pronto quando" da F4).
    ///  - sha256 (hex64) e file_name servem o gancho de integridade e a hospedagem do MSI
    ///    (GET /agent/releases/{file_name} faz streaming do diretório Releases:Directory).
    ///  - published_by uuid|null: operador de backoffice que publicou (a CLI não tem usuário de
    ///    portal logado, então fica null no MVP — a trilha vai em audit_log).
    /// agent_releases NÃO é entidade EF (acesso só por Dapper/Npgsql), então a migration é SQL
    /// cru e o ModelSnapshot não muda (o Designer apenas copia o modelo vigente).
    /// </summary>
    public partial class AgentReleasesF4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE agent_releases (
                  id uuid PRIMARY KEY,
                  channel text NOT NULL DEFAULT 'stable',
                  version text NOT NULL,
                  url text NOT NULL,
                  sha256 text NOT NULL,
                  min_version text NOT NULL,
                  file_name text NOT NULL,
                  is_current boolean NOT NULL DEFAULT false,
                  published_at timestamptz NOT NULL DEFAULT now(),
                  published_by uuid
                );

                -- versão única por canal: publicar a MESMA versão duas vezes no mesmo canal é erro
                CREATE UNIQUE INDEX ux_agent_releases_channel_version
                  ON agent_releases (channel, version);

                -- no máximo UM release current por canal (o endpoint lê exatamente esta linha);
                -- rollback = mover esta flag para outra versão, sem nunca ter dois currents
                CREATE UNIQUE INDEX ux_agent_releases_current_per_channel
                  ON agent_releases (channel) WHERE is_current;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS agent_releases;");
        }
    }
}
