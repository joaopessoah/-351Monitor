<?php
/**
 * Endpoint público do formulário do site (same-origin, sem CORS).
 * Pipeline: rate limit → honeypot (sucesso falso) → time-trap → validação →
 * dedupe (cria flagado, nunca rejeita) → insert. Tudo auditado em intake_log.
 */

require __DIR__ . '/lib/bootstrap.php';

security_headers(false);
header('Content-Type: application/json; charset=utf-8');

function intake_out(int $code, array $body): never
{
    http_response_code($code);
    echo json_encode($body, JSON_UNESCAPED_UNICODE);
    exit;
}

if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
    intake_out(405, ['ok' => false, 'message' => 'Método não permitido.']);
}

$ip = client_ip();
$in = json_decode((string) file_get_contents('php://input'), true);
if (!is_array($in)) {
    $in = $_POST; // fallback form-encoded
}

try {
    // 1. Rate limit por IP: 5/hora e 20/dia
    if (throttle_blocked('intake', $ip, 5, 60) || throttle_blocked('intake', $ip, 20, 1440)) {
        intake_log($ip, 'rate_limited');
        intake_out(429, ['ok' => false, 'message' => 'Muitos envios. Tente mais tarde ou chame direto no WhatsApp.']);
    }
    throttle_add('intake', $ip);

    // 2. Honeypot: bot preencheu o campo escondido → finge sucesso e descarta
    if (trim((string) ($in['site_web'] ?? '')) !== '') {
        intake_log($ip, 'spam_honeypot');
        intake_out(200, ['ok' => true]);
    }

    // 3. Time-trap: form enviado em menos de 3s (ou timestamp ausente/velho demais)
    $ts = $in['form_ts'] ?? null;
    $agoraMs = (int) round(microtime(true) * 1000);
    if (!is_numeric($ts) || $agoraMs - (int) $ts < 3000 || $agoraMs - (int) $ts > 86400000) {
        intake_log($ip, 'spam_timetrap');
        intake_out(400, ['ok' => false, 'message' => 'Não foi possível enviar. Recarregue a página e tente de novo.']);
    }

    // 4. Validação
    $errors = [];
    $nome = norm_text($in['nome'] ?? '', 120);
    if (mb_strlen($nome) < 2) {
        $errors['nome'] = 'Informe seu nome.';
    }
    $empresa = norm_text($in['empresa'] ?? '', 160);
    if (mb_strlen($empresa) < 2) {
        $errors['empresa'] = 'Informe a empresa.';
    }
    $email = norm_email($in['email'] ?? '');
    if ($email === false) {
        $errors['email'] = 'E-mail inválido.';
    }
    $fone = norm_whatsapp($in['whatsapp'] ?? '');
    if ($fone === false) {
        $errors['whatsapp'] = 'WhatsApp inválido — use DDD + número.';
    }
    if (!isset($errors['email'], $errors['whatsapp']) && $email === null && $fone === null) {
        $errors['whatsapp'] = 'Informe e-mail ou WhatsApp para a gente falar com você.';
    }
    $estacoes = norm_int($in['estacoes'] ?? null, 1, 10000);
    if ($estacoes === false) {
        $errors['estacoes'] = 'Número de estações inválido.';
    }
    if ($errors) {
        intake_log($ip, 'invalid', null, implode(',', array_keys($errors)));
        intake_out(422, ['ok' => false, 'errors' => $errors]);
    }

    // 5+6. Dedupe (flag, nunca rejeita) + insert
    $res = lead_create([
        'company'           => $empresa,
        'contact_name'      => $nome,
        'email'             => $email,
        'whatsapp'          => $fone,
        'estimated_devices' => $estacoes,
        'source'            => 'site',
        'utm_source'        => norm_text($in['utm_source'] ?? '', 120) ?: null,
        'utm_medium'        => norm_text($in['utm_medium'] ?? '', 120) ?: null,
        'utm_campaign'      => norm_text($in['utm_campaign'] ?? '', 120) ?: null,
    ], null, 'site');

    intake_log($ip, $res['duplicate_of_lead_id'] !== null ? 'duplicate' : 'created', $res['id']);
    intake_out(200, ['ok' => true]);
} catch (Throwable $e) {
    error_log('intake: ' . $e->getMessage());
    intake_out(500, ['ok' => false, 'message' => 'Erro interno. Chame a gente no WhatsApp.']);
}
