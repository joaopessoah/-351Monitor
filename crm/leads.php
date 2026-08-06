<?php
/** Lista de leads: filtros, busca, paginação e export CSV (backup operacional). */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();

$f = [
    'status'      => $_GET['status'] ?? '',
    'source'      => $_GET['source'] ?? '',
    'q'           => norm_text($_GET['q'] ?? '', 120),
    'so_vencidos' => !empty($_GET['so_vencidos']),
];

// Export CSV com os filtros aplicados (Excel BR: BOM UTF-8 + ';')
if (isset($_GET['export'])) {
    $res = leads_search($f, 1, 100000);
    header('Content-Type: text/csv; charset=utf-8');
    header('Content-Disposition: attachment; filename="leads-' . date('Ymd-Hi') . '.csv"');
    echo "\xEF\xBB\xBF";
    $out = fopen('php://output', 'w');
    fputcsv($out, ['id', 'empresa', 'contato', 'email', 'whatsapp', 'status', 'motivo_perda', 'origem',
        'estacoes', 'plano', 'proxima_acao', 'nota_proxima_acao', 'observacoes',
        'utm_source', 'utm_medium', 'utm_campaign', 'criado_em', 'atualizado_em'], ';');
    foreach ($res['items'] as $l) {
        fputcsv($out, [$l['id'], $l['company'], $l['contact_name'], $l['email'], $l['whatsapp'],
            STATUS_LABELS[$l['status']] ?? $l['status'], $l['lost_reason'],
            SOURCE_LABELS[$l['source']] ?? $l['source'], $l['estimated_devices'],
            PLAN_LABELS[$l['plan_interest']] ?? $l['plan_interest'],
            $l['next_action_at'], $l['next_action_note'], $l['notes'],
            $l['utm_source'], $l['utm_medium'], $l['utm_campaign'],
            $l['created_at'], $l['updated_at']], ';');
    }
    fclose($out);
    exit;
}

$page = max(1, (int) ($_GET['page'] ?? 1));
$res = leads_search($f, $page);
$totalPages = max(1, (int) ceil($res['total'] / $res['per_page']));

/** Querystring preservando filtros (para paginação/export). */
function leads_qs(array $f, array $extra = []): string
{
    $params = array_filter([
        'status'      => $f['status'],
        'source'      => $f['source'],
        'q'           => $f['q'],
        'so_vencidos' => $f['so_vencidos'] ? '1' : '',
    ], fn ($v) => $v !== '' && $v !== null);
    return http_build_query($params + $extra);
}

page_header('Leads', 'leads.php', $user);
?>
<div class="page-head">
  <h1 class="page-title">Leads <span class="muted">(<?= (int) $res['total'] ?>)</span></h1>
  <div>
    <a class="btn btn-ghost" href="leads.php?<?= esc(leads_qs($f, ['export' => 'csv'])) ?>">Exportar CSV</a>
    <a class="btn btn-primary" href="lead.php">+ Novo lead</a>
  </div>
</div>

<form method="get" class="filters">
  <div class="field">
    <label for="f-status">Status</label>
    <select id="f-status" name="status" class="auto-submit">
      <option value="">Todos</option>
      <?php foreach (LEAD_STATUSES as $s): ?>
        <option value="<?= esc($s) ?>" <?= $f['status'] === $s ? 'selected' : '' ?>><?= esc(STATUS_LABELS[$s]) ?></option>
      <?php endforeach; ?>
    </select>
  </div>
  <div class="field">
    <label for="f-source">Origem</label>
    <select id="f-source" name="source" class="auto-submit">
      <option value="">Todas</option>
      <?php foreach (LEAD_SOURCES as $s): ?>
        <option value="<?= esc($s) ?>" <?= $f['source'] === $s ? 'selected' : '' ?>><?= esc(SOURCE_LABELS[$s]) ?></option>
      <?php endforeach; ?>
    </select>
  </div>
  <div class="field">
    <label for="f-q">Busca</label>
    <input id="f-q" name="q" type="search" value="<?= esc($f['q']) ?>" placeholder="Empresa, contato, e-mail…">
  </div>
  <div class="field field-check">
    <input id="f-vencidos" name="so_vencidos" type="checkbox" value="1" class="auto-submit" <?= $f['so_vencidos'] ? 'checked' : '' ?>>
    <label for="f-vencidos">Só follow-ups vencidos</label>
  </div>
  <button class="btn btn-ghost" type="submit">Filtrar</button>
</form>

<div class="card table-wrap">
  <table class="table">
    <thead>
      <tr>
        <th>Empresa</th><th>Contato</th><th>WhatsApp</th><th>Status</th>
        <th>Próxima ação</th><th>Origem</th><th>Atualizado</th>
      </tr>
    </thead>
    <tbody>
      <?php if (!$res['items']): ?>
        <tr><td colspan="7" class="muted">Nenhum lead encontrado.</td></tr>
      <?php endif; ?>
      <?php foreach ($res['items'] as $l): ?>
        <tr>
          <td>
            <a href="lead.php?id=<?= (int) $l['id'] ?>"><?= esc($l['company']) ?></a>
            <?php if ($l['duplicate_of_lead_id']): ?><span class="badge badge-dup">Duplicado</span><?php endif; ?>
          </td>
          <td><?= esc($l['contact_name'] ?: '—') ?></td>
          <td><?= wa_link($l['whatsapp']) ?></td>
          <td><?= status_badge($l['status']) ?></td>
          <td>
            <?php if ($l['next_action_at']): ?>
              <span class="<?= strtotime($l['next_action_at']) <= time() && !in_array($l['status'], ['cliente', 'perdido'], true) ? 'overdue' : '' ?>">
                <?= esc(fmt_dt($l['next_action_at'])) ?>
              </span>
            <?php else: ?>—<?php endif; ?>
          </td>
          <td><?= esc(SOURCE_LABELS[$l['source']] ?? $l['source']) ?></td>
          <td class="muted"><?= esc(fmt_date($l['updated_at'])) ?></td>
        </tr>
      <?php endforeach; ?>
    </tbody>
  </table>
</div>

<?php if ($totalPages > 1): ?>
  <nav class="pager">
    <?php if ($page > 1): ?><a href="leads.php?<?= esc(leads_qs($f, ['page' => $page - 1])) ?>">‹ Anterior</a><?php endif; ?>
    <span class="current"><?= $page ?> / <?= $totalPages ?></span>
    <?php if ($page < $totalPages): ?><a href="leads.php?<?= esc(leads_qs($f, ['page' => $page + 1])) ?>">Próxima ›</a><?php endif; ?>
  </nav>
<?php endif; ?>
<?php page_footer(); ?>
