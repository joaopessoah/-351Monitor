<?php
/**
 * Quadro de tarefas do time — colunas configuráveis, arrastar e soltar.
 *
 * O card É a tarefa (mesma linha de `tasks` que o dashboard e o detalhe do
 * lead usam), então soltar na coluna de conclusão grava o mesmo done_at do
 * botão ✓. Tarefas geradas pela cadência de e-mail ficam ocultas por padrão.
 *
 * ?r=move é o único endpoint JSON de tela do CRM: recebe form-urlencoded (para
 * o csrf_check() valer sem código novo) e devolve {ok:true} ou {error:...}.
 * Sem JS, cada card tem o select "mover para" que posta como qualquer outro form.
 */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();
$userId = (int) $user['id'];

/** URL do quadro com os filtros escolhidos. */
function board_url(bool $minhas, bool $cadencia, int $card = 0): string
{
    $p = [];
    if ($minhas) {
        $p[] = 'minhas=1';
    }
    if ($cadencia) {
        $p[] = 'cadencia=1';
    }
    if ($card > 0) {
        $p[] = 'card=' . $card;
    }
    // #card leva direto ao painel, que fica depois do quadro inteiro
    return 'board.php' . ($p ? '?' . implode('&', $p) : '') . ($card > 0 ? '#card' : '');
}

/** Responde o endpoint do arrastar e encerra. */
function board_json(int $code, array $body): never
{
    http_response_code($code);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode($body, JSON_UNESCAPED_UNICODE);
    exit;
}

if ($_SERVER['REQUEST_METHOD'] === 'POST' && ($_GET['r'] ?? '') === 'move') {
    security_headers(false);
    csrf_check();
    try {
        $ordem = array_filter(explode(',', (string) ($_POST['ordem'] ?? '')), 'strlen');
        board_move((int) ($_POST['task_id'] ?? 0), (int) ($_POST['column_id'] ?? 0), $ordem);
        board_json(200, ['ok' => true]);
    } catch (InvalidArgumentException $e) {
        board_json(422, ['error' => $e->getMessage()]);
    } catch (Throwable $e) {
        error_log('board move: ' . $e->getMessage());
        board_json(500, ['error' => 'Não deu para mover. Recarregue a página.']);
    }
}

$cardId = isset($_GET['card']) ? (int) $_GET['card'] : 0;
// Os filtros vêm do POST no fallback sem JS; nunca confie na string crua.
$fMinhas = !empty($_GET['minhas']) || !empty($_POST['f_minhas']);
$fCadencia = !empty($_GET['cadencia']) || !empty($_POST['f_cadencia']);

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $action = $_POST['action'] ?? '';
    try {
        if ($action === 'card_add') {
            $due = norm_dtlocal($_POST['due_at'] ?? '');
            if ($due === false || $due === null) {
                throw new InvalidArgumentException('Informe o prazo do card.');
            }
            $resp = norm_int($_POST['assigned_to'] ?? null, 1, 2147483647);
            if ($resp === false) {
                throw new InvalidArgumentException('Responsável inválido.');
            }
            $novo = task_add(null, (string) ($_POST['title'] ?? ''), $due, $resp, $userId);
            flash_set('ok', 'Card criado.');
            redirect(board_url($fMinhas, $fCadencia, $novo));
        } elseif ($action === 'card_update' && $cardId > 0) {
            $due = norm_dtlocal($_POST['due_at'] ?? '');
            // tasks.due_at é NOT NULL: prazo vazio precisa virar mensagem, não 500
            if ($due === false || $due === null) {
                throw new InvalidArgumentException('Informe o prazo do card.');
            }
            $resp = norm_int($_POST['assigned_to'] ?? null, 1, 2147483647);
            if ($resp === false) {
                throw new InvalidArgumentException('Responsável inválido.');
            }
            task_update($cardId, [
                'title'       => (string) ($_POST['title'] ?? ''),
                // === '' e não ?: — "0" é uma descrição válida e falsy em PHP
                'description' => trim((string) ($_POST['description'] ?? '')) === ''
                    ? null : trim((string) ($_POST['description'] ?? '')),
                'due_at'      => $due,
                'assigned_to' => $resp,
            ]);
            flash_set('ok', 'Card salvo.');
            redirect(board_url($fMinhas, $fCadencia, $cardId));
        } elseif ($action === 'card_move' && $cardId > 0) {
            // Fallback sem JS: o select "mover para" do card
            board_move($cardId, (int) ($_POST['column_id'] ?? 0), []);
            flash_set('ok', 'Card movido.');
            redirect(board_url($fMinhas, $fCadencia));
        } elseif ($action === 'card_delete' && $cardId > 0) {
            task_delete($cardId);
            flash_set('ok', 'Card excluído.');
            redirect(board_url($fMinhas, $fCadencia));
        }
    } catch (InvalidArgumentException $e) {
        flash_set('erro', $e->getMessage());
        redirect(board_url($fMinhas, $fCadencia, $cardId));
    } catch (Throwable $e) {
        // Violação de FK, coluna sumida, banco fora: vira flash, nunca tela branca
        error_log('board: ' . $e->getMessage());
        flash_set('erro', 'Não deu para salvar. Se acabou de subir arquivos, rode o migrate.php.');
        redirect(board_url($fMinhas, $fCadencia, $cardId));
    }
    redirect(board_url($fMinhas, $fCadencia));
}

