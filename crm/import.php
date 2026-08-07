<?php
/**
 * Import de CSV (lista das 50 empresas-alvo e afins).
 * Passo 1: upload → preview com marcação OK/inválida/duplicada (fica na sessão).
 * Passo 2: confirmar → insere as válidas (duplicadas entram flagadas).
 * Aceita ';' ou ',', UTF-8 ou Windows-1252 (Excel BR).
 */

require __DIR__ . '/lib/bootstrap.php';

$user = auth_require();

const IMPORT_COLS = ['empresa', 'contato', 'email', 'whatsapp', 'estacoes', 'origem', 'observacoes', 'cnpj'];

// Modelo de CSV para download
if (isset($_GET['modelo'])) {
    header('Content-Type: text/csv; charset=utf-8');
    header('Content-Disposition: attachment; filename="modelo-import-leads.csv"');
    echo "\xEF\xBB\xBF";
    $out = fopen('php://output', 'w');
    fputcsv($out, IMPORT_COLS, ';');
    fputcsv($out, ['ACME Contabilidade', 'Fulano Silva', 'fulano@acme.com.br', '11999990000', '25', 'lista_50', 'indicação do José', '00.000.000/0001-91'], ';');
    fclose($out);
    exit;
}

/** Lê o CSV e classifica cada linha. */
function parse_import_csv(string $content): array
{
    if (!mb_check_encoding($content, 'UTF-8')) {
        $content = mb_convert_encoding($content, 'UTF-8', 'Windows-1252');
    }
    $content = str_replace("\r\n", "\n", $content);
    $lines = array_values(array_filter(explode("\n", $content), fn ($l) => trim($l) !== ''));
    if (!$lines) {
        return [];
    }
    $delim = substr_count($lines[0], ';') >= substr_count($lines[0], ',') ? ';' : ',';

    $rows = [];
    $seen = []; // dedupe dentro do próprio arquivo
    foreach ($lines as $i => $line) {
        $cells = str_getcsv($line, $delim);
        // Cabeçalho: primeira linha contendo "empresa"
        if ($i === 0 && stripos($line, 'empresa') !== false) {
            continue;
        }
        $cells = array_pad(array_map('trim', $cells), count(IMPORT_COLS), '');
        [$empresa, $contato, $email, $whatsapp, $estacoes, $origem, $obs, $cnpjRaw] = $cells;

        $r = [
            'linha'    => $i + 1,
            'empresa'  => norm_text($empresa, 160),
            'contato'  => norm_text($contato, 120),
            'email'    => null,
            'whatsapp' => null,
            'estacoes' => null,
            'origem'   => 'lista_50',
            'obs'      => norm_text($obs, 10000),
            'cnpj'     => null,
            'status'   => 'ok',
            'motivo'   => '',
        ];

        if (mb_strlen($r['empresa']) < 2) {
            $r['status'] = 'invalida';
            $r['motivo'] = 'empresa vazia';
            $rows[] = $r;
            continue;
        }
        $e = norm_email($email);
        if ($e === false) {
            $r['status'] = 'invalida';
            $r['motivo'] = 'e-mail inválido';
            $rows[] = $r;
            continue;
        }
        $r['email'] = $e;
        $w = norm_whatsapp($whatsapp);
        if ($w === false) {
            $r['status'] = 'invalida';
            $r['motivo'] = 'whatsapp inválido';
            $rows[] = $r;
            continue;
        }
        $r['whatsapp'] = $w;
        $c = norm_cnpj($cnpjRaw);
        if ($c === false) {
            $r['status'] = 'invalida';
            $r['motivo'] = 'CNPJ inválido';
            $rows[] = $r;
            continue;
        }
        $r['cnpj'] = $c;
        $n = norm_int($estacoes, 1, 10000);
        $r['estacoes'] = $n === false ? null : $n;

        $origemNorm = mb_strtolower(str_replace(['ç', 'ã'], ['c', 'a'], trim($origem)));
        $mapa = ['site' => 'site', 'whatsapp' => 'whatsapp', 'email' => 'email', 'e-mail' => 'email',
            'indicacao' => 'indicacao', 'lista_50' => 'lista_50', 'lista 50' => 'lista_50', 'lista' => 'lista_50',
            '' => 'lista_50'];
        $r['origem'] = $mapa[$origemNorm] ?? 'outro';

        // Duplicada no banco ou no próprio arquivo
        $dupKey = ($r['email'] ?? '') . '|' . ($r['whatsapp'] ?? '') . '|' . ($r['cnpj'] ?? '');
        if ($dupKey !== '||' && isset($seen[$dupKey])) {
            $r['status'] = 'duplicada';
            $r['motivo'] = 'repetida no arquivo (linha ' . $seen[$dupKey] . ')';
        } elseif (lead_find_duplicate($r['email'], $r['whatsapp'], $r['cnpj']) !== null) {
            $r['status'] = 'duplicada';
            $r['motivo'] = 'e-mail/fone/CNPJ já existe no CRM';
        }
        if ($dupKey !== '||') {
            $seen[$dupKey] = $r['linha'];
        }
        $rows[] = $r;
    }
    return $rows;
}

