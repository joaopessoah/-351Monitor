<?php
/**
 * Analytics do site institucional: identificação cookieless do visitante,
 * gravação de visitas/views/cliques (crm/collect.php) e as consultas do
 * painel (crm/analytics.php).
 *
 * Nenhuma função daqui grava IP. O visitante é um hash diário irreversível —
 * ver o cabeçalho de migrations/011_analytics.sql.
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

/** Inatividade que fecha uma visita, em minutos (mesmo critério do Plausible). */
const VISIT_TIMEOUT_MIN = 30;

/** Tetos por visita: seguram flood sem exigir uma linha de throttle por hit. */
const VISIT_MAX_VIEWS  = 200;
const VISIT_MAX_EVENTS = 300;

/** Retenção das visitas que nunca viraram lead (as que viraram ficam com o lead). */
const VISIT_RETENTION_DAYS = 365;

/** Alfabeto do ref_code: sem 0/O/1/I/L, para ninguém errar ao ditar ou digitar. */
const REF_ALPHABET = '23456789ABCDEFGHJKMNPQRSTUVWXYZ';

/** Rótulos pt-BR dos eventos gravados pelo track.js. */
const EVENT_LABELS = [
    'whatsapp'               => 'Clique no WhatsApp',
    'email'                  => 'Clique no e-mail',
    'outbound'               => 'Saída para outro domínio',
    'anchor'                 => 'Navegação interna',
    'calculator_interaction' => 'Mexeu na calculadora',
    'calculator_calculate'   => 'Calculou o impacto',
    'calculator_demo_click'  => 'CTA da calculadora',
];

function analytics_salt(): string
{
    $s = (string) cfg('analytics_salt');
    if (strlen($s) >= 16) {
        return $s;
    }
    // Sem sal configurado: deriva de segredos que já existem, para o hash não
    // ser recalculável por quem conhece apenas o IP e o user-agent do visitante.
    return hash('sha256', 'analytics|' . cfg('db_pass') . '|' . cfg('db_name') . '|' . cfg('migrate_key'));
}

/** Hash do visitante: irreversível e sem valor de um dia para o outro. */
function visitor_hash(string $ip, string $ua): string
{
    return substr(hash('sha256', analytics_salt() . '|' . date('Y-m-d') . '|' . $ip . '|' . $ua), 0, 32);
}

/** Bots, crawlers, monitores de uptime e unfurlers de link não são visita. */
function is_bot(string $ua): bool
{
    if ($ua === '') {
        return true;
    }
    return (bool) preg_match(
        '~bot|crawl|spider|slurp|scrap|monitoring|uptime|preview|headless|phantom|selenium|'
        . 'puppeteer|playwright|lighthouse|pagespeed|gtmetrix|pingdom|curl|wget|python|java/|'
        . 'go-http|okhttp|axios|node-fetch|postman|libwww|httpclient|facebookexternalhit|'
        . 'whatsapp|telegram|discord|slack|twitterbot|linkedin|embedly|semrush|ahrefs|mj12|'
        . 'dotbot|petalbot|yandex|baidu|sogou|bytespider~i',
        $ua
    );
}

/** user-agent → [device, browser, os]. Aproximação suficiente para o painel. */
function ua_parse(string $ua): array
{
    $ehAndroid = stripos($ua, 'Android') !== false;
    $device = 'desktop';
    if (preg_match('~iPad|Tablet|PlayBook|Silk~i', $ua) || ($ehAndroid && stripos($ua, 'Mobile') === false)) {
        $device = 'tablet';
    } elseif (preg_match('~Mobi|iPhone|iPod|Windows Phone~i', $ua) || $ehAndroid) {
        $device = 'mobile';
    }

    // Ordem importa: Edge/Opera/Samsung também se dizem Chrome, e Chrome também se diz Safari.
    $browser = null;
    foreach (['Edg' => 'Edge', 'OPR' => 'Opera', 'SamsungBrowser' => 'Samsung', 'Chrome' => 'Chrome',
              'Firefox' => 'Firefox', 'Safari' => 'Safari'] as $agulha => $nome) {
        if (stripos($ua, $agulha) !== false) {
            $browser = $nome;
            break;
        }
    }

    // Android antes de Linux: o user-agent do Android contém "Linux".
    $os = null;
    foreach (['Android' => 'Android', 'iPhone' => 'iOS', 'iPad' => 'iOS', 'iPod' => 'iOS',
              'Windows' => 'Windows', 'Mac OS X' => 'macOS', 'Macintosh' => 'macOS',
              'CrOS' => 'ChromeOS', 'Linux' => 'Linux'] as $agulha => $nome) {
        if (stripos($ua, $agulha) !== false) {
            $os = $nome;
            break;
        }
    }

    return [$device, $browser, $os];
}

