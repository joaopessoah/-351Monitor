<?php
/**
 * Configurações: troca de senha (obrigatória no primeiro acesso) e
 * cadência de e-mail (prazos em dias úteis + os 5 modelos do link "abrir no Outlook").
 */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();

$error = null;

/** A migration 007 já rodou? Sem ela a cadência fica nos defaults e não grava. */
$cadenciaOk = true;
try {
    scalar('SELECT COUNT(*) FROM app_settings');
} catch (Throwable $e) {
    $cadenciaOk = false;
}

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $action = $_POST['action'] ?? 'senha';

    if ($action === 'coluna') {
        try {
            $op = $_POST['op'] ?? '';
            if ($op === 'add') {
                board_column_add((string) ($_POST['nome'] ?? ''), (string) ($_POST['cor'] ?? 'cinza'));
                flash_set('ok', 'Coluna criada.');
            } elseif ($op === 'update') {
                board_column_update((int) ($_POST['id'] ?? 0),
                    (string) ($_POST['nome'] ?? ''), (string) ($_POST['cor'] ?? 'cinza'));
                flash_set('ok', 'Coluna salva.');
            } elseif ($op === 'done') {
                board_column_set_done((int) ($_POST['id'] ?? 0));
                flash_set('ok', 'Coluna de conclusao trocada.');
            } elseif ($op === 'delete') {
                board_column_delete((int) ($_POST['id'] ?? 0), (int) ($_POST['para'] ?? 0) ?: null);
                flash_set('ok', 'Coluna apagada e cards movidos.');
            } elseif ($op === 'reorder') {
                board_columns_reorder(array_filter(explode(',', (string) ($_POST['ordem'] ?? '')), 'strlen'));
                flash_set('ok', 'Ordem das colunas salva.');
            }
            redirect('settings.php#quadro');
        } catch (InvalidArgumentException $e) {
            $error = $e->getMessage();
        } catch (Throwable $e) {
            error_log('settings coluna: ' . $e->getMessage());
            $error = 'Nao deu para salvar. A migration 010 ja foi aplicada no migrate.php?';
        }
    } elseif ($action === 'cadencia_auto') {
        try {
            $hhmm = function (string $campo, string $rotulo): string {
                $v = trim((string) ($_POST[$campo] ?? ''));
                if (!preg_match('/^([01]?\d|2[0-3]):[0-5]\d$/', $v)) {
                    throw new InvalidArgumentException($rotulo . ' inválido (use HH:MM).');
                }
                return $v;
            };
            $inteiro = function (string $campo, int $min, int $max, string $rotulo): int {
                $n = norm_int($_POST[$campo] ?? null, $min, $max);
                if ($n === false || $n === null) {
                    throw new InvalidArgumentException($rotulo . " inválido ($min a $max).");
                }
                return $n;
            };
            // Colar de outra tela costuma trazer espaco fino, quebra de linha ou
            // caractere invisivel junto. Nada disso e culpa de quem digitou.
            $sandboxBruto = preg_replace('/[\s\x{200B}-\x{200D}\x{FEFF}]+/u', '',
                (string) ($_POST['auto_sandbox_para'] ?? ''));
            $sandboxPara = norm_email($sandboxBruto);
            if ($sandboxPara === false) {
                throw new InvalidArgumentException(
                    'E-mail do sandbox inválido: "' . mb_substr($sandboxBruto, 0, 60) . '".');
            }
            $cc = [];
            foreach (explode(',', (string) ($_POST['auto_cc_avisos'] ?? '')) as $e) {
                $n = norm_email($e);
                if ($n === false) {
                    throw new InvalidArgumentException('Cópia dos avisos: "' . trim($e) . '" não é um e-mail válido.');
                }
                if (is_string($n) && $n !== '') {
                    $cc[] = $n;
                }
            }
            $rodapeOptout = trim(preg_replace('/\r\n|\r/', "\n", (string) ($_POST['auto_optout_texto'] ?? '')));
            if ($rodapeOptout !== '') {
                if (!str_contains($rodapeOptout, '{link}')) {
                    throw new InvalidArgumentException(
                        'O rodapé de descadastro precisa conter {link}, senão a pessoa não tem como sair. '
                        . 'Para tirar o rodapé do corpo, deixe o campo inteiro vazio.');
                }
                if (str_contains(str_replace('{link}', '', $rodapeOptout), '{')) {
                    throw new InvalidArgumentException('O rodapé só aceita a chave {link}. Remova as outras.');
                }
            }
            $etapas = [];
            foreach (explode(',', (string) ($_POST['auto_etapas'] ?? '')) as $p) {
                $n = (int) trim($p);
                if ($n >= 1 && $n <= CADENCIA_EMAIL_PASSOS && !in_array($n, $etapas, true)) {
                    $etapas[] = $n;
                }
            }
            if (!$etapas) {
                throw new InvalidArgumentException('Escolha pelo menos uma etapa ativa (ex.: 1,2,5).');
            }
            sort($etapas);
            $inicio = trim((string) ($_POST['auto_inicio'] ?? ''));
            if ($inicio !== '' && !preg_match('/^\d{4}-\d{2}-\d{2}$/', $inicio)) {
                throw new InvalidArgumentException('Data de início do aquecimento inválida.');
            }
            $ligada = !empty($_POST['auto_ligada']) ? 1 : 0;
            $sandbox = !empty($_POST['auto_sandbox']) ? 1 : 0;
            if ($ligada === 1 && $sandbox === 0 && $inicio === '') {
                $inicio = date('Y-m-d'); // liga o aquecimento a partir de hoje
            }

            settings_save([
                'auto_ligada'            => $ligada,
                'auto_sandbox'           => $sandbox,
                'auto_sandbox_para'      => $sandboxPara ?: '',
                'auto_modo'              => in_enum($_POST['auto_modo'] ?? null, ['aprovacao', 'automatico'], 'aprovacao'),
                'auto_janela1_ini'       => $hhmm('auto_janela1_ini', 'Início da 1ª janela'),
                'auto_janela1_fim'       => $hhmm('auto_janela1_fim', 'Fim da 1ª janela'),
                'auto_janela2_ini'       => $hhmm('auto_janela2_ini', 'Início da 2ª janela'),
                'auto_janela2_fim'       => $hhmm('auto_janela2_fim', 'Fim da 2ª janela'),
                'auto_teto_sem1'         => $inteiro('auto_teto_sem1', 1, 500, 'Teto da semana 1'),
                'auto_teto_sem2'         => $inteiro('auto_teto_sem2', 1, 500, 'Teto da semana 2'),
                'auto_teto'              => $inteiro('auto_teto', 1, 500, 'Teto em regime'),
                'auto_max_tick'          => $inteiro('auto_max_tick', 1, 50, 'Máximo por execução'),
                'auto_dominio_dias'      => $inteiro('auto_dominio_dias', 0, 90, 'Intervalo por domínio'),
                'auto_resumo_hora'       => $inteiro('auto_resumo_hora', 0, 23, 'Hora do resumo'),
                'auto_resumo_minuto'     => $inteiro('auto_resumo_minuto', 0, 59, 'Minuto do resumo'),
                'cadencia_ligacao_dias'  => $inteiro('cadencia_ligacao_dias', 1, 30, 'Dias até a ligação'),
                'cadencia_linkedin_dias' => $inteiro('cadencia_linkedin_dias', 1, 30, 'Dias até o LinkedIn'),
                'auto_avisa_email'       => !empty($_POST['auto_avisa_email']) ? 1 : 0,
                'auto_avisa_telegram'    => !empty($_POST['auto_avisa_telegram']) ? 1 : 0,
                'auto_cc_avisos'         => implode(',', $cc),
                'auto_etapas'            => implode(',', $etapas),
                'auto_inicio'            => $inicio,
                'auto_optout_texto'      => $rodapeOptout,
                'auto_html'              => !empty($_POST['auto_html']) ? 1 : 0,
                'auto_assinatura'        => trim(preg_replace('/\r\n|\r/', "\n",
                    (string) ($_POST['auto_assinatura'] ?? ''))),
                'auto_assinatura_html'   => trim(preg_replace('/\r\n|\r/', "\n",
                    (string) ($_POST['auto_assinatura_html'] ?? ''))),
            ]);
            // A mensagem diz o que FOI GRAVADO, não só que gravou. Se o valor
            // na tela divergir daqui, o problema é de leitura, não de escrita —
            // e quem está usando descobre isso sozinho, na hora.
            flash_set('ok', 'Cadência automática salva. O ensaio (sandbox) entrega em: '
                . ($sandboxPara ?: 'a própria caixa remetente') . '.');
            redirect('settings.php#cadencia-auto');
        } catch (InvalidArgumentException $e) {
            $error = $e->getMessage();
        } catch (Throwable $e) {
            error_log('settings cadencia_auto: ' . $e->getMessage());
            $error = 'Não deu para salvar. A migration 007 já foi aplicada no migrate.php?';
        }
    } elseif ($action === 'email_teste') {
        $conta = mail_conta((string) ($_POST['caixa'] ?? ''));
        if ($conta === null) {
            $error = 'Caixa desconhecida.';
        } else {
            // Mesmo destino do ensaio: um lugar só para configurar, e o botão
            // já mostra qual é. A própria caixa é o padrão porque ela sempre
            // existe — o e-mail do usuário logado nem sempre existe como caixa.
            $alvo = norm_email(setting_str('auto_sandbox_para'));
            $para = is_string($alvo) && $alvo !== '' ? $alvo : (string) $conta['email'];
            // O teste leva a assinatura configurada e sai no mesmo formato da
            // cadencia: assim este botao vira a previa real do que o prospect
            // vai receber, em vez de um "oi" sem contexto.
            $corpoTeste = "Se você está lendo isto, o SMTP da caixa " . $conta['email']
                . " funciona a partir da hospedagem.\n\nEnviado em " . date('d/m/Y H:i')
                . " por " . $user['name'] . ".";
            $assinaturaTeste = trim(setting_str('auto_assinatura'));
            if ($assinaturaTeste !== '') {
                $corpoTeste .= "\n\n" . $assinaturaTeste;
            }
            $r = mail_enviar($conta, [
                'de_nome'    => (string) $conta['nome'],
                'de_email'   => (string) $conta['email'],
                'para_email' => $para,
                'assunto'    => 'Teste de envio do +351 CRM',
                'corpo'      => $corpoTeste,
                'html'       => cadencia_corpo_html($corpoTeste),
                'message_id' => mail_message_id(mail_dominio((string) $conta['email'])),
                'auto'       => true,
            ]);
            flash_set($r['ok'] ? 'ok' : 'erro', $r['ok']
                ? 'E-mail de teste enviado para ' . $para . '.'
                : 'Falhou: ' . $r['erro']);
            redirect('settings.php#cadencia-auto');
        }
    } elseif ($action === 'cadencia') {
        try {
            $kv = [];
            for ($n = 1; $n <= CADENCIA_EMAIL_PASSOS; $n++) {
                $dias = norm_int($_POST['dias_' . $n] ?? null, 1, 365);
                if ($dias === false || $dias === null) {
                    throw new InvalidArgumentException(
                        'Prazo depois do ' . mb_strtolower(CADENCIA_EMAIL_LABELS[$n]) . ' inválido (1 a 365).');
                }
                $assunto = norm_text($_POST['assunto_' . $n] ?? '', 255);
                if ($assunto === '') {
                    throw new InvalidArgumentException(
                        'O ' . mb_strtolower(CADENCIA_EMAIL_LABELS[$n]) . ' está sem assunto.');
                }
                $kv['cadencia_email_' . $n]         = $dias;
                $kv['cadencia_email_assunto_' . $n] = $assunto;
                // Guarda com \n; o mailto converte para CRLF ao montar o link.
                $kv['cadencia_email_corpo_' . $n]   = trim(preg_replace('/\r\n|\r/', "\n",
                    (string) ($_POST['corpo_' . $n] ?? '')));
            }
            $hora = norm_int($_POST['hora'] ?? null, 0, 23);
            if ($hora === false || $hora === null) {
                throw new InvalidArgumentException('Hora do vencimento inválida (0 a 23).');
            }
            $kv['cadencia_hora'] = $hora;

            settings_save($kv);
            flash_set('ok', 'Cadência de e-mail salva.');
            redirect('settings.php');
        } catch (InvalidArgumentException $e) {
            $error = $e->getMessage();
        } catch (Throwable $e) {
            error_log('settings cadencia: ' . $e->getMessage());
            $error = 'Não deu para salvar. A migration 007 já foi aplicada no migrate.php?';
        }
    } else {
        $atual = (string) ($_POST['senha_atual'] ?? '');
        $nova = (string) ($_POST['senha_nova'] ?? '');
        $confirma = (string) ($_POST['senha_confirma'] ?? '');

        $fresh = row('SELECT password_hash FROM users WHERE id = ?', [$user['id']]);
        if ($fresh === null || !password_verify($atual, $fresh['password_hash'])) {
            $error = 'Senha atual incorreta.';
        } elseif (mb_strlen($nova) < 12) {
            $error = 'A nova senha precisa ter pelo menos 12 caracteres.';
        } elseif ($nova !== $confirma) {
            $error = 'A confirmação não confere com a nova senha.';
        } elseif ($nova === $atual) {
            $error = 'A nova senha precisa ser diferente da atual.';
        } else {
            q('UPDATE users SET password_hash = ?, must_change_password = 0 WHERE id = ?',
                [password_hash($nova, PASSWORD_DEFAULT), $user['id']]);
            session_regenerate_id(true);
            flash_set('ok', 'Senha alterada.');
            redirect('index.php');
        }
    }
}

