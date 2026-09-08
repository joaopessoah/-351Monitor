<?php
/** Classificador de retorno: devolucao, resposta automatica, opt-out e gente. */

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
function cfg(string $k) { return null; }

require $CRM . '/lib/validate.php';
require $CRM . '/lib/settings.php';
require $CRM . '/lib/model.php';
require $CRM . '/lib/smtp.php';
require $CRM . '/lib/mailer.php';
require $CRM . '/lib/email_check.php';
require $CRM . '/lib/cadencia.php';
require $CRM . '/lib/inbound.php';

$falhas = 0;
$total = 0;
function check(bool $cond, string $msg): void
{
    global $falhas, $total;
    $total++;
    if (!$cond) { $falhas++; echo "  FALHOU: $msg\n"; }
}

/** Monta uma mensagem crua (cabecalho + corpo) e classifica, como o cron faz. */
function classificaBruta(string $cabecalho, string $corpo): array
{
    $h = inbound_headers($cabecalho);
    $texto = inbound_texto($corpo, $h);
    return inbound_classificar($h, $texto) + ['texto' => $texto];
}

echo "== inbound_headers ==\n";
$h = inbound_headers("Subject: Um assunto\r\n  que dobra em duas linhas\r\nFrom: a@b.com\r\nReceived: x\r\nReceived: y\r\n");
check($h['subject'] === 'Um assunto que dobra em duas linhas', 'unfolding juntou a linha dobrada: ' . $h['subject']);
check($h['from'] === 'a@b.com', 'chave em minusculas');
check($h['received'] === 'x', 'cabecalho repetido mantem o primeiro');
check(!isset($h['nao-existe']), 'chave ausente nao vira null magico');

echo "== inbound_decode_mime ==\n";
check(inbound_decode_mime('=?UTF-8?B?' . base64_encode('Produção da equipe') . '?=') === 'Produção da equipe',
    'base64 UTF-8 decodifica');
check(inbound_decode_mime('=?ISO-8859-1?Q?Produ=E7=E3o?=') === 'Produção', 'quoted-printable latin-1 decodifica');
check(inbound_decode_mime('RE: assunto simples') === 'RE: assunto simples', 'texto puro passa igual');
$dois = '=?UTF-8?B?' . base64_encode('Reunião ') . '?= =?UTF-8?B?' . base64_encode('amanhã') . '?=';
check(inbound_decode_mime($dois) === 'Reunião amanhã', 'dois encoded-words adjacentes: ' . inbound_decode_mime($dois));

echo "== inbound_endereco ==\n";
check(inbound_endereco('Maria Souza <maria@alfa.com.br>') === ['nome' => 'Maria Souza', 'email' => 'maria@alfa.com.br'],
    'nome e endereco separados');
check(inbound_endereco('<maria@alfa.com.br>')['email'] === 'maria@alfa.com.br', 'so o endereco entre <>');
check(inbound_endereco('maria@alfa.com.br')['email'] === 'maria@alfa.com.br', 'endereco cru');
check(inbound_endereco('"Souza, Maria" <MARIA@ALFA.com.br>')['email'] === 'maria@alfa.com.br', 'endereco vira minusculo');
check(inbound_endereco('lixo sem arroba')['email'] === '', 'endereco invalido volta vazio');

echo "== inbound_texto: codificacoes ==\n";
$b64 = inbound_texto(base64_encode("Oi, tudo bem?\nAbraço."),
    ['content-type' => 'text/plain; charset=UTF-8', 'content-transfer-encoding' => 'base64']);
check(str_contains($b64, 'Abraço.'), "base64 decodificado: " . mb_substr($b64, 0, 30));

$qp = inbound_texto("Ol=C3=A1, tudo bem?",
    ['content-type' => 'text/plain; charset=UTF-8', 'content-transfer-encoding' => 'quoted-printable']);
check(str_contains($qp, 'Olá'), "quoted-printable decodificado: $qp");

