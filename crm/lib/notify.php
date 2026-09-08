<?php
/**
 * Avisos: e-mail para quem assina o lead, Telegram opcional, resumo diario e
 * o ping do dead-man switch.
 *
 * Nenhum aviso pode derrubar o cron — todos engolem a excecao e seguem.
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

/** GET simples (Telegram e healthchecks). Sem dependencia nova: curl ou stream. */
function notify_http(string $url, array $post = []): bool
{
    if (function_exists('curl_init')) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 4,
            CURLOPT_TIMEOUT        => 8,
            CURLOPT_USERAGENT      => 'M351-CRM/1.0',
        ]);
        if ($post) {
            curl_setopt($ch, CURLOPT_POST, true);
            curl_setopt($ch, CURLOPT_POSTFIELDS, http_build_query($post));
        }
        $body = curl_exec($ch);
        $code = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);
        return $body !== false && $code < 400;
    }
    $opts = ['http' => ['timeout' => 8, 'ignore_errors' => true, 'header' => "User-Agent: M351-CRM/1.0\r\n"]];
    if ($post) {
        $opts['http']['method'] = 'POST';
        $opts['http']['header'] .= "Content-Type: application/x-www-form-urlencoded\r\n";
        $opts['http']['content'] = http_build_query($post);
    }
    return @file_get_contents($url, false, stream_context_create($opts)) !== false;
}

function telegram_enviar(string $texto): bool
{
    $conf = (array) cfg('telegram');
    $token = trim((string) ($conf['token'] ?? ''));
    $chat = trim((string) ($conf['chat_id'] ?? ''));
    // O token do bot e "123456:AAF-xxxx" e vai CRU no caminho da URL: o ':'
    // codificado quebra o roteamento da Bot API. Validar o formato substitui a
    // codificacao com seguranca (nao ha o que escapar num [0-9]+:[A-Za-z0-9_-]+).
    if ($token === '' || $chat === '' || !setting_bool('auto_avisa_telegram')
        || !preg_match('/^\d+:[A-Za-z0-9_-]+$/', $token)) {
        return false;
    }
    return notify_http('https://api.telegram.org/bot' . $token . '/sendMessage', [
        'chat_id'                  => $chat,
        'text'                     => mb_substr($texto, 0, 3500),
        'disable_web_page_preview' => 'true',
    ]);
}

/** Dead-man switch do cron (mesmo padrao do worker do SaaS). */
function healthchecks_ping(string $sufixo = ''): void
{
    $url = trim((string) cfg('healthchecks_url'));
    if ($url === '') {
        return;
    }
    notify_http(rtrim($url, '/') . ($sufixo !== '' ? '/' . $sufixo : ''));
}

/**
 * Quem recebe o aviso deste lead: a caixa de quem assina a cadencia, com
 * copia para os enderecos de auto_cc_avisos.
 * @return array{conta:?array, para:string, cc:string[]}
 */
function avisos_destinatarios(?int $senderId): array
{
    $conta = mail_conta_usuario($senderId);
    if ($conta === null) {
        $todas = mail_contas();
        $conta = $todas ? reset($todas) : null;
    }
    $cc = [];
    foreach (explode(',', setting_str('auto_cc_avisos')) as $e) {
        $e = norm_email($e);
        if (is_string($e) && $e !== '') {
            $cc[] = $e;
        }
    }
    return ['conta' => $conta, 'para' => (string) ($conta['email'] ?? ''), 'cc' => $cc];
}

function crm_url(string $rel): string
{
    $base = rtrim((string) (cfg('site_url') ?: 'https://www.mais351monitor.com.br'), '/');
    return $base . '/crm/' . ltrim($rel, '/');
}

