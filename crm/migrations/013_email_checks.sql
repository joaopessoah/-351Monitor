-- 013: pre-validacao de e-mail com cache por endereco.
--
-- email_checks e o cache (30 dias) do resultado de sintaxe + MX + genericos +
-- descartaveis. O status por CONTATO fica em lead_contacts porque e o que a
-- tela mostra e o que a cadencia consulta; bounce real sobrescreve tudo e nao
-- expira — e o unico verificador que nunca erra.

CREATE TABLE email_checks (
  email VARCHAR(190) PRIMARY KEY,
  status ENUM('valido','generico','invalido','descartavel','bounce') NOT NULL,
  mx_ok TINYINT(1) NOT NULL DEFAULT 0,
  detail VARCHAR(180) NULL,
  checked_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY idx_check_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

ALTER TABLE lead_contacts
  ADD COLUMN email_status VARCHAR(16) NOT NULL DEFAULT 'nao_verificado' AFTER email,
  ADD COLUMN email_checked_at DATETIME NULL AFTER email_status,
  ADD KEY idx_lc_email_status (email_status);