$latin = inbound_texto(mb_convert_encoding('Reunião confirmada', 'ISO-8859-1', 'UTF-8'),
    ['content-type' => 'text/plain; charset=ISO-8859-1']);
check(str_contains($latin, 'Reunião'), "latin-1 convertido para UTF-8: $latin");

$html = inbound_texto('<p>Oi, <b>Bruna</b>!</p>', ['content-type' => 'text/html; charset=UTF-8']);
check(trim($html) === 'Oi, Bruna!', "HTML sem tags: $html");

$multipart = "--BND\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\nParte de texto puro.\r\n"
    . "--BND\r\nContent-Type: text/html; charset=UTF-8\r\n\r\n<p>Parte HTML</p>\r\n--BND--\r\n";
$mp = inbound_texto($multipart, ['content-type' => 'multipart/alternative; boundary="BND"']);
check(str_contains($mp, 'Parte de texto puro'), "multipart pegou a parte text/plain: " . trim($mp));
check(!str_contains($mp, 'Parte HTML'), 'multipart nao misturou a parte HTML');

echo "== inbound_limpar_citacao ==\n";
$comCitacao = "Oi Bruna, faz sentido sim.\n\nEm 8 de setembro de 2026, Bruna escreveu:\n"
    . "> O +351 Monitor mostra horas ativas\n> Se nao quiser receber meus e-mails, responda SAIR\n";
$limpo = inbound_limpar_citacao($comCitacao);
check(str_contains($limpo, 'faz sentido sim'), 'a resposta da pessoa ficou');
check(!str_contains($limpo, 'SAIR'), 'a citacao do NOSSO rodape sumiu — senao toda resposta viraria opt-out');
check(!str_contains($limpo, 'horas ativas'), 'o texto citado sumiu');
$comIngles = "Sure, let's talk.\n\nOn Sep 8, 2026, Bruna wrote:\n> responda SAIR\n";
check(!str_contains(inbound_limpar_citacao($comIngles), 'SAIR'), 'o corte em ingles tambem funciona');
$comOriginal = "Podemos ver semana que vem.\n\n-----Mensagem original-----\nDe: Bruna\nresponda SAIR\n";
check(!str_contains(inbound_limpar_citacao($comOriginal), 'SAIR'), 'o corte por "Mensagem original" funciona');

echo "== classificacao: devolucao definitiva ==\n";
$cab = "From: MAILER-DAEMON@hostinger.com\r\nTo: bruna@mais351monitor.com.br\r\n"
    . "Subject: Undelivered Mail Returned to Sender\r\n"
    . "Content-Type: multipart/report; report-type=delivery-status; boundary=\"BND\"\r\n";
$corpo = "--BND\r\nContent-Type: text/plain\r\n\r\n"
    . "This is the mail system at host smtp.hostinger.com.\r\n"
    . "<naoexiste@alfa.com.br>: host mx.alfa.com.br said: 550 5.1.1 User unknown\r\n--BND--\r\n";
$c = classificaBruta($cab, $corpo);
check($c['kind'] === 'bounce', "DSN classificado como bounce, veio {$c['kind']}");
check($c['code'] === '5.1.1', "codigo extraido, veio '{$c['code']}'");
check(inbound_bounce_definitivo($c['code']), '5.x.x e definitivo');

echo "== classificacao: devolucao temporaria ==\n";
$cab = "From: postmaster@alfa.com.br\r\nSubject: Delivery Status Notification (Delay)\r\n"
    . "Content-Type: text/plain; charset=UTF-8\r\n";
$c = classificaBruta($cab, "Status: 4.2.2 mailbox full, will retry\r\n");
check($c['kind'] === 'bounce', 'atraso tambem e bounce');
check($c['code'] === '4.2.2', "codigo temporario, veio '{$c['code']}'");
check(!inbound_bounce_definitivo($c['code']), '4.x.x NAO e definitivo — so adia');

echo "== classificacao: resposta automatica ==\n";
$cab = "From: Maria <maria@alfa.com.br>\r\nSubject: Resposta automática: Horas da equipe\r\n"
    . "Auto-Submitted: auto-replied\r\nContent-Type: text/plain; charset=UTF-8\r\n";
