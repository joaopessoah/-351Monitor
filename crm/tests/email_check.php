<?php
/** Pre-validacao de e-mail: as camadas puras, com o DNS injetado. */

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
require $CRM . '/lib/render.php';

$falhas = 0;
$total = 0;
function check(bool $cond, string $msg): void
{
    global $falhas, $total;
    $total++;
    if (!$cond) { $falhas++; echo "  FALHOU: $msg\n"; }
}

$consultados = [];
$mxSim = function (string $d) use (&$consultados): bool { $consultados[] = $d; return true; };
$mxNao = function (string $d) use (&$consultados): bool { $consultados[] = $d; return false; };

echo "== camadas ==\n";
$casos = [
    ['maria@alfa.com.br',              'valido',      'dominio proprio com MX'],
    ['maria.souza@alfa.com.br',        'valido',      'nome com ponto continua sendo pessoa'],
    ['MARIA@ALFA.COM.BR',              'valido',      'maiuscula normaliza'],
    ['joao@gmail.com',                 'valido',      'gratuito e valido (o score do leadgen que penaliza)'],
    ['contato@alfa.com.br',            'generico',    'caixa de papel'],
    ['financeiro.sp@alfa.com.br',      'generico',    'prefixo antes do ponto manda'],
    ['contato+cadencia@alfa.com.br',   'generico',    'prefixo antes do + manda'],
    ['rh@alfa.com.br',                 'generico',    'rh e caixa de papel'],
    ['nao-responda@alfa.com.br',       'generico',    'no-reply e caixa de papel'],
    ['a@mailinator.com',               'descartavel', 'dominio descartavel'],
    ['lixo sem arroba',                'invalido',    'sintaxe'],
    ['',                               'invalido',    'vazio'],
    ['maria@',                         'invalido',    'sem dominio'],
];
foreach ($casos as [$email, $esperado, $porque]) {
    $r = email_check_calc($email, $mxSim);
    check($r['status'] === $esperado, "'$email' ($porque): esperado $esperado, veio {$r['status']}");
    echo "  " . str_pad($email === '' ? "''" : $email, 32) . ' -> ' . str_pad($r['status'], 12)
        . ($r['detail'] !== '' ? '(' . $r['detail'] . ')' : '') . "\n";
}

echo "== dominio sem MX nem A ==\n";
$r = email_check_calc('maria@dominioquenaoexiste.tld', $mxNao);
check($r['status'] === 'invalido', "sem MX vira invalido, veio {$r['status']}");
check($r['mx_ok'] === false, 'mx_ok fica falso');
check(str_contains($r['detail'], 'MX'), "o motivo diz o que houve: {$r['detail']}");

echo "== ordem das camadas ==\n";
$consultados = [];
email_check_calc('a@mailinator.com', $mxNao);
check($consultados === [], 'descartavel e barrado ANTES de gastar uma consulta de DNS');
$consultados = [];
email_check_calc('lixo', $mxNao);
check($consultados === [], 'sintaxe invalida nem chega no DNS');
$consultados = [];
email_check_calc('contato@alfa.com.br', $mxSim);
check($consultados === ['alfa.com.br'], 'generico so e decidido depois de confirmar o dominio');

echo "== cache: quando revalidar ==\n";
check(!email_check_fresco(null), 'sem linha no cache, revalida');
check(email_check_fresco(['status' => 'valido', 'checked_at' => date('Y-m-d H:i:s')]), 'checado hoje esta fresco');
check(!email_check_fresco(['status' => 'valido', 'checked_at' => date('Y-m-d H:i:s', strtotime('-40 days'))]),
    'depois de 30 dias revalida');
check(email_check_fresco(['status' => 'bounce', 'checked_at' => date('Y-m-d H:i:s', strtotime('-400 days'))]),
    'bounce NUNCA expira — o servidor ja disse que o endereco nao existe');

echo "== selos da tela ==\n";
foreach (['valido', 'generico', 'invalido', 'descartavel', 'bounce', 'nao_verificado'] as $s) {
    $html = email_status_badge($s);
    check(str_contains($html, 'badge'), "selo de $s renderiza");
    check(str_contains($html, 'title="'), "selo de $s explica o que significa");
}
check(str_contains(email_status_badge(null), 'nao verificado'), 'status nulo vira "nao verificado"');

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
