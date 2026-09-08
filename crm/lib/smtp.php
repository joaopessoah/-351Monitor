<?php
/**
 * Cliente SMTP minimo, sem dependencia externa.
 *
 * A hospedagem compartilhada nao tem Composer e o repo inteiro e PHP puro
 * (CSRF, throttle, analytics e a consulta de CNPJ tambem foram escritos a mao),
 * entao vendorizar uma biblioteca de e-mail traria dezenas de arquivos de
 * terceiro para o deploy FTPS por causa de um dialogo de 8 comandos.
 *
 * O dialogo e separado do socket de proposito: SmtpStream tem duas
 * implementacoes, a real e a de teste (tests/mailer.php roteiriza as respostas
 * do servidor), entao a conversa inteira — incluindo AUTH, dot-stuffing e os
 * caminhos de erro — e exercitada sem subir servidor nenhum.
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

interface SmtpStream
{
    public function escrever(string $s): bool;

    /** Uma linha COM o CRLF, ou false no fim/erro. */
    public function lerLinha(): string|false;

    /** STARTTLS: liga a cifra no socket ja aberto. */
    public function ligarTls(): bool;

    public function fechar(): void;
}

/** Socket TCP/TLS de verdade. */
final class SmtpSocket implements SmtpStream
{
    /** @param resource $sock */
    private function __construct(private $sock)
    {
    }

    /**
     * @param string $seguranca 'ssl' (porta 465, cifra desde o inicio),
     *                          'tls' (587, STARTTLS depois do EHLO) ou 'nenhuma'
     * @throws RuntimeException quando nao conecta
     */
    public static function abrir(string $host, int $porta, string $seguranca, int $timeout = 15): self
    {
        $alvo = ($seguranca === 'ssl' ? 'ssl://' : 'tcp://') . $host . ':' . $porta;
        $ctx = stream_context_create(['ssl' => [
            'verify_peer'       => true,
            'verify_peer_name'  => true,
            'SNI_enabled'       => true,
            'peer_name'         => $host,
        ]]);
        $errNo = 0;
        $errStr = '';
        $sock = @stream_socket_client($alvo, $errNo, $errStr, $timeout, STREAM_CLIENT_CONNECT, $ctx);
        if ($sock === false) {
            throw new RuntimeException('SMTP: nao conectou em ' . $host . ':' . $porta . ' — ' . $errStr);
        }
        stream_set_timeout($sock, $timeout);
        return new self($sock);
    }

    public function escrever(string $s): bool
    {
        return @fwrite($this->sock, $s) !== false;
    }

    public function lerLinha(): string|false
    {
        $l = @fgets($this->sock, 8192);
        if ($l === false) {
            return false;
        }
        $meta = stream_get_meta_data($this->sock);
        return !empty($meta['timed_out']) ? false : $l;
    }

    public function ligarTls(): bool
    {
        return (bool) @stream_socket_enable_crypto(
            $this->sock,
            true,
            STREAM_CRYPTO_METHOD_TLS_CLIENT | STREAM_CRYPTO_METHOD_TLSv1_2_CLIENT | STREAM_CRYPTO_METHOD_TLSv1_3_CLIENT
        );
    }

    public function fechar(): void
    {
        if (is_resource($this->sock)) {
            @fclose($this->sock);
        }
    }
}

/**
 * Le uma resposta completa (trata o multilinha "250-...\r\n250 ...").
 * @return array{code:int, texto:string}
 */
function smtp_resposta(SmtpStream $s): array
{
    $texto = '';
    $code = 0;
    while (true) {
        $linha = $s->lerLinha();
        if ($linha === false) {
            return ['code' => 0, 'texto' => trim($texto) . ' (conexao caiu ou expirou)'];
        }
        $texto .= $linha;
        $limpa = rtrim($linha, "\r\n");
        if (strlen($limpa) < 3 || !ctype_digit(substr($limpa, 0, 3))) {
            return ['code' => 0, 'texto' => trim($texto)];
        }
        $code = (int) substr($limpa, 0, 3);
        // 4o caractere '-' = tem mais linha; ' ' (ou nada) = acabou
        if (strlen($limpa) === 3 || $limpa[3] !== '-') {
            return ['code' => $code, 'texto' => trim($texto)];
        }
    }
}

/**
 * Manda um comando e devolve a resposta.
 * @return array{code:int, texto:string}
 */
function smtp_cmd(SmtpStream $s, string $cmd): array
{
    $s->escrever($cmd . "\r\n");
    return smtp_resposta($s);
}

/**
 * Ponto final do corpo: linha que comeca com '.' vira '..' (RFC 5321 4.5.2),
 * senao um paragrafo iniciado por ponto encerra o DATA no meio do e-mail.
 * Idempotente nao e — recebe o corpo ja normalizado em CRLF, uma vez so.
 */
function smtp_dot_stuff(string $data): string
{
    if (str_starts_with($data, '.')) {
        $data = '.' . $data;
    }
    return str_replace("\r\n.", "\r\n..", $data);
}

