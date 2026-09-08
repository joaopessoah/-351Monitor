<?php
/**
 * Pre-validacao de e-mail em camadas, com cache por endereco (30 dias).
 *
 * O que NAO fazemos de proposito: sondagem SMTP (conectar no MX e perguntar
 * "esse endereco existe?"). A porta 25 costuma sair bloqueada da hospedagem
 * compartilhada, servidor catch-all responde "sim" para qualquer coisa e a
 * sondagem em massa queima a reputacao do IP. Bounce real e o unico
 * verificador que nunca erra — e ele alimenta esta tabela de volta.
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

const EMAIL_CHECK_DIAS = 30;

const EMAIL_STATUS_LABELS = [
    'nao_verificado' => 'nao verificado',
    'valido'         => 'valido',
    'generico'       => 'generico',
    'invalido'       => 'invalido',
    'descartavel'    => 'descartavel',
    'bounce'         => 'bounce',
];

/** Caixas de papel (nao sao uma pessoa). Nao bloqueiam: entram com prioridade menor. */
const EMAIL_PREFIXOS_GENERICOS = [
    'contato', 'contatos', 'comercial', 'vendas', 'financeiro', 'faturamento',
    'nfe', 'nf', 'fiscal', 'contabil', 'contabilidade', 'rh', 'juridico',
    'atendimento', 'suporte', 'sac', 'adm', 'administrativo', 'administracao',
    'info', 'informacoes', 'marketing', 'ouvidoria', 'secretaria', 'recepcao',
    'cobranca', 'compras', 'diretoria', 'escritorio', 'empresa', 'email',
    'noreply', 'no-reply', 'nao-responda', 'naoresponda', 'donotreply',
];

/** Descartaveis conhecidos: bloqueiam. Lista curta de proposito (manutencao barata). */
const EMAIL_DOMINIOS_DESCARTAVEIS = [
    'mailinator.com', 'yopmail.com', 'guerrillamail.com', 'sharklasers.com',
    'trashmail.com', 'dispostable.com', 'getnada.com', 'temp-mail.org',
    'tempmail.com', '10minutemail.com', 'throwawaymail.com', 'mailnesia.com',
    'maildrop.cc', 'fakeinbox.com', 'mytemp.email', 'emailtemporario.com.br',
];

/** Provedores gratuitos: validos, mas o score do leadgen ja prefere dominio proprio. */
const EMAIL_DOMINIOS_LIVRES = [
    'gmail.com', 'hotmail.com', 'hotmail.com.br', 'outlook.com', 'outlook.com.br',
    'live.com', 'yahoo.com', 'yahoo.com.br', 'uol.com.br', 'bol.com.br',
    'terra.com.br', 'ig.com.br', 'globo.com', 'globomail.com', 'icloud.com',
    'me.com', 'protonmail.com', 'zipmail.com.br', 'r7.com',
];

/** MX (ou A como plano B) do dominio. Isolado para o teste injetar respostas. */
function email_mx_lookup(string $dominio): bool
{
    if ($dominio === '' || !function_exists('dns_get_record')) {
        return true; // sem resolvedor nao da para reprovar ninguem
    }
    $mx = @dns_get_record($dominio, DNS_MX);
    if (is_array($mx) && $mx) {
        return true;
    }
    $a = @dns_get_record($dominio, DNS_A);
    return is_array($a) && (bool) $a;
}

/**
 * O nucleo puro da validacao: nao toca banco nem rede (o DNS entra por
 * $mxLookup). E o que tests/email_check.php exercita.
 *
 * @param callable(string):bool $mxLookup
 * @return array{status:string, mx_ok:bool, detail:string}
 */
function email_check_calc(?string $email, callable $mxLookup): array
{
    $norm = norm_email($email);
    if ($norm === null || $norm === false) {
        return ['status' => 'invalido', 'mx_ok' => false, 'detail' => 'sintaxe invalida'];
    }
    $dominio = mail_dominio($norm);
    $local = substr($norm, 0, strrpos($norm, '@'));

    if (in_array($dominio, EMAIL_DOMINIOS_DESCARTAVEIS, true)) {
        return ['status' => 'descartavel', 'mx_ok' => false, 'detail' => 'dominio descartavel'];
    }
    if (!$mxLookup($dominio)) {
        return ['status' => 'invalido', 'mx_ok' => false, 'detail' => 'dominio sem MX nem A'];
    }
    // "contato+cadencia@" ou "financeiro.sp@": o prefixo antes de + . - _ manda
    $base = preg_split('/[+._-]/', $local)[0] ?? $local;
    $inteiro = in_array($local, EMAIL_PREFIXOS_GENERICOS, true);
    if ($inteiro || in_array($base, EMAIL_PREFIXOS_GENERICOS, true)) {
        return ['status' => 'generico', 'mx_ok' => true,
            'detail' => 'caixa de papel (' . ($inteiro ? $local : $base) . '@)'];
    }
    if (in_array($dominio, EMAIL_DOMINIOS_LIVRES, true)) {
        return ['status' => 'valido', 'mx_ok' => true, 'detail' => 'dominio gratuito'];
    }
    return ['status' => 'valido', 'mx_ok' => true, 'detail' => ''];
}