/** Aviso de retorno: resposta humana, devolucao ou pedido de saida. */
function notificar_retorno(array $reg): void
{
    $leadId = $reg['lead_id'] !== null ? (int) $reg['lead_id'] : null;
    $lead = $leadId !== null ? row('SELECT * FROM leads WHERE id = ?', [$leadId]) : null;
    $empresa = $lead['company'] ?? ($reg['from_email'] ?: 'contato desconhecido');
    $c = $leadId !== null ? cadencia_estado($leadId) : null;
    $dest = avisos_destinatarios($c && $c['sender_user_id'] !== null ? (int) $c['sender_user_id'] : null);

    $titulo = match ($reg['kind']) {
        'humana' => 'Resposta de ' . $empresa,
        'optout' => 'Pediu para sair: ' . $empresa,
        'bounce' => 'E-mail devolvido: ' . $empresa,
        default  => 'Retorno de ' . $empresa,
    };
    $link = $leadId !== null ? crm_url('lead.php?id=' . $leadId) : crm_url('envios.php');
    $corpo = $titulo . "\n\n"
        . 'De: ' . ($reg['from_name'] ? $reg['from_name'] . ' <' . $reg['from_email'] . '>' : $reg['from_email']) . "\n"
        . 'Assunto: ' . $reg['subject'] . "\n"
        . 'Quando: ' . fmt_dt((string) $reg['received_at']) . "\n\n"
        . trim((string) $reg['snippet']) . "\n\n"
        . 'Lead: ' . $link . "\n"
        . 'A cadencia deste lead foi encerrada automaticamente.';

    if (setting_bool('auto_avisa_email') && $dest['para'] !== '') {
        try {
            mail_aviso($dest['conta'], $dest['para'], '[+351 CRM] ' . $titulo, $corpo, $dest['cc']);
        } catch (Throwable $e) {
            error_log('aviso retorno: ' . $e->getMessage());
        }
    }
    try {
        telegram_enviar($titulo . "\n" . mb_substr(trim((string) $reg['snippet']), 0, 400) . "\n" . $link);
    } catch (Throwable $e) {
        error_log('telegram: ' . $e->getMessage());
    }
}

/** Resumo do fim do dia: o que saiu, o que nao saiu e por que. */
function notificar_resumo_diario(): bool
{
    $hora = max(0, min(23, setting_int('auto_resumo_hora')));
    $minuto = max(0, min(59, setting_int('auto_resumo_minuto')));
    $hoje = date('Y-m-d');
    if (state_get('resumo_dia') === $hoje) {
        return false;
    }
    if (date('H:i') < sprintf('%02d:%02d', $hora, $minuto)) {
        return false;
    }
    // Fim de semana e feriado sem envio nao geram resumo.
    if (cadencia_dia_util($hoje, 0) !== $hoje) {
        state_set('resumo_dia', $hoje);
        return false;
    }

    $enviados = rows("SELECT o.seq, o.to_email, l.company, o.sent_at
                      FROM email_outbox o LEFT JOIN leads l ON l.id = o.lead_id
                      WHERE o.status = 'enviado' AND DATE(o.sent_at) = CURDATE() ORDER BY o.sent_at");
    // scheduled_for, e nao updated_at: updated_at e ON UPDATE CURRENT_TIMESTAMP,
    // entao qualquer toque na linha hoje relistaria um pulado de semanas atras.
    $pulados = rows("SELECT o.skip_reason, l.company FROM email_outbox o
                     LEFT JOIN leads l ON l.id = o.lead_id
                     WHERE o.status IN ('pulado','falhou') AND DATE(o.scheduled_for) = CURDATE()");
    $retornos = rows("SELECT kind, from_email, subject FROM email_inbound
                      WHERE DATE(received_at) = CURDATE() AND kind IN ('humana','optout','bounce')");
    $pendentes = (int) scalar("SELECT COUNT(*) FROM email_inbound WHERE handled_at IS NULL
                               AND kind IN ('humana','optout','bounce')");

    $linhas = ['Resumo da cadencia — ' . date('d/m/Y'), ''];
    $linhas[] = 'Enviados: ' . count($enviados) . ' (teto do dia por caixa: ' . cadencia_teto_hoje() . ')';
    foreach ($enviados as $e) {
        $linhas[] = '  ' . date('H:i', strtotime((string) $e['sent_at'])) . '  ' . $e['seq'] . 'o e-mail  '
            . ($e['company'] ?? '') . '  <' . $e['to_email'] . '>';
    }
    if ($pulados) {
        $linhas[] = '';
        $linhas[] = 'Nao sairam: ' . count($pulados);
        foreach ($pulados as $p) {
            $linhas[] = '  ' . ($p['company'] ?? '(lead removido)') . ' — ' . ($p['skip_reason'] ?: 'falha de envio');
        }
    }
    $linhas[] = '';
    $linhas[] = 'Retornos hoje: ' . count($retornos) . ' · nao tratados no total: ' . $pendentes;
    foreach ($retornos as $rt) {
        $linhas[] = '  ' . (INBOUND_KIND_LABELS[$rt['kind']] ?? $rt['kind']) . ' — ' . $rt['from_email']
            . ' — ' . mb_substr((string) $rt['subject'], 0, 60);
    }
    $linhas[] = '';
    $linhas[] = crm_url('envios.php');

    $dest = avisos_destinatarios(null);
    if ($dest['para'] !== '' && setting_bool('auto_avisa_email')) {
        mail_aviso($dest['conta'], $dest['para'], '[+351 CRM] Resumo da cadencia — ' . date('d/m'),
            implode("\n", $linhas), $dest['cc']);
    }
    state_set('resumo_dia', $hoje);
    return true;
}
