<?php
/**
 * Painel do site: visitas, páginas, origens, cliques em CTA, funil da
 * calculadora e as visitas que viraram lead. Dados do crm/collect.php.
 */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    try {
        if (($_POST['action'] ?? '') === 'vincular') {
            $ref = ref_code_norm($_POST['ref'] ?? '');
            if ($ref === null) {
                throw new InvalidArgumentException('Código de visita inválido (são 6 caracteres).');
            }
            visit_link_lead($ref, (int) ($_POST['lead_id'] ?? 0));
            flash_set('ok', 'Visita ' . $ref . ' vinculada ao lead.');
        }
    } catch (InvalidArgumentException $e) {
        flash_set('erro', $e->getMessage());
    } catch (Throwable $e) {
        error_log('analytics: ' . $e->getMessage());
        flash_set('erro', 'Não foi possível vincular a visita.');
    }
    redirect('analytics.php' . (isset($_POST['ref']) ? '?ref=' . urlencode((string) $_POST['ref']) : ''));
}

$dias = (int) in_enum((string) ($_GET['d'] ?? '30'), ['7', '30', '90'], '30');
$buscaRef = ref_code_norm($_GET['ref'] ?? null);
$visitaBuscada = null;

// Uma migration 011 ainda não aplicada não pode derrubar a página: o painel
// avisa e manda rodar o migrate.php.
$tabelasProntas = true;
try {
    $visitaBuscada = $buscaRef !== null ? analytics_visita_por_ref($buscaRef) : null;
    $resumo = analytics_resumo($dias);
    $porDia = analytics_por_dia($dias);
    $paginas = analytics_paginas($dias);
    $origens = analytics_origens($dias);
    $eventos = analytics_eventos($dias);
    $dispositivos = analytics_dispositivos($dias);
    $funil = analytics_funil($dias);
    $recentes = analytics_visitas_recentes(40, true);
} catch (Throwable $e) {
    $tabelasProntas = false;
    error_log('analytics: ' . $e->getMessage());
}

/** Segundos → "2m 05s" / "48s". */
function an_dur(?int $s): string
{
    if ($s === null || $s <= 0) {
        return '—';
    }
    return $s < 60 ? $s . 's' : intdiv($s, 60) . 'm ' . str_pad((string) ($s % 60), 2, '0', STR_PAD_LEFT) . 's';
}

function an_pct(int $parte, int $total): string
{
    return $total > 0 ? number_format($parte / $total * 100, 1, ',', '.') . '%' : '—';
}

function an_num(int $v): string
{
    return number_format($v, 0, ',', '.');
}

page_header('Site', 'analytics.php', $user);
?>
<div class="page-head">
  <h1 class="page-title">Site <span class="muted">— últimos <?= $dias ?> dias</span></h1>
  <div class="an-periodo">
    <?php foreach ([7 => '7 dias', 30 => '30 dias', 90 => '90 dias'] as $d => $rotulo): ?>
      <a class="btn btn-sm <?= $d === $dias ? 'btn-primary' : 'btn-ghost' ?>" href="analytics.php?d=<?= $d ?>"><?= $rotulo ?></a>
    <?php endforeach; ?>
  </div>
</div>

<?php if (!$tabelasProntas): ?>
  <div class="card">
    <h2 class="card-title">Analytics ainda não migrado</h2>
    <p class="muted">As tabelas do analytics não existem neste banco. Rode <code>/crm/migrate.php</code>
      com a <code>migrate_key</code> para aplicar a migration <code>011_analytics.sql</code>.</p>
  </div>
  <?php page_footer(); exit; ?>
<?php endif; ?>

<div class="stats stats-6">
  <div class="stat">
    <span class="stat-n"><?= an_num((int) $resumo['visitas']) ?></span>
    <span class="stat-label">Visitas</span>
  </div>
  <div class="stat">
    <span class="stat-n"><?= an_num((int) $resumo['visitantes']) ?></span>
    <span class="stat-label">Visitantes</span>
  </div>
  <div class="stat">
    <span class="stat-n"><?= an_num((int) $resumo['views']) ?></span>
    <span class="stat-label">Páginas vistas</span>
  </div>
  <div class="stat">
    <span class="stat-n"><?= an_num((int) $resumo['eventos']) ?></span>
    <span class="stat-label">Cliques</span>
  </div>
  <div class="stat">
    <span class="stat-n"><?= esc(an_dur((int) $resumo['seg_medio'])) ?></span>
    <span class="stat-label">Tempo médio</span>
  </div>
  <div class="stat stat-cliente">
    <span class="stat-n"><?= an_num((int) $resumo['viraram_lead']) ?></span>
    <span class="stat-label">Viraram lead</span>
  </div>
