<?php
/**
 * Envios e retornos: a tela de operacao da cadencia automatica.
 *
 * Tudo o que o motor decidiu sozinho aparece aqui com o motivo, e tudo pode
 * ser desfeito: aprovar, adiar, cancelar, corrigir o texto, mandar na hora.
 */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();
$userId = (int) $user['id'];

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $acao = $_POST['acao'] ?? '';
    $id = (int) ($_POST['id'] ?? 0);
    try {
        if ($acao === 'aprovar') {
            q("UPDATE email_outbox SET status = 'agendado' WHERE id = ? AND status = 'aguardando'", [$id]);
            flash_set('ok', 'E-mail aprovado — sai no horario agendado.');
        } elseif ($acao === 'aprovar_todos') {
            $st = q("UPDATE email_outbox SET status = 'agendado' WHERE status = 'aguardando'");
            flash_set('ok', $st->rowCount() . ' e-mail(s) aprovado(s).');
        } elseif ($acao === 'cancelar') {
            $out = row('SELECT lead_id FROM email_outbox WHERE id = ?', [$id]);
            q("UPDATE email_outbox SET status = 'cancelado', skip_reason = 'cancelado na tela'
                WHERE id = ? AND status IN ('aguardando','agendado')", [$id]);
            if ($out !== null) {
                cadencia_auto_pausar((int) $out['lead_id'], 'envio cancelado na tela');
            }
            flash_set('ok', 'Envio cancelado e cadencia do lead pausada.');
        } elseif ($acao === 'reagendar') {
            $quando = norm_dtlocal($_POST['quando'] ?? '');
            if ($quando === null || $quando === false) {
                throw new InvalidArgumentException('Informe a nova data e hora.');
            }
            q("UPDATE email_outbox SET scheduled_for = ? WHERE id = ? AND status IN ('aguardando','agendado')",
                [$quando, $id]);
            q('UPDATE lead_cadence SET next_send_at = ?
                WHERE lead_id = (SELECT lead_id FROM email_outbox WHERE id = ?)', [$quando, $id]);
            flash_set('ok', 'Reagendado para ' . fmt_dt($quando) . '.');
        } elseif ($acao === 'editar') {
            $assunto = norm_text($_POST['assunto'] ?? '', 255);
            $corpo = trim(preg_replace('/\r\n|\r/', "\n", (string) ($_POST['corpo'] ?? '')));
            if ($assunto === '' || $corpo === '') {
                throw new InvalidArgumentException('Assunto e corpo nao podem ficar vazios.');
            }
            if (str_contains($assunto . $corpo, '{')) {
                throw new InvalidArgumentException('Sobrou uma chave {...} no texto — substitua antes de salvar.');
            }
            q("UPDATE email_outbox SET subject = ?, body = ? WHERE id = ? AND status IN ('aguardando','agendado')",
                [$assunto, $corpo, $id]);
            flash_set('ok', 'Texto deste envio atualizado.');
        } elseif ($acao === 'enviar_agora') {
            if (!setting_bool('auto_ligada')) {
                throw new InvalidArgumentException(
                    'O motor esta desligado. Ligue em Configuracoes antes de enviar — o interruptor '
                    . 'geral so serve para alguma coisa se nenhum botao passar por cima dele.');
            }
            $antes = row('SELECT status FROM email_outbox WHERE id = ?', [$id]);
            $st = q("UPDATE email_outbox SET status = 'enviando', attempts = attempts + 1
                     WHERE id = ? AND status IN ('aguardando','agendado')", [$id]);
            if ($st->rowCount() !== 1) {
                throw new InvalidArgumentException('Este envio nao esta mais na fila.');
            }
            $out = row('SELECT * FROM email_outbox WHERE id = ?', [$id]);
            $res = cadencia_enviar_linha($out);
            // Adiado por guarda (fora da janela, teto): a linha volta para
            // 'agendado'. Se ela ainda NAO estava aprovada, aprovar por tabela
            // seria um efeito colateral que ninguem pediu.
            if ($res['resultado'] === 'adiado' && ($antes['status'] ?? '') === 'aguardando') {
                q("UPDATE email_outbox SET status = 'aguardando' WHERE id = ? AND status = 'agendado'", [$id]);
            }
            flash_set($res['resultado'] === 'enviado' ? 'ok' : 'aviso',
                'Resultado: ' . $res['resultado'] . ($res['motivo'] !== '' ? ' — ' . $res['motivo'] : ''));
        } elseif ($acao === 'destravar_enviado') {
            // O processo morreu entre o SMTP aceitar e o UPDATE. Quem confere
            // na caixa de Enviados decide; o CRM nao adivinha.
            $out = row('SELECT * FROM email_outbox WHERE id = ?', [$id]);
            if ($out === null || $out['status'] !== 'enviando') {
                throw new InvalidArgumentException('Este envio nao esta travado.');
            }
            $seq = (int) $out['seq'];
            $iid = null;
            try {
                $iid = interaction_add((int) $out['lead_id'], 'email',
                    (CADENCIA_EMAIL_LABELS[$seq] ?? ('E-mail ' . $seq))
                    . ' enviado (confirmado na mao apos travar). Para: ' . $out['to_email'],
                    date('Y-m-d H:i:s'), $userId, $seq);
            } catch (Throwable $e) {
                error_log('destravar: ' . $e->getMessage());
            }
            q("UPDATE email_outbox SET status = 'enviado', sent_at = NOW(), interaction_id = ? WHERE id = ?",
                [$iid, $id]);
            q('UPDATE lead_cadence SET current_seq = GREATEST(current_seq, ?), last_sent_at = NOW() WHERE lead_id = ?',
                [$seq, (int) $out['lead_id']]);
            cadencia_auto_planejar((int) $out['lead_id']);
            flash_set('ok', 'Marcado como enviado e proxima etapa replanejada.');
        } elseif ($acao === 'destravar_fila') {
            $st = q("UPDATE email_outbox SET status = 'agendado', scheduled_for = NOW()
                     WHERE id = ? AND status = 'enviando'", [$id]);
            flash_set($st->rowCount() === 1 ? 'ok' : 'erro', $st->rowCount() === 1
                ? 'Devolvido para a fila — vai sair na proxima batida do cron.'
                : 'Este envio nao esta travado.');
        } elseif ($acao === 'tratar') {
            inbound_tratar($id, $userId);
            flash_set('ok', 'Retorno marcado como tratado.');
        } elseif ($acao === 'tratar_todos') {
            q('UPDATE email_inbound SET handled_at = NOW(), handled_by = ? WHERE handled_at IS NULL', [$userId]);
            flash_set('ok', 'Todos os retornos foram marcados como tratados.');
        } elseif ($acao === 'rodar') {
            // Botao "rodar agora": util no piloto e no aceite; o cron faz o
            // mesmo a cada 10 min. Pega o MESMO lock do cron — sem ele, um
            // clique no meio de uma batida faria dois inbound_processar
            // concorrentes rebobinarem o cursor de UID um do outro.
            if ((int) scalar("SELECT GET_LOCK('m351_cadencia_tick', 0)") !== 1) {
                throw new InvalidArgumentException('O cron esta rodando agora mesmo — espere alguns segundos.');
            }
            try {
                $envio = cadencia_auto_tick();
                $caixa = inbound_processar();
                // Chave PROPRIA, separada da do cron. Se o clique manual
                // gravasse 'cron_ultimo', o painel diria que o cron acabou de
                // rodar mesmo sem cron nenhum configurado — e esse campo existe
                // exatamente para avisar quando o cron morre.
                state_set('tick_manual', date('Y-m-d H:i:s'));
            } finally {
                scalar("SELECT RELEASE_LOCK('m351_cadencia_tick')");
            }
            flash_set('ok', sprintf('Rodou: %d enviado(s), %d adiado(s), %d parado(s), %d falha(s); %d retorno(s) lido(s).',
                $envio['enviados'], $envio['adiados'], $envio['parados'], $envio['falhas'], $caixa['lidas']));
        }
    } catch (InvalidArgumentException $e) {
        flash_set('erro', $e->getMessage());
    } catch (Throwable $e) {
        error_log('envios: ' . $e->getMessage());
        flash_set('erro', 'Nao deu para concluir. As migrations 012 a 014 ja foram aplicadas?');
    }
    redirect('envios.php');
}

$erroMigration = null;
$painel = ['aguardando' => 0, 'hoje' => 0, 'enviados_hoje' => 0, 'falhas' => 0, 'retornos' => 0,
    'teto' => 0, 'ultimo_tick' => '', 'ligada' => false, 'modo' => 'aprovacao'];
$travados = [];
$aguardando = [];
$agendados = [];
$enviados = [];
$problemas = [];
$retornos = [];
try {
    scalar('SELECT COUNT(*) FROM email_outbox');
    $painel = cadencia_painel();
    $aguardando = rows("SELECT o.*, l.company FROM email_outbox o LEFT JOIN leads l ON l.id = o.lead_id
                        WHERE o.status = 'aguardando' ORDER BY o.scheduled_for LIMIT 60");
    $agendados = rows("SELECT o.*, l.company FROM email_outbox o LEFT JOIN leads l ON l.id = o.lead_id
                       WHERE o.status = 'agendado' ORDER BY o.scheduled_for LIMIT 60");
    $enviados = rows("SELECT o.*, l.company FROM email_outbox o LEFT JOIN leads l ON l.id = o.lead_id
                      WHERE o.status = 'enviado' ORDER BY o.sent_at DESC LIMIT 25");
    $problemas = rows("SELECT o.*, l.company FROM email_outbox o LEFT JOIN leads l ON l.id = o.lead_id
                       WHERE o.status IN ('falhou','pulado','cancelado')
                       ORDER BY o.updated_at DESC LIMIT 25");
    // Travado = reservado para envio e nunca concluido: o processo morreu entre
    // o SMTP aceitar e o UPDATE. Fica de fora de tudo se ninguem mostrar.
    $travados = rows("SELECT o.*, l.company FROM email_outbox o LEFT JOIN leads l ON l.id = o.lead_id
                      WHERE o.status = 'enviando' AND o.updated_at < DATE_SUB(NOW(), INTERVAL 30 MINUTE)
                      ORDER BY o.updated_at LIMIT 25");
    $retornos = inbound_pendentes();
} catch (Throwable $e) {
    $erroMigration = 'Cadencia automatica indisponivel — aplique as migrations 012 a 014 em migrate.php.';
}
$editId = (int) ($_GET['editar'] ?? 0);

/** Uma linha da fila, com as acoes. */
function linha_outbox(array $o, bool $editando): void
{
    $seq = (int) $o['seq'];
    ?>
    <tr>
      <td>
        <?php if ($o['lead_id']): ?>
          <a href="lead.php?id=<?= (int) $o['lead_id'] ?>"><?= esc($o['company'] ?? ('#' . $o['lead_id'])) ?></a>
        <?php else: ?><span class="muted">(lead removido)</span><?php endif; ?>
        <div class="muted small"><?= esc($o['to_name']) ?> &lt;<?= esc($o['to_email']) ?>&gt;</div>
      </td>
      <td><span class="badge badge-seq"><?= $seq ?>º</span></td>
      <td class="nowrap"><?= esc(fmt_dt($o['scheduled_for'])) ?></td>
      <td>
        <div class="assunto"><?= esc($o['subject']) ?></div>
        <?php if ($o['skip_reason']): ?><div class="muted small">motivo: <?= esc($o['skip_reason']) ?></div><?php endif; ?>
        <?php if ($o['last_error']): ?><div class="small overdue">erro: <?= esc($o['last_error']) ?></div><?php endif; ?>
      </td>
      <td class="muted small nowrap"><?= esc($o['from_email']) ?></td>
      <td class="acoes-outbox">
        <?php if (in_array($o['status'], ['aguardando', 'agendado'], true)): ?>
          <?php if ($o['status'] === 'aguardando'): ?>
            <form method="post" class="inline-form"><?= csrf_field() ?>
              <input type="hidden" name="acao" value="aprovar">
              <input type="hidden" name="id" value="<?= (int) $o['id'] ?>">
              <button class="btn btn-primary btn-sm" type="submit">Aprovar</button>
            </form>
          <?php endif; ?>
          <a class="btn btn-ghost btn-sm" href="envios.php?editar=<?= (int) $o['id'] ?>#e<?= (int) $o['id'] ?>">Ver texto</a>
          <form method="post" class="inline-form" data-confirm="Enviar este e-mail agora, sem esperar o horario?">
            <?= csrf_field() ?>
            <input type="hidden" name="acao" value="enviar_agora">
            <input type="hidden" name="id" value="<?= (int) $o['id'] ?>">
            <button class="btn btn-ghost btn-sm" type="submit">Enviar agora</button>
          </form>
          <form method="post" class="inline-form" data-confirm="Cancelar o envio e pausar a cadencia deste lead?">
            <?= csrf_field() ?>
            <input type="hidden" name="acao" value="cancelar">
            <input type="hidden" name="id" value="<?= (int) $o['id'] ?>">
            <button class="btn btn-danger btn-sm" type="submit">Cancelar</button>
          </form>
        <?php else: ?>
          <span class="badge"><?= esc(OUTBOX_STATUS_LABELS[$o['status']] ?? $o['status']) ?></span>
        <?php endif; ?>
      </td>
    </tr>
    <?php if ($editando): ?>
      <tr class="row-edit" id="e<?= (int) $o['id'] ?>">
        <td colspan="6">
          <form method="post" class="form-stack">
            <?= csrf_field() ?>
            <input type="hidden" name="acao" value="editar">
            <input type="hidden" name="id" value="<?= (int) $o['id'] ?>">
            <div class="field">
              <label for="assunto<?= (int) $o['id'] ?>">Assunto</label>
              <input id="assunto<?= (int) $o['id'] ?>" name="assunto" type="text" maxlength="255"
                     value="<?= esc($o['subject']) ?>">
            </div>
            <div class="field">
              <label for="corpo<?= (int) $o['id'] ?>">Corpo (texto puro, exatamente como vai sair)</label>
              <textarea id="corpo<?= (int) $o['id'] ?>" name="corpo" rows="16"><?= esc($o['body']) ?></textarea>
            </div>
            <div class="form-actions">
              <button class="btn btn-primary btn-sm" type="submit">Salvar texto</button>
              <a class="btn btn-ghost btn-sm" href="envios.php">Fechar</a>
            </div>
          </form>
          <form method="post" class="inline-form">
            <?= csrf_field() ?>
            <input type="hidden" name="acao" value="reagendar">
            <input type="hidden" name="id" value="<?= (int) $o['id'] ?>">
            <label class="muted small" for="q<?= (int) $o['id'] ?>">Reagendar para</label>
            <input id="q<?= (int) $o['id'] ?>" name="quando" type="datetime-local"
                   value="<?= esc(dtlocal_value($o['scheduled_for'])) ?>">
            <button class="btn btn-ghost btn-sm" type="submit">Reagendar</button>
          </form>
        </td>
      </tr>
    <?php endif;
}

page_header('Envios', 'envios.php', $user);
?>
<div class="page-head">
  <h1 class="page-title">Envios e retornos</h1>
  <form method="post" class="inline-form" data-confirm="Rodar o motor agora (enviar o que venceu e ler as caixas)?">
    <?= csrf_field() ?>
    <input type="hidden" name="acao" value="rodar">
    <button class="btn btn-ghost" type="submit">Rodar agora</button>
  </form>
</div>

<?php if ($erroMigration !== null): ?>
  <div class="flash flash-aviso"><?= esc($erroMigration) ?></div>
<?php else: ?>

<div class="stats stats-4">
  <div class="stat">
    <span class="stat-n"><?= (int) $painel['enviados_hoje'] ?></span>
    <span class="stat-label">Enviados hoje · teto <?= (int) $painel['teto'] ?> por caixa</span>
  </div>
  <div class="stat">
    <span class="stat-n"><?= (int) $painel['hoje'] ?></span>
    <span class="stat-label">Ainda na fila de hoje</span>
  </div>
  <div class="stat<?= $painel['aguardando'] > 0 ? ' stat-alerta' : '' ?>">
    <span class="stat-n"><?= (int) $painel['aguardando'] ?></span>
    <span class="stat-label">Aguardando aprovacao</span>
  </div>
  <div class="stat<?= $painel['retornos'] > 0 ? ' stat-alerta' : '' ?>">
    <span class="stat-n"><?= (int) $painel['retornos'] ?></span>
    <span class="stat-label">Retornos nao tratados</span>
  </div>
</div>

<div class="card cadencia-estado">
  <?php if ($painel['por_caixa']): ?>
    <p style="margin-bottom:8px">Hoje, por caixa:
      <?php foreach ($painel['por_caixa'] as $caixa => $n): ?>
        <span class="badge <?= $n >= (int) $painel['teto'] ? 'badge-dup' : 'badge-seq' ?>"
              title="<?= $n >= (int) $painel['teto'] ? 'no teto: nada mais sai desta caixa hoje' : 'abaixo do teto' ?>">
          <?= esc($caixa) ?>: <?= (int) $n ?>/<?= (int) $painel['teto'] ?>
        </span>
      <?php endforeach; ?>
    </p>
  <?php endif; ?>
  <p>
    Motor <strong><?= $painel['ligada'] ? 'ligado' : 'DESLIGADO' ?></strong>
    · modo <strong><?= esc($painel['modo'] === 'automatico' ? 'automatico' : 'aprovacao') ?></strong>
    <?php if (setting_bool('auto_sandbox')): ?>
      · <span class="badge badge-demo_agendada">SANDBOX: nada sai para lead de verdade</span>
    <?php endif; ?>
    · ultima execucao do cron:
    <strong><?= $painel['ultimo_tick'] !== '' ? esc(fmt_dt($painel['ultimo_tick'])) : 'nunca' ?></strong>
    <?php if ($painel['ultimo_tick'] === ''): ?>
      <span class="badge badge-demo_agendada" title="Sem cron, o motor so anda quando alguem clica em Rodar agora">
        o cron ainda nao foi configurado
      </span>
    <?php elseif (strtotime($painel['ultimo_tick']) < time() - 3600): ?>
      <span class="badge badge-dup">o cron parou de rodar</span>
    <?php endif; ?>
    <?php if ($painel['ultimo_manual'] !== ''): ?>
      · ultimo "Rodar agora": <?= esc(fmt_dt($painel['ultimo_manual'])) ?>
    <?php endif; ?>
    · <a href="settings.php#cadencia-auto">configurar</a>
  </p>
</div>

<?php if ($travados): ?>
  <div class="card">
    <h2 class="card-title">Travados em envio (<?= count($travados) ?>)</h2>
    <p class="muted">Estes e-mails foram reservados para envio e o processo nao terminou —
      quase sempre porque o PHP foi encerrado no meio. <strong>O e-mail pode ter saido.</strong>
      Confira na caixa de <em>Enviados</em> do remetente antes de decidir: reenfileirar um
      e-mail que ja saiu manda a mesma mensagem duas vezes.</p>
    <div class="table-wrap">
      <table class="table">
        <thead><tr><th>Lead</th><th>Etapa</th><th>Desde</th><th>Para</th><th></th></tr></thead>
        <tbody>
          <?php foreach ($travados as $o): ?>
            <tr>
              <td>
                <?php if ($o['lead_id']): ?>
                  <a href="lead.php?id=<?= (int) $o['lead_id'] ?>"><?= esc($o['company'] ?? '') ?></a>
                <?php endif; ?>
              </td>
              <td><span class="badge badge-seq"><?= (int) $o['seq'] ?>º</span></td>
              <td class="nowrap muted small"><?= esc(fmt_dt($o['updated_at'])) ?></td>
              <td class="muted small"><?= esc($o['to_email']) ?></td>
              <td class="acoes-outbox">
                <form method="post" class="inline-form" data-confirm="Confirmo que este e-mail JA SAIU (conferi na caixa de Enviados).">
                  <?= csrf_field() ?>
                  <input type="hidden" name="acao" value="destravar_enviado">
                  <input type="hidden" name="id" value="<?= (int) $o['id'] ?>">
                  <button class="btn btn-ghost btn-sm" type="submit">Ja saiu</button>
                </form>
                <form method="post" class="inline-form" data-confirm="Confirmo que este e-mail NAO saiu. Reenfileirar agora.">
                  <?= csrf_field() ?>
                  <input type="hidden" name="acao" value="destravar_fila">
                  <input type="hidden" name="id" value="<?= (int) $o['id'] ?>">
                  <button class="btn btn-danger btn-sm" type="submit">Nao saiu — reenfileirar</button>
                </form>
              </td>
            </tr>
          <?php endforeach; ?>
        </tbody>
      </table>
    </div>
  </div>
<?php endif; ?>

<?php if ($retornos): ?>
  <div class="card">
    <div class="card-head">
      <h2 class="card-title">Retornos nao tratados (<?= count($retornos) ?>)</h2>
      <form method="post" class="inline-form" data-confirm="Marcar todos os retornos como tratados?">
        <?= csrf_field() ?>
        <input type="hidden" name="acao" value="tratar_todos">
        <button class="btn btn-ghost btn-sm" type="submit">Marcar todos</button>
      </form>
    </div>
    <ul class="retornos">
      <?php foreach ($retornos as $rt): ?>
        <li class="retorno retorno-<?= esc($rt['kind']) ?>">
          <div class="retorno-topo">
            <span class="badge badge-<?= $rt['kind'] === 'humana' ? 'trial' : ($rt['kind'] === 'bounce' ? 'dup' : 'demo_agendada') ?>">
              <?= esc(INBOUND_KIND_LABELS[$rt['kind']] ?? $rt['kind']) ?>
            </span>
            <?php if ($rt['lead_id']): ?>
              <a href="lead.php?id=<?= (int) $rt['lead_id'] ?>"><?= esc($rt['company'] ?? ('#' . $rt['lead_id'])) ?></a>
            <?php else: ?>
              <span class="muted">sem lead vinculado</span>
            <?php endif; ?>
            <span class="muted small"><?= esc($rt['from_email']) ?> · <?= esc(fmt_dt($rt['received_at'])) ?></span>
            <form method="post" class="inline-form"><?= csrf_field() ?>
              <input type="hidden" name="acao" value="tratar">
              <input type="hidden" name="id" value="<?= (int) $rt['id'] ?>">
              <button class="btn btn-ghost btn-sm" type="submit">Tratado</button>
            </form>
          </div>
          <div class="retorno-assunto"><?= esc($rt['subject']) ?></div>
          <?php if ($rt['snippet']): ?><p class="retorno-trecho"><?= esc($rt['snippet']) ?></p><?php endif; ?>
        </li>
      <?php endforeach; ?>
    </ul>
    <p class="muted small">O trecho tem no maximo 400 caracteres — a mensagem inteira esta na sua caixa de e-mail.</p>
  </div>
<?php endif; ?>

<?php if ($aguardando): ?>
  <div class="card">
    <div class="card-head">
      <h2 class="card-title">Aguardando aprovacao (<?= count($aguardando) ?>)</h2>
      <form method="post" class="inline-form" data-confirm="Aprovar todos os e-mails da fila?">
        <?= csrf_field() ?>
        <input type="hidden" name="acao" value="aprovar_todos">
        <button class="btn btn-primary btn-sm" type="submit">Aprovar todos</button>
      </form>
    </div>
    <div class="table-wrap">
      <table class="table table-outbox">
        <thead><tr><th>Lead</th><th>Etapa</th><th>Quando</th><th>Assunto</th><th>De</th><th></th></tr></thead>
        <tbody>
          <?php foreach ($aguardando as $o) {
              linha_outbox($o, $editId === (int) $o['id']);
          } ?>
        </tbody>
      </table>
    </div>
  </div>
<?php endif; ?>

<div class="card">
  <h2 class="card-title">Agendados (<?= count($agendados) ?>)</h2>
  <?php if (!$agendados): ?>
    <p class="muted">Nada na fila. Inicie a cadencia em um lead ou em lote pela tela de Leads.</p>
  <?php else: ?>
    <div class="table-wrap">
      <table class="table table-outbox">
        <thead><tr><th>Lead</th><th>Etapa</th><th>Quando</th><th>Assunto</th><th>De</th><th></th></tr></thead>
        <tbody>
          <?php foreach ($agendados as $o) {
              linha_outbox($o, $editId === (int) $o['id']);
          } ?>
        </tbody>
      </table>
    </div>
  <?php endif; ?>
</div>

<div class="grid-2">
  <div class="card">
    <h2 class="card-title">Ultimos enviados</h2>
    <?php if (!$enviados): ?>
      <p class="muted">Nenhum e-mail saiu ainda.</p>
    <?php else: ?>
      <ul class="plain-list">
        <?php foreach ($enviados as $o): ?>
          <li>
            <span class="muted small"><?= esc(fmt_dt($o['sent_at'])) ?></span>
            <span class="badge badge-seq"><?= (int) $o['seq'] ?>º</span>
            <?php if ($o['lead_id']): ?>
              <a href="lead.php?id=<?= (int) $o['lead_id'] ?>"><?= esc($o['company'] ?? '') ?></a>
            <?php endif; ?>
            <span class="muted small"><?= esc($o['to_email']) ?></span>
          </li>
        <?php endforeach; ?>
      </ul>
    <?php endif; ?>
  </div>

  <div class="card">
    <h2 class="card-title">Nao sairam</h2>
    <?php if (!$problemas): ?>
      <p class="muted">Nenhum envio pulado, cancelado ou com falha.</p>
    <?php else: ?>
      <ul class="plain-list">
        <?php foreach ($problemas as $o): ?>
          <li>
            <span class="badge badge-<?= $o['status'] === 'falhou' ? 'dup' : 'perdido' ?>">
              <?= esc(OUTBOX_STATUS_LABELS[$o['status']] ?? $o['status']) ?>
            </span>
            <?php if ($o['lead_id']): ?>
              <a href="lead.php?id=<?= (int) $o['lead_id'] ?>"><?= esc($o['company'] ?? '') ?></a>
            <?php endif; ?>
            <span class="badge badge-seq"><?= (int) $o['seq'] ?>º</span>
            <span class="muted small"><?= esc($o['skip_reason'] ?: $o['last_error'] ?: '') ?></span>
          </li>
        <?php endforeach; ?>
      </ul>
    <?php endif; ?>
  </div>
</div>

<?php endif; ?>
<?php page_footer(); ?>
