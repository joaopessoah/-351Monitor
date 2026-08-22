using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpansaoF5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "notice_text",
                table: "tenant_agent_configs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "notice_version",
                table: "tenant_agent_configs",
                type: "integer",
                nullable: false,
                defaultValue: 1); // versão 1 = o aviso padrão que a frota já reconheceu (NOTICE_ACK existente segue válido)

            migrationBuilder.AddColumn<int>(
                name: "goal_weekly_active_hours",
                table: "organizations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "goal_work_related_pct",
                table: "organizations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "transparency_token",
                table: "devices",
                type: "uuid",
                nullable: true);

            // backfill: todo device existente ganha token (novos recebem no enroll)
            migrationBuilder.Sql(
                "UPDATE devices SET transparency_token = gen_random_uuid() WHERE transparency_token IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_devices_transparency_token",
                table: "devices",
                column: "transparency_token",
                unique: true);

            // F5 — estado dos alertas de saúde de frota (cooldown de 24 h por device+tipo) e
            // snapshot MENSAL congelado de cobrança (fecha o caveat do BillingController:
            // arquivar device deixava de reescrever meses passados). Tabelas SEM entidade EF
            // (acesso via Dapper no worker/API, padrão das daily_*).
            migrationBuilder.Sql("""
                CREATE TABLE device_alert_state (
                  tenant_id uuid NOT NULL,
                  device_id uuid NOT NULL,
                  kind text NOT NULL,
                  last_notified_at timestamptz NOT NULL,
                  PRIMARY KEY (device_id, kind)
                );

                CREATE TABLE device_billing_months (
                  tenant_id uuid NOT NULL,
                  month date NOT NULL,
                  device_id uuid NOT NULL,
                  hostname text NOT NULL,
                  display_name text,
                  had_events boolean NOT NULL,
                  was_enrolled boolean NOT NULL,
                  keep_alive boolean NOT NULL,
                  frozen_at timestamptz NOT NULL,
                  PRIMARY KEY (tenant_id, month, device_id)
                );
                CREATE INDEX ix_dbm_tenant_month ON device_billing_months (tenant_id, month);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_transparency_token",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "notice_text",
                table: "tenant_agent_configs");

            migrationBuilder.DropColumn(
                name: "notice_version",
                table: "tenant_agent_configs");

            migrationBuilder.DropColumn(
                name: "goal_weekly_active_hours",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "goal_work_related_pct",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "transparency_token",
                table: "devices");

            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS device_alert_state;
                DROP TABLE IF EXISTS device_billing_months;
                """);
        }
    }
}