</div>
<p class="muted an-nota">Sem cookie e sem IP guardado: o visitante é um hash que troca todo dia, então
  “visitantes” é a soma dos únicos de cada dia — quem volta amanhã conta de novo.
  Visita = 30 min sem atividade fecham a sessão.
  <?php if ((int) $resumo['visitas'] > 0): ?>
    Rejeição (entrou, viu uma página e não clicou em nada): <strong><?= esc(an_pct((int) $resumo['rejeicoes'], (int) $resumo['visitas'])) ?></strong>.
  <?php endif; ?>
</p>

<div class="card">
  <h2 class="card-title">Visitas por dia</h2>
  <?php if ($porDia): ?>
    <?php $topo = max(array_map(fn ($r) => (int) $r['visitas'], $porDia)); ?>
    <div class="an-bars">
      <?php foreach ($porDia as $d): ?>
        <?php $h = $topo > 0 ? max(3, (int) round((int) $d['visitas'] / $topo * 100)) : 3; ?>
        <div class="an-bar-wrap" title="<?= esc(fmt_date($d['dia'])) ?>: <?= an_num((int) $d['visitas']) ?> visita(s), <?= an_num((int) $d['views']) ?> página(s), <?= an_num((int) $d['eventos']) ?> clique(s)">
          <div class="an-bar" style="height: <?= $h ?>%"></div>
        </div>
      <?php endforeach; ?>
    </div>
    <div class="an-eixo">
      <span><?= esc(fmt_date($porDia[0]['dia'])) ?></span>
      <span><?= esc(fmt_date($porDia[count($porDia) - 1]['dia'])) ?></span>
    </div>
  <?php else: ?>
    <p class="muted">Nenhuma visita registrada no período. Se o track.js acabou de subir, espere o primeiro acesso.</p>
  <?php endif; ?>
</div>

<div class="grid-2">
  <div class="card">
    <h2 class="card-title">Páginas</h2>
    <?php if ($paginas): ?>
      <div class="table-wrap">
        <table class="table">
          <thead><tr><th>Página</th><th>Views</th><th>Visitas</th><th>Tempo</th><th>Scroll</th></tr></thead>
          <tbody>
          <?php foreach ($paginas as $p): ?>
            <tr>
              <td><a href="https://www.mais351monitor.com.br<?= esc($p['path']) ?>" target="_blank" rel="noopener"><?= esc($p['path']) ?></a></td>
              <td><?= an_num((int) $p['views']) ?></td>
              <td><?= an_num((int) $p['visitas']) ?></td>
              <td><?= esc(an_dur($p['seg_medio'] !== null ? (int) $p['seg_medio'] : null)) ?></td>
              <td><?= $p['scroll_medio'] !== null ? (int) $p['scroll_medio'] . '%' : '—' ?></td>
            </tr>
          <?php endforeach; ?>
          </tbody>
        </table>
      </div>
    <?php else: ?>
      <p class="muted">Sem dados no período.</p>
    <?php endif; ?>
  </div>

  <div class="card">
    <h2 class="card-title">Origens</h2>
    <?php if ($origens): ?>
      <div class="table-wrap">
        <table class="table">
          <thead><tr><th>Origem</th><th>Campanha</th><th>Visitas</th><th>Cliques</th><th>Leads</th></tr></thead>
          <tbody>
          <?php foreach ($origens as $o): ?>
            <tr>
              <td><?= esc($o['origem']) ?></td>
              <td class="muted"><?= esc(trim(($o['utm_medium'] ?? '') . ' ' . ($o['utm_campaign'] ?? ''))) ?: '—' ?></td>
              <td><?= an_num((int) $o['visitas']) ?></td>
              <td><?= an_num((int) $o['eventos']) ?></td>
              <td><?= an_num((int) $o['leads']) ?></td>
            </tr>
          <?php endforeach; ?>
          </tbody>
        </table>
      </div>
    <?php else: ?>
      <p class="muted">Sem dados no período.</p>
    <?php endif; ?>
  </div>
