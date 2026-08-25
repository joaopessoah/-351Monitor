<?php
/** Funções puras do analytics do site: hash do visitante, user-agent, ref_code. */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

define('CRM', 1);

// Config de mentira: o sal é trocável para exercitar também o caminho derivado.
$GLOBALS['CFG_TESTE'] = [
    'analytics_salt' => 'sal-de-teste-com-tamanho-suficiente',
    'db_pass'        => 'senha-fake',
    'db_name'        => 'banco_fake',
    'migrate_key'    => 'chave-fake',
];
function cfg(string $key)
{
    return $GLOBALS['CFG_TESTE'][$key] ?? null;
}

// Stubs de banco que estouram: nenhum teste daqui pode tocar no MySQL.
function row(string $sql, array $p = []): ?array { throw new RuntimeException('tocou no banco'); }
function rows(string $sql, array $p = []): array { throw new RuntimeException('tocou no banco'); }
function q(string $sql, array $p = []) { throw new RuntimeException('tocou no banco'); }
function scalar(string $sql, array $p = []) { throw new RuntimeException('tocou no banco'); }
function last_id(): int { throw new RuntimeException('tocou no banco'); }

require dirname(__DIR__) . '/lib/analytics.php';

$falhas = 0;
$total = 0;
function check(bool $cond, string $msg): void
{
    global $falhas, $total;
    $total++;
    if (!$cond) {
        $falhas++;
        echo "  FALHOU: $msg\n";
    }
}

// ---------------------------------------------------------------- is_bot
echo "== is_bot: navegador de gente não pode ser barrado ==\n";
$navegadores = [
    'Chrome Windows'  => 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36',
    'Safari iPhone'   => 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1',
    'Firefox Linux'   => 'Mozilla/5.0 (X11; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0',
    'Edge Windows'    => 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0',
    'Chrome Android'  => 'Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36',
    'Safari iPad'     => 'Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/604.1',
    'Samsung Android' => 'Mozilla/5.0 (Linux; Android 13; SAMSUNG SM-A536B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/23.0 Chrome/115.0.0.0 Mobile Safari/537.36',
    'Safari macOS'    => 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15',
];
foreach ($navegadores as $nome => $ua) {
    check(!is_bot($ua), "$nome foi barrado como bot — visita real perdida");
}

echo "== is_bot: robô não pode virar visita ==\n";
$bots = [
    'Googlebot'   => 'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
    'Bingbot'     => 'Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)',
    'WhatsApp'    => 'WhatsApp/2.23.20.0 A',
    'curl'        => 'curl/8.4.0',
    'UptimeRobot' => 'Mozilla/5.0+(compatible; UptimeRobot/2.0; http://www.uptimerobot.com/)',
    'Headless'    => 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/126.0.0.0 Safari/537.36',
    'LinkedInBot' => 'LinkedInBot/1.0 (compatible; Mozilla/5.0; Jakarta Commons-HttpClient/3.1)',
    'vazio'       => '',
];
foreach ($bots as $nome => $ua) {
    check(is_bot($ua), "$nome passou como visita real");
}

// -------------------------------------------------------------- ua_parse
echo "== ua_parse ==\n";
$esperado = [
    'Chrome Windows'  => ['desktop', 'Chrome', 'Windows'],
    'Safari iPhone'   => ['mobile', 'Safari', 'iOS'],
    'Chrome Android'  => ['mobile', 'Chrome', 'Android'],
    'Safari iPad'     => ['tablet', 'Safari', 'iOS'],
    'Edge Windows'    => ['desktop', 'Edge', 'Windows'],
    'Samsung Android' => ['mobile', 'Samsung', 'Android'],
    'Firefox Linux'   => ['desktop', 'Firefox', 'Linux'],
    'Safari macOS'    => ['desktop', 'Safari', 'macOS'],
];
foreach ($esperado as $nome => $esp) {
    $obtido = ua_parse($navegadores[$nome]);
    check($obtido === $esp, "$nome: esperava " . implode('/', $esp) . ', veio ' . implode('/', array_map(fn ($v) => $v ?? 'null', $obtido)));
    // device tem que caber no ENUM da coluna, senão o INSERT quebra
    check(in_array($obtido[0], ['desktop', 'mobile', 'tablet'], true), "$nome: device fora do ENUM");
    check($obtido[1] === null || strlen($obtido[1]) <= 24, "$nome: browser não cabe em VARCHAR(24)");
    check($obtido[2] === null || strlen($obtido[2]) <= 24, "$nome: os não cabe em VARCHAR(24)");
}

