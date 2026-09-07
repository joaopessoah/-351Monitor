using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6 — hourly_activity: distribuição do tempo ativo e ocioso pelas 24 horas LOCAIS do
    /// tenant, por (dia, device, lane de usuário). Alimenta o gráfico "Atividade ao longo do
    /// dia" da Visão Geral, que não tinha fonte: o endpoint activity-by-hour foi desenhado no
    /// design da F2 e nunca construído, e varrer activity_intervals a cada leitura não escala
    /// na ordem de grandeza N25 (~2.500 devices).
    ///
    /// Preenchida na MESMA transação REPEATABLE READ da agregação diária, do mesmo snapshot,
    /// com delete-and-rebuild por (tenant, device, dia) — herda de graça a idempotência e a
    /// coerência com daily_device_summaries (invariante 11.3 entre as três leituras).
    ///
    /// Sem particionamento, por decisão: são no máximo 24 linhas por (dia × device × lane),
    /// uma ordem de grandeza abaixo de daily_app_usage, e a purga de retenção (24 meses, junto
    /// dos outros agregados diários) é um DELETE por data. A PK é o índice de escrita; o índice
    /// por (tenant, summary_date) serve a leitura por período.
    /// </summary>
    public partial class AtividadePorHoraF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE TABLE hourly_activity (
                    tenant_id       uuid     NOT NULL,
                    summary_date    date     NOT NULL,
                    hour_local      smallint NOT NULL,
                    device_id       uuid     NOT NULL,
                    device_user_id  uuid     NOT NULL,
                    seconds_active  int      NOT NULL,
                    seconds_idle    int      NOT NULL,
                    CONSTRAINT pk_hourly_activity
                        PRIMARY KEY (tenant_id, summary_date, hour_local, device_id, device_user_id),
                    CONSTRAINT ck_hourly_activity_hour CHECK (hour_local BETWEEN 0 AND 23)
                );
                CREATE INDEX ix_hourly_activity_tenant_date ON hourly_activity (tenant_id, summary_date);
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP TABLE hourly_activity;");
    }
}
