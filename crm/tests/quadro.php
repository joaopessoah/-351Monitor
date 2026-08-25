<?php
/**
 * Quadro: regras que dá para testar sem banco.
 *
 * board_move() e as funções de coluna falam com o MySQL, então aqui o foco é
 * o que quebra silenciosamente: a saneada da ordem recebida do navegador, a
 * migration 010 contra o parser do migrate.php, o contrato entre o HTML e o
 * JavaScript que arrasta, e as regressões da revisão de código.
 */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

define('CRM', 1);
date_default_timezone_set('America/Sao_Paulo');
$CRM = dirname(__DIR__);

$falhas = 0;
$total = 0;
function check(bool $cond, string $msg): void
{
    global $falhas, $total;
    $total++;
    if (!$cond) { $falhas++; echo "  FALHOU: $msg\n"; }
}

/**
 * Espelho da saneada de board_move(): só ids da coluna, sem repetir, com o
 * card que chega, e os que o cliente não enviou preservados no fim.
 */
function sanea_ordem(array $recebido, array $atual, int $taskId): array
{
    $validos = $atual;
    $validos[] = $taskId;
    $ordem = array_values(array_unique(array_filter(
        array_map('intval', $recebido),
        fn ($id) => in_array($id, $validos, true)
    )));
    if (!in_array($taskId, $ordem, true)) {
        $ordem[] = $taskId;
    }
    foreach (array_values(array_diff($atual, $ordem)) as $id) {
        $ordem[] = $id;
    }
    return $ordem;
}

echo "== ordem vinda do navegador é saneada ==\n";
$atual = [10, 11, 12];   // o que já está na coluna de destino
check(sanea_ordem(['11', '10', '12'], $atual, 7) === [11, 10, 12, 7],
    'card que chega não foi anexado');
check(sanea_ordem(['11', '999', '10'], $atual, 7) === [11, 10, 7, 12],
    'id estranho não descartado, ou o oculto não foi preservado');
check(sanea_ordem(['11', '11', '10'], $atual, 7) === [11, 10, 7, 12],
    'id repetido não foi colapsado');
check(sanea_ordem([], $atual, 7) === [7, 10, 11, 12], 'ordem vazia perdeu a coluna');
check(sanea_ordem(['lixo', '', 'null'], $atual, 7) === [7, 10, 11, 12], 'lixo textual não filtrado');
check(sanea_ordem(['7', '10'], $atual, 7) === [7, 10, 11, 12], 'card presente foi duplicado');

echo "== o filtro não pode fazer sort_order duplicar ==\n";
// CAD está na coluna mas oculto pelo filtro: o navegador manda só A, C, B.
$naColuna = [1, 2, 3];              // A=1, CAD=2, B=3
$enviado = ['3', '1'];              // usuário reordenou os visíveis: B, A
$final = sanea_ordem($enviado, $naColuna, 3);
check(count($final) === count(array_unique($final)), 'a ordem final tem id repetido');
check(count($final) === 3, 'algum card da coluna sumiu da renumeração: ' . implode(',', $final));
check(in_array(2, $final, true), 'o card oculto (CAD) ficou de fora da renumeração');
echo '  visíveis [3,1] + oculto [2] -> ' . implode(',', $final) . " (sem empate)\n";

echo "== migration 010 sobrevive ao parser do migrate.php ==\n";
function sql_statements(string $sql): array
{
    $sql = preg_replace('/^--.*$/m', '', $sql);
    $parts = preg_split('/;\s*(?:\r\n|\r|\n|$)/', $sql);
    $out = [];
    foreach ($parts as $p) {
        $p = trim($p);
        if ($p !== '') { $out[] = $p; }
    }
    return $out;
}
$sql = file_get_contents($CRM . '/migrations/010_quadro.sql');
$stmts = sql_statements($sql);
check(count($stmts) === 6, 'esperava 6 instruções, veio ' . count($stmts));
foreach ($stmts as $i => $st) {
    check(!str_contains($st, ';'), 'instrução ' . ($i + 1) . " ficou com ';' dentro");
    check(substr_count($st, '(') === substr_count($st, ')'),
        'instrução ' . ($i + 1) . ' com parênteses desbalanceados');
}
check(str_contains($sql, 'ON DELETE SET NULL'), 'apagar coluna deixaria tarefa órfã');
check(substr_count($sql, "('A fazer', 1, 0") === 1, 'seed das colunas mudou de forma');

