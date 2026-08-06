-- 002: usuários iniciais (João e Bruna).
-- O hash sentinela '!' nunca valida em password_verify; após aplicar as migrations,
-- o migrate.php troca por uma senha temporária aleatória e a exibe UMA única vez.

INSERT INTO users (name, email, password_hash, must_change_password) VALUES ('João', 'joao@mais351monitor.com.br', '!', 1);

INSERT INTO users (name, email, password_hash, must_change_password) VALUES ('Bruna', 'bruna@mais351monitor.com.br', '!', 1);
