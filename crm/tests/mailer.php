<?php
/** Montagem da mensagem (MIME) e o dialogo SMTP inteiro, sem servidor. */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

define('CRM', 1);
date_default_timezone_set('America/Sao_Paulo');

$CRM = dirname(__DIR__);

function rows(string $sql, array $p = []): array { throw new RuntimeException('sem banco'); }
function q(string $sql, array $p = []) { throw new RuntimeException('sem banco'); }
function scalar(string $sql, array $p = []) { throw new RuntimeException('sem banco'); }
function row(string $sql, array $p = []) { throw new RuntimeException('sem banco'); }
function last_id(): int { return 1; }
function esc(?string $s): string { return htmlspecialchars((string) $s, ENT_QUOTES, 'UTF-8'); }
function db() { throw new RuntimeException('sem banco'); }
function cfg(string $k) { return ['mail_helo' => 'mais351monitor.com.br'][$k] ?? null; }

require $CRM . '/lib/validate.php';
require $CRM . '/lib/settings.php';
require $CRM . '/lib/model.php';
require $CRM . '/lib/smtp.php';
require $CRM . '/lib/mailer.php';

$falhas = 0;
$total = 0;
function check(bool $cond, string $msg): void
{
    global $falhas, $total;
    $total++;
    if (!$cond) { $falhas++; echo "  FALHOU: $msg\n"; }
}

/** Servidor SMTP de mentira: responde o que o roteiro mandar e grava tudo. */
final class SmtpFake implements SmtpStream
{
    public array $escrito = [];
    public bool $tls = false;

    /** @param string[] $roteiro linhas de resposta, na ordem */
    public function __construct(private array $roteiro, private bool $tlsOk = true)
    {
    }

    public function escrever(string $s): bool
    {
        $this->escrito[] = $s;
        return true;
    }

    public function lerLinha(): string|false
    {
        $l = array_shift($this->roteiro);
        return $l === null ? false : $l;
    }

    public function ligarTls(): bool
    {
        $this->tls = true;
        return $this->tlsOk;
    }

    public function fechar(): void
    {
    }

    public function comandos(): string
    {
        return implode('', $this->escrito);
    }
}

echo "== mail_header_encode ==\n";
check(mail_header_encode('Assunto simples') === 'Assunto simples', 'ASCII passa direto');
$enc = mail_header_encode('Horas da equipe na Contabilidade Alfa: produção?');
check(str_starts_with($enc, '=?UTF-8?B?'), "acento vira encoded-word: $enc");
check(str_contains(base64_decode(explode('?', $enc)[3]), 'Horas'), 'o base64 decodifica de volta');
foreach (explode("\r\n ", $enc) as $pedaco) {
    check(strlen($pedaco) <= 75, 'pedaco de encoded-word passou de 75 chars: ' . strlen($pedaco));
}
$longo = mail_header_encode(str_repeat('ação ', 40));
check(str_contains($longo, "\r\n "), 'cabecalho longo foi dobrado');
foreach (explode("\r\n ", $longo) as $pedaco) {
    check(strlen($pedaco) <= 75, 'pedaco longo passou de 75 chars: ' . strlen($pedaco));
}
check(str_contains(mail_header_encode("quebra\r\nembutida"), 'quebra embutida'),
    'CRLF dentro do assunto vira espaco (senao da para injetar cabecalho)');

echo "== mail_endereco ==\n";
check(mail_endereco('', 'a@b.com') === 'a@b.com', 'sem nome sai so o endereco');
check(mail_endereco('Bruna | +351 Monitor', 'b@c.com') === 'Bruna | +351 Monitor <b@c.com>',
    'nome sem caractere especial nao leva aspas: ' . mail_endereco('Bruna | +351 Monitor', 'b@c.com'));
check(mail_endereco('Silva, Joao', 'j@c.com') === '"Silva, Joao" <j@c.com>', 'virgula exige aspas');
check(str_contains(mail_endereco('João', 'j@c.com'), '=?UTF-8?B?'), 'nome com acento vira encoded-word');

