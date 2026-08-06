<?php
/** Layout compartilhado das páginas autenticadas + labels/formatadores pt-BR. */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

const STATUS_LABELS = [
    'novo'           => 'Novo',
    'contato_feito'  => 'Contato feito',
    'demo_agendada'  => 'Demo agendada',
    'demo_realizada' => 'Demo realizada',
    'trial'          => 'Trial',
    'cliente'        => 'Cliente',
    'perdido'        => 'Perdido',
];

const SOURCE_LABELS = [
    'site'      => 'Site',
    'whatsapp'  => 'WhatsApp',
    'email'     => 'E-mail',
    'indicacao' => 'Indicação',
    'lista_50'  => 'Lista 50',
    'outro'     => 'Outro',
];

const PLAN_LABELS = [
    'essencial'  => 'Essencial',
    'pro'        => 'Pro',
    'indefinido' => 'Indefinido',
];

const INTERACTION_LABELS = [
    'whatsapp' => 'WhatsApp',
    'email'    => 'E-mail',
    'ligacao'  => 'Ligação',
    'demo'     => 'Demo',
    'reuniao'  => 'Reunião',
    'outro'    => 'Outro',
];

function fmt_dt(?string $dt): string
{
    return $dt ? date('d/m/Y H:i', strtotime($dt)) : '—';
}

function fmt_date(?string $dt): string
{
    return $dt ? date('d/m/Y', strtotime($dt)) : '—';
}

function flash_set(string $type, string $msg): void
{
    $_SESSION['flash'] = ['type' => $type, 'msg' => $msg];
}

function flash_get(): ?array
{
    $f = $_SESSION['flash'] ?? null;
    unset($_SESSION['flash']);
    return $f;
}

function status_badge(string $status): string
{
    return '<span class="badge badge-' . esc($status) . '">' . esc(STATUS_LABELS[$status] ?? $status) . '</span>';
}

/** Link wa.me a partir do fone normalizado (só dígitos com DDI). */
function wa_link(?string $whatsapp): string
{
    if (!$whatsapp) {
        return '—';
    }
    return '<a href="https://wa.me/' . esc($whatsapp) . '" target="_blank" rel="noopener">' . esc($whatsapp) . '</a>';
}

function page_header(string $title, string $active, array $user): void
{
    security_headers();
    $flash = flash_get();
    $items = [
        'index.php'    => 'Dashboard',
        'leads.php'    => 'Leads',
        'kanban.php'   => 'Kanban',
        'import.php'   => 'Importar',
        'settings.php' => 'Configurações',
    ];
    echo '<!doctype html><html lang="pt-BR"><head><meta charset="utf-8">';
    echo '<meta name="viewport" content="width=device-width, initial-scale=1">';
    echo '<meta name="robots" content="noindex, nofollow">';
    echo '<title>' . esc($title) . ' — +351 CRM</title>';
    echo '<link rel="stylesheet" href="assets/crm.css">';
    echo '</head><body>';
    echo '<header class="topbar"><div class="topbar-inner">';
    echo '<a class="brand" href="index.php"><em>+</em>351 <span>CRM</span></a>';
    echo '<nav class="topnav">';
    foreach ($items as $href => $label) {
        echo '<a' . ($href === $active ? ' class="active"' : '') . ' href="' . $href . '">' . $label . '</a>';
    }
    echo '</nav>';
    echo '<div class="topbar-user"><span class="user-name">' . esc($user['name']) . '</span>';
    echo '<form method="post" action="logout.php" class="inline-form">' . csrf_field();
    echo '<button class="btn btn-ghost btn-sm" type="submit">Sair</button></form>';
    echo '</div></div></header><main class="wrap">';
    if ($flash) {
        echo '<div class="flash flash-' . esc($flash['type']) . '">' . esc($flash['msg']) . '</div>';
    }
}

function page_footer(): void
{
    echo '</main><script src="assets/crm.js" defer></script></body></html>';
}
