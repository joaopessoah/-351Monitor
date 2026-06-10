using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Migration inicial com TODAS as tabelas da Seção 7.1 do spec (tenant_id uuid NOT NULL em
    /// toda tabela de dados desde a 1ª migration — Princípio 4). Tabelas particionadas
    /// (raw_events, activity_intervals, audit_log) usam DDL bruto via Sql() porque o EF não
    /// gerencia partições; as partições do mês corrente e do próximo são criadas aqui
    /// (a manutenção contínua é do job PartitionMaintenance — F2+).
    /// A F0 mapeia em EF apenas: organizations, users, invitations, refresh_tokens,
    /// enrollment_keys, devices, audit_log. As demais existem só no banco até as fases seguintes.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS citext;
                """);

            // ----- Identidade e tenancy -----
            migrationBuilder.Sql("""
                CREATE TABLE organizations (
                  id uuid PRIMARY KEY,
                  name text NOT NULL,
                  slug text UNIQUE NOT NULL,
                  timezone text NOT NULL DEFAULT 'America/Sao_Paulo',
                  business_hours jsonb,
                  plan text NOT NULL DEFAULT 'trial',
                  device_limit int,
                  status text NOT NULL DEFAULT 'active',
                  created_at timestamptz NOT NULL DEFAULT now()
                );

                CREATE TABLE users (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
                  email citext NOT NULL,
                  password_hash text,
                  display_name text NOT NULL,
                  role text NOT NULL CHECK (role IN ('owner','admin','viewer')),
                  mfa_secret_enc bytea,
                  mfa_enabled boolean NOT NULL DEFAULT false,
                  failed_login_count int NOT NULL DEFAULT 0,
                  locked_until timestamptz,
                  status text NOT NULL DEFAULT 'invited',
                  last_login_at timestamptz,
                  UNIQUE (tenant_id, email)
                );
                CREATE INDEX ix_users_email ON users (email);

                CREATE TABLE invitations (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
                  email citext NOT NULL, role text NOT NULL,
                  token_hash bytea NOT NULL,
                  expires_at timestamptz NOT NULL,
                  accepted_at timestamptz, invited_by uuid REFERENCES users(id)
                );
                CREATE INDEX ix_invitations_token_hash ON invitations (token_hash);

                CREATE TABLE refresh_tokens (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
                  user_id uuid NOT NULL REFERENCES users(id),
                  token_hash bytea NOT NULL,
                  expires_at timestamptz NOT NULL,
                  revoked_at timestamptz, user_agent text, ip inet
                );
                CREATE INDEX ix_refresh_tokens_token_hash ON refresh_tokens (token_hash);
                """);

            // ----- Dispositivos e enrollment -----
            migrationBuilder.Sql("""
                CREATE TABLE enrollment_keys (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
                  key_prefix text NOT NULL,
                  key_hash bytea NOT NULL,
                  label text, max_uses int, use_count int NOT NULL DEFAULT 0,
                  expires_at timestamptz, revoked_at timestamptz
                );

                CREATE TABLE devices (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
                  hostname text NOT NULL,
                  display_name text,
                  machine_fingerprint text NOT NULL,
                  os_version text, os_type text NOT NULL DEFAULT 'workstation',
                  agent_version text,
                  enrollment_key_id uuid REFERENCES enrollment_keys(id),
                  token_hash bytea NOT NULL,
                  config_version int NOT NULL DEFAULT 1,
                  tags text[],
                  status text NOT NULL DEFAULT 'active',
                  last_seen_at timestamptz,
                  clock_offset_ms bigint NOT NULL DEFAULT 0,
                  tz_offset_min int,
                  tz_iana text,
                  seq_max bigint NOT NULL DEFAULT 0,
                  notice_acked_at timestamptz,
                  UNIQUE (tenant_id, machine_fingerprint)
                );
                CREATE INDEX ix_devices_tenant_lastseen ON devices (tenant_id, last_seen_at);

                CREATE TABLE device_users (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
                  device_id uuid NOT NULL REFERENCES devices(id),
                  windows_sid text NOT NULL, windows_username text NOT NULL,
                  display_name text,
                  first_seen_at timestamptz NOT NULL, last_seen_at timestamptz NOT NULL,
                  UNIQUE (tenant_id, device_id, windows_sid)
                );

                CREATE TABLE device_commands (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, device_id uuid NOT NULL,
                  type text NOT NULL CHECK (type = 'UNENROLL'),
                  payload jsonb NOT NULL DEFAULT '{}',
                  created_at timestamptz NOT NULL DEFAULT now(), delivered_at timestamptz
                );

                CREATE TABLE device_current_state (
                  tenant_id uuid NOT NULL, device_id uuid PRIMARY KEY,
                  state text NOT NULL,
                  windows_sid text, windows_username text,
                  foreground_process text, foreground_title text,
                  state_since timestamptz,
                  app_since timestamptz,
                  last_contact_at timestamptz NOT NULL,
                  updated_at timestamptz NOT NULL
                );
                """);

            // ----- Telemetria (particionadas — DDL exato da Seção 7.1; EF não gerencia partições) -----
            migrationBuilder.Sql("""
                -- RAW: particionada por DIA (partições criadas por migration/job próprio — SEM pg_partman), retenção 90 dias (N10)
                CREATE TABLE raw_events (
                  tenant_id uuid NOT NULL,
                  device_id uuid NOT NULL,
                  event_id uuid NOT NULL,
                  seq bigint NOT NULL,
                  occurred_at timestamptz NOT NULL,
                  event_type text NOT NULL,
                  tz_offset_min int,
                  mono_ms bigint, boot_id uuid,
                  session_id int, windows_sid text, windows_username text,
                  process_name text,
                  window_title text,
                  payload jsonb,
                  received_at timestamptz NOT NULL DEFAULT now(),
                  PRIMARY KEY (device_id, event_id, occurred_at)
                ) PARTITION BY RANGE (occurred_at);
                CREATE INDEX ix_raw_tenant_dev_time ON raw_events (tenant_id, device_id, occurred_at);

                -- INTERVALOS: particionada por MÊS, retenção 12 meses (N11)
                CREATE TABLE activity_intervals (
                  id uuid NOT NULL,
                  tenant_id uuid NOT NULL, device_id uuid NOT NULL,
                  device_user_id uuid,
                  started_at timestamptz NOT NULL,
                  ended_at timestamptz NOT NULL,
                  state text NOT NULL CHECK (state IN ('active','idle','locked','off_clean','no_data')),
                  app_id uuid,
                  window_title text,
                  data_incomplete boolean NOT NULL DEFAULT false,
                  source_day date NOT NULL,
                  PRIMARY KEY (tenant_id, device_id, started_at, id)
                ) PARTITION BY RANGE (started_at);
                CREATE INDEX ix_intervals_user_time ON activity_intervals (tenant_id, device_user_id, started_at);
                """);

            // ----- Agregados diários (sem partição, retenção 24 meses — N12) -----
            migrationBuilder.Sql("""
                CREATE TABLE daily_device_summaries (
                  tenant_id uuid NOT NULL, summary_date date NOT NULL,
                  device_id uuid NOT NULL,
                  device_user_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                  seconds_active int NOT NULL DEFAULT 0, seconds_idle int NOT NULL DEFAULT 0,
                  seconds_locked int NOT NULL DEFAULT 0, seconds_on int NOT NULL DEFAULT 0,
                  seconds_work_related int NOT NULL DEFAULT 0,
                  seconds_neutral int NOT NULL DEFAULT 0, seconds_not_work_related int NOT NULL DEFAULT 0,
                  first_event_at timestamptz, last_event_at timestamptz,
                  data_incomplete boolean NOT NULL DEFAULT false,
                  computed_at timestamptz NOT NULL,
                  PRIMARY KEY (tenant_id, summary_date, device_id, device_user_id)
                );

                CREATE TABLE daily_app_usage (
                  tenant_id uuid NOT NULL, summary_date date NOT NULL,
                  device_id uuid NOT NULL,
                  device_user_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                  app_id uuid NOT NULL,
                  seconds_active int NOT NULL, focus_count int NOT NULL,
                  PRIMARY KEY (tenant_id, summary_date, device_id, device_user_id, app_id)
                );
                CREATE INDEX ix_dau_tenant_date_app ON daily_app_usage (tenant_id, summary_date, app_id);
                """);

            // ----- Catálogo de apps e categorias -----
            migrationBuilder.Sql("""
                CREATE TABLE app_catalog (
                  id uuid PRIMARY KEY,
                  process_name text UNIQUE NOT NULL,
                  display_name text NOT NULL, vendor text,
                  default_category text,
                  curated boolean NOT NULL DEFAULT false
                );

                CREATE TABLE categories (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
                  name text NOT NULL,
                  classification smallint NOT NULL,
                  color text,
                  UNIQUE (tenant_id, name)
                );

                CREATE TABLE tenant_app_categories (
                  tenant_id uuid NOT NULL, app_id uuid NOT NULL REFERENCES app_catalog(id),
                  category_id uuid NOT NULL REFERENCES categories(id),
                  custom_display_name text,
                  PRIMARY KEY (tenant_id, app_id)
                );
                """);

            // ----- Operação, auditoria e LGPD -----
            migrationBuilder.Sql("""
                CREATE TABLE ingest_cursors (
                  tenant_id uuid NOT NULL, device_id uuid PRIMARY KEY,
                  processed_until timestamptz NOT NULL,
                  dirty_from timestamptz,
                  updated_at timestamptz NOT NULL
                );

                CREATE TABLE dirty_days (
                  tenant_id uuid NOT NULL, device_id uuid NOT NULL, day date NOT NULL,
                  PRIMARY KEY (tenant_id, device_id, day)
                );

                -- append-only (sem UPDATE/DELETE pela role da app); retenção 24 meses (N13)
                CREATE TABLE audit_log (
                  id uuid NOT NULL, tenant_id uuid NOT NULL,
                  actor_user_id uuid, actor_ip inet,
                  action text NOT NULL,
                  target_type text, target_id uuid,
                  detail jsonb,
                  occurred_at timestamptz NOT NULL DEFAULT now(),
                  PRIMARY KEY (id, occurred_at)
                ) PARTITION BY RANGE (occurred_at);

                CREATE TABLE export_jobs (
                  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, requested_by uuid NOT NULL,
                  kind text NOT NULL,
                  params jsonb NOT NULL,
                  status text NOT NULL DEFAULT 'queued',
                  file_path text, row_count int, expires_at timestamptz
                );
                """);

            // ----- Partições iniciais: mês corrente e próximo -----
            // raw_events: partições DIÁRIAS cobrindo o mês corrente e o próximo
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                  d date := date_trunc('month', now())::date;
                  fim date := (date_trunc('month', now()) + interval '2 months')::date;
                BEGIN
                  WHILE d < fim LOOP
                    EXECUTE format(
                      'CREATE TABLE IF NOT EXISTS raw_events_%s PARTITION OF raw_events FOR VALUES FROM (%L) TO (%L)',
                      to_char(d, 'YYYYMMDD'), d, d + 1);
                    d := d + 1;
                  END LOOP;
                END $$;
                """);

            // activity_intervals e audit_log: partições MENSAIS (mês corrente e próximo)
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                  m date := date_trunc('month', now())::date;
                  fim date := (date_trunc('month', now()) + interval '2 months')::date;
                BEGIN
                  WHILE m < fim LOOP
                    EXECUTE format(
                      'CREATE TABLE IF NOT EXISTS activity_intervals_%s PARTITION OF activity_intervals FOR VALUES FROM (%L) TO (%L)',
                      to_char(m, 'YYYYMM'), m, (m + interval '1 month')::date);
                    EXECUTE format(
                      'CREATE TABLE IF NOT EXISTS audit_log_%s PARTITION OF audit_log FOR VALUES FROM (%L) TO (%L)',
                      to_char(m, 'YYYYMM'), m, (m + interval '1 month')::date);
                    m := (m + interval '1 month')::date;
                  END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS export_jobs;
                DROP TABLE IF EXISTS audit_log;
                DROP TABLE IF EXISTS dirty_days;
                DROP TABLE IF EXISTS ingest_cursors;
                DROP TABLE IF EXISTS tenant_app_categories;
                DROP TABLE IF EXISTS categories;
                DROP TABLE IF EXISTS app_catalog;
                DROP TABLE IF EXISTS daily_app_usage;
                DROP TABLE IF EXISTS daily_device_summaries;
                DROP TABLE IF EXISTS activity_intervals;
                DROP TABLE IF EXISTS raw_events;
                DROP TABLE IF EXISTS device_current_state;
                DROP TABLE IF EXISTS device_commands;
                DROP TABLE IF EXISTS device_users;
                DROP TABLE IF EXISTS devices;
                DROP TABLE IF EXISTS enrollment_keys;
                DROP TABLE IF EXISTS refresh_tokens;
                DROP TABLE IF EXISTS invitations;
                DROP TABLE IF EXISTS users;
                DROP TABLE IF EXISTS organizations;
                """);
        }
    }
}
