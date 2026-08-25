<?php
/** Testes das funções puras da cadência, sem banco. */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

define('CRM', 1);
date_default_timezone_set('America/Sao_Paulo');

$CRM = dirname(__DIR__);

// Stubs: settings_all() cai nos defaults quando rows() explode (migration ausente).
function rows(string $sql, array $p = []): array { throw new RuntimeException('sem banco no teste'); }
function q(string $sql, array $p = []) { throw new RuntimeException('sem banco no teste'); }
function scalar(string $sql, array $p = []) { throw new RuntimeException('sem banco no teste'); }
function row(string $sql, array $p = []) { throw new RuntimeException('sem banco no teste'); }
function last_id(): int { return 1; }
// copia literal do esc() em lib/bootstrap.php
function esc(?string $s): string { return htmlspecialchars((string) $s, ENT_QUOTES, 'UTF-8'); }
function db() { throw new RuntimeException('sem banco no teste'); }

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

echo "== business_days_add: propriedades ==\n";
// Varre 90 datas de partida x 0..35 dias e confere invariantes.
$inicio = new DateTimeImmutable('2026-08-01');
for ($i = 0; $i < 90; $i++) {
    $de = $inicio->modify("+$i day");
    foreach ([0, 1, 2, 3, 4, 5, 10, 30, 35] as $dias) {
        $r = business_days_add($de->format('Y-m-d'), $dias);
        $rd = new DateTimeImmutable($r);
        check((int) $rd->format('N') <= 5, "resultado caiu em fim de semana: $r (de {$de->format('Y-m-d')} +$dias)");
        check($rd >= $de, "resultado anterior a partida: $r < {$de->format('Y-m-d')}");
        if ($dias > 0) {
            // conta dias úteis de (de, r]
            $n = 0;
            $c = $de;
            while ($c < $rd) {
                $c = $c->modify('+1 day');
                if ((int) $c->format('N') <= 5) { $n++; }
            }
            check($n === $dias, "contagem errada: de {$de->format('Y-m-d')} +$dias deu $r ($n dias úteis)");
        }
    }
}

echo "== business_days_add: casos nomeados ==\n";
$casos = [
    ['2026-08-24', 4, '2026-08-28'], // segunda + 4 = sexta
    ['2026-08-24', 3, '2026-08-27'], // segunda + 3 = quinta
    ['2026-08-28', 4, '2026-09-03'], // sexta + 4 = quinta seguinte
    ['2026-08-28', 1, '2026-08-31'], // sexta + 1 = segunda
    ['2026-08-29', 0, '2026-08-31'], // sábado + 0 = segunda
    ['2026-08-30', 1, '2026-08-31'], // domingo + 1 = segunda
    ['2026-08-24', 30, '2026-10-05'], // 30 dias úteis = 6 semanas
];
foreach ($casos as [$de, $d, $esperado]) {
    $r = business_days_add($de, $d);
    check($r === $esperado, "$de +$d: esperado $esperado, veio $r");
    echo "  $de +{$d}du -> $r (" . ['','seg','ter','qua','qui','sex','sab','dom'][(int) date('N', strtotime($r))] . ")\n";
}

echo "== fmt_fone ==\n";
$fones = [
    ['5511999998888', '(11) 99999-8888'],
    ['551133334444',  '(11) 3333-4444'],
    ['11999998888',   '(11) 99999-8888'],
    ['1133334444',    '(11) 3333-4444'],
    ['',              ''],
];
foreach ($fones as [$in, $esperado]) {
    $r = fmt_fone($in);
    check($r === $esperado, "fmt_fone('$in'): esperado '$esperado', veio '$r'");
}

echo "== norm_phone ==\n";
check(norm_phone('(11) 3333-4444') === '551133334444', 'norm_phone fixo com máscara');
check(norm_phone('') === null, 'norm_phone vazio -> null');
check(norm_phone('123') === false, 'norm_phone curto -> false');

echo "== cadencia_email_modelo (defaults, sem banco) ==\n";
$lead = ['company' => 'Contabilidade Alfa', 'contact_name' => 'Maria Souza', 'estimated_devices' => 42];
$contato = ['name' => 'Maria Souza', 'cargo' => 'Sócia'];
for ($n = 1; $n <= CADENCIA_EMAIL_PASSOS; $n++) {
    $m = cadencia_email_modelo($n, $lead, $contato, 'Bruna');
    check($m['assunto'] !== '', "modelo $n sem assunto");
    check($m['corpo'] !== '', "modelo $n sem corpo");
    check(!str_contains($m['assunto'] . $m['corpo'], '{'), "modelo $n deixou chave sem substituir: "
        . substr(strstr($m['assunto'] . $m['corpo'], '{'), 0, 30));
    check(str_contains($m['corpo'], 'SAIR'), "modelo $n perdeu o rodapé de opt-out");
    check(str_contains($m['corpo'], 'Bruna'), "modelo $n não assinou com o usuário");
    $link = mailto_link('maria@alfa.com.br', $m);
    check(str_starts_with($link, '<a href="mailto:maria@alfa.com.br?subject='), "mailto $n malformado");
    check(!str_contains($link, '"><'), "mailto $n com aspas soltas");
    $len = strlen($link);
    check($len < 4000, "mailto $n gigante ($len chars)");
    echo "  $n: " . mb_substr($m['assunto'], 0, 55) . " | href " . $len . " chars\n";
}

echo "== mailto: tentativa de injeção via modelo ==\n";
$mal = ['assunto' => 'x" onmouseover="alert(1)', 'corpo' => "linha1\r\nBcc: vitima@x.com\"><script>alert(1)</script>"];
$link = mailto_link('a@b.com', $mal);
check(!str_contains($link, '<script>'), 'mailto deixou passar <script>');
check(!str_contains($link, 'onmouseover='), 'mailto deixou passar atributo injetado');
check(substr_count($link, '"') === 2, 'mailto com número errado de aspas: ' . substr_count($link, '"'));
echo "  href: " . substr($link, 0, 110) . "...\n";

echo "== fallback quando o contato não tem nome ==\n";
$m = cadencia_email_modelo(1, ['company' => 'Beta Ltda', 'contact_name' => '', 'estimated_devices' => null], null, 'Bruna');
check(!str_contains($m['corpo'], 'Oi, ,'), 'saudação ficou com nome vazio');
check(str_contains($m['corpo'], 'Beta Ltda'), 'fallback não usou a empresa');

echo "\n";
echo $falhas === 0
    ? "TODOS OS $total TESTES PASSARAM\n"
    : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