</div>

<div class="grid-2">
  <div class="card">
    <h2 class="card-title">Cliques</h2>
    <?php if ($eventos): ?>
      <div class="table-wrap">
        <table class="table">
          <thead><tr><th>Ação</th><th>Onde / o quê</th><th>Cliques</th><th>Visitas</th></tr></thead>
          <tbody>
          <?php foreach ($eventos as $ev): ?>
            <tr>
              <td><?= esc(EVENT_LABELS[$ev['name']] ?? $ev['name']) ?></td>
              <td class="muted"><?= esc(mb_substr((string) $ev['rotulo'], 0, 70)) ?></td>
              <td><?= an_num((int) $ev['n']) ?></td>
              <td><?= an_num((int) $ev['visitas']) ?></td>
            </tr>
          <?php endforeach; ?>
          </tbody>
        </table>
      </div>
    <?php else: ?>
      <p class="muted">Nenhum clique registrado ainda.</p>
    <?php endif; ?>
  </div>

  <div class="card">
    <h2 class="card-title">Funil da calculadora</h2>
    <?php
      $etapas = [
        'Visitas'                 => (int) ($funil['visitas'] ?? 0),
        'Mexeu na calculadora'    => (int) ($funil['mexeu'] ?? 0),
        'Calculou o impacto'      => (int) ($funil['calculou'] ?? 0),
        'Clicou no CTA do cálculo' => (int) ($funil['cta_calc'] ?? 0),
        'Clicou em algum WhatsApp' => (int) ($funil['whatsapp'] ?? 0),
      ];
      $base = max(1, (int) ($funil['visitas'] ?? 0));
    ?>
    <?php foreach ($etapas as $rotulo => $qtd): ?>
      <div class="an-funil">
        <span class="an-funil-label"><?= esc($rotulo) ?></span>
        <div class="progress"><div class="progress-bar" style="width: <?= min(100, (int) round($qtd / $base * 100)) ?>%"></div></div>
        <span class="an-funil-n"><?= an_num($qtd) ?> <span class="muted"><?= esc(an_pct($qtd, $base)) ?></span></span>
      </div>
    <?php endforeach; ?>

    <h3 class="list-title">Aparelhos</h3>
    <?php if ($dispositivos): ?>
      <ul class="plain-list">
        <?php foreach ($dispositivos as $dv): ?>
          <li><?= esc($dv['device']) ?> · <span class="muted"><?= esc($dv['browser']) ?></span> — <?= an_num((int) $dv['visitas']) ?></li>
        <?php endforeach; ?>
      </ul>
    <?php else: ?>
      <p class="muted">Sem dados no período.</p>
    <?php endif; ?>
  </div>
</div>

