<?php
/** Logout: POST-only (evita logout via link/prefetch), CSRF obrigatório. */

require __DIR__ . '/lib/bootstrap.php';

session_boot();
if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
    redirect('index.php');
}
csrf_check();
auth_logout();
redirect('login.php');
