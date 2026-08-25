<?php
/**
 * Endpoint público do analytics do site (site/assets/js/track.js).
 *
 * Recebe três tipos de batida: 'pv' (pageview), 'ev' (clique/evento) e 'end'
 * (tempo de leitura + scroll da página que está saindo). Body em JSON com
 * Content-Type text/plain — assim navigator.sendBeacon e fetch entram como
 * requisição simples, sem preflight.
 *
 * Não grava IP nem usa cookie: o visitante é um hash diário (lib/analytics.php).
 * A resposta do 'pv' devolve o ref_code da visita, que o track.js pendura no
 * texto dos links do WhatsApp para amarrar a conversa ao lead depois.
 */

require __DIR__ . '/lib/bootstrap.php';

security_headers(false);
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

/** Origens que podem bater aqui (o site é same-origin; o apex entra por garantia). */
function collect_origens_ok(): array
{
    $ok = ['https://www.mais351monitor.com.br', 'https://mais351monitor.com.br'];
    if (cfg('app_env') === 'dev') {
        $ok[] = 'http://localhost:8080';
        $ok[] = 'http://127.0.0.1:8080';
    }
    return $ok;
}

function collect_out(int $code, array $body): never
{
    http_response_code($code);
    echo json_encode($body, JSON_UNESCAPED_UNICODE);
    exit;
}

/** URL ou caminho cru → path limpo, sem query e sem /index.html. */
function collect_path($raw): string
{
    $p = (string) parse_url((string) $raw, PHP_URL_PATH);
    if ($p === '' || $p[0] !== '/') {
        $p = '/' . $p;
    }
    $p = preg_replace('~/index\.html?$~i', '/', $p);
    return substr($p, 0, 190);
}

function collect_str($v, int $max): ?string
{
    $s = trim((string) $v);
    // Lixo binário ou UTF-8 quebrado não entra em coluna utf8mb4.
    if ($s === '' || !mb_check_encoding($s, 'UTF-8')) {
        return null;
    }
    // Controles fora (só ASCII: não toca em byte de continuação UTF-8).
    $s = trim(preg_replace('/[\x00-\x1F\x7F]+/', ' ', $s));
    return $s === '' ? null : mb_substr($s, 0, $max);
}

$origem = $_SERVER['HTTP_ORIGIN'] ?? '';
if ($origem !== '') {
    if (!in_array($origem, collect_origens_ok(), true)) {
        collect_out(403, ['ok' => false]);
    }
    header('Access-Control-Allow-Origin: ' . $origem);
    header('Vary: Origin');
}

if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
    header('Access-Control-Allow-Methods: POST, OPTIONS');
    header('Access-Control-Allow-Headers: Content-Type');
    header('Access-Control-Max-Age: 86400');
    collect_out(204, []);
}
if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
    collect_out(405, ['ok' => false]);
}

$ua = substr((string) ($_SERVER['HTTP_USER_AGENT'] ?? ''), 0, 500);
if (is_bot($ua)) {
    collect_out(200, ['ok' => true, 'ignorado' => 'bot']);
}

$raw = (string) file_get_contents('php://input', false, null, 0, 4096);
$in = json_decode($raw, true);
if (!is_array($in)) {
    collect_out(400, ['ok' => false]);
}

$tipo = (string) ($in['t'] ?? '');
if (!in_array($tipo, ['pv', 'ev', 'end'], true)) {
    collect_out(400, ['ok' => false]);
}

try {
    $ip = client_ip();
    $hash = visitor_hash($ip, $ua);
    $path = collect_path($in['p'] ?? '/');

    // Contexto usado só quando a visita é criada: origem, campanha e aparelho.
    parse_str((string) parse_url((string) ($in['p'] ?? ''), PHP_URL_QUERY), $qs);
    $refHost = null;
    $refPath = null;
    $ref = collect_str($in['r'] ?? null, 500);
    if ($ref !== null) {
        $h = strtolower((string) parse_url($ref, PHP_URL_HOST));
        $meu = strtolower(preg_replace('/^www\./', '', (string) ($_SERVER['HTTP_HOST'] ?? '')));
        // Navegação dentro do próprio site não é "origem".
        if ($h !== '' && preg_replace('/^www\./', '', $h) !== $meu) {
            $refHost = substr($h, 0, 120);
            $refPath = substr((string) parse_url($ref, PHP_URL_PATH), 0, 190);
        }
    }
    [$device, $browser, $os] = ua_parse($ua);

    $visit = row(
        'SELECT * FROM site_visits
          WHERE visitor_hash = ? AND last_seen_at >= DATE_SUB(NOW(), INTERVAL ? MINUTE)
          ORDER BY id DESC LIMIT 1',
        [$hash, VISIT_TIMEOUT_MIN]
    );

    // Aba velha mandando 'end' depois do timeout não abre visita nova (senão o
    // painel contaria uma visita de zero páginas para cada aba esquecida aberta).
    if ($visit === null && $tipo === 'end') {
        collect_out(200, ['ok' => true]);
    }

    if ($visit === null) {
        // Rate limit só na criação de visita: 30/h e 120/dia por IP. Assim a
        // throttle_events não cresce com o tráfego — só com visitantes novos.
        if (throttle_blocked('collect', $ip, 30, 60) || throttle_blocked('collect', $ip, 120, 1440)) {
            collect_out(429, ['ok' => false]);
        }
        throttle_add('collect', $ip);

        $visit = visit_open($hash, [
            'path'          => $path,
            'referrer_host' => $refHost,
            'referrer_path' => $refPath,
            'utm_source'    => collect_str($qs['utm_source'] ?? null, 120),
            'utm_medium'    => collect_str($qs['utm_medium'] ?? null, 120),
            'utm_campaign'  => collect_str($qs['utm_campaign'] ?? null, 120),
            'utm_content'   => collect_str($qs['utm_content'] ?? null, 120),
            'utm_term'      => collect_str($qs['utm_term'] ?? null, 120),
            'device'        => $device,
            'browser'       => $browser,
            'os'            => $os,
            'screen_w'      => norm_int($in['sw'] ?? null, 200, 9999) ?: null,
        ]);
        if ($visit === null) {
            collect_out(503, ['ok' => false]);
        }
    }

    if ($tipo === 'pv') {
        view_record($visit, $path, collect_str($in['ti'] ?? null, 160));
        analytics_prune();
        collect_out(200, ['ok' => true, 'ref' => $visit['ref_code']]);
    }

    if ($tipo === 'end') {
        view_close(
            $visit,
            $path,
            (int) (norm_int($in['s'] ?? null, 0, 7200) ?: 0),
            (int) (norm_int($in['sc'] ?? null, 0, 100) ?: 0)
        );
        collect_out(200, ['ok' => true]);
    }

    // 'ev': nome curto e conhecido, o resto é rótulo livre truncado.
    $nome = collect_str($in['n'] ?? null, 48);
    if ($nome === null || !preg_match('/^[a-z0-9_]{2,48}$/', $nome)) {
        collect_out(400, ['ok' => false]);
    }
    event_record(
        $visit,
        $nome,
        $path,
        collect_str($in['l'] ?? null, 120),
        collect_str($in['tg'] ?? null, 190),
        norm_int($in['v'] ?? null, -2000000000, 2000000000) ?: null
    );
    collect_out(200, ['ok' => true, 'ref' => $visit['ref_code']]);
} catch (Throwable $e) {
    error_log('collect: ' . $e->getMessage());
    collect_out(500, ['ok' => false]);
}