/** Status em cache ainda vale? Bounce nunca expira. */
function email_check_fresco(?array $linha): bool
{
    if ($linha === null) {
        return false;
    }
    if ($linha['status'] === 'bounce') {
        return true;
    }
    return strtotime((string) $linha['checked_at']) >= strtotime('-' . EMAIL_CHECK_DIAS . ' days');
}

/**
 * Valida com cache. Devolve sempre o shape do email_check_calc.
 * Tolerante a migration 013 ausente: sem tabela, valida na hora e nao grava.
 */
function email_check(?string $email, bool $forcar = false): array
{
    $norm = norm_email($email);
    if ($norm === null || $norm === false) {
        return ['status' => 'invalido', 'mx_ok' => false, 'detail' => 'sintaxe invalida'];
    }
    try {
        $linha = row('SELECT * FROM email_checks WHERE email = ?', [$norm]);
        if (!$forcar && email_check_fresco($linha)) {
            return [
                'status' => (string) $linha['status'],
                'mx_ok'  => (int) $linha['mx_ok'] === 1,
                'detail' => (string) ($linha['detail'] ?? ''),
            ];
        }
        if ($linha !== null && $linha['status'] === 'bounce') {
            return ['status' => 'bounce', 'mx_ok' => false, 'detail' => (string) ($linha['detail'] ?? 'bounce')];
        }
    } catch (Throwable $e) {
        return email_check_calc($norm, 'email_mx_lookup');
    }

    $r = email_check_calc($norm, 'email_mx_lookup');
    try {
        q('INSERT INTO email_checks (email, status, mx_ok, detail, checked_at) VALUES (?,?,?,?,NOW())
           ON DUPLICATE KEY UPDATE status = VALUES(status), mx_ok = VALUES(mx_ok),
                                   detail = VALUES(detail), checked_at = NOW()',
            [$norm, $r['status'], $r['mx_ok'] ? 1 : 0, $r['detail'] ?: null]);
    } catch (Throwable $e) {
        // sem cache; a validacao acima continua valendo
    }
    return $r;
}

/** Grava o status no contato (o que a tela do lead mostra). */
function email_status_gravar(?int $contactId, string $status): void
{
    if (!$contactId) {
        return;
    }
    try {
        q('UPDATE lead_contacts SET email_status = ?, email_checked_at = NOW() WHERE id = ?',
            [$status, $contactId]);
    } catch (Throwable $e) {
        // migration 013 ainda nao aplicada
    }
}

/**
 * Bounce definitivo: o endereco vira invalido para sempre (ate alguem
 * corrigir na tela, o que apaga a linha do cache).
 */
function email_marcar_bounce(string $email, string $codigo = ''): void
{
    $norm = norm_email($email);
    if ($norm === null || $norm === false) {
        return;
    }
    try {
        q('INSERT INTO email_checks (email, status, mx_ok, detail, checked_at) VALUES (?,?,0,?,NOW())
           ON DUPLICATE KEY UPDATE status = VALUES(status), detail = VALUES(detail), checked_at = NOW()',
            [$norm, 'bounce', trim('devolvido pelo servidor ' . $codigo)]);
        q('UPDATE lead_contacts SET email_status = ?, email_checked_at = NOW() WHERE email = ?',
            ['bounce', $norm]);
    } catch (Throwable $e) {
        // migration 013 ainda nao aplicada
    }
}

/** Alguem corrigiu o endereco: o bounce anterior nao vale mais. */
function email_limpar_cache(string $email): void
{
    $norm = norm_email($email);
    if ($norm === null || $norm === false) {
        return;
    }
    try {
        q('DELETE FROM email_checks WHERE email = ?', [$norm]);
    } catch (Throwable $e) {
    }
}