echo "== injecao de cabecalho pelo nome do contato ==\n";
// O nome vem de import de CSV e da API, onde norm_text() so corta o tamanho.
$veneno = mail_endereco("Maria\r\nBcc: espiao@evil.com", 'maria@alfa.com.br');
check(!str_contains($veneno, "\r"), "sobrou CR no cabecalho: " . addcslashes($veneno, "\r\n"));
check(!str_contains($veneno, "\n"), "sobrou LF no cabecalho: " . addcslashes($veneno, "\r\n"));
check(!preg_match('/^Bcc:/m', $veneno), 'o Bcc injetado nao virou cabecalho');
$msg = mail_monta([
    'de_nome' => "Bruna\r\nBcc: espiao@evil.com", 'de_email' => 'bruna@x.com',
    'para_nome' => "Maria\nX-Spoof: 1", 'para_email' => 'maria@alfa.com.br',
    'assunto' => "oi\r\nBcc: outro@evil.com", 'corpo' => 'texto',
    'message_id' => '<m@x.com>',
]);
[$cabecalhos] = explode("\r\n\r\n", $msg, 2);
check(!preg_match('/^Bcc:/mi', $cabecalhos), 'nenhum Bcc injetado sobreviveu ao mail_monta');
check(!preg_match('/^X-Spoof:/mi', $cabecalhos), 'nenhum cabecalho forjado pelo nome do destinatario');
check(substr_count($cabecalhos, "\r\nFrom:") + (str_starts_with($cabecalhos, 'From:') ? 1 : 0) === 1,
    'um From so');

echo "== mail_monta ==\n";
$msg = mail_monta([
    'de_nome' => 'Bruna', 'de_email' => 'bruna@mais351monitor.com.br',
    'para_nome' => 'Maria Souza', 'para_email' => 'maria@alfa.com.br',
    'assunto' => 'Produção da equipe', 'corpo' => "Oi, Maria.\nTudo bem?\n",
    'message_id' => '<abc@mais351monitor.com.br>',
    'in_reply_to' => '<anterior@mais351monitor.com.br>',
    'references' => str_repeat('<r1@x.com> ', 12),
    'unsubscribe_url' => 'https://www.mais351monitor.com.br/crm/optout.php?l=1&c=2&t=abc',
]);
[$cab, $corpo] = explode("\r\n\r\n", $msg, 2);
foreach (['Date:', 'From:', 'To:', 'Subject:', 'Message-ID:', 'In-Reply-To:', 'References:',
             'MIME-Version:', 'Content-Type:', 'Content-Transfer-Encoding:',
             'List-Unsubscribe:', 'List-Unsubscribe-Post:'] as $h) {
    check(str_contains($cab, "\r\n" . $h) || str_starts_with($cab, $h), "faltou o cabecalho $h");
}
check(!str_contains($cab, 'Auto-Submitted'), 'e-mail de cadencia NAO leva Auto-Submitted');
check(str_contains($cab, 'List-Unsubscribe-Post: List-Unsubscribe=One-Click'),
    'sem o One-Click o Gmail nao mostra o botao de cancelar inscricao');
check(str_contains($cab, 'text/plain; charset=UTF-8'), 'corpo e texto puro UTF-8');
foreach (explode("\r\n", $cab) as $linha) {
    check(strlen($linha) <= 998, 'linha de cabecalho passou do limite do SMTP: ' . strlen($linha));
}
check(substr_count($cab, "\r\nReferences:") === 1, 'References aparece uma vez so');
check(str_contains($cab, "\r\n ") , 'References longo foi dobrado com continuacao');
check(str_contains(quoted_printable_decode($corpo), 'Oi, Maria.'), 'o corpo decodifica de volta');
check(str_contains($msg, "\r\n"), 'a mensagem usa CRLF');
check(!preg_match('/(?<!\r)\n/', $msg), 'nao pode sobrar \n solto fora de CRLF');

$auto = mail_monta([
    'de_email' => 'a@b.com', 'para_email' => 'c@d.com', 'assunto' => 'x', 'corpo' => 'y',
    'message_id' => '<m@b.com>', 'auto' => true,
]);
check(str_contains($auto, 'Auto-Submitted: auto-generated'), 'aviso interno leva Auto-Submitted');

echo "== mail_texto_para_html ==\n";
$h = mail_texto_para_html("Oi, Maria.\nTudo bem?");
check(str_contains($h, '<br'), "quebra de linha vira <br>: $h");
check(!str_contains(mail_texto_para_html('<script>alert(1)</script>'), '<script>'),
    'HTML vindo do texto e escapado');