function ref_code_new(): string
{
    $out = '';
    $max = strlen(REF_ALPHABET) - 1;
    for ($i = 0; $i < 6; $i++) {
        $out .= REF_ALPHABET[random_int(0, $max)];
    }
    return $out;
}

/** Normaliza um ref_code digitado pelo time (aceita "#k7m2q9", "K7M2Q9", com espaços). */
function ref_code_norm(?string $s): ?string
{
    $c = strtoupper(preg_replace('/[^0-9A-Za-z]+/', '', (string) $s));
    return preg_match('/^[' . REF_ALPHABET . ']{6}$/', $c) ? $c : null;
}

/**
 * Visita corrente do hash — cria uma nova quando a última passou do timeout.
 * $ctx (path/referrer/utm/device/browser/os/screen_w) só é usado na criação.
 */
function visit_open(string $visitorHash, array $ctx): ?array
{
    $v = row(
        'SELECT * FROM site_visits
          WHERE visitor_hash = ? AND last_seen_at >= DATE_SUB(NOW(), INTERVAL ? MINUTE)
          ORDER BY id DESC LIMIT 1',
        [$visitorHash, VISIT_TIMEOUT_MIN]
    );
    if ($v !== null) {
        return $v;
    }
    // 3 tentativas cobrem a colisão de ref_code (31^6 = 887 milhões de combinações).
    for ($tentativa = 0; $tentativa < 3; $tentativa++) {
        try {
            q('INSERT INTO site_visits
                 (visitor_hash, ref_code, landing_path, referrer_host, referrer_path,
                  utm_source, utm_medium, utm_campaign, utm_content, utm_term,
                  device, browser, os, screen_w)
               VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)', [
                $visitorHash,
                ref_code_new(),
                $ctx['path'],
                $ctx['referrer_host'],
                $ctx['referrer_path'],
                $ctx['utm_source'],
                $ctx['utm_medium'],
                $ctx['utm_campaign'],
                $ctx['utm_content'],
                $ctx['utm_term'],
                $ctx['device'],
                $ctx['browser'],
                $ctx['os'],
                $ctx['screen_w'],
            ]);
            return row('SELECT * FROM site_visits WHERE id = ?', [last_id()]);
        } catch (PDOException $e) {
            if ($e->getCode() !== '23000') { // 23000 = UNIQUE violado no ref_code
                throw $e;
            }
        }
    }
    return null;
}

function view_record(array $visit, string $path, ?string $title): void
{
    if ((int) $visit['views'] >= VISIT_MAX_VIEWS) {
        return;
    }
    q('INSERT INTO site_views (visit_id, path, title) VALUES (?,?,?)', [$visit['id'], $path, $title]);
    q('UPDATE site_visits SET views = views + 1, last_seen_at = NOW() WHERE id = ?', [$visit['id']]);
}

/**
 * Fecha a última view daquele path com tempo de leitura e scroll máximo.
 * Idempotente: a aba manda um 'end' a cada troca de foco, então fica sempre o
 * maior valor já visto (GREATEST) em vez de o último, que seria menor.
 */
