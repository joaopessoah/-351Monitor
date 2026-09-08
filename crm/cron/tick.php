<?php
/**
 * Batida do motor da cadencia. Roda pelo cron do hPanel a cada 10 minutos:
 *
 *   /usr/bin/php /home/uXXXX/domains/mais351monitor.com.br/public_html/crm/cron/tick.php
 *
 * Faz, nesta ordem: envia o que venceu (com as guardas), le as caixas e age
 * nos retornos, manda o resumo do dia no horario e pinga o dead-man switch.
 *
 * So CLI. Tres camadas de guarda, porque um tick disparado pela web por
 * qualquer um seria um envio de e-mail sob demanda: .htaccess desta pasta,
 * a checagem de PHP_SAPI aqui e o lock no banco.
 */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

require dirname(__DIR__) . '/lib/bootstrap.php';

$inicio = microtime(true);
$verboso = in_array('-v', $argv ?? [], true) || in_array('--verbose', $argv ?? [], true);
$soLer  = in_array('--so-ler', $argv ?? [], true);

function tick_log(string $linha): void
{
    global $verboso;
    if ($verboso) {
        echo date('H:i:s') . '  ' . $linha . "\n";
    }
}

/**
 * Lock no proprio MariaDB: dois ticks simultaneos (cron atrasado + execucao
 * manual) nunca disputam a mesma linha do outbox. GET_LOCK e por conexao, e a
 * conexao morre no fim do script — nao ha lock orfao.
 */
try {
    $lock = (int) scalar("SELECT GET_LOCK('m351_cadencia_tick', 0)");
} catch (Throwable $e) {
    fwrite(STDERR, 'tick: banco indisponivel — ' . $e->getMessage() . "\n");
    exit(1);
}
if ($lock !== 1) {
    tick_log('outro tick esta rodando; saindo.');
    exit(0);
}

$saida = 0;
$resumoEnvio = ['enviados' => 0, 'adiados' => 0, 'parados' => 0, 'falhas' => 0, 'detalhes' => []];
$resumoInbox = ['lidas' => 0, 'humanas' => 0, 'bounces' => 0, 'optouts' => 0, 'autos' => 0, 'erros' => []];

try {
    if (!$soLer) {
        $resumoEnvio = cadencia_auto_tick();
        foreach ($resumoEnvio['detalhes'] as $d) {
            tick_log('envio  ' . $d);
        }
        tick_log(sprintf('envio  %d enviados, %d adiados, %d parados, %d falhas',
            $resumoEnvio['enviados'], $resumoEnvio['adiados'], $resumoEnvio['parados'], $resumoEnvio['falhas']));
    }
} catch (Throwable $e) {
    $saida = 1;
    error_log('tick envio: ' . $e->getMessage());
    fwrite(STDERR, 'tick envio: ' . $e->getMessage() . "\n");
}

try {
    $resumoInbox = inbound_processar();
    tick_log(sprintf('caixa  %d lidas (%d respostas, %d devolucoes, %d saidas, %d automaticas)',
        $resumoInbox['lidas'], $resumoInbox['humanas'], $resumoInbox['bounces'],
        $resumoInbox['optouts'], $resumoInbox['autos']));
    foreach ($resumoInbox['erros'] as $erro) {
        $saida = 1;
        tick_log('caixa  ERRO ' . $erro);
        error_log('tick caixa: ' . $erro);
    }
} catch (Throwable $e) {
    $saida = 1;
    error_log('tick caixa: ' . $e->getMessage());
    fwrite(STDERR, 'tick caixa: ' . $e->getMessage() . "\n");
}

try {
    if (notificar_resumo_diario()) {
        tick_log('resumo do dia enviado');
    }
} catch (Throwable $e) {
    error_log('tick resumo: ' . $e->getMessage());
}

$ms = (int) round((microtime(true) - $inicio) * 1000);
try {
    state_set('cron_ultimo', date('Y-m-d H:i:s'));
    state_set('cron_ultimo_resumo', sprintf('%d enviados, %d retornos, %d ms',
        $resumoEnvio['enviados'], $resumoInbox['lidas'], $ms));
} catch (Throwable $e) {
}

// So pinga o sucesso quando nada explodiu: o healthchecks e o que avisa que o
// motor parou, e um ping incondicional esconderia exatamente isso.
healthchecks_ping($saida === 0 ? '' : 'fail');

scalar("SELECT RELEASE_LOCK('m351_cadencia_tick')");
tick_log('fim em ' . $ms . ' ms');
exit($saida);
