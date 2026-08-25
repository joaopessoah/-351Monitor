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
    'site'       => 'Site',
    'whatsapp'   => 'WhatsApp',
    'email'      => 'E-mail',
    'indicacao'  => 'Indicação',
    'lista_50'   => 'Lista 50',
    'prospeccao' => 'Prospecção',
    'outro'      => 'Outro',
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


/** Etapas da cadencia de e-mail (interaction.email_seq). */
const CADENCIA_EMAIL_LABELS = [
    1 => 'Primeiro e-mail',
    2 => 'Segundo e-mail',
    3 => 'Terceiro e-mail',
    4 => 'Quarto e-mail',
    5 => 'Quinto e-mail',
];

function fmt_dt(?string $dt): string
{
    return $dt ? date('d/m/Y H:i', strtotime($dt)) : '—';
}

/** DATETIME do banco -> value de input datetime-local. */
function dtlocal_value(?string $dt): string
{
    return $dt ? date('Y-m-d\TH:i', strtotime($dt)) : '';
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

/** 5511999998888 -> (11) 99999-8888. Devolve o que veio se nao reconhecer. */
function fmt_fone(?string $d): string
{
    $d = preg_replace('/\D+/', '', (string) $d);
    if (str_starts_with($d, '55') && (strlen($d) === 12 || strlen($d) === 13)) {
        $d = substr($d, 2);
    }
    if (strlen($d) === 11) {
        return '(' . substr($d, 0, 2) . ') ' . substr($d, 2, 5) . '-' . substr($d, 7);
    }
    if (strlen($d) === 10) {
        return '(' . substr($d, 0, 2) . ') ' . substr($d, 2, 4) . '-' . substr($d, 6);
    }
    return $d;
}

/** Link wa.me a partir do fone normalizado (so digitos com DDI). */
function wa_link(?string $whatsapp): string
{
    if (!$whatsapp) {
        return '—';
    }
    return '<a href="https://wa.me/' . esc($whatsapp) . '" target="_blank" rel="noopener">'
        . esc(fmt_fone($whatsapp)) . '</a>';
}

/** Telefone fixo: link tel: (disca no softphone/celular pareado). */
function tel_link(?string $phone): string
{
    if (!$phone) {
        return '—';
    }
    return '<a href="tel:+' . esc($phone) . '">' . esc(fmt_fone($phone)) . '</a>';
}

/**
 * E-mail como link mailto:, opcionalmente com assunto e corpo do modelo da
 * cadencia — abre o Outlook (ou o app de e-mail padrao) ja preenchido.
 * Corpo em texto puro, cortado em 1500 chars para nao estourar a URL.
 *
 * @param array{assunto: string, corpo: string}|null $modelo
 */
function mailto_link(?string $email, ?array $modelo = null, string $titulo = ''): string
{
    if (!$email) {
        return '—';
    }
    // '?' e '&' sao atext valido em local-part, entao o endereco tambem
    // precisa ser codificado - senao da para injetar bcc no link.
    $href = 'mailto:' . str_replace('%40', '@', rawurlencode($email));
    if ($modelo !== null) {
        $href .= '?subject=' . rawurlencode($modelo['assunto'])
            // Normaliza para CRLF de forma idempotente: o textarea do settings.php
            // ja envia \r\n, e um str_replace ingenuo viraria \r\r\n no Outlook.
            . '&body=' . rawurlencode(mb_substr(
                preg_replace('/\r\n|\r|\n/', "\r\n", $modelo['corpo']), 0, 1500));
    }
    return '<a href="' . esc($href) . '"' . ($titulo !== '' ? ' title="' . esc($titulo) . '"' : '') . '>'
        . esc($email) . '</a>';
}

function page_header(string $title, string $active, array $user): void
{
    security_headers();
    $flash = flash_get();
    $items = [
        'index.php'    => 'Dashboard',
        'leads.php'    => 'Leads',
        'kanban.php'   => 'Kanban',
        'board.php'    => 'Quadro',
        'fila.php'     => 'Fila',
        'import.php'   => 'Importar',
        'settings.php' => 'Configurações',
    ];
    echo '<!doctype html><html lang="pt-BR"><head><meta charset="utf-8">';
    echo '<meta name="viewport" content="width=device-width, initial-scale=1">';
    echo '<meta name="robots" content="noindex, nofollow">';
    echo '<title>' . esc($title) . ' — +351 CRM</title>';
    echo '<link rel="stylesheet" href="' . esc(asset_url('assets/crm.css')) . '">';
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
    echo '</main><script src="' . esc(asset_url('assets/crm.js')) . '" defer></script></body></html>';
}

/** URL do asset com cache-busting automático (?v=mtime) — o .htaccess do site herda 7d de cache para CSS/JS. */
function asset_url(string $rel): string
{
    $mtime = @filemtime(dirname(__DIR__) . '/' . $rel);
    return $rel . ($mtime ? '?v=' . $mtime : '');
}
