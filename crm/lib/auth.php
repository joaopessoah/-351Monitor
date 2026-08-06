<?php
/** Autenticação por sessão: 2 usuários (João e Bruna), troca de senha forçada no 1º acesso. */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

function auth_user(): ?array
{
    if (empty($_SESSION['uid'])) {
        return null;
    }
    static $user = null;
    if ($user === null) {
        $user = row(
            'SELECT id, name, email, must_change_password FROM users WHERE id = ? AND is_active = 1',
            [$_SESSION['uid']]
        );
        if ($user === null) {
            unset($_SESSION['uid']);
            return null;
        }
    }
    return $user;
}

/** Exige login; enquanto must_change_password=1, só settings.php é acessível. */
function auth_require(): array
{
    session_boot();
    $user = auth_user();
    if ($user === null) {
        redirect('login.php');
    }
    $script = basename($_SERVER['SCRIPT_NAME'] ?? '');
    if ((int) $user['must_change_password'] === 1 && $script !== 'settings.php') {
        redirect('settings.php');
    }
    return $user;
}

function auth_login(string $email, string $password): ?array
{
    $u = row('SELECT * FROM users WHERE email = ? AND is_active = 1', [mb_strtolower(trim($email))]);
    // '!' é o hash sentinela do seed: nunca valida (senha real só existe após o migrate).
    if ($u === null || $u['password_hash'] === '!' || !password_verify($password, $u['password_hash'])) {
        return null;
    }
    session_regenerate_id(true);
    $_SESSION['uid'] = (int) $u['id'];
    q('UPDATE users SET last_login_at = NOW() WHERE id = ?', [$u['id']]);
    if (password_needs_rehash($u['password_hash'], PASSWORD_DEFAULT)) {
        q('UPDATE users SET password_hash = ? WHERE id = ?', [password_hash($password, PASSWORD_DEFAULT), $u['id']]);
    }
    return $u;
}

function auth_logout(): void
{
    $_SESSION = [];
    if (ini_get('session.use_cookies')) {
        $p = session_get_cookie_params();
        setcookie(session_name(), '', time() - 42000, $p['path'], $p['domain'], $p['secure'], $p['httponly']);
    }
    session_destroy();
}
