<?php
/**
 * Montagem e envio de e-mail (texto puro) pelas caixas reais do time.
 *
 * O e-mail sai autenticado NA CAIXA de quem assina (bruna@ ou joao@), entao a
 * resposta cai no Outlook dela como sempre caiu — o CRM so observa. Nada de
 * remetente tecnico "nao-responda@": o playbook exige remetente real.
 *
 * Credenciais: chave 'mail' do crm_config.php, fora do webroot (ver README).
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

/** Rodape com o descadastro de 1 clique. {link} e a unica chave. */
const CADENCIA_OPTOUT_RODAPE = 'Se preferir cancelar em um clique, use este link: {link}';

/** Contas configuradas, indexadas pelo endereco em minusculas. */
function mail_contas(): array
{
    static $c = null;
    if ($c !== null) {
        return $c;
    }
    $c = [];
    foreach ((array) cfg('mail') as $email => $conf) {
        if (!is_array($conf)) {
            continue;
        }
        $email = mb_strtolower(trim((string) $email));
        if ($email === '') {
            continue;
        }
        $c[$email] = $conf + [
            'nome'       => '+351 Monitor',
            'smtp_host'  => 'smtp.hostinger.com',
            'smtp_porta' => 587,
            'smtp_seg'   => 'tls',
            'usuario'    => $email,
            'senha'      => '',
            'imap_host'  => 'imap.hostinger.com',
            'imap_porta' => 993,
            'imap_usuario' => $email,
            'imap_senha' => '',
            'email'      => $email,
        ];
        if ($c[$email]['imap_senha'] === '') {
            $c[$email]['imap_senha'] = $c[$email]['senha'];
        }
    }
    return $c;
}

function mail_conta(?string $email): ?array
{
    if (!$email) {
        return null;
    }
    return mail_contas()[mb_strtolower(trim($email))] ?? null;
}

/** Conta do usuario do CRM (users.email precisa ser a propria caixa). */
function mail_conta_usuario(?int $userId): ?array
{
    if (!$userId) {
        return null;
    }
    $u = row('SELECT email FROM users WHERE id = ?', [$userId]);
    return $u === null ? null : mail_conta($u['email']);
}

/** Usuarios ativos que TEM caixa configurada — as opcoes de remetente. */
function mail_remetentes(): array
{
    $out = [];
    foreach (users_ativos() as $u) {
        $full = row('SELECT id, name, email FROM users WHERE id = ?', [(int) $u['id']]);
        if ($full !== null && mail_conta($full['email']) !== null) {
            $out[] = $full;
        }
    }
    return $out;
}

function mail_dominio(string $email): string
{
    $p = strrpos($email, '@');
    return $p === false ? '' : mb_strtolower(substr($email, $p + 1));
}

/** Message-ID novo, gerado ANTES do envio (e a chave que amarra o retorno). */
function mail_message_id(string $dominio): string
{
    return '<' . bin2hex(random_bytes(12)) . '.' . time() . '@' . ($dominio ?: 'mais351monitor.com.br') . '>';
}

/**
 * Cabecalho com acento vira encoded-word base64 (RFC 2047), em pedacos que
 * respeitam o limite de 75 caracteres e nunca cortam um caractere UTF-8 no
 * meio. ASCII puro passa direto — assunto legivel no servidor tambem conta
 * para entregabilidade.
 */
function mail_header_encode(string $s): string
{
    $s = trim(preg_replace('/[\r\n\t]+/', ' ', $s));
    if ($s === '' || !preg_match('/[^\x20-\x7E]/', $s)) {
        return $s;
    }
    // 75 - len("=?UTF-8?B?") - len("?=") = 63 bytes de base64 => 45 bytes crus
    $partes = [];
    $atual = '';
    $len = mb_strlen($s, 'UTF-8');
    for ($i = 0; $i < $len; $i++) {
        $ch = mb_substr($s, $i, 1, 'UTF-8');
        if (strlen($atual) + strlen($ch) > 45) {
            $partes[] = $atual;
            $atual = '';
        }
        $atual .= $ch;
    }
    if ($atual !== '') {
        $partes[] = $atual;
    }
    $out = [];
    foreach ($partes as $p) {
        $out[] = '=?UTF-8?B?' . base64_encode($p) . '?=';
    }
    return implode("\r\n ", $out);
}

/**
 * "Nome" <email@dominio>, com aspas so quando o nome pede.
 *
 * CR, LF e tab caem fora ANTES de qualquer coisa: o nome do contato vem de
 * import de CSV e da API, onde norm_text() so corta o tamanho. Um "Maria\r\nBcc:
 * espiao@evil.com" viraria um cabecalho de verdade — e o caminho ASCII-sem-
 * caractere-especial nao passa pelo mail_header_encode, que e quem limparia.
 */
function mail_endereco(string $nome, string $email): string
{
    $nome = trim(str_replace(["\r", "\n", "\t"], ' ', $nome));
    $email = trim(str_replace(["\r", "\n", "\t", ' '], '', $email));
    if ($nome === '') {
        return $email;
    }
    if (preg_match('/[^\x20-\x7E]/', $nome)) {
        return mail_header_encode($nome) . ' <' . $email . '>';
    }
    if (preg_match('/[()<>@,;:\\\\".\[\]]/', $nome)) {
        return '"' . addcslashes($nome, '"\\') . '" <' . $email . '>';
    }
    return $nome . ' <' . $email . '>';
}