<div class="card">
  <h2 class="card-title">Código da visita <span class="muted">(o <code>#XXXXXX</code> que chega na conversa do WhatsApp)</span></h2>
  <p class="muted">Todo link de WhatsApp do site sai com um código curto no fim da mensagem. Cole aqui o
    código que a pessoa mandou para ver por onde ela andou — e amarre a visita ao lead.</p>
  <form method="get" class="task-quick">
    <input name="ref" type="text" placeholder="Ex.: K7M2Q9" maxlength="10" value="<?= esc($buscaRef ?? '') ?>" required>
    <button class="btn btn-ghost btn-sm" type="submit">Buscar visita</button>
  </form>

  <?php if ($buscaRef !== null && $visitaBuscada === null): ?>
    <p class="muted">Nenhuma visita com o código <code><?= esc($buscaRef) ?></code>
      (visitas sem lead são apagadas depois de <?= VISIT_RETENTION_DAYS ?> dias).</p>
  <?php elseif ($visitaBuscada !== null): ?>
    <?php $v = $visitaBuscada; ?>
    <div class="card card-inner">
      <h3 class="list-title">Visita <code><?= esc($v['ref_code']) ?></code>
        — <?= esc(fmt_dt($v['started_at'])) ?>
        <span class="muted">· <?= esc($v['device']) ?>/<?= esc($v['browser'] ?? '?') ?>
          · origem <?= esc($v['utm_source'] ?? $v['referrer_host'] ?? 'direto') ?></span>
      </h3>

      <?php if ($v['lead_id']): ?>
        <p>Já vinculada ao lead
          <a href="lead.php?id=<?= (int) $v['lead_id'] ?>"><?= esc($v['lead_company'] ?? ('#' . $v['lead_id'])) ?></a>.</p>
      <?php else: ?>
        <form method="post" class="task-quick">
          <?= csrf_field() ?>
          <input type="hidden" name="action" value="vincular">
          <input type="hidden" name="ref" value="<?= esc($v['ref_code']) ?>">
          <select name="lead_id" required>
            <option value="">Vincular a um lead…</option>
            <?php foreach (rows('SELECT id, company FROM leads ORDER BY updated_at DESC LIMIT 200') as $l): ?>
              <option value="<?= (int) $l['id'] ?>"><?= esc($l['company']) ?></option>
            <?php endforeach; ?>
          </select>
          <button class="btn btn-primary btn-sm" type="submit">Vincular</button>
        </form>
      <?php endif; ?>

      <h4 class="list-title">Páginas (<?= count($v['views_list']) ?>)</h4>
      <ul class="plain-list">
        <?php foreach ($v['views_list'] as $w): ?>
          <li><span class="muted"><?= esc(date('H:i', strtotime($w['created_at']))) ?></span>
            <?= esc($w['path']) ?>
            <span class="muted">— <?= esc(an_dur($w['seconds'] !== null ? (int) $w['seconds'] : null)) ?>
              <?php if ($w['scroll_pct'] !== null): ?>· <?= (int) $w['scroll_pct'] ?>% da página<?php endif; ?></span>
          </li>
        <?php endforeach; ?>
      </ul>

      <h4 class="list-title">Cliques (<?= count($v['events_list']) ?>)</h4>
      <?php if ($v['events_list']): ?>
        <ul class="plain-list">
          <?php foreach ($v['events_list'] as $ev): ?>
            <li><span class="muted"><?= esc(date('H:i', strtotime($ev['created_at']))) ?></span>
              <?= esc(EVENT_LABELS[$ev['name']] ?? $ev['name']) ?>
              <?php if ($ev['label']): ?>— <?= esc($ev['label']) ?><?php endif; ?>
              <?php if ($ev['value_num'] !== null): ?><span class="muted"> (<?= an_num((int) $ev['value_num']) ?>)</span><?php endif; ?>
            </li>
          <?php endforeach; ?>
        </ul>
      <?php else: ?>
        <p class="muted">Nenhum clique nessa visita.</p>
      <?php endif; ?>
    </div>
  <?php endif; ?>
</div>

<div class="card">
  <h2 class="card-title">Visitas com intenção <span class="muted">(as que clicaram em alguma coisa)</span></h2>
  <?php if ($recentes): ?>
    <div class="table-wrap">
      <table class="table">
        <thead><tr><th>Quando</th><th>Código</th><th>Entrou por</th><th>Origem</th><th>Páginas</th><th>Cliques</th><th>Lead</th></tr></thead>
        <tbody>
        <?php foreach ($recentes as $v): ?>
          <tr>
            <td><?= esc(fmt_dt($v['started_at'])) ?></td>
            <td><a href="analytics.php?d=<?= $dias ?>&amp;ref=<?= esc($v['ref_code']) ?>"><code><?= esc($v['ref_code']) ?></code></a></td>
            <td class="muted"><?= esc($v['landing_path']) ?></td>
            <td class="muted"><?= esc($v['utm_source'] ?? $v['referrer_host'] ?? 'direto') ?></td>
            <td><?= (int) $v['views'] ?></td>
            <td><?= (int) $v['events'] ?></td>
            <td><?php if ($v['lead_id']): ?>
              <a href="lead.php?id=<?= (int) $v['lead_id'] ?>"><?= esc($v['lead_company'] ?? ('#' . $v['lead_id'])) ?></a>
            <?php else: ?><span class="muted">—</span><?php endif; ?></td>
          </tr>
        <?php endforeach; ?>
        </tbody>
      </table>
    </div>
  <?php else: ?>
    <p class="muted">Ninguém clicou em nada ainda.</p>
  <?php endif; ?>
</div>
<?php page_footer(); ?>