page_header('Configurações', 'settings.php', $user);
?>
<h1 class="page-title">Configurações</h1>

<?php if ((int) $user['must_change_password'] === 1): ?>
  <div class="flash flash-aviso">Primeiro acesso: defina uma nova senha para continuar.</div>
<?php endif; ?>

<?php if ($error !== null): ?>
  <div class="flash flash-erro"><?= esc($error) ?></div>
<?php endif; ?>

<?php if (!$cadenciaOk): ?>
  <div class="flash flash-aviso">A migration <strong>007</strong> ainda não foi aplicada: a cadência abaixo mostra
    os valores padrão e não consegue salvar. Rode o <a href="migrate.php">migrate.php</a> primeiro.</div>
<?php endif; ?>

<div class="grid-2">
  <div class="card">
    <h2 class="card-title">Trocar senha</h2>
    <form method="post" class="form-stack">
      <?= csrf_field() ?>
      <input type="hidden" name="action" value="senha">
      <div class="field">
        <label for="senha_atual">Senha atual</label>
        <input id="senha_atual" name="senha_atual" type="password" autocomplete="current-password" required>
      </div>
      <div class="field">
        <label for="senha_nova">Nova senha (mínimo 12 caracteres)</label>
        <input id="senha_nova" name="senha_nova" type="password" autocomplete="new-password" minlength="12" required>
      </div>
      <div class="field">
        <label for="senha_confirma">Confirme a nova senha</label>
        <input id="senha_confirma" name="senha_confirma" type="password" autocomplete="new-password" minlength="12" required>
      </div>
      <button class="btn btn-primary" type="submit">Salvar nova senha</button>
    </form>
  </div>

  <div class="card">
    <h2 class="card-title">Conta e backup</h2>
    <p><strong>Login:</strong> <?= esc($user['email']) ?></p>
    <p class="muted">Backup do banco: o plano da Hostinger mantém backups automáticos (hPanel → Arquivos → Backups).
    Antes de aplicar uma migration nova, exporte o banco pelo phpMyAdmin. O botão
    “Exportar CSV” na tela de Leads serve como cópia operacional rápida.</p>
    <p class="muted">Leads sem avanço devem ser eliminados em até 12 meses (política de privacidade).
    O detalhe do lead não tem mais botão de excluir — a limpeza é feita pelo phpMyAdmin.
    Quem pediu para não ser contactado deve ser <strong>marcado</strong> como “não contactar”, nunca apagado:
    é o registro que garante que ele não volte pela fila nem por uma importação.</p>
  </div>