// -------------------------------------------------------------- ref_code
echo "== ref_code ==\n";
check(!preg_match('/[01OIL]/', REF_ALPHABET), 'o alfabeto tem caractere confundível (0/1/O/I/L)');
$vistos = [];
$forma = true;
for ($i = 0; $i < 2000; $i++) {
    $c = ref_code_new();
    if (!preg_match('/^[' . REF_ALPHABET . ']{6}$/', $c)) {
        $forma = false;
    }
    $vistos[$c] = true;
}
check($forma, 'ref_code_new gerou código fora do alfabeto ou fora de 6 chars');
check(count($vistos) === 2000, 'houve colisão em 2000 sorteios: ' . count($vistos) . ' distintos');

check(ref_code_norm('k7m2q9') === 'K7M2Q9', 'ref_code_norm não subiu a caixa');
check(ref_code_norm('  #K7M2 Q9 ') === 'K7M2Q9', 'ref_code_norm não limpou # e espaços');
check(ref_code_norm('K7M2QO') === null, 'ref_code_norm aceitou letra fora do alfabeto');
check(ref_code_norm('K7M2Q') === null, 'ref_code_norm aceitou código curto');
check(ref_code_norm('K7M2Q99') === null, 'ref_code_norm aceitou código longo');
check(ref_code_norm('') === null, 'ref_code_norm aceitou vazio');
check(ref_code_norm(null) === null, 'ref_code_norm quebrou com null');

// --------------------------------------------------------- visitor_hash
echo "== visitor_hash (é ele que substitui o cookie) ==\n";
$chrome = $navegadores['Chrome Windows'];
$h1 = visitor_hash('187.1.2.3', $chrome);
$h2 = visitor_hash('187.1.2.3', $chrome);
check($h1 === $h2, 'o mesmo visitante gerou hashes diferentes no mesmo dia');
check($h1 !== visitor_hash('187.1.2.4', $chrome), 'IPs diferentes deram o mesmo hash');
check($h1 !== visitor_hash('187.1.2.3', $navegadores['Firefox Linux']), 'navegadores diferentes deram o mesmo hash');
check(strlen($h1) === 32, 'o hash não cabe exatamente no CHAR(32): ' . strlen($h1));
check(preg_match('/^[0-9a-f]{32}$/', $h1) === 1, 'o hash não é hexadecimal');
check(!str_contains($h1, '187.1.2.3'), 'o IP vazou dentro do hash');

echo "== o sal muda o hash (e sem sal configurado ainda existe um) ==\n";
$GLOBALS['CFG_TESTE']['analytics_salt'] = 'outro-sal-completamente-diferente';
check($h1 !== visitor_hash('187.1.2.3', $chrome), 'trocar o sal não mudou o hash — o sal não está sendo usado');
$GLOBALS['CFG_TESTE']['analytics_salt'] = null;
check(strlen(analytics_salt()) >= 32, 'sem analytics_salt o fallback derivado ficou curto demais');
check(analytics_salt() !== 'senha-fake', 'o fallback expôs a senha do banco em vez de derivar dela');
$GLOBALS['CFG_TESTE']['analytics_salt'] = 'sal-de-teste-com-tamanho-suficiente';

// --------------------------------------------------- contrato com o schema
echo "== nomes de evento cabem na coluna e passam pelo collect.php ==\n";
foreach (array_keys(EVENT_LABELS) as $nome) {
    check(strlen($nome) <= 48, "evento '$nome' não cabe em VARCHAR(48)");
    check(preg_match('/^[a-z0-9_]{2,48}$/', $nome) === 1, "evento '$nome' seria recusado pelo collect.php");
}

echo "== o track.js só emite eventos que o painel sabe rotular ==\n";
$js = file_get_contents(dirname(__DIR__, 2) . '/site/assets/js/track.js');
preg_match_all("/evento\('([a-z_]+)'/", $js, $m);
foreach (array_unique($m[1]) as $nome) {
    check(isset(EVENT_LABELS[$nome]), "track.js emite '$nome', que não tem rótulo em EVENT_LABELS");
}

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
