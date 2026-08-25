-- 010: quadro de tarefas do time (o "Trello" interno).
-- O card E a tarefa: as colunas novas vivem em tasks, para o botao de concluir
-- do dashboard, a tarefa do lead e o card do quadro operarem o mesmo done_at.
-- A coluna com is_done = 1 e a de conclusao: soltar o card la grava done_at.

CREATE TABLE board_columns (
  id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(40) NOT NULL,
  sort_order INT UNSIGNED NOT NULL DEFAULT 0,
  is_done TINYINT(1) NOT NULL DEFAULT 0,
  color VARCHAR(16) NOT NULL DEFAULT 'cinza',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY idx_bc_order (sort_order)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO board_columns (name, sort_order, is_done, color) VALUES
  ('A fazer', 1, 0, 'cinza'),
  ('Fazendo', 2, 0, 'azul'),
  ('Bloqueado', 3, 0, 'laranja'),
  ('Feito', 4, 1, 'verde');

ALTER TABLE tasks
  ADD COLUMN description TEXT NULL AFTER title,
  ADD COLUMN column_id INT UNSIGNED NULL AFTER kind,
  ADD COLUMN sort_order INT UNSIGNED NOT NULL DEFAULT 0 AFTER column_id,
  ADD CONSTRAINT fk_task_column FOREIGN KEY (column_id) REFERENCES board_columns(id) ON DELETE SET NULL,
  ADD KEY idx_tasks_board (column_id, sort_order);

UPDATE tasks SET column_id = (SELECT id FROM board_columns WHERE is_done = 0 ORDER BY sort_order LIMIT 1)
  WHERE done_at IS NULL;

UPDATE tasks SET column_id = (SELECT id FROM board_columns WHERE is_done = 1 ORDER BY sort_order LIMIT 1)
  WHERE done_at IS NOT NULL;

UPDATE tasks SET sort_order = id WHERE sort_order = 0;
