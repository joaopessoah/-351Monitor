-- 012: cadencia automatica de e-mail — estado por lead, fila de saida e feriados.
--
-- lead_cadence: um registro por lead (o lead so esta em uma cadencia por vez).
-- email_outbox: UM e-mail pendente por lead de cada vez. O proximo so e
-- planejado depois que o anterior sai — assim as guardas sao sempre avaliadas
-- com o mundo do dia do envio, e o texto gravado e exatamente o que foi enviado.
-- feriados: business_days_add so pulava fim de semana; a cadencia automatica
-- nao pode agendar o 2o e-mail para 7 de setembro.
--
-- MariaDB-safe: uma instrucao por bloco terminada em ';' no fim da linha
-- (contrato do migrate.php, exercitado por tests/migrations.php).

CREATE TABLE lead_cadence (
  lead_id BIGINT UNSIGNED PRIMARY KEY,
  state ENUM('ativa','pausada','concluida','interrompida') NOT NULL DEFAULT 'ativa',
  current_seq TINYINT UNSIGNED NOT NULL DEFAULT 0,
  sender_user_id INT UNSIGNED NULL,
  contact_id BIGINT UNSIGNED NULL,
  personal_line VARCHAR(300) NULL,
  personal_line_state ENUM('nenhuma','rascunho','aprovada') NOT NULL DEFAULT 'nenhuma',
  next_send_at DATETIME NULL,
  last_sent_at DATETIME NULL,
  stop_reason VARCHAR(180) NULL,
  started_by INT UNSIGNED NULL,
  started_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  stopped_at DATETIME NULL,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  CONSTRAINT fk_cad_lead FOREIGN KEY (lead_id) REFERENCES leads(id) ON DELETE CASCADE,
  CONSTRAINT fk_cad_sender FOREIGN KEY (sender_user_id) REFERENCES users(id) ON DELETE SET NULL,
  CONSTRAINT fk_cad_starter FOREIGN KEY (started_by) REFERENCES users(id) ON DELETE SET NULL,
  CONSTRAINT fk_cad_contact FOREIGN KEY (contact_id) REFERENCES lead_contacts(id) ON DELETE SET NULL,
  KEY idx_cad_state (state, next_send_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE email_outbox (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  lead_id BIGINT UNSIGNED NOT NULL,
  contact_id BIGINT UNSIGNED NULL,
  seq TINYINT UNSIGNED NOT NULL,
  sender_user_id INT UNSIGNED NULL,
  from_email VARCHAR(190) NOT NULL,
  from_name VARCHAR(120) NOT NULL DEFAULT '',
  to_email VARCHAR(190) NOT NULL,
  to_name VARCHAR(120) NOT NULL DEFAULT '',
  subject VARCHAR(255) NOT NULL,
  body MEDIUMTEXT NOT NULL,
  message_id VARCHAR(190) NOT NULL,
  in_reply_to VARCHAR(190) NULL,
  references_hdr TEXT NULL,
  status ENUM('aguardando','agendado','enviando','enviado','falhou','cancelado','pulado') NOT NULL DEFAULT 'agendado',
  skip_reason VARCHAR(180) NULL,
  scheduled_for DATETIME NOT NULL,
  sent_at DATETIME NULL,
  attempts TINYINT UNSIGNED NOT NULL DEFAULT 0,
  last_error VARCHAR(255) NULL,
  interaction_id BIGINT UNSIGNED NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  CONSTRAINT fk_out_lead FOREIGN KEY (lead_id) REFERENCES leads(id) ON DELETE CASCADE,
  CONSTRAINT fk_out_contact FOREIGN KEY (contact_id) REFERENCES lead_contacts(id) ON DELETE SET NULL,
  CONSTRAINT fk_out_sender FOREIGN KEY (sender_user_id) REFERENCES users(id) ON DELETE SET NULL,
  UNIQUE KEY uq_out_msgid (message_id),
  KEY idx_out_due (status, scheduled_for),
  KEY idx_out_lead (lead_id, seq),
  KEY idx_out_sent (sent_at, sender_user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE feriados (
  dia DATE PRIMARY KEY,
  nome VARCHAR(60) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO feriados (dia, nome) VALUES
  ('2026-01-01', 'Confraternizacao Universal'),
  ('2026-02-16', 'Carnaval'),
  ('2026-02-17', 'Carnaval'),
  ('2026-04-03', 'Sexta-feira Santa'),
  ('2026-04-21', 'Tiradentes'),
  ('2026-05-01', 'Dia do Trabalho'),
  ('2026-06-04', 'Corpus Christi'),
  ('2026-09-07', 'Independencia'),
  ('2026-10-12', 'Nossa Senhora Aparecida'),
  ('2026-11-02', 'Finados'),
  ('2026-11-15', 'Proclamacao da Republica'),
  ('2026-11-20', 'Consciencia Negra'),
  ('2026-12-25', 'Natal'),
  ('2027-01-01', 'Confraternizacao Universal'),
  ('2027-02-08', 'Carnaval'),
  ('2027-02-09', 'Carnaval'),
  ('2027-03-26', 'Sexta-feira Santa'),
  ('2027-04-21', 'Tiradentes'),
  ('2027-05-01', 'Dia do Trabalho'),
  ('2027-05-27', 'Corpus Christi'),
  ('2027-09-07', 'Independencia'),
  ('2027-10-12', 'Nossa Senhora Aparecida'),
  ('2027-11-02', 'Finados'),
  ('2027-11-15', 'Proclamacao da Republica'),
  ('2027-11-20', 'Consciencia Negra'),
  ('2027-12-25', 'Natal');
