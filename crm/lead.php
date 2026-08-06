<?php
/**
 * Lead: criação (sem id) e detalhe (com id) — dados, status com motivo de perda,
 * próxima ação, interações, tarefas, timeline e exclusão (LGPD).
 */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();
$userId = (int) $user['id'];

/** Valida o form de dados do lead. @return array{0: array, 1: array} [dados, erros] */
function lead_form_validate(array $in): array
{
    $errors = [];
    $d = [];
    $d['company'] = norm_text($in['company'] ?? '', 160);
    if (mb_strlen($d['company']) < 2) {
        $errors['company'] = 'Informe a empresa (mínimo 2 caracteres).';
    }
    $d['contact_name'] = norm_text($in['contact_name'] ?? '', 120);

    $email = norm_email($in['email'] ?? '');
    if ($email === false) {
        $errors['email'] = 'E-mail inválido.';
    } else {
        $d['email'] = $email;
    }
    $fone = norm_whatsapp($in['whatsapp'] ?? '');
    if ($fone === false) {
        $errors['whatsapp'] = 'WhatsApp inválido — use DDD + número.';
    } else {
        $d['whatsapp'] = $fone;
    }
    $est = norm_int($in['estimated_devices'] ?? null, 1, 10000);
    if ($est === false) {
        $errors['estimated_devices'] = 'Nº de estações inválido (1 a 10000).';
    } else {
        $d['estimated_devices'] = $est;
    }
    $d['source'] = in_enum($in['source'] ?? null, LEAD_SOURCES, 'outro');
    $d['plan_interest'] = in_enum($in['plan_interest'] ?? null, LEAD_PLANS, 'indefinido');

    $na = norm_dtlocal($in['next_action_at'] ?? '');
    if ($na === false) {
        $errors['next_action_at'] = 'Data/hora da próxima ação inválida.';
    } else {
        $d['next_action_at'] = $na;
    }
    $d['next_action_note'] = norm_text($in['next_action_note'] ?? '', 255) ?: null;
    $d['notes'] = trim((string) ($in['notes'] ?? '')) ?: null;
    return [$d, $errors];
}

$id = isset($_GET['id']) ? (int) $_GET['id'] : 0;
$errors = [];
$old = [];

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $action = $_POST['action'] ?? '';
    try {
        if ($action === 'create') {
            [$d, $errors] = lead_form_validate($_POST);
            if (!$errors) {
                $res = lead_create($d, $userId, 'ui');
                if ($res['duplicate_of_lead_id']) {
                    flash_set('aviso', 'Lead criado, mas parece duplicado do lead #' . $res['duplicate_of_lead_id'] . '.');
                } else {
                    flash_set('ok', 'Lead criado.');
                }
                redirect('lead.php?id=' . $res['id']);
            }
            $old = $_POST;
        } elseif ($action === 'update' && $id > 0) {
            [$d, $errors] = lead_form_validate($_POST);
            if (!$errors) {
                lead_update($id, $d);
                flash_set('ok', 'Dados salvos.');
                redirect('lead.php?id=' . $id);
            }
            $old = $_POST;
        } elseif ($action === 'status' && $id > 0) {
            lead_set_status($id, (string) ($_POST['status'] ?? ''), $_POST['lost_reason'] ?? null, $userId);
            flash_set('ok', 'Status atualizado.');
            redirect('lead.php?id=' . $id);
        } elseif ($action === 'next_action' && $id > 0) {
            $na = norm_dtlocal($_POST['next_action_at'] ?? '');
            if ($na === false) {
                throw new InvalidArgumentException('Data/hora inválida.');
            }
            lead_update($id, [
                'next_action_at'   => $na,
                'next_action_note' => norm_text($_POST['next_action_note'] ?? '', 255) ?: null,
            ]);
            flash_set('ok', $na === null ? 'Próxima ação removida.' : 'Próxima ação agendada.');
            redirect('lead.php?id=' . $id);
        } elseif ($action === 'interaction' && $id > 0) {
            $oc = norm_dtlocal($_POST['occurred_at'] ?? '');
            if ($oc === false) {
                throw new InvalidArgumentException('Data/hora da interação inválida.');
            }
            interaction_add($id, (string) ($_POST['type'] ?? ''), (string) ($_POST['summary'] ?? ''), $oc, $userId);
            flash_set('ok', 'Interação registrada.');
            redirect('lead.php?id=' . $id);
        } elseif ($action === 'task' && $id > 0) {
            $due = norm_dtlocal($_POST['due_at'] ?? '');
            if ($due === null || $due === false) {
                throw new InvalidArgumentException('Informe o vencimento da tarefa.');
            }
            task_add($id, (string) ($_POST['title'] ?? ''), $due, $userId, $userId);
            flash_set('ok', 'Tarefa criada.');
            redirect('lead.php?id=' . $id);
        } elseif ($action === 'task_done' && $id > 0) {
            task_done((int) ($_POST['task_id'] ?? 0));
            flash_set('ok', 'Tarefa concluída.');
            redirect('lead.php?id=' . $id);
        } elseif ($action === 'delete' && $id > 0) {
            lead_delete($id);
            flash_set('ok', 'Lead excluído definitivamente.');
            redirect('leads.php');
        }
    } catch (InvalidArgumentException $e) {
        flash_set('erro', $e->getMessage());
        redirect('lead.php' . ($id > 0 ? '?id=' . $id : ''));
    }
}