$c = classificaBruta($cab, "Estou de férias até 20/09. Em caso de urgência, procure o financeiro.\r\n");
check($c['kind'] === 'auto', "ausencia do escritorio e 'auto', veio {$c['kind']}");

$cab = "From: Maria <maria@alfa.com.br>\r\nSubject: Automatic reply: out of office\r\n"
    . "Content-Type: text/plain; charset=UTF-8\r\n";
check(classificaBruta($cab, "I am away.\r\n")['kind'] === 'auto', 'out of office em ingles tambem');

$cab = "From: Maria <maria@alfa.com.br>\r\nSubject: RE: Horas da equipe\r\n"
    . "X-Auto-Response-Suppress: All\r\nContent-Type: text/plain; charset=UTF-8\r\n";
check(classificaBruta($cab, "Volto dia 20.\r\n")['kind'] === 'auto', 'X-Auto-Response-Suppress marca automatica');

echo "== classificacao: pedido de saida ==\n";
$cabHumano = "From: Maria <maria@alfa.com.br>\r\nSubject: RE: Horas da equipe\r\n"
    . "Content-Type: text/plain; charset=UTF-8\r\n";
check(classificaBruta($cabHumano, "SAIR\r\n")['kind'] === 'optout', 'resposta so com SAIR');
check(classificaBruta($cabHumano, "sair, por favor\r\n")['kind'] === 'optout', 'sair minusculo com virgula');
check(classificaBruta($cabHumano, "Por favor, descadastrar meu e-mail desta lista. Obrigado, Maria\r\n")['kind'] === 'optout',
    'pedido explicito de descadastro');
check(classificaBruta($cabHumano, "Remova meu contato da base de voces.\r\n")['kind'] === 'optout',
    '"remova meu contato" e pedido de saida');
check(classificaBruta($cabHumano, "Nao quero receber mais e-mails sobre isso.\r\n")['kind'] === 'optout',
    '"nao quero receber mais" e pedido de saida');

echo "== classificacao: gente (o caso que nao pode dar errado) ==\n";
$corpoHumano = "Oi Bruna, faz sentido sim. Pode ser quinta as 15h?\r\n\r\n"
    . "Em 8 de setembro de 2026, Bruna escreveu:\r\n"
    . "> Vale uma demonstracao de 10 minutos pelo WhatsApp?\r\n"
    . "> Se nao quiser receber meus e-mails, responda SAIR que removo seu contato.\r\n";
$c = classificaBruta($cabHumano, $corpoHumano);
check($c['kind'] === 'humana', "resposta citando o nosso rodape TEM que ser humana, veio {$c['kind']}");
check(classificaBruta($cabHumano, "Bom dia! Manda mais detalhes por favor.\r\n")['kind'] === 'humana',
    'resposta interessada e humana');
check(classificaBruta($cabHumano, "Vou sair de ferias amanha, me procure em outubro.\r\n")['kind'] === 'humana',
    '"vou sair de ferias" no meio da frase NAO e pedido de descadastro');
check(classificaBruta($cabHumano, "Hoje nao da, vou sair mais cedo. Semana que vem eu retorno.\r\n")['kind'] === 'humana',
    '"vou sair mais cedo" tambem nao e descadastro');

echo "== classificacao: mensagem vazia nao vira opt-out ==\n";
check(classificaBruta($cabHumano, "\r\n")['kind'] === 'humana', 'corpo vazio cai em humana (pede olho de gente)');

echo "== HTML: as quebras precisam sobreviver ao strip_tags ==\n";
$htmlSimples = inbound_html_para_texto('<div>Primeira linha</div><div>Segunda linha</div>');
check(str_contains($htmlSimples, "\n"), "as quebras sumiram: " . str_replace("\n", '\n', $htmlSimples));
check(!str_contains($htmlSimples, 'linhaSegunda'), 'as linhas coladas viram uma so');
check(str_contains(inbound_html_para_texto('a<br>b'), "\n"), '<br> vira quebra');
check(inbound_html_para_texto('<p>Ol&aacute;, tudo bem?</p>') === 'Olá, tudo bem?',
    'entidades HTML sao decodificadas: ' . inbound_html_para_texto('<p>Ol&aacute;, tudo bem?</p>'));
