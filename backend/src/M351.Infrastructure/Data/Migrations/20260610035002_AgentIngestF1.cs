using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F1 — Ingestão fim-a-fim. Todas as tabelas da Seção 7.1 já existem desde a InitialCreate
    /// (raw_events, devices, device_users, device_commands, device_current_state, ingest_cursors...).
    /// Esta migration adiciona APENAS o que a Seção 7 exige para a F1 e ainda não existia:
    ///  - tenant_agent_configs: a config canônica do agente (Seção 5.5) é por tenant e versionada
    ///    (config_version), entregue pelo ack; a Seção 7.1 não a modela explicitamente, mas as
    ///    Seções 5.5/8.7 ("bump de config_version → propaga no próximo ack") a exigem persistida.
    ///  - índice por token_hash em devices: lookup do device token (Bearer dt_...) a cada batch.
    /// (device_commands passou a ser mapeada no EF nesta fase, mas a tabela já existia — nada a criar.)
    /// </summary>
    public partial class AgentIngestF1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE tenant_agent_configs (
                  tenant_id uuid PRIMARY KEY,
                  config_version int NOT NULL DEFAULT 1,
                  heartbeat_sec int NOT NULL DEFAULT 60,                -- N2
                  active_window_poll_sec int NOT NULL DEFAULT 5,        -- N1
                  idle_threshold_sec int NOT NULL DEFAULT 300,          -- N4
                  window_title_policy text NOT NULL DEFAULT 'MASKED_PATTERNS'
                    CHECK (window_title_policy IN ('FULL','MASKED_PATTERNS','APP_ONLY')),
                  masked_patterns text[] NOT NULL,
                  ignored_processes text[] NOT NULL,
                  collection_window jsonb NOT NULL DEFAULT '{"mode":"ALWAYS","days":null,"start":null,"end":null}',
                  updated_at timestamptz NOT NULL DEFAULT now()
                );

                CREATE INDEX ix_devices_token_hash ON devices (token_hash);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_devices_token_hash;
                DROP TABLE IF EXISTS tenant_agent_configs;
                """);
        }
    }
}
