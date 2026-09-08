using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// AGREGADOS MENSAIS (item 3 do estudo). O teto de 92 dias dos endpoints históricos existe
    /// porque varrer daily_device_summaries num ano inteiro não é leitura de tela — e por causa
    /// dele trimestre e ano eram impossíveis de prometer. monthly_summaries resume a diária no
    /// MESMO grão menos o dia, o que preserva tudo o que o recorte precisa: o filtro por etiqueta
    /// (que mora em devices), a contagem de pessoas (lanes distintas) e a de dispositivos.
    ///
    /// DDL cru como nas tabelas irmãs: daily_*, hourly_*, people e management_alerts também não
    /// são entidades do EF, e o corpo da migration é o esquema real.
    ///
    /// int nos segundos, como na diária: o teto por (mês, device, lane) é 31 × 86 400 = 2 678 400,
    /// longe dos 2,1 bilhões do int.
    /// </summary>
    public partial class AgregadosMensaisF9 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE TABLE monthly_summaries (
                  tenant_id uuid NOT NULL,
                  -- primeiro dia do mês, no fuso do tenant já resolvido pela agregação diária
                  month_start date NOT NULL,
                  device_id uuid NOT NULL,
                  device_user_id uuid NOT NULL,
                  seconds_active int NOT NULL DEFAULT 0, seconds_idle int NOT NULL DEFAULT 0,
                  seconds_locked int NOT NULL DEFAULT 0, seconds_on int NOT NULL DEFAULT 0,
                  seconds_work_related int NOT NULL DEFAULT 0,
                  seconds_neutral int NOT NULL DEFAULT 0,
                  seconds_not_work_related int NOT NULL DEFAULT 0,
                  seconds_unclassified int NOT NULL DEFAULT 0,
                  -- dias do mês em que ESTA lane teve tempo ligado: é o person_days do período,
                  -- que se perderia ao somar os dias fora e não dá para reconstruir depois
                  days_with_data int NOT NULL DEFAULT 0,
                  data_incomplete boolean NOT NULL DEFAULT false,
                  computed_at timestamptz NOT NULL,
                  PRIMARY KEY (tenant_id, month_start, device_id, device_user_id)
                );

                -- MARCA-D'ÁGUA por tenant. Sem ela o job de hora em hora revarreria 24 meses de
                -- diária a cada ciclo, para sempre. Com ela, o ciclo custa o que mudou desde o
                -- anterior — e a reagregação retroativa (decisão 7) se propaga sozinha, porque
                -- reagregar reescreve computed_at das linhas afetadas.
                CREATE TABLE monthly_rollup_state (
                  tenant_id  uuid PRIMARY KEY REFERENCES organizations(id) ON DELETE CASCADE,
                  watermark  timestamptz NOT NULL DEFAULT '-infinity',
                  updated_at timestamptz NOT NULL DEFAULT now()
                );

                -- o rollup pergunta "que meses mudaram desde a marca?" por (tenant, computed_at);
                -- sem este índice a pergunta é uma varredura sequencial da diária inteira
                CREATE INDEX ix_dds_tenant_computed
                  ON daily_device_summaries (tenant_id, computed_at);
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_dds_tenant_computed;
                DROP TABLE IF EXISTS monthly_rollup_state;
                DROP TABLE IF EXISTS monthly_summaries;
                """);
    }
}