function view_close(array $visit, string $path, int $seconds, int $scrollPct): void
{
    q(
        'UPDATE site_views
            SET seconds = GREATEST(COALESCE(seconds, 0), ?),
                scroll_pct = GREATEST(COALESCE(scroll_pct, 0), ?)
          WHERE visit_id = ? AND path = ?
          ORDER BY id DESC LIMIT 1',
        [$seconds, $scrollPct, $visit['id'], $path]
    );
    q('UPDATE site_visits SET last_seen_at = NOW() WHERE id = ?', [$visit['id']]);
}

function event_record(array $visit, string $name, string $path, ?string $label, ?string $target, ?int $value): void
{
    if ((int) $visit['events'] >= VISIT_MAX_EVENTS) {
        return;
    }
    q('INSERT INTO site_events (visit_id, name, path, label, target, value_num) VALUES (?,?,?,?,?,?)',
        [$visit['id'], $name, $path, $label, $target, $value]);
    q('UPDATE site_visits SET events = events + 1, last_seen_at = NOW() WHERE id = ?', [$visit['id']]);
}

/** Poda oportunista (~0,5% das escritas). Visitas ligadas a um lead ficam. */
function analytics_prune(): void
{
    if (random_int(1, 200) !== 1) {
        return;
    }
    q('DELETE FROM site_visits
        WHERE lead_id IS NULL AND last_seen_at < DATE_SUB(NOW(), INTERVAL ? DAY)
        LIMIT 500', [VISIT_RETENTION_DAYS]);
}

// ---------- Consultas do painel ----------

function analytics_resumo(int $dias): array
{
    $r = row(
        'SELECT COUNT(*) AS visitas,
                COUNT(DISTINCT visitor_hash) AS visitantes,
                CAST(COALESCE(SUM(views), 0) AS SIGNED) AS views,
                CAST(COALESCE(SUM(events), 0) AS SIGNED) AS eventos,
                CAST(COALESCE(SUM(CASE WHEN views <= 1 AND events = 0 THEN 1 ELSE 0 END), 0) AS SIGNED) AS rejeicoes,
                CAST(COALESCE(SUM(CASE WHEN lead_id IS NOT NULL THEN 1 ELSE 0 END), 0) AS SIGNED) AS viraram_lead
           FROM site_visits
          WHERE started_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)',
        [$dias]
    ) ?? [];
    $r['seg_medio'] = (int) scalar(
        'SELECT CAST(COALESCE(ROUND(AVG(seconds)), 0) AS SIGNED) FROM site_views
          WHERE seconds IS NOT NULL AND created_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)',
        [$dias]
    );
    return $r;
}

function analytics_por_dia(int $dias): array
{
    return rows(
        'SELECT DATE(started_at) AS dia, COUNT(*) AS visitas,
                CAST(COALESCE(SUM(views), 0) AS SIGNED) AS views, CAST(COALESCE(SUM(events), 0) AS SIGNED) AS eventos
           FROM site_visits
          WHERE started_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)
          GROUP BY DATE(started_at) ORDER BY dia',
        [$dias]
    );
}

function analytics_paginas(int $dias): array
{
    return rows(
        'SELECT path, COUNT(*) AS views, COUNT(DISTINCT visit_id) AS visitas,
                CAST(ROUND(AVG(seconds)) AS SIGNED) AS seg_medio, CAST(ROUND(AVG(scroll_pct)) AS SIGNED) AS scroll_medio
           FROM site_views
          WHERE created_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)
          GROUP BY path ORDER BY views DESC LIMIT 25',
        [$dias]
    );
}

function analytics_origens(int $dias): array
{
    return rows(
        "SELECT COALESCE(utm_source, referrer_host, '(direto)') AS origem,
                utm_medium, utm_campaign,
                COUNT(*) AS visitas, CAST(COALESCE(SUM(events), 0) AS SIGNED) AS eventos,
                CAST(COALESCE(SUM(CASE WHEN lead_id IS NOT NULL THEN 1 ELSE 0 END), 0) AS SIGNED) AS leads
           FROM site_visits
          WHERE started_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)
          GROUP BY origem, utm_medium, utm_campaign
          ORDER BY visitas DESC LIMIT 25",
        [$dias]
    );
}

