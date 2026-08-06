<?php
/** Rate limiting por tabela (throttle_events): buckets login/intake/migrate/api. */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

/** True quando o bucket/IP já atingiu $max eventos na janela de $windowMin minutos. */
function throttle_blocked(string $bucket, string $ip, int $max, int $windowMin): bool
{
    $n = (int) scalar(
        'SELECT COUNT(*) FROM throttle_events
          WHERE bucket = ? AND ip = ? AND created_at >= DATE_SUB(NOW(), INTERVAL ? MINUTE)',
        [$bucket, $ip, $windowMin]
    );
    return $n >= $max;
}

function throttle_add(string $bucket, string $ip): void
{
    q('INSERT INTO throttle_events (bucket, ip) VALUES (?, ?)', [$bucket, $ip]);
    // Poda oportunista (~1% das escritas): throttle 2 dias, log de intake 90 dias (LGPD).
    if (random_int(1, 100) === 1) {
        q('DELETE FROM throttle_events WHERE created_at < DATE_SUB(NOW(), INTERVAL 2 DAY)');
        q('DELETE FROM intake_log WHERE created_at < DATE_SUB(NOW(), INTERVAL 90 DAY)');
    }
}