$preview = $_SESSION['import_preview'] ?? null;
$reportMsg = null;

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $action = $_POST['action'] ?? '';

    if ($action === 'preview') {
        if (empty($_FILES['csv']['tmp_name']) || !is_uploaded_file($_FILES['csv']['tmp_name'])) {
            flash_set('erro', 'Selecione um arquivo CSV.');
            redirect('import.php');
        }
        if (($_FILES['csv']['size'] ?? 0) > 1024 * 1024) {
            flash_set('erro', 'Arquivo grande demais (máximo 1 MB).');
            redirect('import.php');
        }
        $rows = parse_import_csv((string) file_get_contents($_FILES['csv']['tmp_name']));
        if (!$rows) {
            flash_set('erro', 'Não encontrei linhas válidas no arquivo.');
            redirect('import.php');
        }
        $_SESSION['import_preview'] = $rows;
        redirect('import.php');
    }

    if ($action === 'cancel') {
        unset($_SESSION['import_preview']);
        redirect('import.php');
    }

    if ($action === 'confirm' && is_array($preview)) {
        $criadas = 0;
        $flagadas = 0;
        $ignoradas = 0;
        foreach ($preview as $r) {
            if ($r['status'] === 'invalida') {
                $ignoradas++;
                continue;
            }
            $res = lead_create([
                'company'           => $r['empresa'],
                'cnpj'              => $r['cnpj'] ?? null,
                'contact_name'      => $r['contato'],
                'email'             => $r['email'],
                'whatsapp'          => $r['whatsapp'],
                'estimated_devices' => $r['estacoes'],
                'source'            => $r['origem'],
                'notes'             => $r['obs'] !== '' ? $r['obs'] : null,
            ], (int) $user['id'], 'import');
            $res['duplicate_of_lead_id'] !== null ? $flagadas++ : $criadas++;
        }
        unset($_SESSION['import_preview']);
        flash_set('ok', "Importação concluída: $criadas criada(s), $flagadas duplicada(s) flagada(s), $ignoradas inválida(s) ignorada(s).");
        redirect('leads.php');
    }
}

page_header('Importar', 'import.php', $user);
?>
<h1 class="page-title">Importar leads (CSV)</h1>

<?php if (!$preview): ?>
  <div class="card">
    <p>Colunas esperadas (com cabeçalho): <code>empresa;contato;email;whatsapp;estacoes;origem;observacoes;cnpj</code>
      — <a href="import.php?modelo=1">baixar modelo</a>.</p>
    <p class="muted">Aceita separador <code>;</code> ou <code>,</code> e arquivos salvos pelo Excel (Windows-1252) ou UTF-8.
      Linhas sem e-mail e sem WhatsApp são aceitas (prospecção): a duplicidade fica por conta da empresa.</p>
    <form method="post" enctype="multipart/form-data" class="form-stack">
      <?= csrf_field() ?>
      <input type="hidden" name="action" value="preview">
      <div class="field">
        <label for="csv">Arquivo CSV</label>
        <input id="csv" name="csv" type="file" accept=".csv,text/csv" required>
      </div>
      <button class="btn btn-primary" type="submit">Pré-visualizar</button>
    </form>
  </div>
<?php else: ?>
  <?php
    $nOk = count(array_filter($preview, fn ($r) => $r['status'] === 'ok'));
    $nDup = count(array_filter($preview, fn ($r) => $r['status'] === 'duplicada'));
    $nInv = count(array_filter($preview, fn ($r) => $r['status'] === 'invalida'));
  ?>
  <div class="card">
    <p><strong><?= $nOk ?></strong> ok · <strong class="import-duplicada"><?= $nDup ?></strong> duplicada(s) (serão criadas com flag)
      · <strong class="import-invalida"><?= $nInv ?></strong> inválida(s) (serão ignoradas)</p>
    <div class="form-actions">
      <form method="post" class="inline-form"><?= csrf_field() ?>
        <input type="hidden" name="action" value="confirm">
        <button class="btn btn-primary" type="submit">Confirmar importação</button>
      </form>
      <form method="post" class="inline-form"><?= csrf_field() ?>
        <input type="hidden" name="action" value="cancel">
        <button class="btn btn-ghost" type="submit">Cancelar</button>
      </form>
    </div>
  </div>
  <div class="card table-wrap">
    <table class="table">
      <thead><tr><th>Linha</th><th>Situação</th><th>Empresa</th><th>CNPJ</th><th>Contato</th><th>E-mail</th><th>WhatsApp</th><th>Estações</th><th>Origem</th><th>Motivo</th></tr></thead>
      <tbody>
        <?php foreach ($preview as $r): ?>
          <tr>
            <td><?= (int) $r['linha'] ?></td>
            <td class="import-<?= esc($r['status']) ?>"><?= esc($r['status']) ?></td>
            <td><?= esc($r['empresa']) ?></td>
            <td><?= esc(isset($r['cnpj']) && $r['cnpj'] ? cnpj_format($r['cnpj']) : '—') ?></td>
            <td><?= esc($r['contato']) ?></td>
            <td><?= esc($r['email'] ?? '—') ?></td>
            <td><?= esc($r['whatsapp'] ?? '—') ?></td>
            <td><?= esc((string) ($r['estacoes'] ?? '—')) ?></td>
            <td><?= esc(SOURCE_LABELS[$r['origem']] ?? $r['origem']) ?></td>
            <td class="muted"><?= esc($r['motivo']) ?></td>
          </tr>
        <?php endforeach; ?>
      </tbody>
    </table>
  </div>
<?php endif; ?>
<?php page_footer(); ?>
