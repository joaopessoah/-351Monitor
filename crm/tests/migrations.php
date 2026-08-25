<?php
/** As migrations 007/008/009 sobrevivem ao splitter do migrate.php? */

// Cópia LITERAL de sql_statements() em crm/migrate.php (linhas 14-26).
if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

$CRM = dirname(__DIR__);

function sql_statements(string $sql): array
{
    $sql = preg_replace('/^--.*$/m', '', $sql);
    $parts = preg_split('/;\s*(?:\r\n|\r|\n|$)/', $sql);
    $stmts = [];
    foreach ($parts as $p) {
        $p = trim($p);
        if ($p !== '') {
            $stmts[] = $p;
        }
    }
    return $stmts;
}

$falhas = 0;
$total = 0;
function check(bool $cond, string $msg): void
{
    global $falhas, $total;
    $total++;
    if (!$cond) { $falhas++; echo "  FALHOU: $msg\n"; }
}

$esperado = [
    '007_cadencia_email.sql'   => 3,  // ALTER interactions, ALTER tasks, CREATE app_settings
    '008_contato_telefone.sql' => 1,  // ALTER lead_contacts
    '009_lead_no_contact.sql'  => 1,  // ALTER leads
];

foreach ($esperado as $arq => $n) {
    $sql = file_get_contents($CRM . '/migrations/' . $arq);
    check($sql !== false, "não li $arq");
    $stmts = sql_statements($sql);
    echo "== $arq: " . count($stmts) . " instrução(ões) ==\n";
    foreach ($stmts as $i => $st) {
        $resumo = preg_replace('/\s+/', ' ', mb_substr($st, 0, 78));
        echo '  [' . ($i + 1) . "] {$resumo}…\n";
        // nenhuma instrução pode ter sobrado com ';' no meio nem comentário órfão
        check(!str_contains($st, ';'), "$arq: instrução " . ($i + 1) . " ficou com ';' dentro");
        check(!str_starts_with(ltrim($st), '--'), "$arq: instrução " . ($i + 1) . ' virou comentário');
        check(preg_match('/^(ALTER|CREATE|INSERT|UPDATE|DROP)\s/i', $st) === 1,
            "$arq: instrução " . ($i + 1) . ' não começa com verbo DDL/DML');
        // parênteses balanceados
        check(substr_count($st, '(') === substr_count($st, ')'),
            "$arq: instrução " . ($i + 1) . ' com parênteses desbalanceados');
    }
    check(count($stmts) === $n, "$arq: esperava $n instrução(ões), veio " . count($stmts));
}

echo "== colunas prometidas pelas migrations ==\n";
$todoSql = '';
foreach (glob($CRM . '/migrations/*.sql') as $f) {
    $todoSql .= file_get_contents($f) . "\n";
}
$colunas = [
    'interactions.email_seq'    => 'email_seq',
    'tasks.kind'                => 'kind',
    'lead_contacts.phone'       => 'phone',
    'leads.no_contact'          => 'no_contact',
    'leads.no_contact_at'       => 'no_contact_at',
    'leads.no_contact_reason'   => 'no_contact_reason',
    'app_settings'              => 'app_settings',
];
foreach ($colunas as $nome => $token) {
    check(str_contains($todoSql, $token), "coluna/tabela $nome não existe em nenhuma migration");
}

echo "== o PHP não referencia coluna inexistente ==\n";
$php = '';
foreach (['lib/model.php', 'lib/settings.php', 'lead.php', 'leads.php', 'settings.php', 'api/index.php'] as $f) {
    $php .= file_get_contents($CRM . '/' . $f) . "\n";
}
// colunas novas usadas no PHP têm que aparecer nas migrations
preg_match_all('/\b(email_seq|no_contact_at|no_contact_reason|no_contact|phone|kind|app_settings)\b/', $php, $m);
$usadas = array_unique($m[1]);
sort($usadas);
foreach ($usadas as $u) {
    check(str_contains($todoSql, $u), "PHP usa '$u' mas nenhuma migration cria");
}
echo '  usadas no PHP: ' . implode(', ', $usadas) . "\n";

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