</div>

<div class="card" id="quadro">
  <h2 class="card-title">Colunas do quadro</h2>
  <?php $bcols = board_columns(); ?>
  <?php if (!$bcols): ?>
    <p class="muted">A migration <strong>010</strong> ainda não foi aplicada — o quadro não existe no banco.
      Rode o <a href="migrate.php">migrate.php</a>.</p>
  <?php else: ?>
    <p class="muted">A coluna marcada como <strong>conclui a tarefa</strong> é a que grava o <code>done_at</code> —
      a mesma conclusão do ✓ no dashboard e do detalhe do lead. Sempre existe exatamente uma.</p>

    <?php foreach ($bcols as $i => $c): ?>
      <div class="bcol-linha">
        <span class="bcol-chip bcol-<?= esc($c['color']) ?>"></span>
        <form method="post" class="inline-form">
          <?= csrf_field() ?>
          <input type="hidden" name="action" value="coluna">
          <input type="hidden" name="op" value="update">
          <input type="hidden" name="id" value="<?= (int) $c['id'] ?>">
          <input name="nome" type="text" maxlength="40" required value="<?= esc($c['name']) ?>"
                 aria-label="Nome da coluna">
          <select name="cor" aria-label="Cor da coluna">
            <?php foreach (BOARD_CORES as $cor): ?>
              <option value="<?= esc($cor) ?>"<?= $cor === $c['color'] ? ' selected' : '' ?>><?= esc(ucfirst($cor)) ?></option>
            <?php endforeach; ?>
          </select>
          <button class="btn btn-ghost btn-sm" type="submit">Salvar</button>
        </form>

        <?php if ((int) $c['is_done'] === 1): ?>
          <span class="badge badge-trial">conclui a tarefa</span>
        <?php else: ?>
          <form method="post" class="inline-form"
                data-confirm="Marcar “<?= esc($c['name']) ?>” como a coluna que conclui? Os cards que estiverem nela passam a contar como concluídos, e os da coluna atual reabrem.">
            <?= csrf_field() ?>
            <input type="hidden" name="action" value="coluna">
            <input type="hidden" name="op" value="done">
            <input type="hidden" name="id" value="<?= (int) $c['id'] ?>">
            <button class="btn btn-ghost btn-sm" type="submit">Tornar a de conclusão</button>
          </form>
        <?php endif; ?>

        <?php if ($i > 0): ?>
          <?php
            $ordem = array_column($bcols, 'id');
            [$ordem[$i - 1], $ordem[$i]] = [$ordem[$i], $ordem[$i - 1]];
          ?>
          <form method="post" class="inline-form">
            <?= csrf_field() ?>
            <input type="hidden" name="action" value="coluna">
            <input type="hidden" name="op" value="reorder">
            <input type="hidden" name="ordem" value="<?= esc(implode(',', $ordem)) ?>">
            <button class="btn btn-ghost btn-sm" type="submit" title="Mover para a esquerda">←</button>
          </form>
        <?php endif; ?>

        <?php if ((int) $c['is_done'] !== 1 && count($bcols) > 1): ?>
          <form method="post" class="inline-form"
                data-confirm="Apagar “<?= esc($c['name']) ?>”? Os cards dela vão para a coluna escolhida.">
            <?= csrf_field() ?>
            <input type="hidden" name="action" value="coluna">
            <input type="hidden" name="op" value="delete">
            <input type="hidden" name="id" value="<?= (int) $c['id'] ?>">
            <select name="para" aria-label="Mover os cards para">
              <?php foreach ($bcols as $d): ?>
                <?php if ((int) $d['id'] !== (int) $c['id']): ?>
                  <option value="<?= (int) $d['id'] ?>"><?= esc($d['name']) ?><?= (int) $d['is_done'] === 1 ? ' (conclui os cards!)' : '' ?></option>
                <?php endif; ?>
              <?php endforeach; ?>
            </select>
            <button class="btn btn-danger btn-sm" type="submit">Apagar</button>
          </form>
        <?php endif; ?>
      </div>
    <?php endforeach; ?>

    <form method="post" class="task-quick">
      <?= csrf_field() ?>
      <input type="hidden" name="action" value="coluna">
      <input type="hidden" name="op" value="add">
      <input name="nome" type="text" placeholder="Nova coluna…" maxlength="40" required>
      <select name="cor" aria-label="Cor da nova coluna">
        <?php foreach (BOARD_CORES as $cor): ?>
          <option value="<?= esc($cor) ?>"><?= esc(ucfirst($cor)) ?></option>
        <?php endforeach; ?>
      </select>
      <button class="btn btn-ghost btn-sm" type="submit">Criar coluna</button>
    </form>
  <?php endif; ?>