$lead = null;
if ($id > 0) {
    $lead = row('SELECT * FROM leads WHERE id = ?', [$id]);
    if ($lead === null) {
        flash_set('erro', 'Lead não encontrado.');
        redirect('leads.php');
    }
}

$isNew = $lead === null;
/** Valor exibido no form: POST com erro > banco > vazio. */
$v = function (string $key, $default = '') use ($old, $lead) {
    if (array_key_exists($key, $old)) {
        return (string) $old[$key];
    }
    if ($lead !== null && array_key_exists($key, $lead)) {
        return (string) ($lead[$key] ?? '');
    }
    return (string) $default;
};
/** DATETIME do banco → value de input datetime-local. */
function dtlocal_value(?string $dt): string
{
    return $dt ? date('Y-m-d\TH:i', strtotime($dt)) : '';
}

page_header($isNew ? 'Novo lead' : $lead['company'], 'leads.php', $user);

if ($errors) {
    echo '<div class="flash flash-erro">Corrija os campos destacados: ' . esc(implode(' ', $errors)) . '</div>';
}
?>

<?php if ($isNew): ?>
  <h1 class="page-title">Novo lead</h1>
<?php else: ?>
  <div class="lead-head">
    <h1><?= esc($lead['company']) ?></h1>
    <?= status_badge($lead['status']) ?>
    <?php if ($lead['duplicate_of_lead_id']): ?><span class="badge badge-dup">Duplicado</span><?php endif; ?>
  </div>
  <p class="muted">Criado em <?= esc(fmt_dt($lead['created_at'])) ?> via <?= esc($lead['created_via']) ?>
    · origem <?= esc(SOURCE_LABELS[$lead['source']] ?? $lead['source']) ?>
    <?php if ($lead['utm_source']): ?> · UTM: <?= esc($lead['utm_source']) ?>/<?= esc($lead['utm_medium'] ?? '-') ?>/<?= esc($lead['utm_campaign'] ?? '-') ?><?php endif; ?>
    <?php if ($lead['status'] === 'perdido' && $lead['lost_reason']): ?> · <strong>Motivo da perda:</strong> <?= esc($lead['lost_reason']) ?><?php endif; ?>
  </p>
  <?php if ($lead['duplicate_of_lead_id']): ?>
    <div class="dup-banner">Possível duplicado do lead
      <a href="lead.php?id=<?= (int) $lead['duplicate_of_lead_id'] ?>">#<?= (int) $lead['duplicate_of_lead_id'] ?></a>.
      Compare e mantenha um só (excluindo o outro).</div>
  <?php endif; ?>
<?php endif; ?>

