-- 014: retorno de e-mail (leitura IMAP das caixas remetentes).
--
-- Guarda so o cabecalho util e 400 caracteres de trecho: a mensagem inteira
-- continua na caixa de e-mail, que ja e o registro dela. Apagar o lead apaga
-- os trechos junto (CASCADE) — mesma retencao de 12 meses do resto.
-- A chave unica (mailbox, uid) e o que impede reprocessar a mesma mensagem.

CREATE TABLE email_inbound (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  mailbox VARCHAR(190) NOT NULL,
  uid INT UNSIGNED NOT NULL,
  message_id VARCHAR(190) NULL,
  in_reply_to VARCHAR(190) NULL,
  from_email VARCHAR(190) NOT NULL DEFAULT '',
  from_name VARCHAR(120) NOT NULL DEFAULT '',
  subject VARCHAR(255) NOT NULL DEFAULT '',
  kind ENUM('humana','auto','bounce','optout','ignorada') NOT NULL,
  bounce_code VARCHAR(12) NULL,
  lead_id BIGINT UNSIGNED NULL,
  outbox_id BIGINT UNSIGNED NULL,
  snippet VARCHAR(400) NULL,
  received_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  handled_at DATETIME NULL,
  handled_by INT UNSIGNED NULL,
  CONSTRAINT fk_in_lead FOREIGN KEY (lead_id) REFERENCES leads(id) ON DELETE CASCADE,
  CONSTRAINT fk_in_outbox FOREIGN KEY (outbox_id) REFERENCES email_outbox(id) ON DELETE SET NULL,
  CONSTRAINT fk_in_user FOREIGN KEY (handled_by) REFERENCES users(id) ON DELETE SET NULL,
  UNIQUE KEY uq_in_uid (mailbox, uid),
  KEY idx_in_pendentes (handled_at, received_at),
  KEY idx_in_lead (lead_id),
  KEY idx_in_kind (kind, received_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
