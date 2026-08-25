-- 011: analytics próprio do site (views + cliques), sem cookie e sem IP.
-- Gravado por crm/collect.php a partir do site/assets/js/track.js.
--
-- Privacidade (LGPD): não há cookie nem storage no navegador do visitante e o
-- IP não é gravado em nenhuma destas tabelas. O visitante é um hash SHA-256 de
-- (sal secreto + data + IP + user-agent) — irreversível e trocado toda
-- meia-noite, então não dá para seguir a mesma pessoa de um dia para o outro.
-- "Visita" = janela de 30 min de inatividade do mesmo hash (critério do Plausible).
--
-- MariaDB-safe: uma instrução por bloco terminada em ';' no fim da linha.

CREATE TABLE site_visits (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  visitor_hash CHAR(32) NOT NULL,
  ref_code CHAR(6) NOT NULL,
  landing_path VARCHAR(190) NOT NULL,
  referrer_host VARCHAR(120) NULL,
  referrer_path VARCHAR(190) NULL,
  utm_source VARCHAR(120) NULL,
  utm_medium VARCHAR(120) NULL,
  utm_campaign VARCHAR(120) NULL,
  utm_content VARCHAR(120) NULL,
  utm_term VARCHAR(120) NULL,
  device ENUM('desktop','mobile','tablet') NOT NULL DEFAULT 'desktop',
  browser VARCHAR(24) NULL,
  os VARCHAR(24) NULL,
  screen_w SMALLINT UNSIGNED NULL,
  views SMALLINT UNSIGNED NOT NULL DEFAULT 0,
  events SMALLINT UNSIGNED NOT NULL DEFAULT 0,
  lead_id BIGINT UNSIGNED NULL,
  started_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_seen_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_visit_lead FOREIGN KEY (lead_id) REFERENCES leads(id) ON DELETE SET NULL,
  UNIQUE KEY uk_visit_ref (ref_code),
  KEY idx_visit_recente (visitor_hash, last_seen_at),
  KEY idx_visit_started (started_at),
  KEY idx_visit_lead (lead_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE site_views (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  visit_id BIGINT UNSIGNED NOT NULL,
  path VARCHAR(190) NOT NULL,
  title VARCHAR(160) NULL,
  seconds SMALLINT UNSIGNED NULL,
  scroll_pct TINYINT UNSIGNED NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_view_visit FOREIGN KEY (visit_id) REFERENCES site_visits(id) ON DELETE CASCADE,
  KEY idx_view_created (created_at),
  KEY idx_view_path (path, created_at),
  KEY idx_view_visit (visit_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE site_events (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  visit_id BIGINT UNSIGNED NOT NULL,
  name VARCHAR(48) NOT NULL,
  path VARCHAR(190) NOT NULL,
  label VARCHAR(120) NULL,
  target VARCHAR(190) NULL,
  value_num INT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_event_visit FOREIGN KEY (visit_id) REFERENCES site_visits(id) ON DELETE CASCADE,
  KEY idx_event_created (created_at),
  KEY idx_event_name (name, created_at),
  KEY idx_event_visit (visit_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

ALTER TABLE leads ADD COLUMN visit_ref CHAR(6) NULL AFTER utm_campaign, ADD KEY idx_leads_visit_ref (visit_ref);