$comLink = mail_texto_para_html('Cancele aqui: https://www.mais351monitor.com.br/crm/optout.php?l=1&c=2&t=abc');
check(str_contains($comLink, '<a href="https://www.mais351monitor.com.br/crm/optout.php?l=1&amp;c=2&amp;t=abc"'),
    "a URL virou link com o & escapado: $comLink");
check(substr_count($comLink, '<a ') === 1, 'um link so');
check(!str_contains(mail_texto_para_html('fim da frase. https://ex.com/a.'), 'a.</a>'),
    'o ponto final da frase nao entra dentro do link');

echo "== mail_html_de_texto: a assinatura de texto vira a visual ==\n";
$assTxt = "Abraço,\n\nBruna Rondelli · COO\nmais351monitor.com.br";
$assHtml = '<table><tr><td>Bruna Rondelli</td></tr></table>';
$textoFinal = "Oi, Maria, tudo bem?\n\nVale uma conversa?\n\n" . $assTxt
    . "\n\nNão quer mais receber? Responda SAIR:\nhttps://ex.com/sair?t=1";
$htmlFinal = mail_html_de_texto($textoFinal, $assTxt, $assHtml);
check(str_contains($htmlFinal, $assHtml), 'a assinatura visual entrou');
check(!str_contains($htmlFinal, 'Bruna Rondelli · COO'), 'a assinatura de TEXTO nao aparece duplicada');
check(str_contains($htmlFinal, 'Vale uma conversa?'), 'o corpo continua');
check(str_contains($htmlFinal, 'font-size:11px'), 'o rodape de descadastro saiu pequeno e discreto');
check(str_contains($htmlFinal, '<a href="https://ex.com/sair?t=1"'), 'o link de saida virou hiperlink de verdade');
check(str_starts_with($htmlFinal, '<!doctype html>'), 'documento HTML completo');
// Sem assinatura configurada, nada de substituicao: so o texto convertido.
$semAss = mail_html_de_texto("Oi.\n\nAté logo.", '', '');
check(str_contains($semAss, 'Até logo.'), 'sem assinatura o texto passa inteiro');
check(!str_contains($semAss, '<table'), 'sem assinatura nao inventa bloco visual');
// Assinatura configurada mas ausente do corpo: nao pode injetar do nada.
$naoBate = mail_html_de_texto("Oi, so isso.", $assTxt, $assHtml);
check(!str_contains($naoBate, $assHtml), 'assinatura que nao esta no texto nao e injetada no HTML');

echo "== mail_monta: multipart/alternative ==\n";
$msgMp = mail_monta([
    'de_email' => 'bruna@x.com', 'para_email' => 'maria@alfa.com.br',
    'assunto' => 'Produção', 'corpo' => "Oi, Maria.\nTudo bem?",
    'html' => '<!doctype html><html><body><p>Oi, Maria.</p></body></html>',
    'message_id' => '<m@x.com>',
]);
check(preg_match('/Content-Type: multipart\/alternative; boundary="(=_m351_[0-9a-f]{24})"/', $msgMp, $mb) === 1,
    'cabecalho multipart com fronteira');
$fronteira = $mb[1];
check(substr_count($msgMp, '--' . $fronteira) === 3, 'duas partes e o fechamento');
check(str_ends_with(rtrim($msgMp), '--' . $fronteira . '--'), 'termina com a fronteira de fechamento');
$posTexto = strpos($msgMp, 'Content-Type: text/plain');
$posHtml  = strpos($msgMp, 'Content-Type: text/html');
check($posTexto !== false && $posHtml !== false && $posTexto < $posHtml,
    'texto antes do HTML (em alternative, a ultima parte e a preferida)');
check(!str_contains($msgMp, $fronteira . "\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n\r\n"),
    'parte de texto nao esta vazia');
check(str_contains(quoted_printable_decode($msgMp), 'Tudo bem?'), 'o texto sobreviveu ao quoted-printable');
check(!preg_match('/(?<!\r)\n/', $msgMp), 'multipart tambem so usa CRLF');
// Sem html, nada muda em relacao ao formato antigo.
$msgTxt = mail_monta(['de_email' => 'a@b.com', 'para_email' => 'c@d.com', 'assunto' => 'x',
    'corpo' => 'y', 'message_id' => '<m@b.com>']);