/** Dobra um cabecalho longo (References) em linhas de ate ~78 colunas. */
function mail_fold(string $nome, string $valor): string
{
    $linha = $nome . ': ';
    $out = '';
    foreach (preg_split('/\s+/', trim($valor)) as $token) {
        if ($token === '') {
            continue;
        }
        if (strlen($linha) + strlen($token) + 1 > 78 && trim($linha) !== $nome . ':') {
            $out .= rtrim($linha) . "\r\n";
            $linha = ' ';
        }
        $linha .= $token . ' ';
    }
    return $out . rtrim($linha);
}

/**
 * Monta a mensagem inteira (cabecalhos + corpo), pronta para o DATA.
 *
 * Corpo em quoted-printable: texto puro com acento passa legivel pelos
 * servidores antigos e nao vira o bloco base64 que filtro de spam estranha.
 *
 * @param array $m de_nome, de_email, para_nome, para_email, assunto, corpo,
 *                 message_id, reply_to, in_reply_to, references,
 *                 unsubscribe_url, auto (bool)
 */
function mail_monta(array $m): string
{
    $corpo = preg_replace('/\r\n|\r|\n/', "\r\n", rtrim((string) $m['corpo'])) . "\r\n";
    $h = [];
    $h[] = 'Date: ' . date('r');
    $h[] = 'From: ' . mail_endereco((string) ($m['de_nome'] ?? ''), (string) $m['de_email']);
    $h[] = 'To: ' . mail_endereco((string) ($m['para_nome'] ?? ''), (string) $m['para_email']);
    if (!empty($m['reply_to'])) {
        $h[] = 'Reply-To: ' . $m['reply_to'];
    }
    $h[] = 'Subject: ' . mail_header_encode((string) $m['assunto']);
    $h[] = 'Message-ID: ' . $m['message_id'];
    if (!empty($m['in_reply_to'])) {
        $h[] = 'In-Reply-To: ' . $m['in_reply_to'];
    }
    if (!empty($m['references'])) {
        $h[] = mail_fold('References', (string) $m['references']);
    }
    if (!empty($m['unsubscribe_url'])) {
        // O par List-Unsubscribe + One-Click e o que faz o botao "cancelar
        // inscricao" do Gmail/Outlook aparecer, em vez de o "marcar como spam".
        $h[] = 'List-Unsubscribe: <' . $m['unsubscribe_url'] . '>, <mailto:' . $m['de_email'] . '?subject=SAIR>';
        $h[] = 'List-Unsubscribe-Post: List-Unsubscribe=One-Click';
    }
    if (!empty($m['auto'])) {
        // So nos avisos internos e na confirmacao de opt-out: evita laco de
        // resposta automatica. E-mail de cadencia NAO leva (foi escrito por
        // gente, so o disparo e agendado).
        $h[] = 'Auto-Submitted: auto-generated';
        $h[] = 'X-Auto-Response-Suppress: All';
    }
    $h[] = 'MIME-Version: 1.0';
    $h[] = 'Content-Type: text/plain; charset=UTF-8';
    $h[] = 'Content-Transfer-Encoding: quoted-printable';

    return implode("\r\n", $h) . "\r\n\r\n" . quoted_printable_encode($corpo);
}

/**
 * Envia de verdade. Nao lanca: devolve o resultado para o chamador gravar.
 * @return array{ok:bool, erro:string, incerto:bool}
 *         incerto = pode ter saido; nao reenvie sem olho humano.
 */
function mail_enviar(array $conta, array $m): array
{
    $mensagem = mail_monta($m);
    try {
        $stream = SmtpSocket::abrir(
            (string) $conta['smtp_host'],
            (int) $conta['smtp_porta'],
            (string) $conta['smtp_seg']
        );
    } catch (Throwable $e) {
        // Nem conectou: com certeza nao saiu, pode tentar de novo.
        return ['ok' => false, 'erro' => $e->getMessage(), 'incerto' => false];
    }
    $r = smtp_dialogo($stream, [
        'seguranca' => (string) $conta['smtp_seg'],
        'usuario'   => (string) $conta['usuario'],
        'senha'     => (string) $conta['senha'],
        'helo'      => (string) (cfg('mail_helo') ?: mail_dominio((string) $conta['email'])),
    ], (string) $conta['email'], [(string) $m['para_email']], $mensagem);

    return ['ok' => $r['ok'], 'erro' => $r['erro'], 'incerto' => !empty($r['incerto'])];
}

/**
 * Aviso interno (retorno recebido, resumo do dia, falha de envio). Sai pela
 * caixa informada; se ela nao existir, pela primeira configurada.
 */
function mail_aviso(?array $conta, string $paraEmail, string $assunto, string $corpo, array $cc = []): bool
{
    if ($conta === null) {
        $todas = mail_contas();
        $conta = $todas ? reset($todas) : null;
    }
    if ($conta === null || $paraEmail === '') {
        return false;
    }
    $ok = mail_enviar($conta, [
        'de_nome'     => '+351 CRM',
        'de_email'    => $conta['email'],
        'para_email'  => $paraEmail,
        'assunto'     => $assunto,
        'corpo'       => $corpo,
        'message_id'  => mail_message_id(mail_dominio((string) $conta['email'])),
        'auto'        => true,
    ]);
    foreach ($cc as $extra) {
        $extra = mb_strtolower(trim((string) $extra));
        if ($extra === '' || $extra === mb_strtolower($paraEmail)) {
            continue;
        }
        mail_enviar($conta, [
            'de_nome'    => '+351 CRM',
            'de_email'   => $conta['email'],
            'para_email' => $extra,
            'assunto'    => $assunto,
            'corpo'      => $corpo,
            'message_id' => mail_message_id(mail_dominio((string) $conta['email'])),
            'auto'       => true,
        ]);
    }
    return $ok['ok'];
}
