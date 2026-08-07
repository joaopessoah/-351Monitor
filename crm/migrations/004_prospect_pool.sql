-- 004: fila de prospecção (prospect_pool).
-- Alimentada mensalmente pelo pipeline tools/leadgen (dados abertos da RFB);
-- o botão "Puxar leads" promove as melhores empresas ainda não usadas a leads.

CREATE TABLE prospect_pool (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  cnpj VARCHAR(14) NOT NULL,
  company VARCHAR(160) NOT NULL,
  contact_name VARCHAR(120) NOT NULL DEFAULT '',
  email VARCHAR(190) NULL,
  whatsapp VARCHAR(20) NULL,
  estacoes SMALLINT UNSIGNED NULL,
  vertical VARCHAR(30) NOT NULL DEFAULT 'outro',
  score TINYINT UNSIGNED NOT NULL DEFAULT 0,
  uf CHAR(2) NULL,
  municipio VARCHAR(120) NULL,
  observacoes TEXT NULL,
  mes_referencia CHAR(7) NULL,
  promoted_lead_id BIGINT UNSIGNED NULL,
  promoted_at DATETIME NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  UNIQUE KEY uq_pool_cnpj (cnpj),
  KEY idx_pool_pick (promoted_at, vertical, score),
  CONSTRAINT fk_pool_lead FOREIGN KEY (promoted_lead_id) REFERENCES leads(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