check(str_contains($msgTxt, 'Content-Type: text/plain; charset=UTF-8'), 'sem html continua text/plain simples');
check(!str_contains($msgTxt, 'multipart'), 'sem html nao vira multipart');

echo "== smtp_dot_stuff ==\n";
check(smtp_dot_stuff("linha\r\n.ponto\r\n") === "linha\r\n..ponto\r\n", 'ponto no inicio da linha e duplicado');
check(smtp_dot_stuff(".comeco\r\nfim") === "..comeco\r\nfim", 'ponto na primeira linha tambem');
check(smtp_dot_stuff("sem ponto\r\nnenhum") === "sem ponto\r\nnenhum", 'texto normal fica igual');
check(smtp_dot_stuff("a\r\n.\r\nb") === "a\r\n..\r\nb", 'linha com so um ponto (o terminador!) e escapada');

echo "== smtp_resposta: multilinha ==\n";
$fake = new SmtpFake(["250-mail.hostinger.com\r\n", "250-SIZE 52428800\r\n", "250 AUTH PLAIN LOGIN\r\n"]);
$r = smtp_resposta($fake);
check($r['code'] === 250, 'codigo do multilinha e 250, veio ' . $r['code']);
check(str_contains($r['texto'], 'AUTH PLAIN LOGIN'), 'o texto junta todas as linhas');

echo "== smtp_dialogo: caminho feliz (SSL, sem STARTTLS) ==\n";
$fake = new SmtpFake([
    "220 smtp.hostinger.com ESMTP\r\n",
    "250-smtp.hostinger.com\r\n", "250 AUTH PLAIN LOGIN\r\n",
    "235 2.7.0 Authentication successful\r\n",
    "250 2.1.0 Ok\r\n",
    "250 2.1.5 Ok\r\n",
    "354 End data with <CR><LF>.<CR><LF>\r\n",
    "250 2.0.0 Ok: queued as 4XYZ\r\n",
    "221 Bye\r\n",
]);
$r = smtp_dialogo($fake, ['seguranca' => 'ssl', 'usuario' => 'bruna@x.com', 'senha' => 'segredo', 'helo' => 'x.com'],
    'bruna@x.com', ['maria@alfa.com.br'], "Subject: oi\r\n\r\ncorpo\r\n");
check($r['ok'], 'dialogo feliz deveria dar ok: ' . $r['erro']);
$cmds = $fake->comandos();
check(str_contains($cmds, "EHLO x.com\r\n"), 'mandou EHLO');
check(str_contains($cmds, 'AUTH PLAIN ' . base64_encode("\0bruna@x.com\0segredo")), 'AUTH PLAIN com o payload certo');
check(str_contains($cmds, "MAIL FROM:<bruna@x.com>\r\n"), 'MAIL FROM com o envelope certo');
check(str_contains($cmds, "RCPT TO:<maria@alfa.com.br>\r\n"), 'RCPT TO com o destinatario');
check(str_contains($cmds, "\r\n.\r\n"), 'terminou o DATA com o ponto sozinho');
check(str_contains($cmds, "QUIT\r\n"), 'encerrou com QUIT');
check(!$fake->tls, 'em modo ssl nao existe STARTTLS');
check(!str_contains($cmds, 'segredo'), 'a senha nunca vai em claro no comando');

echo "== smtp_dialogo: STARTTLS na 587 ==\n";
$fake = new SmtpFake([
    "220 smtp.hostinger.com ESMTP\r\n",
    "250-smtp.hostinger.com\r\n", "250 STARTTLS\r\n",
    "220 2.0.0 Ready to start TLS\r\n",
    "250-smtp.hostinger.com\r\n", "250 AUTH LOGIN\r\n",
    "334 VXNlcm5hbWU6\r\n",
    "334 UGFzc3dvcmQ6\r\n",
    "235 ok\r\n",
    "250 ok\r\n", "250 ok\r\n", "354 go\r\n", "250 queued\r\n", "221 bye\r\n",
]);
$r = smtp_dialogo($fake, ['seguranca' => 'tls', 'usuario' => 'u@x.com', 'senha' => 'p', 'helo' => 'x.com'],
    'u@x.com', ['a@b.com'], "corpo\r\n");
