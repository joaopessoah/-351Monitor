<?php
/** Regressão: o corpo do mailto nunca pode ter \r duplicado. */

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

require $CRM . '/lib/validate.php';
require $CRM . '/lib/settings.php';
require $CRM . '/lib/model.php';
require $CRM . '/lib/render.php';

$falhas = 0;
$total = 0;
function check(bool $cond, string $msg): void
{
    global $falhas, $total;
    $total++;
    if (!$cond) { $falhas++; echo "  FALHOU: $msg\n"; }
}

/** Extrai e decodifica o body= do href gerado. */
function corpo_do_link(string $html): string
{
    preg_match('/&amp;body=([^"]*)/', $html, $m);
    return rawurldecode($m[1] ?? '');
}

echo "== mailto: quebras de linha ==\n";
$entradas = [
    'LF'        => "linha1\nlinha2\n\nlinha4",
    'CRLF'      => "linha1\r\nlinha2\r\n\r\nlinha4",   // o que o <textarea> envia
    'CR'        => "linha1\rlinha2\r\rlinha4",          // mac clássico
    'misturado' => "linha1\r\nlinha2\nlinha3\rlinha4",
];
foreach ($entradas as $nome => $corpo) {
    $link = mailto_link('a@b.com', ['assunto' => 'x', 'corpo' => $corpo]);
    $saida = corpo_do_link($link);
    check(!str_contains($saida, "\r\r"), "$nome: gerou \\r duplicado");
    // toda quebra tem que ser exatamente CRLF
    $semCrlf = str_replace("\r\n", '', $saida);
    check(!str_contains($semCrlf, "\r"), "$nome: sobrou \\r solto fora de CRLF");
    check(!str_contains($semCrlf, "\n"), "$nome: sobrou \\n solto fora de CRLF");
    check(substr_count($saida, "\r\n") === 3, "$nome: esperava 3 quebras, veio " . substr_count($saida, "\r\n"));
    echo "  $nome -> " . substr_count($saida, "\r\n") . " CRLF, "
        . substr_count($saida, "\r\r") . " CR duplicado\n";
}

echo "== idempotência: aplicar duas vezes dá o mesmo ==\n";
$corpo = "a\r\nb\nc";
$um = corpo_do_link(mailto_link('a@b.com', ['assunto' => 'x', 'corpo' => $corpo]));
$dois = corpo_do_link(mailto_link('a@b.com', ['assunto' => 'x', 'corpo' => $um]));
check($um === $dois, 'não é idempotente: ' . bin2hex($um) . ' vs ' . bin2hex($dois));

echo "== corte em 1500 chars não estoura a URL ==\n";
$gigante = str_repeat("linha de texto bem comprida para inflar o corpo\n", 200);
$link = mailto_link('a@b.com', ['assunto' => 'x', 'corpo' => $gigante]);
check(mb_strlen(corpo_do_link($link)) <= 1500, 'corte de 1500 não foi aplicado');
check(strlen($link) < 6000, 'href ficou grande demais: ' . strlen($link));
echo "  href com corpo gigante: " . strlen($link) . " chars\n";

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