echo "== contrato entre board.php e crm.js ==\n";
$php = file_get_contents($CRM . '/board.php');
$js = file_get_contents($CRM . '/assets/crm.js');
$css = file_get_contents($CRM . '/assets/crm.css');
$model = file_get_contents($CRM . '/lib/model.php');
$set = file_get_contents($CRM . '/settings.php');

foreach ([['.board', 'class="kanban board"'], ['.board-drop', 'class="board-drop"'],
          ['.board-card', 'board-card']] as [$noJs, $noHtml]) {
    check(str_contains($js, $noJs), "o JS não procura $noJs");
    check(str_contains($php, $noHtml), "o HTML não emite $noHtml");
}
check(str_contains($js, 'dataset.id') && str_contains($php, 'data-id="'), 'data-id desalinhado');
check(str_contains($js, 'dataset.col') && str_contains($php, 'data-col="'), 'data-col desalinhado');
check(str_contains($js, 'dataset.csrf') && str_contains($php, 'data-csrf="'), 'data-csrf desalinhado');
foreach (['task_id', 'column_id', 'ordem'] as $campo) {
    check(str_contains($js, "corpo.set('$campo'"), "o fetch não manda $campo");
    check(str_contains($php, "\$_POST['$campo']"), "o endpoint não lê $campo");
}
check(str_contains($js, 'application/x-www-form-urlencoded'),
    'sem form-urlencoded o $_POST fica vazio e o csrf_check derruba o request');
check(str_contains($php, "(\$_GET['r'] ?? '') === 'move'"), 'a rota ?r=move sumiu');

echo "== degradação sem JavaScript e em tela de toque ==\n";
check(str_contains($php, 'name="action" value="card_move"'), 'não há fallback de mover sem JS');
check(str_contains($php, 'class="btn btn-ghost btn-sm board-mover" type="submit"'),
    'o form de mover não tem botão de submit — sem JS o select não posta nada');
check(str_contains($js, "matchMedia('(pointer: fine)')"),
    'o arrasto não é limitado a ponteiro fino — em celular o card ficaria imóvel');
check(str_contains($js, "board.classList.add('has-dnd')"), 'o JS não marca has-dnd');
check(str_contains($css, '.board.has-dnd .board-card form:focus-within'),
    'o select escondido não reaparece no foco — sem isso não há como mover por teclado');
check(!str_contains($css, '.board.has-dnd .board-card form { display: none; }'),
    'o select ainda some do foco com display:none');

echo "== arrasto cancelado não deixa o card na coluna errada ==\n";
check(str_contains($js, 'soltou = false;'), 'falta a flag de "houve drop"');
check(str_contains($js, 'if (!soltou && origem)'), 'o dragend não desfaz um arrasto cancelado');
check(str_contains($js, 'window.location.reload();'),
    'o rollback não tem saída quando o DOM mudou embaixo dele');
check(str_contains($js, "ids.join(',') === ordemAntes"), 'soltar no mesmo lugar ainda gasta request');
check(str_contains($js, ':not([hidden])'), 'o alvo de soltar não ignora os placeholders ocultos');

echo "== cards ocultos pelo filtro continuam no DOM ==\n";
check(str_contains($php, 'class="board-card is-oculto" hidden'),
    'os cards filtrados não viram placeholder — a ordem enviada ficaria incompleta');
check(str_contains($model, "\$t['_oculto'] = \$oculto;"), 'board_cards não marca os ocultos');
check(str_contains($model, '$faltando = array_values(array_diff($atual, $ordem));')
    && str_contains($model, 'foreach ($faltando as $id)'),
    'board_move não preserva quem não veio na lista');
check(str_contains($php, '$visiveis = count(array_filter('), 'o contador da coluna conta os ocultos');

