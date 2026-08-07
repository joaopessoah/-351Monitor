-- 005: múltiplos contatos por lead (com cargo e flag de decisor) +
--      cargo do contato na fila de prospecção.
-- O contato "principal" espelha os campos contact_name/email/whatsapp do lead
-- (mantidos por compatibilidade com intake, import, API e dedupe).

CREATE TABLE lead_contacts (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  lead_id BIGINT UNSIGNED NOT NULL,
  name VARCHAR(120) NOT NULL,
  cargo VARCHAR(80) NULL,
  email VARCHAR(190) NULL,
  whatsapp VARCHAR(20) NULL,
  is_principal TINYINT(1) NOT NULL DEFAULT 0,
  is_decisor TINYINT(1) NOT NULL DEFAULT 0,
  notes VARCHAR(255) NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  CONSTRAINT fk_lc_lead FOREIGN KEY (lead_id) REFERENCES leads(id) ON DELETE CASCADE,
  KEY idx_lc_lead (lead_id),
  KEY idx_lc_email (email),
  KEY idx_lc_whatsapp (whatsapp)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO lead_contacts (lead_id, name, email, whatsapp, is_principal, is_decisor)
SELECT id, contact_name, email, whatsapp, 1, 0 FROM leads WHERE contact_name <> '';

ALTER TABLE prospect_pool ADD COLUMN contact_cargo VARCHAR(80) NULL AFTER contact_name;
