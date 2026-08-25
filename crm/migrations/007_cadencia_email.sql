-- 007: cadência de e-mail — qual e-mail da sequência foi enviado (1º a 5º),
-- tipo da tarefa (para a anterior fechar sozinha) e tabela de configurações.
-- app_settings guarda os prazos em DIAS ÚTEIS e os 5 modelos de e-mail
-- (assunto + corpo), todos editáveis em settings.php. Os defaults moram no
-- PHP (lib/settings.php), então esta migration só cria a estrutura.

ALTER TABLE interactions ADD COLUMN email_seq TINYINT UNSIGNED NULL AFTER type;

ALTER TABLE tasks
  ADD COLUMN kind VARCHAR(20) NOT NULL DEFAULT 'manual' AFTER title,
  ADD KEY idx_tasks_kind (lead_id, kind, done_at);

CREATE TABLE app_settings (
  k VARCHAR(64) PRIMARY KEY,
  v TEXT NOT NULL,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
