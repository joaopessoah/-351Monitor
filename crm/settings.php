<?php
/** Configurações: troca de senha (obrigatória no primeiro acesso). */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();

$error = null;
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $atual = (string) ($_POST['senha_atual'] ?? '');
    $nova = (string) ($_POST['senha_nova'] ?? '');
    $confirma = (string) ($_POST['senha_confirma'] ?? '');

    $fresh = row('SELECT password_hash FROM users WHERE id = ?', [$user['id']]);
    if ($fresh === null || !password_verify($atual, $fresh['password_hash'])) {
        $error = 'Senha atual incorreta.';
    } elseif (mb_strlen($nova) < 12) {
        $error = 'A nova senha precisa ter pelo menos 12 caracteres.';
    } elseif ($nova !== $confirma) {
        $error = 'A confirmação não confere com a nova senha.';
    } elseif ($nova === $atual) {
        $error = 'A nova senha precisa ser diferente da atual.';
    } else {
        q('UPDATE users SET password_hash = ?, must_change_password = 0 WHERE id = ?',
            [password_hash($nova, PASSWORD_DEFAULT), $user['id']]);
        session_regenerate_id(true);
        flash_set('ok', 'Senha alterada.');
        redirect('index.php');
    }
}

page_header('Configurações', 'settings.php', $user);
?>
<h1 class="page-title">Configurações</h1>

<?php if ((int) $user['must_change_password'] === 1): ?>
  <div class="flash flash-aviso">Primeiro acesso: defina uma nova senha para continuar.</div>
<?php endif; ?>

<?php if ($error !== null): ?>
  <div class="flash flash-erro"><?= esc($error) ?></div>
<?php endif; ?>

<div class="grid-2">
  <div class="card">
    <h2 class="card-title">Trocar senha</h2>
    <form method="post" class="form-stack">
      <?= csrf_field() ?>
      <div class="field">
        <label for="senha_atual">Senha atual</label>
        <input id="senha_atual" name="senha_atual" type="password" autocomplete="current-password" required>
      </div>
      <div class="field">
        <label for="senha_nova">Nova senha (mínimo 12 caracteres)</label>
        <input id="senha_nova" name="senha_nova" type="password" autocomplete="new-password" minlength="12" required>
      </div>
      <div class="field">
        <label for="senha_confirma">Confirme a nova senha</label>
        <input id="senha_confirma" name="senha_confirma" type="password" autocomplete="new-password" minlength="12" required>
      </div>
      <button class="btn btn-primary" type="submit">Salvar nova senha</button>
    </form>
  </div>

  <div class="card">
    <h2 class="card-title">Conta e backup</h2>
    <p><strong>Login:</strong> <?= esc($user['email']) ?></p>
    <p class="muted">Backup do banco: o plano da Hostinger mantém backups automáticos (hPanel → Arquivos → Backups).
    Antes de aplicar uma migration nova, exporte o banco pelo phpMyAdmin. O botão
    “Exportar CSV” na tela de Leads serve como cópia operacional rápida.</p>
    <p class="muted">Leads sem avanço devem ser eliminados em até 12 meses (política de privacidade).
    Use o botão “Excluir” no detalhe do lead.</p>
  </div>
</div>
<?php page_footer(); ?>
