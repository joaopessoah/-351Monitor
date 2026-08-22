using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F5, assinatura do relatório de jornada semanal por e-mail. A PREFERÊNCIA já existe
    /// (user_email_prefs.jornada_weekly, migration WeeklyDigestF5); o que falta é a ENTREGA em
    /// duas etapas: toda segunda 07h (no fuso da org) o job enfileira um export jornada_csv no
    /// pipeline assíncrono que já existe e, num ciclo seguinte, quando o arquivo fica pronto,
    /// manda o LINK do download autenticado por e-mail (nunca anexo).
    ///
    /// jornada_report_deliveries costura as duas etapas: guarda qual export job atende qual
    /// assinante em qual semana e se o e-mail já saiu.
    ///  - PK id (UUIDv7, padrão das demais tabelas);
    ///  - UNIQUE (user_id, week_start) é a IDEMPOTÊNCIA do job: rodando de 5 em 5 minutos dentro
    ///    da janela das 07h, o segundo INSERT da mesma semana é recusado pelo próprio banco, sem
    ///    depender de relógio nem de estado em memória, e duas instâncias do worker também não
    ///    enfileiram duas vezes;
    ///  - índice parcial das pendentes: o passo de entrega varre só o que ainda não saiu;
    ///  - FK de user_id para users e de export_job_id para export_jobs, ambas ON DELETE CASCADE
    ///    (usuário removido ou job expurgado pelo housekeeping não deixam entrega órfã).
    ///
    /// Tabela SEM entidade EF (acesso via Npgsql no worker), mesmo padrão de device_alert_state.
    /// </summary>
    public partial class JornadaSemanalF5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE jornada_report_deliveries (
                  id uuid PRIMARY KEY,
                  tenant_id uuid NOT NULL,
                  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
                  export_job_id uuid NOT NULL REFERENCES export_jobs (id) ON DELETE CASCADE,
                  week_start date NOT NULL,
                  week_end date NOT NULL,
                  queued_at timestamptz NOT NULL,
                  emailed_at timestamptz,
                  gave_up_at timestamptz
                );

                CREATE UNIQUE INDEX ux_jrd_user_week ON jornada_report_deliveries (user_id, week_start);
                CREATE INDEX ix_jrd_pendentes ON jornada_report_deliveries (queued_at)
                  WHERE emailed_at IS NULL AND gave_up_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS jornada_report_deliveries;");
        }
    }
}
