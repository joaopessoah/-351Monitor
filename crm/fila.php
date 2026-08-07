<?php
/**
 * Fila de prospecção: estatísticas do pool (alimentado mensalmente pelo
 * pipeline tools/leadgen via GitHub Actions) e o botão "Puxar leads",
 * que promove as melhores empresas ainda não usadas a leads.
 */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    if (($_POST['action'] ?? '') === 'pull') {
        $qtd = (int) ($_POST['qtd'] ?? 25);
        $vertical = in_enum($_POST['vertical'] ?? null, POOL_VERTICAIS, '') ?: null;
        try {
            $res = pool_pull($qtd, $vertical, (int) $user['id']);
            if ($res['criados'] === 0 && $res['reconciliados'] === 0) {
                flash_set('aviso', 'A fila está vazia para esse filtro — rode o pipeline mensal ou escolha outra vertical.');
            } else {
                $msg = $res['criados'] . ' lead(s) novo(s) criado(s)';
                if ($res['reconciliados'] > 0) {
                    $msg .= ' (' . $res['reconciliados'] . ' já existiam no CRM e foram apenas marcados na fila)';
                }
                flash_set('ok', $msg . '.');
            }
        } catch (Throwable $e) {
            error_log('fila: ' . $e->getMessage());
            flash_set('erro', 'Não foi possível puxar da fila agora.');
        }
    }
    redirect('fila.php');
}

$stats = null;
$statsErro = null;
try {
    $stats = pool_stats();
} catch (Throwable $e) {
    $statsErro = 'Fila ainda não disponível — aplique as migrations (migrate.php).';
}

page_header('Fila de prospecção', 'fila.php', $user);
?>
<div class="page-head">
  <h1 class="page-title">Fila de prospecção</h1>
</div>

<?php if ($statsErro !== null): ?>
  <div class="flash flash-aviso"><?= esc($statsErro) ?></div>
<?php else: ?>
  <div class="grid-2">
    <div class="card">
      <h2 class="card-title">Puxar leads da fila</h2>
      <p class="muted">Promove as empresas de maior score (dados abertos da Receita, atualizados
        mensalmente) a leads com origem <strong>Prospecção</strong>. Puxe no ritmo da sua
        capacidade de contato — o restante fica guardado na fila.</p>
      <form method="post" class="form-stack">
        <?= csrf_field() ?>
        <input type="hidden" name="action" value="pull">
        <div class="form-grid">
          <div class="field">
            <label for="qtd">Quantidade</label>
            <select id="qtd" name="qtd">
              <option value="10">10</option>
              <option value="25" selected>25</option>
              <option value="50">50</option>
            </select>
          </div>
          <div class="field">
            <label for="vertical">Vertical</label>
            <select id="vertical" name="vertical">
              <option value="">Melhores de todas</option>
              <?php foreach (POOL_VERTICAIS as $v): ?>
                <option value="<?= esc($v) ?>"><?= esc(POOL_VERTICAL_LABELS[$v]) ?></option>
              <?php endforeach; ?>
            </select>
          </div>
        </div>
        <button class="btn btn-primary" type="submit">Puxar leads</button>
      </form>
    </div>

    <div class="card">
      <h2 class="card-title">Estoque da fila
        <?php if ($stats['mes']): ?><span class="muted">· base RFB <?= esc($stats['mes']) ?></span><?php endif; ?>
      </h2>
      <div class="table-wrap">
        <table class="table">
          <thead><tr><th>Vertical</th><th>Disponíveis</th><th>Já usados</th></tr></thead>
          <tbody>
            <?php foreach (POOL_VERTICAIS as $v): $s = $stats['verticais'][$v] ?? ['disponiveis' => 0, 'promovidos' => 0]; ?>
              <tr>
                <td><?= esc(POOL_VERTICAL_LABELS[$v]) ?></td>
                <td><?= (int) $s['disponiveis'] ?></td>
                <td class="muted"><?= (int) $s['promovidos'] ?></td>
              </tr>
            <?php endforeach; ?>
            <tr>
              <td><strong>Total</strong></td>
              <td><strong><?= (int) $stats['disponiveis'] ?></strong></td>
              <td class="muted"><?= (int) $stats['promovidos'] ?></td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="muted">A fila é atualizada todo mês automaticamente (GitHub Actions → dados
        abertos do CNPJ). Empresas que já viraram lead nunca entram de novo.</p>
    </div>
  </div>

  <div class="card">
    <h2 class="card-title">Últimos puxados</h2>
    <?php
      $ultimos = rows('SELECT p.promoted_at, p.vertical, p.score, l.id AS lead_id, l.company, l.status
                       FROM prospect_pool p JOIN leads l ON l.id = p.promoted_lead_id
                       WHERE p.promoted_at IS NOT NULL
                       ORDER BY p.promoted_at DESC LIMIT 15');
    ?>
    <?php if (!$ultimos): ?>
      <p class="muted">Nenhuma empresa puxada ainda.</p>
    <?php else: ?>
      <div class="table-wrap">
        <table class="table">
          <thead><tr><th>Empresa</th><th>Vertical</th><th>Score</th><th>Status</th><th>Quando</th></tr></thead>
          <tbody>
            <?php foreach ($ultimos as $u): ?>
              <tr>
                <td><a href="lead.php?id=<?= (int) $u['lead_id'] ?>"><?= esc($u['company']) ?></a></td>
                <td><?= esc(POOL_VERTICAL_LABELS[$u['vertical']] ?? $u['vertical']) ?></td>
                <td><?= (int) $u['score'] ?></td>
                <td><?= status_badge($u['status']) ?></td>
                <td class="muted"><?= esc(fmt_dt($u['promoted_at'])) ?></td>
              </tr>
            <?php endforeach; ?>
          </tbody>
        </table>
      </div>
    <?php endif; ?>
  </div>
<?php endif; ?>
<?php page_footer(); ?>
