<?php
/** Kanban do funil: 7 colunas, mover via select no card (sem drag-and-drop). */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    if (($_POST['action'] ?? '') === 'move') {
        try {
            lead_set_status(
                (int) ($_POST['lead_id'] ?? 0),
                (string) ($_POST['status'] ?? ''),
                $_POST['lost_reason'] ?? null,
                (int) $user['id']
            );
            flash_set('ok', 'Lead movido.');
        } catch (InvalidArgumentException $e) {
            flash_set('erro', $e->getMessage());
        }
    }
    redirect('kanban.php');
}

$leads = rows('SELECT id, company, contact_name, status, next_action_at, duplicate_of_lead_id FROM leads ORDER BY updated_at DESC');

// Dias na etapa atual = dias desde a última mudança de status
$lastChange = [];
foreach (rows('SELECT lead_id, MAX(changed_at) AS m FROM lead_status_history GROUP BY lead_id') as $r) {
    $lastChange[(int) $r['lead_id']] = $r['m'];
}

$cols = array_fill_keys(LEAD_STATUSES, []);
foreach ($leads as $l) {
    $cols[$l['status']][] = $l;
}

page_header('Kanban', 'kanban.php', $user);
?>
<div class="page-head">
  <h1 class="page-title">Kanban</h1>
  <a class="btn btn-primary" href="lead.php">+ Novo lead</a>
</div>

<div class="kanban">
  <?php foreach (LEAD_STATUSES as $status): ?>
    <div class="kanban-col">
      <h3><?= esc(STATUS_LABELS[$status]) ?> <span class="n"><?= count($cols[$status]) ?></span></h3>
      <?php foreach ($cols[$status] as $l): ?>
        <?php
          $dias = isset($lastChange[(int) $l['id']])
              ? (int) floor((time() - strtotime($lastChange[(int) $l['id']])) / 86400)
              : null;
          $vencida = $l['next_action_at'] && strtotime($l['next_action_at']) <= time()
              && !in_array($status, ['cliente', 'perdido'], true);
        ?>
        <div class="kanban-card">
          <a class="kc-company" href="lead.php?id=<?= (int) $l['id'] ?>"><?= esc($l['company']) ?></a>
          <span class="kc-meta">
            <?= esc($l['contact_name'] ?: '—') ?>
            <?php if ($dias !== null): ?> · <?= $dias ?>d na etapa<?php endif; ?>
            <?php if ($l['duplicate_of_lead_id']): ?> · <span class="badge badge-dup">dup</span><?php endif; ?>
          </span>
          <?php if ($l['next_action_at']): ?>
            <span class="kc-meta <?= $vencida ? 'overdue' : '' ?>">Próx.: <?= esc(fmt_dt($l['next_action_at'])) ?></span>
          <?php endif; ?>
          <form method="post">
            <?= csrf_field() ?>
            <input type="hidden" name="action" value="move">
            <input type="hidden" name="lead_id" value="<?= (int) $l['id'] ?>">
            <input type="hidden" name="lost_reason" value="">
            <select class="kanban-move" name="status" data-current="<?= esc($status) ?>" aria-label="Mover lead">
              <?php foreach (LEAD_STATUSES as $s): ?>
                <option value="<?= esc($s) ?>" <?= $s === $status ? 'selected' : '' ?>><?= esc(STATUS_LABELS[$s]) ?></option>
              <?php endforeach; ?>
            </select>
          </form>
        </div>
      <?php endforeach; ?>
      <?php if (!$cols[$status]): ?><p class="muted">—</p><?php endif; ?>
    </div>
  <?php endforeach; ?>
</div>
<?php page_footer(); ?>
