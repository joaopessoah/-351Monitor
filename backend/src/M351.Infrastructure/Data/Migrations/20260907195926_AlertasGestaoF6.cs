using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6 — ALERTAS DE GESTÃO (spec de 07/09/2026, seção 4). Duas coisas:
    ///
    /// 1) <c>management_alerts</c>: o ESTADO dos alertas avaliados pelo worker sobre os
    ///    agregados diários. Tabela de estado, não de log: a PK
    ///    (tenant_id, kind, scope_type, scope_key) garante UM alerta por (tenant, regra,
    ///    escopo) — reabrir o mesmo alerta atualiza a linha em vez de criar uma nova, que é o
    ///    que impede a Visão Geral de virar uma pilha de repetições do mesmo aviso a cada
    ///    ciclo de 15 min. Mesma escolha do <c>device_alert_state</c> dos alertas de frota.
    ///
    ///    <c>first_seen_at</c> é quando o alerta nasceu (a idade que a tela mostra),
    ///    <c>last_seen_at</c> a última avaliação em que ele ainda estava valendo e
    ///    <c>resolved_at</c> o momento em que deixou de valer. Linha resolvida FICA: é o que
    ///    dá o cooldown anti-flapping (métrica oscilando em volta do limiar não levanta
    ///    alerta novo a cada ciclo) e a memória de "isto já aconteceu aqui".
    ///
    ///    <c>detail</c> é jsonb com os NÚMEROS que geraram o alerta (variação, horas, dias
    ///    contados). O texto exibido é montado a partir deles no AlertsController, para o
    ///    vocabulário ("variação e contexto, nunca julgamento") ter uma fonte única.
    ///
    ///    Sem FK para devices/people de propósito: o escopo é uma CHAVE DE TEXTO
    ///    (etiqueta de equipe, windows_sid, uuid de device, ou '-' na organização) e um
    ///    escopo que desaparece deve resolver o alerta pela regra, não cascatear um DELETE.
    ///
    /// 2) <c>organizations.person_alerts_enabled</c>: o opt-in da decisão 5 — regras de
    ///    escopo PESSOA (dias longos, fora do horário) só são avaliadas se a controladora
    ///    ligar. Default false, sem backfill: o padrão do produto é alerta por equipe.
    /// </summary>
    public partial class AlertasGestaoF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // decisão 5 do spec: alertas de escopo pessoa são opt-in da organização
            migrationBuilder.AddColumn<bool>(
                name: "person_alerts_enabled",
                table: "organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                CREATE TABLE management_alerts (
                    tenant_id     uuid        NOT NULL,
                    kind          text        NOT NULL,
                    scope_type    text        NOT NULL,
                    scope_key     text        NOT NULL,
                    first_seen_at timestamptz NOT NULL,
                    last_seen_at  timestamptz NOT NULL,
                    resolved_at   timestamptz NULL,
                    detail        jsonb       NOT NULL DEFAULT '{}'::jsonb,
                    CONSTRAINT pk_management_alerts
                        PRIMARY KEY (tenant_id, kind, scope_type, scope_key),
                    CONSTRAINT ck_management_alerts_scope_type
                        CHECK (scope_type IN ('organization','team','person','device')),
                    CONSTRAINT ck_management_alerts_janela
                        CHECK (last_seen_at >= first_seen_at)
                );

                -- a consulta do GET /alerts é sempre "os vivos deste tenant, mais novos
                -- primeiro"; o índice parcial deixa as linhas já resolvidas fora do índice.
                CREATE INDEX ix_management_alerts_vivos
                    ON management_alerts (tenant_id, first_seen_at DESC)
                    WHERE resolved_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE management_alerts;");

            migrationBuilder.DropColumn(
                name: "person_alerts_enabled",
                table: "organizations");
        }
    }
}
