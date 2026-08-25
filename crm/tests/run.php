<?php
/**
 * Roda todos os testes das funções puras do CRM. Sem banco, sem servidor.
 *
 *   php crm/tests/run.php
 *
 * Cada arquivo é um processo próprio porque todos declaram os mesmos stubs
 * de banco (rows/q/scalar/row) e se atropelariam no mesmo processo.
 * Sai com código 1 se qualquer suíte falhar — serve para CI.
 */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("Só na linha de comando.\n");
}

$suites = ['cadencia.php', 'mailto_crlf.php', 'migrations.php', 'correcoes.php', 'quadro.php'];
$php = PHP_BINARY;
$falhou = [];

foreach ($suites as $s) {
    $arquivo = __DIR__ . DIRECTORY_SEPARATOR . $s;
    echo str_repeat('─', 60), "\n", $s, "\n", str_repeat('─', 60), "\n";
    $saida = [];
    $code = 0;
    exec(escapeshellarg($php) . ' ' . escapeshellarg($arquivo) . ' 2>&1', $saida, $code);
    echo implode("\n", $saida), "\n\n";
    if ($code !== 0) {
        $falhou[] = $s;
    }
}

echo str_repeat('═', 60), "\n";
if ($falhou) {
    echo 'FALHOU: ' . implode(', ', $falhou) . "\n";
    exit(1);
}
echo count($suites) . " suítes, todas passaram.\n";
exit(0);