</div>

<div class="card" id="cadencia-auto">
  <h2 class="card-title">Cadência automática (envio, validação e retorno)</h2>
  <?php $contas = mail_contas(); ?>
  <?php if (!$contas): ?>
    <div class="flash flash-aviso">Nenhuma caixa configurada. Adicione a chave <code>mail</code> ao
      <code>crm_config.php</code> (modelo em <code>crm/README.md</code>) — sem ela o motor não envia nem lê nada.</div>
  <?php else: ?>
    <div class="table-wrap">
      <table class="table">
        <thead><tr><th>Caixa</th><th>SMTP</th><th>IMAP</th><th>Último UID lido</th><th>Teste avulso</th></tr></thead>
        <tbody>
          <?php foreach ($contas as $endereco => $c): ?>
            <tr>
              <td class="nowrap"><?= esc($c['nome']) ?><div class="muted small"><?= esc($endereco) ?></div></td>
              <td class="muted small"><?= esc($c['smtp_host']) ?>:<?= (int) $c['smtp_porta'] ?> (<?= esc($c['smtp_seg']) ?>)</td>
              <td class="muted small">
                <?= esc($c['imap_host']) ?>:<?= (int) $c['imap_porta'] ?>
                <?= (string) $c['imap_senha'] === '' ? '<span class="badge badge-perdido">sem senha</span>' : '' ?>
              </td>
              <td class="muted small"><?= esc(state_get('imap_' . $endereco) ?: '—') ?></td>
              <td>
                <?php
                  // SEM campo de texto aqui, de proposito. Ter duas caixinhas de
                  // e-mail parecidas na mesma tela, uma que salva e outra que
                  // nao, e um convite a digitar no lugar errado e concluir que o
                  // sistema nao grava. O destino do teste passa a ser o mesmo do
                  // ensaio, e o botao diz qual e antes de voce clicar.
                  $destinoTeste = norm_email(setting_str('auto_sandbox_para'));
                  $destinoTeste = is_string($destinoTeste) && $destinoTeste !== '' ? $destinoTeste : $endereco;
                ?>
                <form method="post" class="inline-form">
                  <?= csrf_field() ?>
                  <input type="hidden" name="action" value="email_teste">
                  <input type="hidden" name="caixa" value="<?= esc($endereco) ?>">
                  <button class="btn btn-ghost btn-sm" type="submit">
                    Enviar teste para <?= esc($destinoTeste) ?>
                  </button>
                </form>
                <p class="muted small" style="margin: 4px 0 0">
                  Para mudar esse destino, use o campo <strong>“E-mail do sandbox”</strong> mais abaixo.
                </p>
              </td>
            </tr>
          <?php endforeach; ?>
        </tbody>
      </table>
    </div>
  <?php endif; ?>

  <form method="post" class="form-stack">
    <?= csrf_field() ?>
    <input type="hidden" name="action" value="cadencia_auto">

    <div class="form-grid">
      <div class="field field-check">
        <input id="auto_ligada" name="auto_ligada" type="checkbox" value="1" <?= setting_bool('auto_ligada') ? 'checked' : '' ?>>
        <label for="auto_ligada"><strong>Motor ligado</strong> — sem isto nada é enviado (a caixa continua sendo lida)</label>
      </div>
      <div class="field field-check">
        <input id="auto_sandbox" name="auto_sandbox" type="checkbox" value="1" <?= setting_bool('auto_sandbox') ? 'checked' : '' ?>>
        <label for="auto_sandbox"><strong>Sandbox</strong> — todo e-mail vai para o endereço abaixo, nunca para o lead</label>
      </div>
      <div class="field">
        <label for="auto_sandbox_para">E-mail do sandbox
          <span class="muted">— enquanto o ensaio estiver ligado, <strong>todo</strong> e-mail da cadência vai para cá</span></label>
        <!-- type="text", nao "email", de proposito: com type="email" o navegador
             recusa o envio do formulario inteiro por conta propria, com um balao
             que pode passar despercebido num formulario longo, e o usuario ve
             "nao salvou" sem nenhuma explicacao. A validacao do servidor logo
             abaixo diz exatamente o que esta errado, em vermelho, no topo. -->
        <input id="auto_sandbox_para" name="auto_sandbox_para" type="text" inputmode="email"
               maxlength="190" autocomplete="off" spellcheck="false"
               placeholder="vazio = vai para a própria caixa remetente"
               value="<?= esc(setting_str('auto_sandbox_para')) ?>">
        <?php
          $alvoSandbox = norm_email(setting_str('auto_sandbox_para'));
          $contasSandbox = mail_contas();
          $alvoSandbox = is_string($alvoSandbox) && $alvoSandbox !== ''
              ? $alvoSandbox
              : (string) (($contasSandbox ? reset($contasSandbox)['email'] : '—'));
        ?>
        <p class="muted small" style="margin: 4px 0 0">
          Hoje o ensaio entrega em: <strong><?= esc($alvoSandbox) ?></strong>.
          Este campo <strong>fica salvo</strong> ao clicar em "Salvar cadência automática".
        </p>
      </div>
      <div class="field">
        <label for="auto_modo">Modo</label>
        <select id="auto_modo" name="auto_modo">
          <option value="aprovacao"<?= setting_str('auto_modo') === 'aprovacao' ? ' selected' : '' ?>>Aprovação — você libera cada e-mail em Envios</option>
          <option value="automatico"<?= setting_str('auto_modo') === 'automatico' ? ' selected' : '' ?>>Automático — sai sozinho no horário</option>
        </select>
      </div>
      <div class="field">
        <label for="auto_etapas">Etapas ativas <span class="muted">(1,2,3,4,5 — use 1,2,5 para os 3 toques do playbook)</span></label>
        <input id="auto_etapas" name="auto_etapas" type="text" maxlength="20" value="<?= esc(setting_str('auto_etapas')) ?>">
      </div>
      <div class="field">
        <label for="auto_inicio">Início do aquecimento <span class="muted">(vazio = ainda na semana 1)</span></label>
        <input id="auto_inicio" name="auto_inicio" type="date" value="<?= esc(setting_str('auto_inicio')) ?>">
      </div>
    </div>

    <h3 class="list-title">Janelas de envio (dias úteis, sem feriado nacional)</h3>
    <div class="form-grid">
      <div class="field">
        <label for="auto_janela1_ini">1ª janela — início</label>
        <input id="auto_janela1_ini" name="auto_janela1_ini" type="time" required value="<?= esc(setting_str('auto_janela1_ini')) ?>">
      </div>
      <div class="field">
        <label for="auto_janela1_fim">1ª janela — fim</label>
        <input id="auto_janela1_fim" name="auto_janela1_fim" type="time" required value="<?= esc(setting_str('auto_janela1_fim')) ?>">
      </div>
      <div class="field">
        <label for="auto_janela2_ini">2ª janela — início</label>
        <input id="auto_janela2_ini" name="auto_janela2_ini" type="time" required value="<?= esc(setting_str('auto_janela2_ini')) ?>">
      </div>
      <div class="field">
        <label for="auto_janela2_fim">2ª janela — fim</label>
        <input id="auto_janela2_fim" name="auto_janela2_fim" type="time" required value="<?= esc(setting_str('auto_janela2_fim')) ?>">
      </div>
    </div>

    <h3 class="list-title">Ritmo</h3>
    <div class="form-grid">
      <div class="field">
        <label for="auto_teto_sem1">Teto diário por caixa — semana 1</label>
        <input id="auto_teto_sem1" name="auto_teto_sem1" type="number" min="1" max="500" required value="<?= setting_int('auto_teto_sem1') ?>">
      </div>
      <div class="field">
        <label for="auto_teto_sem2">Semana 2</label>
        <input id="auto_teto_sem2" name="auto_teto_sem2" type="number" min="1" max="500" required value="<?= setting_int('auto_teto_sem2') ?>">
      </div>
      <div class="field">
        <label for="auto_teto">Em regime</label>
        <input id="auto_teto" name="auto_teto" type="number" min="1" max="500" required value="<?= setting_int('auto_teto') ?>">
      </div>
      <div class="field">
        <label for="auto_max_tick">Máximo por execução do cron</label>
        <input id="auto_max_tick" name="auto_max_tick" type="number" min="1" max="50" required value="<?= setting_int('auto_max_tick') ?>">
      </div>
      <div class="field">
        <label for="auto_dominio_dias">Dias entre e-mails para o mesmo domínio</label>
        <input id="auto_dominio_dias" name="auto_dominio_dias" type="number" min="0" max="90" required value="<?= setting_int('auto_dominio_dias') ?>">
      </div>
      <div class="field">
        <label for="cadencia_ligacao_dias">Ligação: dias úteis após o 1º e-mail</label>
        <input id="cadencia_ligacao_dias" name="cadencia_ligacao_dias" type="number" min="1" max="30" required value="<?= setting_int('cadencia_ligacao_dias') ?>">
      </div>
      <div class="field">
        <label for="cadencia_linkedin_dias">LinkedIn: dias úteis após o 1º e-mail</label>
        <input id="cadencia_linkedin_dias" name="cadencia_linkedin_dias" type="number" min="1" max="30" required value="<?= setting_int('cadencia_linkedin_dias') ?>">
      </div>
    </div>

    <h3 class="list-title">Assinatura</h3>
    <div class="form-grid">
      <div class="field field-span">
        <label for="auto_assinatura">Assinatura em texto <span class="muted">— colada no fim de todo e-mail da cadência, num lugar só em vez de repetida nos cinco modelos</span></label>
        <textarea id="auto_assinatura" name="auto_assinatura" rows="5"
                  placeholder="Bruna Rondelli · COO&#10;+351 Monitor · produtividade em tempo real&#10;+55 11 99220-9235 · bruna@mais351monitor.com.br&#10;mais351monitor.com.br"><?= esc(setting_str('auto_assinatura')) ?></textarea>
        <p class="muted small" style="margin: 4px 0 0">
          Só o bloco de identificação. A despedida (“Abraço,”) fica no fim do <strong>modelo</strong>,
          porque na versão visual este bloco inteiro é trocado pelo cartão — e a despedida sumiria junto.
          Se você já tem a assinatura escrita dentro dos modelos, tire de lá antes de preencher aqui,
          senão ela sai duas vezes.
        </p>
      </div>
      <div class="field field-check field-span">
        <input id="auto_html" name="auto_html" type="checkbox" value="1" <?= setting_bool('auto_html') ? 'checked' : '' ?>>
        <label for="auto_html">Mandar também a <strong>versão visual</strong> (HTML) junto com a de texto</label>
      </div>
      <div class="field field-span">
        <label for="auto_assinatura_html">Assinatura visual <span class="muted">— HTML, usada só quando a caixa acima está marcada <em>e</em> a assinatura em texto está preenchida</span></label>
        <textarea id="auto_assinatura_html" name="auto_assinatura_html" rows="8"
                  style="font-family: var(--font-mono, monospace); font-size: 0.8rem"><?= esc(setting_str('auto_assinatura_html')) ?></textarea>
        <p class="muted small" style="margin: 4px 0 0">
          O e-mail sai com as duas versões na mesma mensagem: quem tem cliente moderno vê esta, quem não tem
          vê a de texto, e ninguém perde conteúdo. O logo aqui é <strong>tipografia, não imagem</strong>, de
          propósito: imagem em e-mail frio costuma ser bloqueada pelo cliente e pesa contra a entrega.
          Depois de salvar, use o botão <strong>“Enviar teste”</strong> lá em cima para ver o resultado real na sua caixa.
        </p>
      </div>
    </div>

    <h3 class="list-title">Rodapé de descadastro</h3>
    <div class="form-grid">
      <div class="field field-span">
        <label for="auto_optout_texto">Texto colado no fim de todo e-mail da cadência
          <span class="muted">— <code>{link}</code> vira o endereço de cancelamento daquele contato</span></label>
        <textarea id="auto_optout_texto" name="auto_optout_texto" rows="3"><?= esc(setting_str('auto_optout_texto')) ?></textarea>
        <p class="muted small" style="margin: 4px 0 0">
          O e-mail é texto puro, então não existe palavra com hiperlink: o endereço aparece inteiro.
          Em compensação, todo e-mail leva o cabeçalho <code>List-Unsubscribe</code>, e é ele que faz o
          Gmail e o Outlook mostrarem o botão "cancelar inscrição" ao lado do remetente.
          Deixando este campo <strong>vazio</strong>, o corpo fica sem link nenhum e sobra só esse botão —
          nesse caso mantenha um "responda SAIR" dentro do próprio modelo.
        </p>
      </div>
    </div>

    <h3 class="list-title">Avisos</h3>
    <div class="form-grid">
      <div class="field field-check">
        <input id="auto_avisa_email" name="auto_avisa_email" type="checkbox" value="1" <?= setting_bool('auto_avisa_email') ? 'checked' : '' ?>>
        <label for="auto_avisa_email">Avisar por e-mail em resposta, devolução e pedido de saída</label>
      </div>
      <div class="field field-check">
        <input id="auto_avisa_telegram" name="auto_avisa_telegram" type="checkbox" value="1" <?= setting_bool('auto_avisa_telegram') ? 'checked' : '' ?>>
        <label for="auto_avisa_telegram">Avisar também no Telegram <span class="muted">(precisa de <code>telegram</code> no crm_config.php)</span></label>
      </div>
      <div class="field">
        <label for="auto_cc_avisos">Cópia dos avisos <span class="muted">(e-mails separados por vírgula)</span></label>
        <input id="auto_cc_avisos" name="auto_cc_avisos" type="text" maxlength="255" value="<?= esc(setting_str('auto_cc_avisos')) ?>">
      </div>
      <div class="field">
        <label for="auto_resumo_hora">Resumo do dia — hora</label>
        <input id="auto_resumo_hora" name="auto_resumo_hora" type="number" min="0" max="23" required value="<?= setting_int('auto_resumo_hora') ?>">
      </div>
      <div class="field">
        <label for="auto_resumo_minuto">Resumo do dia — minuto</label>
        <input id="auto_resumo_minuto" name="auto_resumo_minuto" type="number" min="0" max="59" required value="<?= setting_int('auto_resumo_minuto') ?>">
      </div>
    </div>

    <p class="muted cad-chaves">O cron precisa estar configurado no hPanel:
      <code>php <?= esc(dirname(__DIR__)) ?>/crm/cron/tick.php</code> a cada 10 minutos.
      Última execução: <strong><?= esc(state_get('cron_ultimo') ?: 'nunca') ?></strong>
      <?php $ult = state_get('cron_ultimo_resumo'); ?>
      <?= $ult !== '' ? '(' . esc($ult) . ')' : '' ?>.</p>

    <div class="form-actions">
      <button class="btn btn-primary" type="submit" <?= $cadenciaOk ? '' : 'disabled' ?>>Salvar cadência automática</button>
    </div>
  </form>