echo "== coluna vazia continua sendo alvo de soltar ==\n";
$i = strpos($php, 'class="board-drop"');
$fim = strpos($php, 'board-vazia');
check($i !== false && $fim !== false && $fim > $i, 'não achei os dois marcadores');
$entre = substr($php, $i, $fim - $i);
check(substr_count($entre, '</div>') < substr_count($entre, '<div') + 2,
    'o aviso de coluna vazia parece ter ficado fora do .board-drop');
check(str_contains($php, "<p class=\"muted board-vazia\"<?= \$visiveis ? ' hidden' : '' ?>>"),
    'o aviso não nasce escondido quando há cards — não conseguiria reaparecer');

echo "== done_at: as regressões que apagavam conclusão ==\n";
check(str_contains($model, '} elseif ($mudouColuna) {'),
    'board_move ainda reabre a tarefa ao apenas reordenar dentro da coluna');
check(str_contains($model, '$mudouColuna = (int) $t[\'column_id\'] !== $columnId;'),
    'board_move não compara a coluna de origem');
check(str_contains($model, 'WHERE column_id = ? AND done_at IS NOT NULL AND kind <> ?'),
    'trocar a coluna de conclusão ainda varre o CRM inteiro (perda de done_at)');
check(!str_contains($model, "WHERE column_id <> ? AND done_at IS NOT NULL"),
    'o UPDATE destrutivo com column_id <> ? continua no código');
check(str_contains($model, '$antiga = board_column_done();'),
    'não captura a coluna de conclusão anterior antes de trocar');
check(str_contains($model, 'if ($antigaId === $id) {'), 'trocar para a mesma coluna não é no-op');

echo "== o ✓ do dashboard e o quadro não divergem ==\n";
check(str_contains($model, "q('UPDATE tasks SET column_id = ? WHERE id = ?', [(int) \$done['id'], \$id]);"),
    'task_done não move o card para a coluna de conclusão');

echo "== apagar coluna não conclui card por acidente ==\n";
check(str_contains($model, "if ((int) \$c['is_done'] !== 1) {\n            \$destino = \$c;"),
    'sem destino escolhido, a coluna de conclusão ainda pode ser o destino padrão');
check(str_contains($model, 'AND done_at IS NULL AND kind <> ?'),
    'apagar coluna com destino "Feito" ainda conclui tarefa de cadência');
check(str_contains($model, '$fim = (int) scalar(\'SELECT COALESCE(MAX(sort_order), 0) FROM tasks WHERE column_id = ?\''),
    'fundir colunas não renumera: os cards chegam empatando sort_order');
check(str_contains($set, "(conclui os cards!)"), 'o destino que conclui não avisa no select');

echo "== concorrência ==\n";
check(str_contains($model, 'ORDER BY sort_order, id FOR UPDATE'),
    'a leitura da coluna está fora do lock — dois arrastos simultâneos empatam');
check(str_contains($model, "q('UPDATE tasks SET sort_order = sort_order + 1 WHERE column_id = ?', [\$colId]);"),
    'task_add não abre espaço no topo em transação');

echo "== sem a migration 010 o CRM não pode quebrar inteiro ==\n";
check(str_contains($model, 'if ($col === null) {'), 'task_add não tem caminho sem quadro');
check(str_contains($model, "q('INSERT INTO tasks (lead_id, title, kind, due_at, assigned_to, created_by) VALUES (?,?,?,?,?,?)'"),
    'falta o INSERT antigo para quando as colunas do quadro não existem');
check(str_contains($php, 'catch (Throwable $e)') && str_contains($php, "error_log('board: '"),
    'falha de banco no quadro ainda vira tela branca');

echo "== detalhes ==\n";
check(str_contains($php, "(\$card > 0 ? '#card' : '')"), 'a URL do card não leva a âncora #card');
check(str_contains($php, "=== ''\n                    ? null"), 'descrição "0" ainda seria apagada pelo ?:');
check(str_contains($php, 'required value="<?= esc(dtlocal_value($card[\'due_at\'])) ?>"'),
    'o campo de prazo do detalhe não é obrigatório');
check(str_contains($css, '.board .kanban-col { border-top:'),
    'a faixa colorida não está escopada e vaza para o kanban.php');
check(str_contains($js, 'setDragImage'), 'arrastar pelo título usaria a imagem da âncora');

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