check(!str_contains(inbound_html_para_texto('<div>Sim!</div><blockquote>responda SAIR</blockquote>'), 'SAIR'),
    'o que o cliente marcou como citacao (blockquote) sai fora');

echo "== O CASO QUE NAO PODE DAR ERRADO: resposta HTML citando o nosso rodape ==\n";
// Outlook responde em HTML e monta a citacao com <div>, sem '>' e sem \n.
// Antes da correcao, "optout.php" na URL do NOSSO link casava com /opt.?out/
// e uma pessoa pedindo demonstracao era marcada como "nao contactar".
$respostaOutlook = '<div>Claro, quinta as 10h funciona para mim.</div>'
    . '<div>&nbsp;</div>'
    . '<div>De: Bruna &lt;bruna@mais351monitor.com.br&gt;</div>'
    . '<div>Enviada em: terca-feira, 8 de setembro de 2026 09:12</div>'
    . '<div>Assunto: Horas da equipe na Alfa</div>'
    . '<div>Se nao quiser receber meus e-mails, responda SAIR que removo seu contato.</div>'
    . '<div>Se preferir cancelar em um clique, use este link: '
    . 'https://www.mais351monitor.com.br/crm/optout.php?l=42&amp;c=91&amp;t=abc123</div>';
$cabHtml = "From: Maria <maria@alfa.com.br>\r\nSubject: RE: Horas da equipe na Alfa\r\n"
    . "Content-Type: text/html; charset=UTF-8\r\n";
$c = classificaBruta($cabHtml, $respostaOutlook);
check($c['kind'] === 'humana',
    "resposta HTML citando o nosso proprio link de descadastro virou '{$c['kind']}' em vez de 'humana'");

// Mesmo que o corte de citacao falhe, a URL sozinha nao pode decidir nada.
check(inbound_classificar(['from' => 'x@y.com', 'subject' => 'RE: oi'],
    'Vamos marcar. https://www.mais351monitor.com.br/crm/optout.php?l=1&c=2&t=x')['kind'] === 'humana',
    'a URL de optout no meio do texto NAO e um pedido de descadastro');
check(inbound_classificar(['from' => 'x@y.com', 'subject' => 'RE: oi'],
    'Me tira dessa lista: descadastrar por favor')['kind'] === 'optout',
    'o pedido de verdade continua sendo reconhecido');

echo "== multipart aninhado (resposta com anexo) ==\n";
$aninhado = "--EXTERNO\r\nContent-Type: multipart/alternative; boundary=\"INTERNO\"\r\n\r\n"
    . "--INTERNO\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\nPode ser quinta as 10h.\r\n"
    . "--INTERNO\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"
    . base64_encode('<p>Pode ser quinta as 10h.</p>') . "\r\n--INTERNO--\r\n"
    . "--EXTERNO\r\nContent-Type: application/pdf; name=\"proposta.pdf\"\r\n"
    . "Content-Disposition: attachment; filename=\"proposta.pdf\"\r\n"
    . "Content-Transfer-Encoding: base64\r\n\r\nJVBERi0xLjQK\r\n--EXTERNO--\r\n";
$texto = inbound_texto($aninhado, ['content-type' => 'multipart/mixed; boundary="EXTERNO"']);
check(str_contains($texto, 'Pode ser quinta'), "o texto de dentro do aninhado nao foi achado: " . mb_substr($texto, 0, 80));
check(!str_contains($texto, 'Content-Type'), 'sobrou cabecalho MIME no trecho');
check(!str_contains($texto, 'JVBERi0'), 'o anexo em base64 vazou para o trecho');
check(!str_contains($texto, 'INTERNO'), 'sobrou boundary no trecho');

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