<div class="grid-2">
  <div class="card">
    <h2 class="card-title">Dados</h2>
    <form method="post" class="form-stack">
      <?= csrf_field() ?>
      <input type="hidden" name="action" value="<?= $isNew ? 'create' : 'update' ?>">
      <div class="form-grid">
        <div class="field">
          <label for="company">Empresa *</label>
          <input id="company" name="company" type="text" maxlength="160" required value="<?= esc($v('company')) ?>">
        </div>
        <div class="field">
          <label for="contact_name">Contato</label>
          <input id="contact_name" name="contact_name" type="text" maxlength="120" value="<?= esc($v('contact_name')) ?>">
        </div>
        <div class="field">
          <label for="email">E-mail</label>
          <input id="email" name="email" type="email" maxlength="190" value="<?= esc($v('email')) ?>">
        </div>
        <div class="field">
          <label for="whatsapp">WhatsApp (DDD + número)</label>
          <input id="whatsapp" name="whatsapp" type="tel" maxlength="20" value="<?= esc($v('whatsapp')) ?>" placeholder="(11) 99999-9999">
        </div>
        <div class="field">
          <label for="source">Origem</label>
          <select id="source" name="source">
            <?php foreach (LEAD_SOURCES as $s): ?>
              <option value="<?= esc($s) ?>" <?= $v('source', 'outro') === $s ? 'selected' : '' ?>><?= esc(SOURCE_LABELS[$s]) ?></option>
            <?php endforeach; ?>
          </select>
        </div>
        <div class="field">
          <label for="estimated_devices">Nº de estações (estimado)</label>
          <input id="estimated_devices" name="estimated_devices" type="number" min="1" max="10000" value="<?= esc($v('estimated_devices')) ?>">
        </div>
        <div class="field">
          <label for="plan_interest">Plano de interesse</label>
          <select id="plan_interest" name="plan_interest">
            <?php foreach (LEAD_PLANS as $p): ?>
              <option value="<?= esc($p) ?>" <?= $v('plan_interest', 'indefinido') === $p ? 'selected' : '' ?>><?= esc(PLAN_LABELS[$p]) ?></option>
            <?php endforeach; ?>
          </select>
        </div>
        <?php if ($isNew): ?>
          <div class="field">
            <label for="next_action_at">Próxima ação (opcional)</label>
            <input id="next_action_at" name="next_action_at" type="datetime-local" value="<?= esc($v('next_action_at')) ?>">
          </div>
          <div class="field field-span">
            <label for="next_action_note">Nota da próxima ação</label>
            <input id="next_action_note" name="next_action_note" type="text" maxlength="255" value="<?= esc($v('next_action_note')) ?>">
          </div>
        <?php endif; ?>
        <div class="field field-span">
          <label for="notes">Observações</label>
          <textarea id="notes" name="notes" maxlength="10000"><?= esc($v('notes')) ?></textarea>
        </div>
      </div>
      <div class="form-actions">
        <button class="btn btn-primary" type="submit"><?= $isNew ? 'Criar lead' : 'Salvar dados' ?></button>
        <?php if ($isNew): ?><a class="btn btn-ghost" href="leads.php">Cancelar</a><?php endif; ?>
      </div>
    </form>
  </div>

  <?php if (!$isNew): ?>
  <div>
    <div class="card">
      <h2 class="card-title">Status</h2>
      <form method="post" class="form-stack">
        <?= csrf_field() ?>
        <input type="hidden" name="action" value="status">
        <div class="field">
          <label for="status-select">Etapa do funil</label>
          <select id="status-select" name="status">
            <?php foreach (LEAD_STATUSES as $s): ?>
              <option value="<?= esc($s) ?>" <?= $lead['status'] === $s ? 'selected' : '' ?>><?= esc(STATUS_LABELS[$s]) ?></option>
            <?php endforeach; ?>
          </select>
        </div>
        <div class="field" id="lost-reason-wrap" hidden>
          <label for="lost_reason">Motivo da perda *</label>
          <input id="lost_reason" name="lost_reason" type="text" maxlength="255" value="<?= esc($lead['lost_reason'] ?? '') ?>">
        </div>
        <button class="btn btn-ghost" type="submit">Salvar status</button>
      </form>
    </div>

    <div class="card">
      <h2 class="card-title">Próxima ação</h2>
      <form method="post" class="form-stack">
        <?= csrf_field() ?>
        <input type="hidden" name="action" value="next_action">
        <div class="field">
          <label for="na_at">Quando</label>
          <input id="na_at" name="next_action_at" type="datetime-local" value="<?= esc(dtlocal_value($lead['next_action_at'])) ?>">
        </div>
        <div class="field">
          <label for="na_note">O que fazer</label>
          <input id="na_note" name="next_action_note" type="text" maxlength="255" value="<?= esc($lead['next_action_note'] ?? '') ?>" placeholder="Ex.: cobrar retorno no WhatsApp">
        </div>
        <button class="btn btn-ghost" type="submit">Salvar próxima ação</button>
      </form>
    </div>

    <div class="card">
      <h2 class="card-title">Tarefas</h2>
      <?php $leadTasks = rows('SELECT * FROM tasks WHERE lead_id = ? AND done_at IS NULL ORDER BY due_at', [$id]); ?>
      <?php if ($leadTasks): ?>
        <ul class="task-list">
          <?php foreach ($leadTasks as $t): ?>
            <li>
              <form method="post" class="inline-form"><?= csrf_field() ?>
                <input type="hidden" name="action" value="task_done">
                <input type="hidden" name="task_id" value="<?= (int) $t['id'] ?>">
                <button class="task-check" type="submit" title="Concluir">✓</button>
              </form>
              <span class="task-due <?= strtotime($t['due_at']) < time() ? 'overdue' : '' ?>"><?= esc(fmt_dt($t['due_at'])) ?></span>
              <span class="task-title"><?= esc($t['title']) ?></span>
            </li>
          <?php endforeach; ?>
        </ul>
      <?php else: ?>
        <p class="muted">Sem tarefas abertas.</p>
      <?php endif; ?>
      <form method="post" class="task-quick">
        <?= csrf_field() ?>
        <input type="hidden" name="action" value="task">
        <input name="title" type="text" placeholder="Nova tarefa…" maxlength="200" required>
        <input name="due_at" type="datetime-local" required>
        <button class="btn btn-ghost btn-sm" type="submit">Criar</button>
      </form>
    </div>

    <div class="card">
      <h2 class="card-title">Zona de risco</h2>
      <form method="post" data-confirm="Excluir DEFINITIVAMENTE este lead e todo o histórico? Não dá para desfazer.">
        <?= csrf_field() ?>
        <input type="hidden" name="action" value="delete">
        <button class="btn btn-danger" type="submit">Excluir lead (LGPD)</button>
      </form>
    </div>
  </div>
  <?php endif; ?>
