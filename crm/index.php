<?php
/**
 * Dashboard: funil por status, demos do mês vs meta, tarefas de hoje/atrasadas,
 * follow-ups vencidos e leads novos parados há 48h+.
 */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $action = $_POST['action'] ?? '';
    try {
        if ($action === 'task_done') {
            task_done((int) ($_POST['id'] ?? 0));
            flash_set('ok', 'Tarefa concluída.');
        } elseif ($action === 'task_add') {
            $due = norm_dtlocal($_POST['due_at'] ?? '');
            if ($due === null || $due === false) {
                throw new InvalidArgumentException('Informe o vencimento da tarefa.');
            }
            task_add(null, (string) ($_POST['title'] ?? ''), $due, (int) $user['id'], (int) $user['id']);
            flash_set('ok', 'Tarefa criada.');
        }
    } catch (InvalidArgumentException $e) {
        flash_set('erro', $e->getMessage());
    }
    redirect('index.php');
}

$counts = metrics_status_counts();
$demos = metrics_demos_mes();
$tarefasHoje = tasks_lista('hoje');
$tarefasAtrasadas = tasks_lista('atrasadas');
$followups = leads_followups_vencidos();
$parados = leads_novos_parados();
$pct = min(100, (int) round($demos['realizadas'] / DEMO_META_MES * 100));

page_header('Dashboard', 'index.php', $user);
?>
<div class="page-head">
  <h1 class="page-title">Dashboard</h1>
  <a class="btn btn-primary" href="lead.php">+ Novo lead</a>
</div>

<div class="stats">
  <?php foreach (LEAD_STATUSES as $s): ?>
    <a class="stat stat-<?= esc($s) ?>" href="leads.php?status=<?= esc($s) ?>">
      <span class="stat-n"><?= (int) $counts[$s] ?></span>
      <span class="stat-label"><?= esc(STATUS_LABELS[$s]) ?></span>
    </a>
  <?php endforeach; ?>
</div>

<div class="card">
  <div class="demo-meta">
    <h2 class="card-title">Demos realizadas em <?= esc(date('m/Y')) ?></h2>
    <p class="demo-count"><strong><?= (int) $demos['realizadas'] ?></strong> / <?= DEMO_META_MES ?>
      <span class="muted">· <?= (int) $demos['agendadas'] ?> agendada(s) no mês</span></p>
  </div>
  <div class="progress"><div class="progress-bar" style="width: <?= $pct ?>%"></div></div>
  <?php
    try {
        $filaDisp = (int) scalar('SELECT COUNT(*) FROM prospect_pool WHERE promoted_at IS NULL');
        echo '<p class="muted" style="margin: 12px 0 0">Fila de prospecção: <strong>'
            . number_format($filaDisp, 0, ',', '.')
            . '</strong> empresa(s) disponível(is) — <a href="fila.php">puxar leads</a>.</p>';
    } catch (Throwable $e) {
        // tabela ainda não migrada — o card do dashboard segue sem a fila
    }
  ?>
</div>

<div class="grid-2">
  <div class="card">
    <h2 class="card-title">Tarefas</h2>

    <?php if ($tarefasAtrasadas): ?>
      <h3 class="list-title list-title-warn">Atrasadas (<?= count($tarefasAtrasadas) ?>)</h3>
      <ul class="task-list">
        <?php foreach ($tarefasAtrasadas as $t): ?>
          <li>
            <form method="post" class="inline-form"><?= csrf_field() ?>
              <input type="hidden" name="action" value="task_done">
              <input type="hidden" name="id" value="<?= (int) $t['id'] ?>">
              <button class="task-check" type="submit" title="Concluir">✓</button>
            </form>
            <span class="task-due overdue"><?= esc(fmt_dt($t['due_at'])) ?></span>
            <span class="task-title"><?= esc($t['title']) ?></span>
            <?php if ($t['lead_id']): ?>
              <a class="task-lead" href="lead.php?id=<?= (int) $t['lead_id'] ?>"><?= esc($t['company'] ?? ('#' . $t['lead_id'])) ?></a>
            <?php endif; ?>
          </li>
        <?php endforeach; ?>
      </ul>
    <?php endif; ?>

    <h3 class="list-title">Hoje (<?= count($tarefasHoje) ?>)</h3>
    <?php if ($tarefasHoje): ?>
      <ul class="task-list">
        <?php foreach ($tarefasHoje as $t): ?>
          <li>
            <form method="post" class="inline-form"><?= csrf_field() ?>
              <input type="hidden" name="action" value="task_done">
              <input type="hidden" name="id" value="<?= (int) $t['id'] ?>">
              <button class="task-check" type="submit" title="Concluir">✓</button>
            </form>
            <span class="task-due"><?= esc(date('H:i', strtotime($t['due_at']))) ?></span>
            <span class="task-title"><?= esc($t['title']) ?></span>
            <?php if ($t['lead_id']): ?>
              <a class="task-lead" href="lead.php?id=<?= (int) $t['lead_id'] ?>"><?= esc($t['company'] ?? ('#' . $t['lead_id'])) ?></a>
            <?php endif; ?>
          </li>
        <?php endforeach; ?>
      </ul>
    <?php else: ?>
      <p class="muted">Nada para hoje.</p>
    <?php endif; ?>

    <form method="post" class="task-quick">
      <?= csrf_field() ?>
      <input type="hidden" name="action" value="task_add">
      <input name="title" type="text" placeholder="Nova tarefa avulsa…" maxlength="200" required>
      <input name="due_at" type="datetime-local" required>
      <button class="btn btn-ghost btn-sm" type="submit">Criar</button>
    </form>
  </div>

  <div class="card">
    <h2 class="card-title">Precisa de atenção</h2>

    <h3 class="list-title list-title-warn">Follow-ups vencidos (<?= count($followups) ?>)</h3>
    <?php if ($followups): ?>
      <ul class="plain-list">
        <?php foreach ($followups as $l): ?>
          <li>
            <a href="lead.php?id=<?= (int) $l['id'] ?>"><?= esc($l['company']) ?></a>
            <?= status_badge($l['status']) ?>
            <span class="muted overdue"><?= esc(fmt_dt($l['next_action_at'])) ?></span>
            <?php if ($l['next_action_note']): ?><span class="muted">— <?= esc($l['next_action_note']) ?></span><?php endif; ?>
          </li>
        <?php endforeach; ?>
      </ul>
    <?php else: ?>
      <p class="muted">Nenhum follow-up vencido. 👌</p>
    <?php endif; ?>

    <h3 class="list-title">Novos sem contato há 48h+ (<?= count($parados) ?>)</h3>
    <?php if ($parados): ?>
      <ul class="plain-list">
        <?php foreach ($parados as $l): ?>
          <li>
            <a href="lead.php?id=<?= (int) $l['id'] ?>"><?= esc($l['company']) ?></a>
            <span class="muted">desde <?= esc(fmt_dt($l['created_at'])) ?></span>
          </li>
        <?php endforeach; ?>
      </ul>
    <?php else: ?>
      <p class="muted">Nenhum lead novo parado.</p>
    <?php endif; ?>
  </div>
</div>
<?php page_footer(); ?>