</div>

<div class="card">
  <h2 class="card-title">Cadência de e-mail</h2>
  <p class="muted">Ao registrar uma interação de e-mail no lead, a tarefa anterior fecha sozinha e nasce a próxima,
    com vencimento em <strong>dias úteis</strong> (seg-sex, sem feriados). O modelo de cada etapa é o que abre no
    Outlook quando você clica no e-mail do contato.</p>

  <form method="post" class="form-stack">
    <?= csrf_field() ?>
    <input type="hidden" name="action" value="cadencia">

    <div class="field" style="max-width: 260px">
      <label for="hora">Hora do vencimento das tarefas</label>
      <input id="hora" name="hora" type="number" min="0" max="23" required
             value="<?= (int) setting_int('cadencia_hora') ?>">
    </div>

    <?php for ($n = 1; $n <= CADENCIA_EMAIL_PASSOS; $n++): ?>
      <?php $ultimo = $n === CADENCIA_EMAIL_PASSOS; ?>
      <!-- todos abertos de proposito: input required dentro de <details>
           fechado bloqueia o submit sem mostrar mensagem nenhuma -->
      <details class="tpl" open>
        <summary>
          <?= esc(CADENCIA_EMAIL_LABELS[$n]) ?>
          <span class="muted">— depois dele, cobrar em
            <strong><?= (int) setting_int('cadencia_email_' . $n) ?></strong> dias úteis
            <?= $ultimo ? '(retomada)' : '' ?></span>
        </summary>
        <div class="form-grid">
          <div class="field">
            <label for="dias_<?= $n ?>">Cobrar em quantos dias úteis depois deste e-mail</label>
            <input id="dias_<?= $n ?>" name="dias_<?= $n ?>" type="number" min="1" max="365" required
                   value="<?= (int) setting_int('cadencia_email_' . $n) ?>">
          </div>
          <div class="field">
            <label for="assunto_<?= $n ?>">Assunto</label>
            <input id="assunto_<?= $n ?>" name="assunto_<?= $n ?>" type="text" maxlength="255" required
                   value="<?= esc(setting_str('cadencia_email_assunto_' . $n)) ?>">
          </div>
          <div class="field field-span">
            <label for="corpo_<?= $n ?>">Corpo do e-mail <span class="muted">(texto puro — o Outlook não recebe
              formatação por este caminho)</span></label>
            <textarea id="corpo_<?= $n ?>" name="corpo_<?= $n ?>" rows="12"><?= esc(setting_str('cadencia_email_corpo_' . $n)) ?></textarea>
          </div>
        </div>
        <p class="muted">
          <?php if ($ultimo): ?>
            Este é o último e-mail: depois dele a tarefa criada é a de <strong>retomada</strong>
            (“cadência esgotada — retomar contato”), com o prazo longo acima.
          <?php else: ?>
            Depois deste, a tarefa criada é “<?= esc(CADENCIA_EMAIL_LABELS[$n + 1]) ?> — cobrar retorno”.
          <?php endif; ?>
        </p>
      </details>
    <?php endfor; ?>

    <p class="muted cad-chaves">Chaves que você pode usar no assunto e no corpo:
      <?php foreach (CADENCIA_EMAIL_CHAVES as $chave): ?><code><?= esc($chave) ?></code> <?php endforeach; ?>
      — <code>{estacoes}</code> vira “suas” quando o lead não tem o número preenchido.</p>

    <div class="form-actions">
      <button class="btn btn-primary" type="submit" <?= $cadenciaOk ? '' : 'disabled' ?>>Salvar cadência</button>
    </div>
  </form>
</div>
<?php page_footer(); ?>
