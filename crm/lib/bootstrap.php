<?php
/**
 * Bootstrap do CRM de leads (+351 Monitor).
 *
 * Ferramenta interna do time comercial — vive fora do spec do produto
 * (docs/PROMPT-DESENVOLVIMENTO.md) e fora do banco PostgreSQL do SaaS.
 * Roda na hospedagem compartilhada da Hostinger (PHP 8.2+ / MariaDB).
 *
 * A configuração (credenciais do banco, chaves) mora em crm_config.php,
 * FORA do webroot e fora do git — ver crm/README.md.
 */

// Acesso HTTP direto a este arquivo não existe (lib/ é negada no .htaccess);
// o guard cobre o caso de a negação falhar.
if (PHP_SAPI !== 'cli' && realpath($_SERVER['SCRIPT_FILENAME'] ?? '') === __FILE__) {
    http_response_code(403);
    exit;
}

define('CRM', 1);

error_reporting(E_ALL);
ini_set('display_errors', '0');
ini_set('log_errors', '1');

date_default_timezone_set('America/Sao_Paulo');

// Localiza crm_config.php: acima do webroot no servidor; raiz do repo no dev local.
$crmConfigCandidates = [];
if (!empty($_SERVER['DOCUMENT_ROOT'])) {
    $crmConfigCandidates[] = dirname($_SERVER['DOCUMENT_ROOT']) . '/crm_config.php';
}
$crmConfigCandidates[] = dirname(__DIR__, 2) . '/crm_config.php'; // dev: <repo>/crm_config.php
$crmConfigCandidates[] = dirname(__DIR__, 3) . '/crm_config.php'; // servidor: domains/<dominio>/crm_config.php

$crmConfig = null;
foreach (array_unique($crmConfigCandidates) as $crmConfigPath) {
    if (is_file($crmConfigPath)) {
        $crmConfig = require $crmConfigPath;
        break;
    }
}
if (!is_array($crmConfig)) {
    http_response_code(503);
    header('Content-Type: text/plain; charset=utf-8');
    exit("CRM nao configurado: crie crm_config.php acima do webroot (instrucoes em crm/README.md).\n");
}

$GLOBALS['CRM_CONFIG'] = $crmConfig + [
    'db_host'       => 'localhost',
    'db_name'       => '',
    'db_user'       => '',
    'db_pass'       => '',
    'migrate_key'   => '',
    'api_tokens'    => [],
    'cookie_secure' => true,
    'app_env'       => 'prod',
];

if ($GLOBALS['CRM_CONFIG']['app_env'] === 'dev') {
    ini_set('display_errors', '1');
}

function cfg(string $key)
{
    return $GLOBALS['CRM_CONFIG'][$key] ?? null;
}

function esc(?string $s): string
{
    return htmlspecialchars((string) $s, ENT_QUOTES, 'UTF-8');
}

/** Headers de segurança. $html=false para respostas JSON (intake/API). */
function security_headers(bool $html = true): void
{
    header('X-Robots-Tag: noindex, nofollow');
    header('X-Content-Type-Options: nosniff');
    header('Referrer-Policy: same-origin');
    if ($html) {
        header("Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'");
        header('X-Frame-Options: DENY');
    }
}

/** Sessão endurecida, isolada do save_path compartilhado da conta. Idempotente. */
function session_boot(): void
{
    if (session_status() === PHP_SESSION_ACTIVE) {
        return;
    }
    ini_set('session.use_strict_mode', '1');
    ini_set('session.gc_maxlifetime', '28800'); // 8h de expediente
    if (!empty($_SERVER['DOCUMENT_ROOT'])) {
        $sessDir = dirname($_SERVER['DOCUMENT_ROOT']) . '/crm_sessions';
        if (is_dir($sessDir) && is_writable($sessDir)) {
            session_save_path($sessDir);
        }
    }
    session_name('crmsess');
    session_set_cookie_params([
        'lifetime' => 0,
        'path'     => '/',
        'secure'   => (bool) cfg('cookie_secure'),
        'httponly' => true,
        'samesite' => 'Lax',
    ]);
    session_start();
}

/** IP do cliente. Shared hosting: REMOTE_ADDR é o valor confiável (não honrar XFF). */
function client_ip(): string
{
    return substr($_SERVER['REMOTE_ADDR'] ?? '0.0.0.0', 0, 45);
}

function redirect(string $to): never
{
    header('Location: ' . $to);
    exit;
}

require __DIR__ . '/db.php';
require __DIR__ . '/csrf.php';
require __DIR__ . '/throttle.php';
require __DIR__ . '/auth.php';
require __DIR__ . '/validate.php';
require __DIR__ . '/cnpj.php';
require __DIR__ . '/settings.php';
require __DIR__ . '/model.php';
require __DIR__ . '/analytics.php';
require __DIR__ . '/render.php';
