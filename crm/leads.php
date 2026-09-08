<?php
/** Lista de leads: filtros, busca, paginação e export CSV (backup operacional). */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();
$userId = (int) $user['id'];

/* ---------- Acao em lote: iniciar cadencia com previa obrigatoria ---------- */
$previa = null;
$previaIds = [];
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $acao = $_POST['acao'] ?? '';
    $previaIds = array_slice(array_map('intval', (array) ($_POST['ids'] ?? [])), 0, 200);
    try {
        if ($acao === 'previa') {
            if (!$previaIds) {
                flash_set('aviso', 'Selecione pelo menos um lead.');
                redirect('leads.php');
            }
            $previa = cadencia_auto_previa($previaIds);
        } elseif ($acao === 'iniciar') {
            $sender = (int) ($_POST['sender_user_id'] ?? 0) ?: null;
            $criados = 0;
            $falhas = 0;
            foreach ($previaIds as $lid) {
                $res = cadencia_auto_iniciar($lid, $sender, null, $userId, null);
                $res['ok'] ? $criados++ : $falhas++;
            }
            flash_set($criados > 0 ? 'ok' : 'erro', $criados . ' lead(s) em cadência'
                . ($falhas > 0 ? ' · ' . $falhas . ' não entrou/entraram' : '') . '. '
                . (setting_str('auto_modo') === 'automatico'
                    ? 'Os e-mails saem nos horários agendados.'
                    : 'Os e-mails ficam em Envios aguardando aprovação.'));
            redirect('envios.php');
        }
    } catch (Throwable $e) {
        error_log('leads lote: ' . $e->getMessage());
        flash_set('erro', 'Não deu para iniciar a cadência. As migrations 012 a 014 já foram aplicadas?');
        redirect('leads.php');
    }
}

$f = [
    'status'      => $_GET['status'] ?? '',
    'source'      => $_GET['source'] ?? '',
    'q'           => norm_text($_GET['q'] ?? '', 120),
    'so_vencidos' => !empty($_GET['so_vencidos']),
    'so_decisor'  => !empty($_GET['so_decisor']),
];

