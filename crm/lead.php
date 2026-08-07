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
    $cnpj = norm_cnpj($in['cnpj'] ?? '');
    if ($cnpj === false) {
        $errors['cnpj'] = 'CNPJ inválido — confira os dígitos.';
    } else {
        $d['cnpj'] = $cnpj;
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
    $site = norm_url($in['website'] ?? '');
    if ($site === false) {
        $errors['website'] = 'Site inválido — use algo como acme.com.br.';
    } else {
        $d['website'] = $site;
    }
    $li = norm_url($in['linkedin'] ?? '');
    if ($li === false) {
        $errors['linkedin'] = 'LinkedIn inválido — cole a URL do perfil/página.';
    } else {
        $d['linkedin'] = $li;
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
$prefill = null;      // dados da RFB para o resumo do fluxo "começar pelo CNPJ"
$prefillMiss = false; // CNPJ válido, mas não encontrado/consulta indisponível

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $action = $_POST['action'] ?? '';
    try {
        if ($action === 'cnpj_prefill' && $id === 0) {
            // Fluxo recomendado: começa pelo CNPJ e o cadastro vem preenchido.
            $c = norm_cnpj($_POST['cnpj'] ?? '');
            if ($c === null || $c === false) {
                flash_set('erro', 'Informe um CNPJ válido para buscar.');
                redirect('lead.php');
            }
            $prefill = cnpj_lookup($c);
            if ($prefill === null) {
                $prefillMiss = true;
                $old = ['cnpj' => cnpj_format($c)];
            } else {
                // Razão social sempre: o nome_fantasia da RFB é irregular ("MATRIZ", "DIRECAO GERAL"...).
                $old = [
                    'cnpj'    => cnpj_format($c),
                    'company' => $prefill['razao_social'],
                ];
            }
            // sem redirect: cai na renderização do form de criação já preenchido
        } elseif ($action === 'create') {
            [$d, $errors] = lead_form_validate($_POST);
            if (!$errors) {
                $res = lead_create($d, $userId, 'ui');
                if (!empty($d['cnpj'])) {
                    try {
                        lead_enrich_cnpj($res['id']); // melhor esforço: falha não bloqueia a criação
                    } catch (Throwable $e) {
                    }
                }
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
                sync_lead_to_principal($id);
                flash_set('ok', 'Dados salvos.');
                redirect('lead.php?id=' . $id);
            }
            $old = $_POST;
        } elseif ($action === 'contact_add' && $id > 0) {
            $email = norm_email($_POST['c_email'] ?? '');
            if ($email === false) {
                throw new InvalidArgumentException('E-mail do contato inválido.');
            }
            $fone = norm_whatsapp($_POST['c_whatsapp'] ?? '');
            if ($fone === false) {
                throw new InvalidArgumentException('WhatsApp do contato inválido.');
            }
            $li = norm_url($_POST['c_linkedin'] ?? '');
            if ($li === false) {
                throw new InvalidArgumentException('LinkedIn do contato inválido.');
            }
            contact_add($id, [
                'name'         => (string) ($_POST['c_name'] ?? ''),
                'cargo'        => $_POST['c_cargo'] ?? null,
                'email'        => $email,
                'whatsapp'     => $fone,
                'linkedin'     => $li,
                'is_principal' => !empty($_POST['c_principal']),
            ]);
            flash_set('ok', 'Contato adicionado.');
            redirect('lead.php?id=' . $id);
        } elseif ($action === 'contact_delete' && $id > 0) {
            contact_delete((int) ($_POST['contact_id'] ?? 0));
            flash_set('ok', 'Contato removido.');
            redirect('lead.php?id=' . $id);
        } elseif ($action === 'contact_principal' && $id > 0) {
            contact_set_principal((int) ($_POST['contact_id'] ?? 0));
            flash_set('ok', 'Contato principal atualizado.');
            redirect('lead.php?id=' . $id);
        } elseif ($action === 'contact_decisor' && $id > 0) {
            contact_toggle_decisor((int) ($_POST['contact_id'] ?? 0));
            redirect('lead.php?id=' . $id);
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
        } elseif ($action === 'cnpj_lookup' && $id > 0) {
            lead_enrich_cnpj($id);
            flash_set('ok', 'Dados da Receita atualizados.');
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

  <?php if (!$old && !$errors): ?>
    <div class="card cnpj-hero">
      <p class="cnpj-hero-eyebrow"><span class="plus">+</span>Novo lead</p>
      <h2 class="cnpj-hero-title">Comece pelo CNPJ</h2>
      <p class="cnpj-hero-sub">A gente consulta a Receita e o cadastro já vem preenchido — razão social, situação, CNAE e sócios.</p>
      <form method="post" class="cnpj-start" id="cnpj-start-form">
        <?= csrf_field() ?>
        <input type="hidden" name="action" value="cnpj_prefill">
        <div class="cnpj-group">
          <svg class="cnpj-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M3 21h18"/><path d="M5 21V5a1 1 0 0 1 1-1h7a1 1 0 0 1 1 1v16"/>
            <path d="M14 9h4a1 1 0 0 1 1 1v11"/><path d="M8 8h2M8 12h2M8 16h2"/>
          </svg>
          <input id="cnpj-hero-input" class="cnpj-mask" name="cnpj" type="text" inputmode="text"
                 maxlength="18" placeholder="00.000.000/0000-00" autocomplete="off" spellcheck="false" autofocus
                 aria-label="CNPJ" aria-describedby="cnpj-live">
          <button class="btn btn-primary" id="cnpj-hero-btn" type="submit">Buscar na Receita<span class="btn-arrow" aria-hidden="true">→</span></button>
        </div>
        <p class="cnpj-live" id="cnpj-live" data-state="idle">Digite ou cole o CNPJ — aceita o novo formato alfanumérico.</p>
      </form>
      <p class="muted cnpj-hero-skip">Sem o CNPJ agora? Preencha os dados manualmente logo abaixo.</p>
    </div>
  <?php endif; ?>

  <?php if ($prefill !== null): ?>
    <div class="card">
      <h2 class="card-title">Encontrado na Receita
        <?php if ($prefill['situacao'] !== ''): ?>
          <span class="badge <?= stripos($prefill['situacao'], 'ativa') !== false ? 'badge-rf-ok' : 'badge-rf-alerta' ?>"><?= esc($prefill['situacao']) ?></span>
        <?php endif; ?>
      </h2>
      <ul class="rf-list">
        <li><strong><?= esc($prefill['razao_social']) ?></strong><?php if ($prefill['nome_fantasia'] !== ''): ?> <span class="muted">(<?= esc($prefill['nome_fantasia']) ?>)</span><?php endif; ?></li>
        <?php if ($prefill['cnae'] !== ''): ?><li>CNAE: <?= esc($prefill['cnae']) ?></li><?php endif; ?>
        <?php if ($prefill['porte'] !== ''): ?><li>Porte: <?= esc($prefill['porte']) ?></li><?php endif; ?>
        <?php if ($prefill['municipio'] !== ''): ?><li>Local: <?= esc($prefill['municipio']) ?><?= $prefill['uf'] !== '' ? '/' . esc($prefill['uf']) : '' ?></li><?php endif; ?>
        <?php if ($prefill['abertura'] !== ''): ?><li>Abertura: <?= esc(fmt_date($prefill['abertura'])) ?></li><?php endif; ?>
        <?php if ($prefill['socios']): ?><li>Sócios: <?= esc(implode(' · ', $prefill['socios'])) ?></li><?php endif; ?>
      </ul>
      <p class="muted">Confira e complete o cadastro abaixo — ao criar, a consulta completa fica salva no lead.</p>
    </div>
  <?php elseif ($prefillMiss): ?>
    <div class="flash flash-aviso">CNPJ válido, mas ainda não consta na base pública da Receita (empresas novas demoram
      algumas semanas para aparecer) — ou a consulta está fora do ar. Siga com o cadastro manual; o CNPJ já ficou preenchido.</div>
  <?php endif; ?>
<?php else: ?>
  <div class="lead-head">
    <h1><?= esc($lead['company']) ?></h1>
    <?= status_badge($lead['status']) ?>
    <?php if ($lead['duplicate_of_lead_id']): ?><span class="badge badge-dup">Duplicado</span><?php endif; ?>
  </div>
  <p class="muted">Criado em <?= esc(fmt_dt($lead['created_at'])) ?> via <?= esc($lead['created_via']) ?>
    · origem <?= esc(SOURCE_LABELS[$lead['source']] ?? $lead['source']) ?>
    <?php if ($lead['website']): ?> · <a href="<?= esc($lead['website']) ?>" target="_blank" rel="noopener">site ↗</a><?php endif; ?>
    <?php if ($lead['linkedin']): ?> · <a href="<?= esc($lead['linkedin']) ?>" target="_blank" rel="noopener">LinkedIn ↗</a><?php endif; ?>
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
          <label for="cnpj">CNPJ</label>
          <input id="cnpj" name="cnpj" class="cnpj-mask" type="text" maxlength="18" placeholder="00.000.000/0000-00" value="<?= esc($v('cnpj')) ?>">
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
          <label for="website">Site da empresa</label>
          <input id="website" name="website" type="text" maxlength="190" value="<?= esc($v('website')) ?>" placeholder="acme.com.br">
        </div>
        <div class="field">
          <label for="linkedin">LinkedIn da empresa</label>
          <input id="linkedin" name="linkedin" type="text" maxlength="190" value="<?= esc($v('linkedin')) ?>" placeholder="linkedin.com/company/acme">
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
      <h2 class="card-title">Receita Federal</h2>
      <?php if (!$lead['cnpj']): ?>
        <p class="muted">Preencha o CNPJ em Dados e salve para habilitar a consulta.</p>
      <?php else: ?>
        <p>
          <code><?= esc(cnpj_format($lead['cnpj'])) ?></code>
          <?php if ($lead['cnpj_situacao']): ?>
            <span class="badge <?= stripos($lead['cnpj_situacao'], 'ativa') !== false ? 'badge-rf-ok' : 'badge-rf-alerta' ?>"><?= esc($lead['cnpj_situacao']) ?></span>
          <?php endif; ?>
        </p>
        <?php if ($lead['cnpj_checked_at']): $rf = json_decode((string) $lead['cnpj_json'], true) ?: []; ?>
          <ul class="rf-list">
            <li><strong><?= esc($lead['cnpj_razao_social'] ?? '') ?></strong><?php if (!empty($rf['nome_fantasia'])): ?> <span class="muted">(<?= esc($rf['nome_fantasia']) ?>)</span><?php endif; ?></li>
            <?php if (!empty($rf['cnae'])): ?><li>CNAE: <?= esc($rf['cnae']) ?></li><?php endif; ?>
            <?php if (!empty($rf['porte'])): ?><li>Porte: <?= esc($rf['porte']) ?></li><?php endif; ?>
            <?php if (!empty($rf['municipio'])): ?><li>Local: <?= esc($rf['municipio']) ?><?= !empty($rf['uf']) ? '/' . esc($rf['uf']) : '' ?></li><?php endif; ?>
            <?php if (!empty($rf['abertura'])): ?><li>Abertura: <?= esc(fmt_date($rf['abertura'])) ?></li><?php endif; ?>
            <?php if (!empty($rf['capital_social'])): ?><li>Capital social: R$ <?= esc(number_format((float) $rf['capital_social'], 2, ',', '.')) ?></li><?php endif; ?>
            <?php if (!empty($rf['socios'])): ?><li>Sócios: <?= esc(implode(' · ', $rf['socios'])) ?></li><?php endif; ?>
          </ul>
          <p class="muted">Consulta em <?= esc(fmt_dt($lead['cnpj_checked_at'])) ?> — dados abertos da RFB.</p>
        <?php endif; ?>
        <form method="post">
          <?= csrf_field() ?>
          <input type="hidden" name="action" value="cnpj_lookup">
          <button class="btn btn-ghost" type="submit"><?= $lead['cnpj_checked_at'] ? 'Atualizar consulta' : 'Consultar na Receita' ?></button>
        </form>
      <?php endif; ?>
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
  <div class="card">
    <h2 class="card-title">Contatos <span class="muted">(o principal alimenta a lista e o dedupe)</span></h2>
    <?php $contatos = contacts_of($id); ?>
    <?php if ($contatos): ?>
      <div class="table-wrap">
        <table class="table">
          <thead><tr><th>Nome</th><th>Cargo</th><th>E-mail</th><th>WhatsApp</th><th>LinkedIn</th><th></th><th></th></tr></thead>
          <tbody>
            <?php foreach ($contatos as $c): ?>
              <tr>
                <td>
                  <?= esc($c['name']) ?>
                  <?php if ($c['is_principal']): ?><span class="badge badge-trial">Principal</span><?php endif; ?>
                </td>
                <td>
                  <?= esc($c['cargo'] ?: '—') ?>
                  <?php if ($c['is_decisor']): ?><span class="badge badge-decisor">★ Decisor</span><?php endif; ?>
                </td>
                <td><?= esc($c['email'] ?: '—') ?></td>
                <td><?= wa_link($c['whatsapp']) ?></td>
                <td><?php if ($c['linkedin']): ?><a href="<?= esc($c['linkedin']) ?>" target="_blank" rel="noopener">perfil ↗</a><?php else: ?>—<?php endif; ?></td>
                <td>
                  <form method="post" class="inline-form"><?= csrf_field() ?>
                    <input type="hidden" name="action" value="contact_decisor">
                    <input type="hidden" name="contact_id" value="<?= (int) $c['id'] ?>">
                    <button class="btn btn-ghost btn-sm" type="submit"><?= $c['is_decisor'] ? 'Tirar decisor' : 'Marcar decisor' ?></button>
                  </form>
                  <?php if (!$c['is_principal']): ?>
                    <form method="post" class="inline-form"><?= csrf_field() ?>
                      <input type="hidden" name="action" value="contact_principal">
                      <input type="hidden" name="contact_id" value="<?= (int) $c['id'] ?>">
                      <button class="btn btn-ghost btn-sm" type="submit">Tornar principal</button>
                    </form>
                  <?php endif; ?>
                </td>
                <td>
                  <form method="post" class="inline-form" data-confirm="Remover este contato?"><?= csrf_field() ?>
                    <input type="hidden" name="action" value="contact_delete">
                    <input type="hidden" name="contact_id" value="<?= (int) $c['id'] ?>">
                    <button class="btn btn-danger btn-sm" type="submit">Remover</button>
                  </form>
                </td>
              </tr>
            <?php endforeach; ?>
          </tbody>
        </table>
      </div>
    <?php else: ?>
      <p class="muted">Nenhum contato cadastrado ainda.</p>
    <?php endif; ?>
    <form method="post" class="form-stack">
      <?= csrf_field() ?>
      <input type="hidden" name="action" value="contact_add">
      <div class="form-grid">
        <div class="field">
          <label for="c_name">Nome *</label>
          <input id="c_name" name="c_name" type="text" maxlength="120" required>
        </div>
        <div class="field">
          <label for="c_cargo">Cargo <span class="muted">(sócio, CEO, diretor… marca decisor sozinho)</span></label>
          <input id="c_cargo" name="c_cargo" type="text" maxlength="80" placeholder="Ex.: Sócio-administrador">
        </div>
        <div class="field">
          <label for="c_email">E-mail</label>
          <input id="c_email" name="c_email" type="email" maxlength="190">
        </div>
        <div class="field">
          <label for="c_whatsapp">WhatsApp</label>
          <input id="c_whatsapp" name="c_whatsapp" type="tel" maxlength="20" placeholder="(11) 99999-9999">
        </div>
        <div class="field">
          <label for="c_linkedin">LinkedIn</label>
          <input id="c_linkedin" name="c_linkedin" type="text" maxlength="190" placeholder="linkedin.com/in/fulano">
        </div>
        <div class="field field-check">
          <input id="c_principal" name="c_principal" type="checkbox" value="1">
          <label for="c_principal">Tornar principal</label>
        </div>
      </div>
      <div class="form-actions">
        <button class="btn btn-ghost" type="submit">Adicionar contato</button>
      </div>
    </form>
  </div>

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
