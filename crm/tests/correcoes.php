<?php
/** Regressão dos achados corrigidos após a revisão adversarial. */

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

echo "== A) mailto: injeção de bcc pelo endereço ==\n";
// Endereço que o FILTER_VALIDATE_EMAIL aceita e que carrega parâmetros de mailto.
$veneno = 'x?to=vendas%40cliente.com.br&bcc=espiao%40evil.com&z=@cliente.com.br';
check(norm_email($veneno) !== false, 'pré-condição: o validador do PHP aceita mesmo esse endereço');
$link = mailto_link($veneno, ['assunto' => 'oi', 'corpo' => 'texto']);
// Tudo depois de "mailto:" e antes do primeiro "?" é o endereço; não pode conter & nem ?
preg_match('/href="mailto:([^"]*)"/', $link, $m);
$href = html_entity_decode($m[1] ?? '', ENT_QUOTES, 'UTF-8');
$partes = explode('?', $href, 2);
// o regex ja captura depois de "mailto:"
$endereco = $partes[0];
$query = $partes[1] ?? '';
check(!str_contains($endereco, '&'), "endereço ainda carrega '&': $endereco");
check(!str_contains($endereco, '?'), "endereço ainda carrega '?': $endereco");
check(!str_contains($query, 'bcc='), 'bcc vazou para a query do mailto');
check(str_starts_with($query, 'subject='), "query não começa em subject=: $query");
echo "  endereço codificado: $endereco\n";
echo "  query: " . substr($query, 0, 60) . "…\n";

echo "== A2) endereço normal continua legível para o Outlook ==\n";
$link = mailto_link('maria.souza@alfa.com.br', ['assunto' => 'oi', 'corpo' => 'texto']);
check(str_contains($link, 'mailto:maria.souza@alfa.com.br?subject='),
    'endereço comum foi mangled: ' . substr($link, 0, 90));
$link2 = mailto_link('maria.souza@alfa.com.br');
check($link2 === '<a href="mailto:maria.souza@alfa.com.br">maria.souza@alfa.com.br</a>',
    'mailto sem modelo saiu errado: ' . $link2);

echo "== E) cadência conta da data do e-mail, com piso em hoje ==\n";
$hoje = date('Y-m-d');
// e-mail registrado hoje: 4 dias úteis a partir de hoje
$due = cadencia_due_at(4, null);
check(substr($due, 0, 10) === business_days_add($hoje, 4), "sem data base: $due");
// e-mail registrado hoje mas ENVIADO há 10 dias: o prazo já passou -> piso em hoje
$antigo = date('Y-m-d H:i:s', strtotime('-10 days'));
$due = cadencia_due_at(3, $antigo);
check(substr($due, 0, 10) >= $hoje, "data antiga gerou tarefa no passado: $due");
check((int) date('N', strtotime($due)) <= 5, "piso caiu em fim de semana: $due");
// e-mail enviado ontem: conta de ontem, ainda no futuro
$ontem = date('Y-m-d H:i:s', strtotime('-1 day'));
$due = cadencia_due_at(4, $ontem);
check(substr($due, 0, 10) === business_days_add(date('Y-m-d', strtotime('-1 day')), 4),
    "não contou a partir de ontem: $due");
check(str_ends_with($due, ' 09:00:00'), "hora configurada não aplicada: $due");
echo "  hoje+4du = " . substr(cadencia_due_at(4, null), 0, 10)
   . " | enviado há 10d = " . substr(cadencia_due_at(3, $antigo), 0, 10)
   . " | enviado ontem+4du = " . substr(cadencia_due_at(4, $ontem), 0, 10) . "\n";

echo "== M) settings.php: nenhum required escondido em <details> fechado ==\n";
/**
 * Lê um fonte normalizando CRLF para LF. As asserções abaixo casam trechos de
 * código com quebra de linha embutida; num checkout Windows (.gitattributes dá
 * \r\n no working copy) elas falhariam sem nada ter quebrado de verdade.
 */
function fonte(string $caminho): string
{
    return str_replace("\r\n", "\n", (string) file_get_contents($caminho));
}

$s = fonte($CRM . '/settings.php');
check(!str_contains($s, "'open' : ''"), 'ainda há <details> que nasce fechado');
check(substr_count($s, '<details class="tpl" open>') === 1, 'o <details> não está fixo em open');

echo "== N) seletor de <code> não vaza para outros cards ==\n";
$css = fonte($CRM . '/assets/crm.css');
check(!str_contains($css, '.card code'), 'o seletor .card code ainda existe');
check(str_contains($css, '.cad-chaves code'), 'o seletor escopado não foi criado');
check(str_contains($s, 'class="muted cad-chaves"'), 'o parágrafo das chaves não recebeu a classe');

echo "== G/H) contact_* recusam contato de outro lead ==\n";
$src = fonte($CRM . '/lib/model.php');
foreach (['contact_delete', 'contact_set_principal', 'contact_toggle_decisor'] as $fn) {
    check((bool) preg_match('/function ' . $fn . '\(int \$id, \?int \$expectLeadId = null\)/', $src),
        "$fn não recebe expectLeadId");
}
check(substr_count($src, 'contact_assert_lead($id, $expectLeadId);') === 3,
    'contact_assert_lead não é chamado nas três funções');
$leadSrc = fonte($CRM . '/lead.php');
foreach (['contact_delete', 'contact_set_principal', 'contact_toggle_decisor'] as $fn) {
    check(str_contains($leadSrc, $fn . "((int) (\$_POST['contact_id'] ?? 0), \$id)"),
        "lead.php não passa o lead esperado para $fn");
}

echo "== B) contact_update não apaga notes quando a chave não vem ==\n";
check(str_contains($src, "if (array_key_exists('notes', \$d)) {"),
    'contact_update ainda escreve notes incondicionalmente');
check(!str_contains($src, 'phone = ?, linkedin = ?, notes = ?'), 'o SET fixo com notes continua lá');

echo "== D) opt-out limpa a próxima ação ==\n";
check(str_contains($src, 'next_action_at = NULL, next_action_note = NULL WHERE id = ?'),
    'lead_set_no_contact não zera next_action_at');

echo "== F) cadência encerrada tem função e é usada ==\n";
check(str_contains($src, 'function cadencia_email_encerrada(int $leadId): bool'), 'falta cadencia_email_encerrada');
check(str_contains($leadSrc, '$cadenciaFim = cadencia_email_encerrada($id);'), 'lead.php não usa a flag');
check(str_contains($leadSrc, 'mailto_link($c[\'email\'], null,'), 'não há mailto sem modelo após o 5º');

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