</div>

<?php if (!$isNew): ?>
  <div class="grid-2">
    <div class="card">
      <h2 class="card-title">Registrar interação</h2>
      <form method="post" class="form-stack">
        <?= csrf_field() ?>
        <input type="hidden" name="action" value="interaction">
        <div class="form-grid">
          <div class="field">
            <label for="int-type">Tipo</label>
            <select id="int-type" name="type">
              <?php foreach (INTERACTION_TYPES as $t): ?>
                <option value="<?= esc($t) ?>"><?= esc(INTERACTION_LABELS[$t]) ?></option>
              <?php endforeach; ?>
            </select>
          </div>
          <div class="field">
            <label for="int-at">Ocorrida em</label>
            <input id="int-at" name="occurred_at" type="datetime-local" value="<?= esc(date('Y-m-d\TH:i')) ?>">
          </div>
          <div class="field field-span">
            <label for="int-summary">Resumo *</label>
            <textarea id="int-summary" name="summary" required placeholder="O que foi conversado, próximos passos…"></textarea>
          </div>
        </div>
        <button class="btn btn-primary" type="submit">Registrar</button>
      </form>
    </div>

    <div class="card">
      <h2 class="card-title">Timeline</h2>
      <?php $timeline = lead_timeline($id); ?>
      <?php if (!$timeline): ?>
        <p class="muted">Sem eventos ainda.</p>
      <?php else: ?>
        <ul class="timeline">
          <?php foreach ($timeline as $ev): ?>
            <?php if ($ev['kind'] === 'interacao'): $d = $ev['data']; ?>
              <li class="tl-interacao">
                <div class="tl-head"><strong><?= esc(INTERACTION_LABELS[$d['type']] ?? $d['type']) ?></strong>
                  · <?= esc(fmt_dt($d['occurred_at'])) ?><?= $d['user_name'] ? ' · ' . esc($d['user_name']) : '' ?></div>
                <div class="tl-body"><?= esc($d['summary']) ?></div>
              </li>
            <?php else: $d = $ev['data']; ?>
              <li class="tl-status">
                <div class="tl-head">
                  <?php if ($d['from_status'] === null): ?>
                    Lead criado como <strong><?= esc(STATUS_LABELS[$d['to_status']] ?? $d['to_status']) ?></strong>
                  <?php else: ?>
                    Status: <?= esc(STATUS_LABELS[$d['from_status']] ?? $d['from_status']) ?> →
                    <strong><?= esc(STATUS_LABELS[$d['to_status']] ?? $d['to_status']) ?></strong>
                  <?php endif; ?>
                  · <?= esc(fmt_dt($d['changed_at'])) ?><?= $d['user_name'] ? ' · ' . esc($d['user_name']) : ' · site/API' ?>
                </div>
              </li>
            <?php endif; ?>
          <?php endforeach; ?>
        </ul>
      <?php endif; ?>
    </div>
  </div>
<?php endif; ?>
<?php page_footer(); ?>
