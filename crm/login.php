<?php
/** Login do CRM: rate limit por IP (8/15min), CSRF, mensagens genéricas. */

require __DIR__ . '/lib/bootstrap.php';

session_boot();
if (auth_user() !== null) {
    redirect('index.php');
}

$error = null;
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $ip = client_ip();
    if (throttle_blocked('login', $ip, 8, 15)) {
        http_response_code(429);
        $error = 'Muitas tentativas. Aguarde 15 minutos.';
    } else {
        $u = auth_login((string) ($_POST['email'] ?? ''), (string) ($_POST['password'] ?? ''));
        if ($u === null) {
            throttle_add('login', $ip);
            $error = 'E-mail ou senha inválidos.';
        } else {
            redirect((int) $u['must_change_password'] === 1 ? 'settings.php' : 'index.php');
        }
    }
}

security_headers();
?>
<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>Entrar — +351 CRM</title>
<link rel="stylesheet" href="<?= esc(asset_url('assets/crm.css')) ?>">
</head>
<body class="login-body">
<main class="login-card">
  <p class="login-brand"><em>+</em>351 <span>CRM</span></p>
  <h1>Entrar</h1>
  <?php if ($error !== null): ?>
    <div class="flash flash-erro"><?= esc($error) ?></div>
  <?php endif; ?>
  <form method="post" class="form-stack">
    <?= csrf_field() ?>
    <div class="field">
      <label for="email">E-mail</label>
      <input id="email" name="email" type="email" autocomplete="username" required autofocus>
    </div>
    <div class="field">
      <label for="password">Senha</label>
      <input id="password" name="password" type="password" autocomplete="current-password" required>
    </div>
    <button class="btn btn-primary btn-block" type="submit">Entrar</button>
  </form>
  <p class="login-foot">Uso interno do time +351 Monitor.</p>
</main>
</body>
</html>