check($r['ok'], 'STARTTLS deveria funcionar: ' . $r['erro']);
check($fake->tls, 'a cifra foi ligada no socket');
check(substr_count($fake->comandos(), 'EHLO') === 2, 'EHLO tem que ser repetido depois do STARTTLS');
check(str_contains($fake->comandos(), 'AUTH LOGIN'), 'caiu no AUTH LOGIN quando o servidor nao ofereceu PLAIN');

echo "== smtp_dialogo: servidor sem STARTTLS na porta 587 ==\n";
$fake = new SmtpFake(["220 ok\r\n", "250 smtp.hostinger.com\r\n"]);
$r = smtp_dialogo($fake, ['seguranca' => 'tls', 'usuario' => 'u', 'senha' => 'p'], 'u@x.com', ['a@b.com'], 'x');
check(!$r['ok'], 'sem STARTTLS o envio tem que falhar, nunca cair para texto claro');
check(str_contains($r['erro'], 'STARTTLS'), 'o erro diz o que aconteceu: ' . $r['erro']);

echo "== smtp_dialogo: autenticacao recusada ==\n";
$fake = new SmtpFake([
    "220 ok\r\n", "250-x\r\n", "250 AUTH PLAIN\r\n",
    "535 5.7.8 Error: authentication failed: bruna@x.com\r\n",
]);
$r = smtp_dialogo($fake, ['seguranca' => 'ssl', 'usuario' => 'bruna@x.com', 'senha' => 'errada'],
    'bruna@x.com', ['a@b.com'], 'x');
check(!$r['ok'], 'AUTH recusado tem que falhar');
check(str_contains($r['erro'], '535'), 'o codigo aparece no erro: ' . $r['erro']);
check(!str_contains($r['erro'], 'bruna@x.com'), 'o erro nao pode ecoar a resposta crua (vaza usuario no log)');

echo "== smtp_dialogo: destinatario recusado ==\n";
$fake = new SmtpFake([
    "220 ok\r\n", "250 x\r\n", "250 ok\r\n",
    "550 5.1.1 <naoexiste@alfa.com.br>: Recipient address rejected\r\n",
]);
$r = smtp_dialogo($fake, ['seguranca' => 'nenhuma'], 'bruna@x.com', ['naoexiste@alfa.com.br'], 'x');
check(!$r['ok'], 'RCPT recusado tem que falhar');
check(str_contains($r['erro'], '550'), 'o motivo chega ao chamador: ' . $r['erro']);

echo "== smtp_dialogo: conexao cai no meio ==\n";
$fake = new SmtpFake(["220 ok\r\n", "250 x\r\n"]);
$r = smtp_dialogo($fake, ['seguranca' => 'nenhuma'], 'a@b.com', ['c@d.com'], 'x');
check(!$r['ok'], 'socket sem resposta nao pode virar sucesso');
check(empty($r['incerto']), 'cair ANTES do DATA e certeza de que nao saiu — pode tentar de novo');

echo "== smtp_dialogo: entrega incerta (a conexao cai DEPOIS do corpo) ==\n";
// O servidor pode ter enfileirado a mensagem e so nao ter respondido.
// Reenviar por conta propria mandaria o mesmo e-mail duas vezes ao prospect.
$fake = new SmtpFake(["220 ok\r\n", "250 x\r\n", "250 ok\r\n", "250 ok\r\n", "354 go\r\n"]);
$r = smtp_dialogo($fake, ['seguranca' => 'nenhuma'], 'a@b.com', ['c@d.com'], "corpo\r\n");
check(!$r['ok'], 'sem confirmacao final nao e sucesso');
check(!empty($r['incerto']), 'cair DEPOIS do corpo tem que marcar a entrega como incerta');

$fake = new SmtpFake(["220 ok\r\n", "250 x\r\n", "250 ok\r\n", "250 ok\r\n", "354 go\r\n",
    "552 5.3.4 Message too big\r\n"]);
$r = smtp_dialogo($fake, ['seguranca' => 'nenhuma'], 'a@b.com', ['c@d.com'], "corpo\r\n");
check(!$r['ok'], 'recusa explicita no fim do DATA e falha');
check(empty($r['incerto']), 'recusa EXPLICITA (552) e certeza de que nao saiu — nao e incerta');

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