// Export CSV com os filtros aplicados — sem filtro, baixa a base inteira
// (contatos agregados e flag de decisor incluídos). Excel BR: BOM UTF-8 + ';'.
if (isset($_GET['export'])) {
    $res = leads_search($f, 1, 100000);
    $ids = array_map(fn ($l) => (int) $l['id'], $res['items']);
    $agregados = contacts_agregados($ids);
    $decisores = leads_com_decisor($ids);
    header('Content-Type: text/csv; charset=utf-8');
    header('Content-Disposition: attachment; filename="leads-' . date('Ymd-Hi') . '.csv"');
    echo "\xEF\xBB\xBF";
    $out = fopen('php://output', 'w');
    fputcsv($out, ['id', 'empresa', 'cnpj', 'razao_social_receita', 'situacao_receita', 'site', 'linkedin',
        'contato_principal', 'email', 'whatsapp', 'tem_decisor', 'contatos',
        'status', 'motivo_perda', 'origem',
        'estacoes', 'plano', 'proxima_acao', 'nota_proxima_acao', 'observacoes',
        'utm_source', 'utm_medium', 'utm_campaign', 'criado_em', 'atualizado_em'], ';');
    foreach ($res['items'] as $l) {
        fputcsv($out, [$l['id'], $l['company'], cnpj_format($l['cnpj']), $l['cnpj_razao_social'], $l['cnpj_situacao'],
            $l['website'], $l['linkedin'],
            $l['contact_name'], $l['email'], $l['whatsapp'],
            isset($decisores[(int) $l['id']]) ? 'Sim' : 'Não',
            $agregados[(int) $l['id']] ?? '',
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
        'so_decisor'  => $f['so_decisor'] ? '1' : '',
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
  <div class="field field-check">
    <input id="f-decisor" name="so_decisor" type="checkbox" value="1" class="auto-submit" <?= $f['so_decisor'] ? 'checked' : '' ?>>
    <label for="f-decisor">Só com decisor</label>
  </div>
  <button class="btn btn-ghost" type="submit">Filtrar</button>
</form>

<?php if ($previa !== null): ?>
  <div class="card previa-lote">
    <h2 class="card-title">Iniciar cadência — prévia de <?= count($previaIds) ?> lead(s)</h2>
    <p class="muted">Nada é enviado agora: o primeiro e-mail de cada lead entra na fila
      <?= setting_str('auto_modo') === 'automatico' ? 'já agendado' : 'aguardando aprovação' ?>,
      no horário sorteado dentro das janelas de envio.</p>

    <?php if ($previa['aptos']): ?>
      <h3 class="list-title">Entram (<?= count($previa['aptos']) ?>)</h3>
      <div class="table-wrap">
        <table class="table">
          <thead><tr><th>Empresa</th><th>Contato</th><th>E-mail</th><th>Situação do e-mail</th><th>Histórico</th></tr></thead>
          <tbody>
            <?php foreach ($previa['aptos'] as $p): ?>
              <tr>
                <td><a href="lead.php?id=<?= (int) $p['lead']['id'] ?>"><?= esc($p['lead']['company']) ?></a></td>
                <td><?= esc($p['contato']['name'] ?? '—') ?><?= !empty($p['contato']['is_decisor']) ? ' ★' : '' ?></td>
                <td class="muted small"><?= esc($p['contato']['email'] ?? '') ?></td>
                <td><?= email_status_badge($p['check']['status'] ?? null) ?>
                  <?php if (!empty($p['check']['detail'])): ?>
                    <span class="muted small"><?= esc($p['check']['detail']) ?></span>
                  <?php endif; ?>
                </td>
                <td>
                  <?php $ant = $p['anterior'] ?? null; ?>
                  <?php if ($ant === null): ?>
                    <span class="muted">primeira vez</span>
                  <?php else: ?>
                    <span class="badge badge-dup" title="Recomeçar manda o 1º e-mail de novo para quem já recebeu a sequência">
                      já recebeu <?= (int) $ant['current_seq'] ?> e-mail(s)
                    </span>
                    <span class="muted small"><?= esc(CADENCIA_ESTADO_LABELS[$ant['state']] ?? $ant['state']) ?><?php
                      if ($ant['stopped_at']) { echo ' em ' . esc(fmt_date($ant['stopped_at'])); } ?></span>
                  <?php endif; ?>
                </td>
              </tr>
            <?php endforeach; ?>
          </tbody>
        </table>
      </div>
    <?php else: ?>
      <p class="muted">Nenhum dos leads selecionados pode entrar em cadência agora.</p>
    <?php endif; ?>

    <?php if ($previa['barrados']): ?>
      <h3 class="list-title list-title-warn">Ficam de fora (<?= count($previa['barrados']) ?>)</h3>
      <ul class="plain-list">
        <?php foreach ($previa['barrados'] as $p): ?>
          <li>
            <a href="lead.php?id=<?= (int) $p['lead']['id'] ?>"><?= esc($p['lead']['company']) ?></a>
            <span class="muted">— <?= esc($p['motivo']) ?></span>
          </li>
        <?php endforeach; ?>
      </ul>
    <?php endif; ?>

    <?php if ($previa['aptos']): ?>
      <?php $remetentes = mail_remetentes(); ?>
      <?php if (!$remetentes): ?>
        <p class="muted">Nenhuma caixa configurada no <code>crm_config.php</code> — o motor não tem por onde enviar.</p>
      <?php else: ?>
        <form method="post" class="form-stack">
          <?= csrf_field() ?>
          <input type="hidden" name="acao" value="iniciar">
          <?php foreach ($previa['aptos'] as $p): ?>
            <input type="hidden" name="ids[]" value="<?= (int) $p['lead']['id'] ?>">
          <?php endforeach; ?>
          <div class="field" style="max-width: 340px">
            <label for="lote_sender">Quem assina os e-mails</label>
            <select id="lote_sender" name="sender_user_id">
              <?php foreach ($remetentes as $r): ?>
                <option value="<?= (int) $r['id'] ?>"<?= (int) $r['id'] === $userId ? ' selected' : '' ?>>
                  <?= esc($r['name']) ?> &lt;<?= esc($r['email']) ?>&gt;
                </option>
              <?php endforeach; ?>
            </select>
          </div>
          <div class="form-actions">
            <button class="btn btn-primary" type="submit">Confirmar e iniciar <?= count($previa['aptos']) ?> cadência(s)</button>
            <a class="btn btn-ghost" href="leads.php">Cancelar</a>
          </div>
        </form>
      <?php endif; ?>
    <?php endif; ?>
  </div>
<?php endif; ?>

<?php
  $idsPagina = array_map(fn ($l) => (int) $l['id'], $res['items']);
  $decisores = leads_com_decisor($idsPagina);
  $cadencias = cadencias_dos_leads($idsPagina);
?>
<form method="post" id="form-lote">
<?= csrf_field() ?>
<input type="hidden" name="acao" value="previa">
<div class="card table-wrap">
  <table class="table">
    <thead>
      <tr>
        <th class="col-check"><input type="checkbox" id="check-todos" aria-label="Selecionar todos"></th>
        <th>Empresa</th><th>Contato</th><th>WhatsApp</th><th>Status</th>
        <th>Cadência</th><th>Próxima ação</th><th>Origem</th><th>Atualizado</th>
      </tr>
    </thead>
    <tbody>
      <?php if (!$res['items']): ?>
        <tr><td colspan="9" class="muted">Nenhum lead encontrado.</td></tr>
      <?php endif; ?>
      <?php foreach ($res['items'] as $l): ?>
        <?php $cd = $cadencias[(int) $l['id']] ?? null; ?>
        <tr>
          <td class="col-check">
            <input type="checkbox" name="ids[]" value="<?= (int) $l['id'] ?>" class="check-lead"
                   aria-label="Selecionar <?= esc($l['company']) ?>">
          </td>
          <td>
            <a href="lead.php?id=<?= (int) $l['id'] ?>"><?= esc($l['company']) ?></a>
            <?php if ($l['duplicate_of_lead_id']): ?><span class="badge badge-dup">Duplicado</span><?php endif; ?>
            <?php if (!empty($l['no_contact'])): ?><span class="badge badge-nc" title="Pediu para não ser contactado">Não contactar</span><?php endif; ?>
          </td>
          <td>
            <?= esc($l['contact_name'] ?: '—') ?>
            <?php if (isset($decisores[(int) $l['id']])): ?><span class="badge badge-decisor" title="Tem contato decisor">★</span><?php endif; ?>
          </td>
          <td><?= wa_link($l['whatsapp']) ?></td>
          <td><?= status_badge($l['status']) ?></td>
          <td>
            <?php if ($cd === null): ?><span class="muted">—</span>
            <?php elseif ($cd['state'] === 'ativa'): ?>
              <span class="badge badge-trial" title="<?= $cd['next'] ? 'Próximo e-mail em ' . esc(fmt_dt($cd['next'])) : 'Sem envio agendado' ?>">
                <?= (int) $cd['seq'] ?>º de <?= count(cadencia_etapas_ativas()) ?>
              </span>
            <?php else: ?>
              <span class="badge badge-perdido"><?= esc(CADENCIA_ESTADO_LABELS[$cd['state']] ?? $cd['state']) ?></span>
            <?php endif; ?>
          </td>
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
<div class="barra-lote" id="barra-lote" hidden>
  <span class="lote-n"><span id="lote-conta">0</span> lead(s) selecionado(s)</span>
  <button class="btn btn-primary btn-sm" type="submit">Iniciar cadência…</button>
  <span class="muted small">Você vê a prévia antes de qualquer envio.</span>
</div>
</form>

<?php if ($totalPages > 1): ?>
  <nav class="pager">
    <?php if ($page > 1): ?><a href="leads.php?<?= esc(leads_qs($f, ['page' => $page - 1])) ?>">‹ Anterior</a><?php endif; ?>
    <span class="current"><?= $page ?> / <?= $totalPages ?></span>
    <?php if ($page < $totalPages): ?><a href="leads.php?<?= esc(leads_qs($f, ['page' => $page + 1])) ?>">Próxima ›</a><?php endif; ?>
  </nav>
<?php endif; ?>
<?php page_footer(); ?>
