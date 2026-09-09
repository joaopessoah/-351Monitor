<?php
/**
 * Usuários e remetentes.
 *
 * O bug que originou este arquivo: um endereço adicionado à chave `mail` do
 * crm_config.php não aparecia como remetente em leads.php. Não era defeito —
 * mail_remetentes() percorre a tabela `users`, então caixa sem usuário ativo
 * nunca é olhada. Os testes fixam esse contrato e a saída de
 * mail_caixas_sem_usuario(), que é o que a tela usa para explicar o sumiço.
 */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

define('CRM', 1);
date_default_timezone_set('America/Sao_Paulo');
$CRM = dirname(__DIR__);

/** Tabela users de mentira: as duas únicas consultas que o código faz aqui. */
$USERS = [];
$CONFIG = [];

function rows(string $sql, array $p = []): array
{
    global $USERS;
    if (str_contains($sql, 'FROM users')) {
        $out = array_values(array_filter($USERS,
            fn ($u) => !str_contains($sql, 'is_active = 1') || (int) $u['is_active'] === 1));
        usort($out, fn ($a, $b) => strcmp((string) $a['name'], (string) $b['name']));
        return $out;
    }
    throw new RuntimeException('consulta inesperada: ' . $sql);
}

function row(string $sql, array $p = [])
{
    global $USERS;
    if (str_contains($sql, 'FROM users') && str_contains($sql, 'WHERE id = ?')) {
        foreach ($USERS as $u) {
            if ((int) $u['id'] === (int) $p[0]) {
                return $u;
            }
        }
        return null;
    }
    if (str_contains($sql, 'FROM users') && str_contains($sql, 'WHERE email = ?')) {
        foreach ($USERS as $u) {
            if ($u['email'] === $p[0]) {
                return $u;
            }
        }
        return null;
    }
    throw new RuntimeException('consulta inesperada: ' . $sql);
}

function q(string $sql, array $p = []) { throw new RuntimeException('sem banco'); }
function scalar(string $sql, array $p = []) { throw new RuntimeException('sem banco'); }
function last_id(): int { return 99; }
function db() { throw new RuntimeException('sem banco'); }
function esc(?string $s): string { return htmlspecialchars((string) $s, ENT_QUOTES, 'UTF-8'); }
function cfg(string $k) { global $CONFIG; return $CONFIG[$k] ?? null; }

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

/** mail_contas() tem cache static; cada cenário roda num processo já limpo é caro,
 *  então os cenários que mudam a config vêm depois dos que a compartilham. */
function cenario(array $users, array $mail): void
{
    global $USERS, $CONFIG;
    $USERS = $users;
    $CONFIG = ['mail' => $mail];
}

echo "== so aparece como remetente quem esta nos DOIS lados ==\n";
cenario(
    [
        ['id' => 1, 'name' => 'Bruna', 'email' => 'bruna@mais351monitor.com.br', 'is_active' => 1],
        ['id' => 2, 'name' => 'Joao',  'email' => 'joao@mais351monitor.com.br',  'is_active' => 1],
        ['id' => 3, 'name' => 'Ex',    'email' => 'ex@mais351monitor.com.br',    'is_active' => 0],
    ],
    [
        'bruna@mais351monitor.com.br' => ['nome' => 'Bruna | +351', 'senha' => 'x'],
        // joao@ NAO esta configurado: usuario ativo sem caixa nao assina
        'comercial@mais351monitor.com.br' => ['nome' => 'Comercial', 'senha' => 'x'],
        'ex@mais351monitor.com.br' => ['nome' => 'Ex', 'senha' => 'x'],
    ]
);

$remetentes = array_column(mail_remetentes(), 'email');
check($remetentes === ['bruna@mais351monitor.com.br'],
    'remetentes deveria ser so a bruna@, veio: ' . implode(', ', $remetentes));
check(!in_array('comercial@mais351monitor.com.br', $remetentes, true),
    'caixa configurada SEM usuario virou remetente — o bug de origem voltou');
check(!in_array('ex@mais351monitor.com.br', $remetentes, true),
    'usuario desativado nao pode assinar e-mail');
check(!in_array('joao@mais351monitor.com.br', $remetentes, true),
    'usuario ativo sem caixa no crm_config.php nao tem por onde enviar');

echo "== mail_caixas_sem_usuario aponta exatamente o que sumiu ==\n";
$orfas = array_keys(mail_caixas_sem_usuario());
sort($orfas);
check($orfas === ['comercial@mais351monitor.com.br', 'ex@mais351monitor.com.br'],
    'orfas erradas: ' . implode(', ', $orfas));
check(!in_array('bruna@mais351monitor.com.br', $orfas, true),
    'caixa que JA assina nao pode aparecer como orfa');

echo "== o endereco e comparado em minusculas dos dois lados ==\n";
// mail_contas() tem cache static: um processo so consegue exercitar uma config.
$saida = [];
$code = 0;
exec(escapeshellarg(PHP_BINARY) . ' ' . escapeshellarg(__DIR__ . '/usuarios_caixa.php') . ' 2>&1', $saida, $code);
echo '  ' . implode("\n  ", $saida) . "\n";
check($code === 0, 'sub-suite de caixa alta/baixa falhou');

echo "== validacao do usuario novo ==\n";
$v = user_valida_novo('  Bruna  ', '  BRUNA@Mais351Monitor.com.BR ');
check($v['nome'] === 'Bruna', 'nome nao foi aparado');
check($v['email'] === 'bruna@mais351monitor.com.br', 'e-mail nao foi normalizado para minusculas');

$erro = null;
try { user_valida_novo('B', 'a@b.com'); } catch (InvalidArgumentException $e) { $erro = $e->getMessage(); }
check($erro !== null, 'nome de 1 caractere passou');

$erro = null;
try { user_valida_novo('Fulano', 'nao-e-email'); } catch (InvalidArgumentException $e) { $erro = $e->getMessage(); }
check($erro !== null, 'e-mail invalido passou');
check($erro !== null && str_contains($erro, 'nao-e-email'),
    'a mensagem tem que mostrar o que foi digitado, senao nao da para achar o erro de digitacao');

$erro = null;
try { user_valida_novo('Fulano', '   '); } catch (InvalidArgumentException $e) { $erro = $e->getMessage(); }
check($erro !== null, 'e-mail vazio passou — norm_email devolve null, nao false');

echo "== senha temporaria ==\n";
$vistas = [];
for ($i = 0; $i < 200; $i++) {
    $s = senha_temporaria();
    check(strlen($s) === 16, 'senha temporaria com tamanho errado: ' . strlen($s));
    check(preg_match('#[+/]#', $s) === 0, 'senha temporaria com + ou / atrapalha o copiar e colar: ' . $s);
    $vistas[$s] = true;
}
check(count($vistas) === 200, 'senha temporaria repetiu em 200 sorteios');

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