/**
 * Roda o dialogo inteiro sobre um SmtpStream ja aberto.
 *
 * @param array $conta host, porta, seguranca, usuario, senha, helo
 * @param string $de   envelope MAIL FROM (o Return-Path real)
 * @param string[] $para envelope RCPT TO
 * @param string $mensagem cabecalhos + corpo, ja em CRLF
 * @return array{ok:bool, erro:string, incerto:bool, log:string[]}
 *         incerto = a mensagem JA FOI ENTREGUE ao servidor e a resposta final
 *         nao chegou. Reenviar nesse caso duplica o e-mail no destinatario.
 */
function smtp_dialogo(SmtpStream $s, array $conta, string $de, array $para, string $mensagem): array
{
    $log = [];
    $falha = function (string $msg, bool $incerto = false) use (&$log, $s): array {
        @smtp_cmd($s, 'QUIT');
        $s->fechar();
        return ['ok' => false, 'erro' => $msg, 'incerto' => $incerto, 'log' => $log];
    };

    $r = smtp_resposta($s);
    $log[] = 'banner: ' . $r['code'];
    if ($r['code'] !== 220) {
        return $falha('servidor recusou a conexao: ' . $r['texto']);
    }

    $helo = $conta['helo'] ?? 'localhost';
    $r = smtp_cmd($s, 'EHLO ' . $helo);
    $log[] = 'EHLO: ' . $r['code'];
    if ($r['code'] !== 250) {
        return $falha('EHLO recusado: ' . $r['texto']);
    }
    $capacidades = strtoupper($r['texto']);

    if (($conta['seguranca'] ?? 'tls') === 'tls') {
        if (!str_contains($capacidades, 'STARTTLS')) {
            return $falha('servidor nao oferece STARTTLS (use seguranca "ssl" na porta 465)');
        }
        $r = smtp_cmd($s, 'STARTTLS');
        $log[] = 'STARTTLS: ' . $r['code'];
        if ($r['code'] !== 220 || !$s->ligarTls()) {
            return $falha('STARTTLS falhou: ' . $r['texto']);
        }
        $r = smtp_cmd($s, 'EHLO ' . $helo);
        $log[] = 'EHLO/TLS: ' . $r['code'];
        if ($r['code'] !== 250) {
            return $falha('EHLO pos-TLS recusado: ' . $r['texto']);
        }
        $capacidades = strtoupper($r['texto']);
    }

    $usuario = (string) ($conta['usuario'] ?? '');
    if ($usuario !== '') {
        $senha = (string) ($conta['senha'] ?? '');
        if (str_contains($capacidades, 'AUTH') && str_contains($capacidades, 'PLAIN')) {
            $r = smtp_cmd($s, 'AUTH PLAIN ' . base64_encode("\0" . $usuario . "\0" . $senha));
            $log[] = 'AUTH PLAIN: ' . $r['code'];
        } else {
            $r = smtp_cmd($s, 'AUTH LOGIN');
            $log[] = 'AUTH LOGIN: ' . $r['code'];
            if ($r['code'] === 334) {
                $r = smtp_cmd($s, base64_encode($usuario));
                $log[] = 'AUTH usuario: ' . $r['code'];
            }
            if ($r['code'] === 334) {
                $r = smtp_cmd($s, base64_encode($senha));
                $log[] = 'AUTH senha: ' . $r['code'];
            }
        }
        if ($r['code'] !== 235) {
            // Nunca ecoar a resposta crua aqui: alguns servidores devolvem o
            // usuario na mensagem de erro e isso vai parar no log/tela.
            return $falha('autenticacao recusada pelo servidor (codigo ' . $r['code'] . ')');
        }
    }

    $r = smtp_cmd($s, 'MAIL FROM:<' . $de . '>');
    $log[] = 'MAIL FROM: ' . $r['code'];
    if ($r['code'] !== 250) {
        return $falha('MAIL FROM recusado: ' . $r['texto']);
    }
    foreach ($para as $p) {
        $r = smtp_cmd($s, 'RCPT TO:<' . $p . '>');
        $log[] = 'RCPT TO: ' . $r['code'];
        if ($r['code'] !== 250 && $r['code'] !== 251) {
            return $falha('destinatario recusado (' . $r['code'] . '): ' . $r['texto']);
        }
    }

    $r = smtp_cmd($s, 'DATA');
    $log[] = 'DATA: ' . $r['code'];
    if ($r['code'] !== 354) {
        return $falha('DATA recusado: ' . $r['texto']);
    }
    $s->escrever(smtp_dot_stuff($mensagem) . "\r\n.\r\n");
    $r = smtp_resposta($s);
    $log[] = 'fim do DATA: ' . $r['code'];
    if ($r['code'] !== 250) {
        // code 0 = a conexao caiu ou expirou DEPOIS de o corpo inteiro ter
        // sido escrito. O servidor pode ter enfileirado a mensagem e so nao
        // ter conseguido responder: reenviar por conta propria mandaria o
        // mesmo e-mail duas vezes para o prospect. Quem decide e gente.
        return $falha('servidor nao aceitou a mensagem: ' . $r['texto'], $r['code'] === 0);
    }

    smtp_cmd($s, 'QUIT');
    $s->fechar();
    return ['ok' => true, 'erro' => '', 'incerto' => false, 'log' => $log];
}