function analytics_eventos(int $dias): array
{
    return rows(
        "SELECT name, COALESCE(NULLIF(label, ''), target, '—') AS rotulo,
                COUNT(*) AS n, COUNT(DISTINCT visit_id) AS visitas
           FROM site_events
          WHERE created_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)
          GROUP BY name, rotulo ORDER BY n DESC LIMIT 30",
        [$dias]
    );
}

function analytics_dispositivos(int $dias): array
{
    return rows(
        "SELECT device, COALESCE(browser, '?') AS browser, COUNT(*) AS visitas
           FROM site_visits
          WHERE started_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)
          GROUP BY device, browser ORDER BY visitas DESC LIMIT 12",
        [$dias]
    );
}

/** Funil calculadora → WhatsApp, contado em visitas distintas. */
function analytics_funil(int $dias): array
{
    return row(
        "SELECT
           (SELECT COUNT(*) FROM site_visits
             WHERE started_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)) AS visitas,
           (SELECT COUNT(DISTINCT visit_id) FROM site_events
             WHERE name = 'calculator_interaction'
               AND created_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)) AS mexeu,
           (SELECT COUNT(DISTINCT visit_id) FROM site_events
             WHERE name = 'calculator_calculate'
               AND created_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)) AS calculou,
           (SELECT COUNT(DISTINCT visit_id) FROM site_events
             WHERE name = 'calculator_demo_click'
               AND created_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)) AS cta_calc,
           (SELECT COUNT(DISTINCT visit_id) FROM site_events
             WHERE name = 'whatsapp'
               AND created_at >= DATE_SUB(CURDATE(), INTERVAL ? DAY)) AS whatsapp",
        [$dias, $dias, $dias, $dias, $dias]
    ) ?? [];
}

/** Visitas mais recentes; $soComClique deixa só as que geraram alguma intenção. */
function analytics_visitas_recentes(int $limite = 40, bool $soComClique = false): array
{
    $limite = max(1, min(200, $limite)); // int — interpolação segura
    $where = $soComClique ? 'WHERE v.events > 0' : '';
    return rows(
        "SELECT v.*, l.company AS lead_company
           FROM site_visits v
           LEFT JOIN leads l ON l.id = v.lead_id
           $where
          ORDER BY v.last_seen_at DESC LIMIT $limite"
    );
}

function analytics_visita_por_ref(string $ref): ?array
{
    $v = row(
        'SELECT v.*, l.company AS lead_company
           FROM site_visits v LEFT JOIN leads l ON l.id = v.lead_id
          WHERE v.ref_code = ?',
        [$ref]
    );
    if ($v === null) {
        return null;
    }
    $v['views_list'] = rows('SELECT * FROM site_views WHERE visit_id = ? ORDER BY id', [$v['id']]);
    $v['events_list'] = rows('SELECT * FROM site_events WHERE visit_id = ? ORDER BY id', [$v['id']]);
    return $v;
}

/** Amarra a visita ao lead nos dois sentidos (leads.visit_ref e site_visits.lead_id). */
function visit_link_lead(string $ref, int $leadId): void
{
    $v = row('SELECT id FROM site_visits WHERE ref_code = ?', [$ref]);
    if ($v === null) {
        throw new InvalidArgumentException('Código de visita não encontrado.');
    }
    if (row('SELECT id FROM leads WHERE id = ?', [$leadId]) === null) {
        throw new InvalidArgumentException('Lead não encontrado.');
    }
    q('UPDATE site_visits SET lead_id = ? WHERE id = ?', [$leadId, $v['id']]);
    q('UPDATE leads SET visit_ref = ? WHERE id = ?', [$ref, $leadId]);
}

/** Jornada no site de um lead já vinculado (card do lead.php). */
function analytics_jornada_do_lead(int $leadId): ?array
{
    $ref = scalar('SELECT visit_ref FROM leads WHERE id = ?', [$leadId]);
    return $ref ? analytics_visita_por_ref((string) $ref) : null;
}