$colunas = board_columns();
$semQuadro = !$colunas;
$cards = $semQuadro ? [] : board_cards(['mine' => $fMinhas ? $userId : 0, 'cadencia' => $fCadencia]);
$usuarios = users_ativos();
$colDone = board_column_done();

$card = null;
if ($cardId > 0) {
    $card = row('SELECT t.*, l.company FROM tasks t LEFT JOIN leads l ON l.id = t.lead_id WHERE t.id = ?', [$cardId]);
    if ($card === null) {
        flash_set('erro', 'Card não encontrado.');
        redirect(board_url($fMinhas, $fCadencia));
    }
}

page_header('Quadro', 'board.php', $user);
?>
<div class="page-head">
  <h1 class="page-title">Quadro</h1>
  <div class="board-filtros">
    <a class="btn btn-ghost btn-sm<?= $fMinhas ? ' is-on' : '' ?>" href="<?= esc(board_url(!$fMinhas, $fCadencia)) ?>">
      <?= $fMinhas ? '✓ ' : '' ?>Só as minhas</a>
    <a class="btn btn-ghost btn-sm<?= $fCadencia ? ' is-on' : '' ?>" href="<?= esc(board_url($fMinhas, !$fCadencia)) ?>">
      <?= $fCadencia ? '✓ ' : '' ?>Mostrar cadência</a>
  </div>
</div>

<?php if ($semQuadro): ?>
  <div class="flash flash-aviso">A migration <strong>010</strong> ainda não foi aplicada — o quadro não existe no banco.
    Rode o <a href="migrate.php">migrate.php</a>.</div>
<?php else: ?>

<?php if ($colDone === null): ?>
  <div class="flash flash-aviso">Nenhuma coluna conclui tarefa. Marque uma em
    <a href="settings.php">Configurações</a> — sem isso o quadro não fecha nada.</div>
<?php endif; ?>

<div class="card board-novo">
  <form method="post" class="task-quick">
    <?= csrf_field() ?>
    <input type="hidden" name="action" value="card_add">
    <?php if ($fMinhas): ?><input type="hidden" name="f_minhas" value="1"><?php endif; ?>
    <?php if ($fCadencia): ?><input type="hidden" name="f_cadencia" value="1"><?php endif; ?>
    <input name="title" type="text" placeholder="Novo card…" maxlength="200" required>
    <input name="due_at" type="datetime-local" value="<?= esc(date('Y-m-d\T09:00', strtotime('+1 day'))) ?>" required>
    <select name="assigned_to" aria-label="Responsável">
      <option value="">Sem responsável</option>
      <?php foreach ($usuarios as $u): ?>
        <option value="<?= (int) $u['id'] ?>"<?= (int) $u['id'] === $userId ? ' selected' : '' ?>><?= esc($u['name']) ?></option>
      <?php endforeach; ?>
    </select>
    <button class="btn btn-primary btn-sm" type="submit">Criar</button>
  </form>
</div>

