<?php
/**
 * Runner de migrations. GET mostra o status e um form pedindo a chave;
 * POST com a MIGRATE_KEY correta aplica os .sql pendentes em ordem e,
 * na primeira vez, gera as senhas temporárias dos usuários seed (hash '!').
 */

require __DIR__ . '/lib/bootstrap.php';

security_headers();
header('Content-Type: text/html; charset=utf-8');

/** Divide um arquivo .sql em instruções (uma por bloco, ';' no fim da linha). */
function sql_statements(string $sql): array
{
    $sql = preg_replace('/^--.*$/m', '', $sql);
    $parts = preg_split('/;\s*(?:\r\n|\r|\n|$)/', $sql);
    $stmts = [];
    foreach ($parts as $p) {
        $p = trim($p);
        if ($p !== '') {
            $stmts[] = $p;
        }
    }
    return $stmts;
}

$report = [];
$tempPasswords = [];
$error = null;
$dbInfo = null;
$applied = [];
$pending = [];

// Estado atual (também no GET, para a tela de status)
try {
    $dbInfo = (string) scalar('SELECT VERSION()');
    db()->exec('CREATE TABLE IF NOT EXISTS schema_migrations (
        version VARCHAR(64) PRIMARY KEY,
        applied_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci');
    $applied = array_column(rows('SELECT version FROM schema_migrations ORDER BY version'), 'version');
} catch (Throwable $e) {
    $error = 'Não conectou no banco. Confira o crm_config.php (host/nome/usuário/senha).';
    error_log('migrate: ' . $e->getMessage());
}

$files = glob(__DIR__ . '/migrations/*.sql') ?: [];
sort($files);
foreach ($files as $f) {
    if (!in_array(basename($f), $applied, true)) {
        $pending[] = basename($f);
    }
}

if ($_SERVER['REQUEST_METHOD'] === 'POST' && $error === null) {
    $ip = client_ip();
    $blocked = false;
    try {
        $blocked = throttle_blocked('migrate', $ip, 5, 15);
    } catch (Throwable $e) {
        // throttle_events pode ainda não existir na primeira execução
    }
    $key = (string) ($_POST['key'] ?? '');
    $migrateKey = (string) cfg('migrate_key');
    if ($blocked) {
        http_response_code(429);
        $error = 'Muitas tentativas. Aguarde 15 minutos.';
    } elseif ($migrateKey === '' || strlen($migrateKey) < 32) {
        http_response_code(500);
        $error = 'migrate_key ausente ou curta demais no crm_config.php (mínimo 32 caracteres).';
    } elseif (!hash_equals($migrateKey, $key)) {
        try {
            throttle_add('migrate', $ip);
        } catch (Throwable $e) {
        }
        http_response_code(403);
        $error = 'Chave de migração incorreta.';
    } else {
        try {
            foreach ($files as $f) {
                $version = basename($f);
                if (in_array($version, $applied, true)) {
                    continue;
                }
                foreach (sql_statements((string) file_get_contents($f)) as $stmt) {
                    db()->exec($stmt);
                }
                q('INSERT INTO schema_migrations (version) VALUES (?)', [$version]);
                $applied[] = $version;
                $report[] = "Aplicada: $version";
            }
            if (!$report) {
                $report[] = 'Nenhuma migration pendente.';
            }
            // Usuários seed: gera senha temporária e mostra UMA única vez.
            foreach (rows("SELECT id, name, email FROM users WHERE password_hash = '!'") as $u) {
                $tmp = substr(strtr(base64_encode(random_bytes(12)), '+/', 'Ax'), 0, 16);
                q('UPDATE users SET password_hash = ?, must_change_password = 1 WHERE id = ?',
                    [password_hash($tmp, PASSWORD_DEFAULT), $u['id']]);
                $tempPasswords[] = ['name' => $u['name'], 'email' => $u['email'], 'senha' => $tmp];
            }
            $pending = [];
        } catch (Throwable $e) {
            http_response_code(500);
            $error = 'Falha ao aplicar migration: ' . $e->getMessage();
            error_log('migrate: ' . $e->getMessage());
        }
    }
}
?>
<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>Migrations — +351 CRM</title>
<link rel="stylesheet" href="<?= esc(asset_url('assets/crm.css')) ?>">
</head>
<body>
<main class="wrap wrap-narrow">
  <div class="card">
    <h1 class="card-title"><em class="brand-plus">+</em>351 CRM — Migrations</h1>

    <?php if ($error !== null): ?>
      <div class="flash flash-erro"><?= esc($error) ?></div>
    <?php endif; ?>

    <?php foreach ($report as $r): ?>
      <div class="flash flash-ok"><?= esc($r) ?></div>
    <?php endforeach; ?>

    <?php if ($tempPasswords): ?>
      <div class="card card-inner">
        <h2>Senhas temporárias — copie agora (não aparecem de novo)</h2>
        <table class="table">
          <thead><tr><th>Usuário</th><th>E-mail (login)</th><th>Senha temporária</th></tr></thead>
          <tbody>
          <?php foreach ($tempPasswords as $t): ?>
            <tr><td><?= esc($t['name']) ?></td><td><?= esc($t['email']) ?></td><td><code><?= esc($t['senha']) ?></code></td></tr>
          <?php endforeach; ?>
          </tbody>
        </table>
        <p>No primeiro login a troca de senha é obrigatória.</p>
      </div>
    <?php endif; ?>

    <?php if ($dbInfo !== null): ?>
      <p class="muted">Banco: <?= esc($dbInfo) ?> · Aplicadas: <?= count($applied) ?> · Pendentes: <?= count($pending) ?></p>
      <?php if ($applied): ?><p class="muted">Histórico: <?= esc(implode(', ', $applied)) ?></p><?php endif; ?>
      <?php if ($pending): ?><p class="muted">Pendentes: <?= esc(implode(', ', $pending)) ?></p><?php endif; ?>
    <?php endif; ?>

    <?php if ($pending || $dbInfo === null): ?>
      <form method="post" class="form-stack">
        <div class="field">
          <label for="key">Chave de migração (migrate_key do crm_config.php)</label>
          <input id="key" name="key" type="password" autocomplete="off" required>
        </div>
        <button class="btn btn-primary" type="submit">Aplicar migrations pendentes</button>
      </form>
    <?php else: ?>
      <p><a class="btn btn-ghost" href="login.php">Ir para o login</a></p>
    <?php endif; ?>
  </div>
</main>
</body>
</html>
