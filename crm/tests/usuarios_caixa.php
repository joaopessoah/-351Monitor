<?php
/**
 * Sub-suíte de tests/usuarios.php: mail_contas() guarda o resultado num static,
 * então um processo só consegue exercitar UMA configuração. Aqui o cenário é o
 * endereço escrito em caixa alta no crm_config.php e no cadastro do usuário —
 * copiar e colar do hPanel traz maiúscula com frequência, e um casamento
 * sensível a caixa faria o remetente sumir de novo, agora sem explicação.
 */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

define('CRM', 1);
date_default_timezone_set('America/Sao_Paulo');
$CRM = dirname(__DIR__);

function rows(string $sql, array $p = []): array
{
    return str_contains($sql, 'FROM users')
        ? [['id' => 1, 'name' => 'Bruna', 'email' => 'Bruna@Mais351Monitor.com.BR', 'is_active' => 1]]
        : throw new RuntimeException('consulta inesperada: ' . $sql);
}

function row(string $sql, array $p = [])
{
    return rows($sql, $p)[0] ?? null;
}

function q(string $sql, array $p = []) { throw new RuntimeException('sem banco'); }
function scalar(string $sql, array $p = []) { throw new RuntimeException('sem banco'); }
function last_id(): int { return 1; }
function db() { throw new RuntimeException('sem banco'); }
function esc(?string $s): string { return htmlspecialchars((string) $s, ENT_QUOTES, 'UTF-8'); }
function cfg(string $k)
{
    return ['mail' => ['  BRUNA@mais351monitor.COM.br  ' => ['nome' => 'Bruna', 'senha' => 'x']]][$k] ?? null;
}

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
    if (!$cond) { $falhas++; echo "FALHOU: $msg\n"; }
}

check(array_keys(mail_contas()) === ['bruna@mais351monitor.com.br'],
    'a chave do crm_config.php nao foi normalizada (espaco/maiuscula)');
check(count(mail_remetentes()) === 1, 'e-mail em caixa alta no cadastro nao casou com a caixa configurada');
check(mail_caixas_sem_usuario() === [], 'caixa que casa por normalizacao foi acusada de orfa');

echo $falhas === 0 ? "os $total testes de caixa alta/baixa passaram\n" : "$falhas FALHAS de $total\n";
exit($falhas === 0 ? 0 : 1);