<div class="kanban board" data-csrf="<?= esc(csrf_token()) ?>">
  <?php foreach ($colunas as $col): ?>
    <?php
      $lista = $cards[(int) $col['id']] ?? [];
      $visiveis = count(array_filter($lista, fn ($t) => empty($t['_oculto'])));
    ?>
    <div class="kanban-col bcol-<?= esc($col['color']) ?>">
      <h3><?= esc($col['name']) ?><span class="n"><?= $visiveis ?></span></h3>
      <div class="board-drop" data-col="<?= (int) $col['id'] ?>">
        <?php foreach ($lista as $t): ?>
          <?php if (!empty($t['_oculto'])): ?>
            <?php /* Escondido pelo filtro, mas presente no DOM: e assim que o
                     arrasto consegue mandar a ordem COMPLETA da coluna e o
                     servidor nao renumera por cima do que o filtro escondeu. */ ?>
            <div class="board-card is-oculto" hidden data-id="<?= (int) $t['id'] ?>"></div>
            <?php continue; ?>
          <?php endif; ?>
          <?php $vencida = $t['done_at'] === null && $t['due_at'] && strtotime($t['due_at']) < strtotime('today'); ?>
          <div class="kanban-card board-card<?= $vencida ? ' is-overdue' : '' ?>"
               draggable="true" data-id="<?= (int) $t['id'] ?>">
            <a class="kc-company" href="<?= esc(board_url($fMinhas, $fCadencia, (int) $t['id'])) ?>"><?= esc($t['title']) ?></a>
            <span class="kc-meta">
              <span class="<?= $vencida ? 'overdue' : '' ?>"><?= esc(fmt_date($t['due_at'])) ?></span>
              <?php if ($t['assignee_name']): ?> · <?= esc($t['assignee_name']) ?><?php endif; ?>
            </span>
            <?php if ($t['lead_id']): ?>
              <a class="kc-lead" href="lead.php?id=<?= (int) $t['lead_id'] ?>"><?= esc($t['company'] ?? ('#' . $t['lead_id'])) ?></a>
            <?php endif; ?>
            <?php if ($t['kind'] === TASK_KIND_CADENCIA): ?><span class="badge badge-cad">cadência</span><?php endif; ?>
            <form method="post" action="board.php?card=<?= (int) $t['id'] ?>">
              <?= csrf_field() ?>
              <input type="hidden" name="action" value="card_move">
              <?php if ($fMinhas): ?><input type="hidden" name="f_minhas" value="1"><?php endif; ?>
              <?php if ($fCadencia): ?><input type="hidden" name="f_cadencia" value="1"><?php endif; ?>
              <select class="auto-submit" name="column_id" aria-label="Mover card">
                <?php foreach ($colunas as $c2): ?>
                  <option value="<?= (int) $c2['id'] ?>"<?= (int) $c2['id'] === (int) $col['id'] ? ' selected' : '' ?>><?= esc($c2['name']) ?></option>
                <?php endforeach; ?>
              </select>
              <?php /* auto-submit depende do crm.js; sem JS o botao e a unica saida */ ?>
              <button class="btn btn-ghost btn-sm board-mover" type="submit">Mover</button>
            </form>
          </div>
        <?php endforeach; ?>
        <?php /* Dentro do .board-drop: coluna vazia precisa continuar sendo alvo
                 de soltar, e o aviso precisa poder reaparecer quando esvaziar. */ ?>
        <p class="muted board-vazia"<?= $visiveis ? ' hidden' : '' ?>>—</p>
      </div>
    </div>
  <?php endforeach; ?>
</div>

<?php if ($card !== null): ?>
  <div class="card board-detalhe" id="card">
    <h2 class="card-title">Card #<?= (int) $card['id'] ?>
      <?php if ($card['done_at']): ?><span class="badge badge-trial">Concluído</span><?php endif; ?>
      <?php if ($card['kind'] === TASK_KIND_CADENCIA): ?><span class="badge badge-cad">cadência</span><?php endif; ?>
    </h2>
    <form method="post" action="<?= esc(board_url($fMinhas, $fCadencia, (int) $card['id'])) ?>" class="form-stack">
      <?= csrf_field() ?>
      <input type="hidden" name="action" value="card_update">
      <div class="form-grid">
        <div class="field field-span">
          <label for="c_title">Título *</label>
          <input id="c_title" name="title" type="text" maxlength="200" required value="<?= esc($card['title']) ?>">
        </div>
        <div class="field">
          <label for="c_due">Prazo</label>
          <input id="c_due" name="due_at" type="datetime-local" required value="<?= esc(dtlocal_value($card['due_at'])) ?>">
        </div>
        <div class="field">
          <label for="c_resp">Responsável</label>
          <select id="c_resp" name="assigned_to">
            <option value="">Sem responsável</option>
            <?php foreach ($usuarios as $u): ?>
              <option value="<?= (int) $u['id'] ?>"<?= (int) $u['id'] === (int) $card['assigned_to'] ? ' selected' : '' ?>><?= esc($u['name']) ?></option>
            <?php endforeach; ?>
          </select>
        </div>
        <div class="field field-span">
          <label for="c_desc">Descrição</label>
          <textarea id="c_desc" name="description" rows="8" placeholder="Contexto, links, o que precisa acontecer…"><?= esc($card['description'] ?? '') ?></textarea>
        </div>
      </div>
      <div class="form-actions">
        <button class="btn btn-primary" type="submit">Salvar card</button>
        <a class="btn btn-ghost" href="<?= esc(board_url($fMinhas, $fCadencia)) ?>">Voltar ao quadro</a>
        <?php if ($card['lead_id']): ?>
          <a class="btn btn-ghost" href="lead.php?id=<?= (int) $card['lead_id'] ?>">Abrir <?= esc($card['company'] ?? 'o lead') ?></a>
        <?php endif; ?>
      </div>
    </form>
    <form method="post" action="<?= esc(board_url($fMinhas, $fCadencia, (int) $card['id'])) ?>" class="board-excluir"
          data-confirm="Excluir este card? Não dá para desfazer.">
      <?= csrf_field() ?>
      <input type="hidden" name="action" value="card_delete">
      <button class="btn btn-danger btn-sm" type="submit">Excluir card</button>
    </form>
  </div>
<?php endif; ?>

<?php endif; ?>
<?php page_footer(); ?>
