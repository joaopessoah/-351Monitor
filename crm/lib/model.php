<?php
/**
 * Regras de negócio — porta única de escrita usada por UI, intake do site, API e import.
 * Toda mudança de status grava lead_status_history; 'perdido' exige motivo;
 * duplicados são marcados (duplicate_of_lead_id), nunca rejeitados.
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

const LEAD_STATUSES     = ['novo', 'contato_feito', 'demo_agendada', 'demo_realizada', 'trial', 'cliente', 'perdido'];
const LEAD_SOURCES      = ['site', 'whatsapp', 'email', 'indicacao', 'lista_50', 'prospeccao', 'outro'];
const LEAD_PLANS        = ['essencial', 'pro', 'indefinido'];
const INTERACTION_TYPES = ['whatsapp', 'email', 'ligacao', 'demo', 'reuniao', 'outro'];
const DEMO_META_MES     = 10;
const TASK_KIND_CADENCIA = 'cadencia_email'; // tarefa gerada pela cadencia de e-mail // meta comercial: 10 demos/mês (docs/CONSIDERACOES-E-DECISOES.md:343)

/* ---------- Leads ---------- */

function lead_find_duplicate(?string $email, ?string $whatsapp, ?string $cnpj = null): ?int
{
    $conds = [];
    $params = [];
    if ($email !== null) {
        $conds[] = 'email = ?';
        $params[] = $email;
    }
    if ($whatsapp !== null) {
        $conds[] = 'whatsapp = ?';
        $params[] = $whatsapp;
    }
    if ($cnpj !== null) {
        $conds[] = 'cnpj = ?';
        $params[] = $cnpj;
    }
    if (!$conds) {
        return null;
    }
    $id = scalar('SELECT id FROM leads WHERE ' . implode(' OR ', $conds) . ' ORDER BY id LIMIT 1', $params);
    if ($id !== null) {
        return (int) $id;
    }
    // Também considera os contatos adicionais (lead_contacts)
    if ($email !== null || $whatsapp !== null) {
        $conds2 = [];
        $params2 = [];
        if ($email !== null) {
            $conds2[] = 'email = ?';
            $params2[] = $email;
        }
        if ($whatsapp !== null) {
            $conds2[] = 'whatsapp = ?';
            $params2[] = $whatsapp;
        }
        $id = scalar('SELECT lead_id FROM lead_contacts WHERE ' . implode(' OR ', $conds2) . ' ORDER BY lead_id LIMIT 1', $params2);
        if ($id !== null) {
            return (int) $id;
        }
    }
    return null;
}

/** @return array{id:int, duplicate_of_lead_id:?int} */
function lead_create(array $d, ?int $userId, string $via): array
{
    $dup = lead_find_duplicate($d['email'] ?? null, $d['whatsapp'] ?? null, $d['cnpj'] ?? null);
    db()->beginTransaction();
    try {
        q('INSERT INTO leads (company, cnpj, contact_name, email, whatsapp, website, linkedin, status, source,
                              utm_source, utm_medium, utm_campaign, estimated_devices, plan_interest,
                              next_action_at, next_action_note, notes, duplicate_of_lead_id, created_via, created_by)
           VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)', [
            $d['company'],
            $d['cnpj'] ?? null,
            $d['contact_name'] ?? '',
            $d['email'] ?? null,
            $d['whatsapp'] ?? null,
            $d['website'] ?? null,
            $d['linkedin'] ?? null,
            'novo',
            in_enum($d['source'] ?? null, LEAD_SOURCES, 'outro'),
            $d['utm_source'] ?? null,
            $d['utm_medium'] ?? null,
            $d['utm_campaign'] ?? null,
            $d['estimated_devices'] ?? null,
            in_enum($d['plan_interest'] ?? null, LEAD_PLANS, 'indefinido'),
            $d['next_action_at'] ?? null,
            $d['next_action_note'] ?? null,
            $d['notes'] ?? null,
            $dup,
            $via,
            $userId,
        ]);
        $id = last_id();
        q('INSERT INTO lead_status_history (lead_id, from_status, to_status, changed_by) VALUES (?, NULL, ?, ?)',
            [$id, 'novo', $userId]);
        db()->commit();
    } catch (Throwable $e) {
        db()->rollBack();
        throw $e;
    }
    // Opt-out e herdado de QUALQUER registro irmao marcado, nao so do que o
    // dedupe elegeu: quem pediu para nao ser contactado nao volta ao radar
    // por uma entrada nova (site, CSV, API ou fila).
    lead_no_contact_herdar($id, $d['email'] ?? null, $d['whatsapp'] ?? null, $d['cnpj'] ?? null);
    // Contato principal vira registro estruturado (com cargo, quando conhecido)
    if (!empty($d['contact_name']) || !empty($d['email']) || !empty($d['whatsapp'])) {
        contact_add($id, [
            'name'         => $d['contact_name'] ?: 'Contato',
            'cargo'        => $d['contact_cargo'] ?? null,
            'email'        => $d['email'] ?? null,
            'whatsapp'     => $d['whatsapp'] ?? null,
            'is_principal' => 1,
        ]);
    }
    return ['id' => $id, 'duplicate_of_lead_id' => $dup];
}

/** Atualiza campos editáveis (whitelist). Valores já normalizados pelo chamador. */
function lead_update(int $id, array $d): void
{
    $allowed = ['company', 'cnpj', 'contact_name', 'email', 'whatsapp', 'website', 'linkedin',
        'source', 'estimated_devices',
        'plan_interest', 'next_action_at', 'next_action_note', 'notes',
        'utm_source', 'utm_medium', 'utm_campaign'];
    $sets = [];
    $params = [];
    foreach ($allowed as $col) {
        if (array_key_exists($col, $d)) {
            $sets[] = "$col = ?";
            $params[] = $d[$col];
        }
    }
    if (!$sets) {
        return;
    }
    $params[] = $id;
    q('UPDATE leads SET ' . implode(', ', $sets) . ' WHERE id = ?', $params);
}

function lead_set_status(int $id, string $to, ?string $lostReason, ?int $userId): void
{
    if (!in_array($to, LEAD_STATUSES, true)) {
        throw new InvalidArgumentException('Status inválido.');
    }
    $lead = row('SELECT status FROM leads WHERE id = ?', [$id]);
    if ($lead === null) {
        throw new InvalidArgumentException('Lead não encontrado.');
    }
    if ($lead['status'] === $to) {
        return;
    }
    $lostReason = $to === 'perdido' ? trim((string) $lostReason) : null;
    if ($to === 'perdido' && ($lostReason === '' || $lostReason === null)) {
        throw new InvalidArgumentException('Informe o motivo da perda.');
    }
    db()->beginTransaction();
    try {
        q('UPDATE leads SET status = ?, lost_reason = ? WHERE id = ?', [$to, $lostReason, $id]);
        q('INSERT INTO lead_status_history (lead_id, from_status, to_status, changed_by) VALUES (?,?,?,?)',
            [$id, $lead['status'], $to, $userId]);
        db()->commit();
    } catch (Throwable $e) {
        db()->rollBack();
        throw $e;
    }
}

/**
 * Eliminação definitiva (LGPD): CASCADE remove interações, tarefas e histórico.
 * Sem botão na tela por decisão de produto — a limpeza dos 12 meses é feita
 * pelo phpMyAdmin. Mantida aqui para não fechar a porta programática.
 */
function lead_delete(int $id): void
{
    q('DELETE FROM leads WHERE id = ?', [$id]);
}


/* ---------- Opt-out: "nao me contacte" (lista de supressao) ---------- */

/** Lead pediu para não ser contactado? Tolerante à migration 009 ausente. */
function lead_no_contact(int $leadId): bool
{
    try {
        return (int) scalar('SELECT no_contact FROM leads WHERE id = ?', [$leadId]) === 1;
    } catch (Throwable $e) {
        return false; // migration 009 ainda não aplicada
    }
}

/**
 * Marca/desmarca o opt-out. Ao marcar, encerra as tarefas abertas do lead:
 * não faz sentido seguir sendo cobrado por quem pediu para sair.
 */
function lead_set_no_contact(int $leadId, bool $on, ?string $motivo = null): void
{
    if (scalar('SELECT id FROM leads WHERE id = ?', [$leadId]) === null) {
        throw new InvalidArgumentException('Lead não encontrado.');
    }
    if ($on) {
        // Zera tambem a proxima acao: senao o lead segue aparecendo nos
        // follow-ups do dashboard depois de pedir para nao ser contactado.
        q('UPDATE leads SET no_contact = 1, no_contact_at = NOW(), no_contact_reason = ?,
                 next_action_at = NULL, next_action_note = NULL WHERE id = ?',
            [norm_text((string) $motivo, 255) ?: null, $leadId]);
        q('UPDATE tasks SET done_at = NOW() WHERE lead_id = ? AND done_at IS NULL', [$leadId]);
    } else {
        q('UPDATE leads SET no_contact = 0, no_contact_at = NULL, no_contact_reason = NULL WHERE id = ?', [$leadId]);
    }
}

/**
 * Marca o lead novo se QUALQUER registro que compartilhe e-mail, WhatsApp ou
 * CNPJ ja estiver em opt-out - inclusive pelos contatos adicionais. Olhar so o
 * duplicado eleito pelo dedupe (o mais antigo) deixava o marcado voltar.
 */
function lead_no_contact_herdar(int $novoId, ?string $email, ?string $whatsapp, ?string $cnpj): void
{
    $conds = [];
    $lc = [];
    $ids = [];
    if ($email !== null && $email !== '') {
        $conds[] = 'l.email = ?';
        $lc[] = 'lc.email = ?';
        $ids[] = $email;
    }
    if ($whatsapp !== null && $whatsapp !== '') {
        $conds[] = 'l.whatsapp = ?';
        $lc[] = 'lc.whatsapp = ?';
        $ids[] = $whatsapp;
    }
    if ($cnpj !== null && $cnpj !== '') {
        $conds[] = 'l.cnpj = ?';
        $ids[] = $cnpj;
    }
    if (!$conds) {
        return;
    }
    $sql = 'SELECT l.no_contact_at, l.no_contact_reason FROM leads l
            WHERE l.no_contact = 1 AND l.id <> ? AND ((' . implode(' OR ', $conds) . ')';
    $params = array_merge([$novoId], $ids);
    if ($lc) {
        $sql .= ' OR EXISTS (SELECT 1 FROM lead_contacts lc WHERE lc.lead_id = l.id
                             AND (' . implode(' OR ', $lc) . '))';
        // os parametros de lead_contacts sao os mesmos, na mesma ordem
        foreach ($lc as $i => $_) {
            $params[] = $ids[$i];
        }
    }
    $sql .= ') ORDER BY l.no_contact_at LIMIT 1';
    try {
        $p = row($sql, $params);
        if ($p !== null) {
            q('UPDATE leads SET no_contact = 1, no_contact_at = ?, no_contact_reason = ? WHERE id = ?',
                [$p['no_contact_at'], $p['no_contact_reason'], $novoId]);
        }
    } catch (Throwable $e) {
        // migration 009 ainda nao aplicada
    }
}

/**
 * Consulta o CNPJ do lead nos dados públicos da Receita e grava o snapshot.
 * @return array os dados normalizados (shape do cnpj_lookup)
 */
function lead_enrich_cnpj(int $id): array
{
    $lead = row('SELECT id, cnpj FROM leads WHERE id = ?', [$id]);
    if ($lead === null) {
        throw new InvalidArgumentException('Lead não encontrado.');
    }
    if (!$lead['cnpj']) {
        throw new InvalidArgumentException('Preencha e salve o CNPJ antes de consultar.');
    }
    $data = cnpj_lookup($lead['cnpj']);
    if ($data === null) {
        throw new InvalidArgumentException('Consulta indisponível agora ou CNPJ não encontrado na base da Receita.');
    }
    q('UPDATE leads SET cnpj_razao_social = ?, cnpj_situacao = ?, cnpj_json = ?, cnpj_checked_at = NOW() WHERE id = ?', [
        mb_substr($data['razao_social'], 0, 160),
        mb_substr($data['situacao'], 0, 40),
        json_encode($data, JSON_UNESCAPED_UNICODE),
        $id,
    ]);
    return $data;
}

function leads_search(array $f, int $page = 1, int $perPage = 25): array
{
    $where = [];
    $params = [];
    if (!empty($f['status']) && in_array($f['status'], LEAD_STATUSES, true)) {
        $where[] = 'l.status = ?';
        $params[] = $f['status'];
    }
    if (!empty($f['source']) && in_array($f['source'], LEAD_SOURCES, true)) {
        $where[] = 'l.source = ?';
        $params[] = $f['source'];
    }
    if (!empty($f['q'])) {
        $where[] = '(l.company LIKE ? OR l.contact_name LIKE ? OR l.email LIKE ? OR l.whatsapp LIKE ? OR l.cnpj LIKE ? OR l.cnpj_razao_social LIKE ?)';
        $like = '%' . $f['q'] . '%';
        array_push($params, $like, $like, $like, $like, $like, $like);
    }
    if (!empty($f['so_vencidos'])) {
        $where[] = "l.next_action_at IS NOT NULL AND l.next_action_at <= NOW() AND l.status NOT IN ('cliente','perdido')";
    }
    if (!empty($f['so_decisor'])) {
        $where[] = 'EXISTS (SELECT 1 FROM lead_contacts lc WHERE lc.lead_id = l.id AND lc.is_decisor = 1)';
    }
    $sqlWhere = $where ? 'WHERE ' . implode(' AND ', $where) : '';
    $total = (int) scalar("SELECT COUNT(*) FROM leads l $sqlWhere", $params);
    $page = max(1, $page);
    $offset = ($page - 1) * $perPage; // ints — interpolação segura
    $items = rows("SELECT l.* FROM leads l $sqlWhere ORDER BY l.updated_at DESC LIMIT $perPage OFFSET $offset", $params);
    return ['items' => $items, 'total' => $total, 'page' => $page, 'per_page' => $perPage];
}

/** Timeline mesclada (interações + mudanças de status), mais recente primeiro. */
function lead_timeline(int $leadId): array
{
    $events = [];
    $ints = rows('SELECT i.*, u.name AS user_name FROM interactions i
                  LEFT JOIN users u ON u.id = i.user_id
                  WHERE i.lead_id = ?', [$leadId]);
    foreach ($ints as $i) {
        $events[] = ['kind' => 'interacao', 'at' => $i['occurred_at'], 'data' => $i];
    }
    $hists = rows('SELECT h.*, u.name AS user_name FROM lead_status_history h
                   LEFT JOIN users u ON u.id = h.changed_by
                   WHERE h.lead_id = ?', [$leadId]);
    foreach ($hists as $h) {
        $events[] = ['kind' => 'status', 'at' => $h['changed_at'], 'data' => $h];
    }
    usort($events, fn ($a, $b) => strcmp($b['at'], $a['at']));
    return $events;
}

/* ---------- Interações e tarefas ---------- */

function interaction_add(int $leadId, string $type, string $summary, ?string $occurredAt, ?int $userId, ?int $emailSeq = null): int
{
    if (!in_array($type, INTERACTION_TYPES, true)) {
        throw new InvalidArgumentException('Tipo de interação inválido.');
    }
    $summary = trim($summary);
    if ($summary === '') {
        throw new InvalidArgumentException('Descreva a interação.');
    }
    if (scalar('SELECT id FROM leads WHERE id = ?', [$leadId]) === null) {
        throw new InvalidArgumentException('Lead não encontrado.');
    }
    // A etapa so existe para e-mail (1º ao 5º da cadencia).
    if ($type !== 'email') {
        $emailSeq = null;
    } elseif ($emailSeq !== null && ($emailSeq < 1 || $emailSeq > CADENCIA_EMAIL_PASSOS)) {
        throw new InvalidArgumentException('Etapa do e-mail inválida (1 a ' . CADENCIA_EMAIL_PASSOS . ').');
    }
    q('INSERT INTO interactions (lead_id, user_id, type, email_seq, summary, occurred_at) VALUES (?,?,?,?,?,?)',
        [$leadId, $userId, $type, $emailSeq, $summary, $occurredAt ?: date('Y-m-d H:i:s')]);
    return last_id();
}

function task_add(?int $leadId, string $title, string $dueAt, ?int $assignedTo, ?int $createdBy, string $kind = 'manual'): int
{
    $title = trim($title);
    if ($title === '') {
        throw new InvalidArgumentException('Dê um título à tarefa.');
    }
    if ($leadId !== null && scalar('SELECT id FROM leads WHERE id = ?', [$leadId]) === null) {
        throw new InvalidArgumentException('Lead não encontrado.');
    }
    if ($leadId !== null && lead_no_contact($leadId)) {
        throw new InvalidArgumentException('Lead marcado como “não contactar” — não dá para abrir tarefa.');
    }
    // Sem a migration 010 as colunas do quadro nao existem: cai no INSERT
    // antigo para nao derrubar dashboard, lead e cadencia no meio do deploy.
    $col = board_column_entrada();
    if ($col === null) {
        q('INSERT INTO tasks (lead_id, title, kind, due_at, assigned_to, created_by) VALUES (?,?,?,?,?,?)',
            [$leadId, $title, $kind, $dueAt, $assignedTo, $createdBy]);
        return last_id();
    }
    // Toda tarefa nasce na coluna de entrada, no topo da pilha. Em transacao
    // para duas criacoes simultaneas nao empatarem em sort_order = 1.
    $colId = (int) $col['id'];
    db()->beginTransaction();
    try {
        q('UPDATE tasks SET sort_order = sort_order + 1 WHERE column_id = ?', [$colId]);
        q('INSERT INTO tasks (lead_id, title, kind, column_id, sort_order, due_at, assigned_to, created_by)
           VALUES (?,?,?,?,1,?,?,?)',
            [$leadId, $title, $kind, $colId, $dueAt, $assignedTo, $createdBy]);
        $id = last_id();
        db()->commit();
    } catch (Throwable $e) {
        db()->rollBack();
        throw $e;
    }
    return $id;
}

function task_done(int $id): void
{
    q('UPDATE tasks SET done_at = NOW() WHERE id = ? AND done_at IS NULL', [$id]);
    // O botao de concluir do dashboard e do lead tambem move o card, senao ele
    // fica aberto no quadro por 30 dias e depois some sem nunca chegar em Feito.
    $done = board_column_done();
    if ($done !== null) {
        q('UPDATE tasks SET column_id = ? WHERE id = ?', [(int) $done['id'], $id]);
    }
}
/** Tarefas abertas: 'hoje', 'atrasadas' ou 'abertas' (todas). */
function tasks_lista(string $which): array
{
    $base = 'SELECT t.*, l.company FROM tasks t LEFT JOIN leads l ON l.id = t.lead_id WHERE t.done_at IS NULL';
    return match ($which) {
        'hoje'      => rows("$base AND DATE(t.due_at) = CURDATE() ORDER BY t.due_at"),
        'atrasadas' => rows("$base AND t.due_at < CURDATE() ORDER BY t.due_at"),
        default     => rows("$base ORDER BY t.due_at"),
    };
}


/* ---------- Cadencia de e-mail ---------- */

/**
 * Soma N dias uteis (seg-sex) e devolve 'Y-m-d'. O resultado nunca cai em
 * fim de semana, mesmo com $days = 0. Feriados nao sao considerados.
 */
function business_days_add(string $from, int $days): string
{
    $d = new DateTimeImmutable($from);
    for ($n = max(0, $days); $n > 0; $n--) {
        do {
            $d = $d->modify('+1 day');
        } while ((int) $d->format('N') > 5);
    }
    while ((int) $d->format('N') > 5) {
        $d = $d->modify('+1 day');
    }
    return $d->format('Y-m-d');
}

/**
 * Vencimento: N dias uteis a partir de $de (default hoje), na hora configurada.
 * Registrar hoje um e-mail enviado na semana passada tem que contar da data do
 * e-mail; se isso ja venceu, o piso e hoje para a tarefa nao nascer no passado.
 */
function cadencia_due_at(int $businessDays, ?string $de = null): string
{
    $hora = max(0, min(23, setting_int('cadencia_hora')));
    $base = $de !== null && $de !== '' ? date('Y-m-d', strtotime($de)) : date('Y-m-d');
    $dia = business_days_add($base, $businessDays);
    if ($dia < date('Y-m-d')) {
        $dia = business_days_add(date('Y-m-d'), 0);
    }
    return $dia . sprintf(' %02d:00:00', $hora);
}

/** Maior etapa de e-mail ja registrada no lead (0 = nenhum). */
function cadencia_email_ultimo(int $leadId): int
{
    try {
        return (int) scalar(
            "SELECT COALESCE(MAX(email_seq), 0) FROM interactions WHERE lead_id = ? AND type = 'email'",
            [$leadId]
        );
    } catch (Throwable $e) {
        return 0; // migration 007 ainda não aplicada
    }
}

/** Etapa sugerida no formulario: a proxima da sequencia, travada no 5o. */
function cadencia_email_proximo(int $leadId): int
{
    return min(CADENCIA_EMAIL_PASSOS, cadencia_email_ultimo($leadId) + 1);
}

/**
 * Os 5 e-mails ja sairam? Nesse caso nao ha 'proximo' modelo para o mailto -
 * seguir sugerindo o 5o faria o link abrir a despedida para sempre.
 */
function cadencia_email_encerrada(int $leadId): bool
{
    return cadencia_email_ultimo($leadId) >= CADENCIA_EMAIL_PASSOS;
}

/**
 * Fecha a tarefa de cadencia aberta do lead e agenda a proxima.
 * @return string|null vencimento da tarefa criada; null se o lead esta em opt-out
 */
function cadencia_email_agendar(int $leadId, int $seq, ?int $userId, ?string $ocorridaEm = null): ?string
{
    $seq = max(1, min(CADENCIA_EMAIL_PASSOS, $seq));
    // Uma tarefa de cadência aberta por lead: a anterior fecha sozinha.
    q('UPDATE tasks SET done_at = NOW() WHERE lead_id = ? AND kind = ? AND done_at IS NULL',
        [$leadId, TASK_KIND_CADENCIA]);
    if (lead_no_contact($leadId)) {
        return null;
    }
    $titulo = $seq >= CADENCIA_EMAIL_PASSOS
        ? 'Cadência esgotada — retomar contato'
        : (CADENCIA_EMAIL_LABELS[$seq + 1] ?? 'Próximo e-mail') . ' — cobrar retorno';
    $due = cadencia_due_at(setting_int('cadencia_email_' . $seq), $ocorridaEm);
    task_add($leadId, $titulo, $due, $userId, $userId, TASK_KIND_CADENCIA);
    return $due;
}

/**
 * Modelo do No e-mail com as chaves ja substituidas, pronto para o mailto.
 * @return array{assunto: string, corpo: string}
 */
function cadencia_email_modelo(int $seq, array $lead, ?array $contato, ?string $meuNome): array
{
    $seq = max(1, min(CADENCIA_EMAIL_PASSOS, $seq));
    $empresa = (string) ($lead['company'] ?? '');
    $nome = trim((string) ($contato['name'] ?? $lead['contact_name'] ?? ''));
    $partes = $nome !== '' ? preg_split('/\s+/', $nome) : [];
    $vars = [
        '{empresa}'       => $empresa,
        '{contato}'       => $nome !== '' ? $nome : $empresa,
        '{primeiro_nome}' => $partes ? $partes[0] : $empresa,
        '{cargo}'         => (string) ($contato['cargo'] ?? ''),
        '{estacoes}'      => isset($lead['estimated_devices']) && $lead['estimated_devices'] !== null
            ? (string) (int) $lead['estimated_devices'] : 'suas',
        '{meu_nome}'      => (string) ($meuNome ?? ''),
    ];
    return [
        'assunto' => strtr(setting_str('cadencia_email_assunto_' . $seq), $vars),
        'corpo'   => strtr(setting_str('cadencia_email_corpo_' . $seq), $vars),
    ];
}

/* ---------- Quadro de tarefas (board) ---------- */

/** Cores aceitas nas colunas do quadro (viram a classe .bcol-<cor>). */
const BOARD_CORES = ['cinza', 'azul', 'verde', 'laranja', 'vermelho', 'roxo'];

/** Colunas do quadro, na ordem. Vazio quando a migration 010 nao rodou. */
function board_columns(bool $refresh = false): array
{
    static $cache = null;
    if ($refresh) {
        $cache = null;
    }
    if ($cache !== null) {
        return $cache;
    }
    try {
        $cache = rows('SELECT * FROM board_columns ORDER BY sort_order, id');
    } catch (Throwable $e) {
        $cache = []; // migration 010 ainda nao aplicada
    }
    return $cache;
}

/** A coluna que significa "concluido". Null se ninguem marcou nenhuma. */
function board_column_done(): ?array
{
    foreach (board_columns() as $c) {
        if ((int) $c['is_done'] === 1) {
            return $c;
        }
    }
    return null;
}

/** Primeira coluna nao-concluida: onde nasce tarefa criada fora do quadro. */
function board_column_entrada(): ?array
{
    foreach (board_columns() as $c) {
        if ((int) $c['is_done'] !== 1) {
            return $c;
        }
    }
    return board_columns()[0] ?? null;
}

/**
 * Cards do quadro agrupados por coluna.
 *
 * @param array $f mine (int user), label (nao usado na etapa 1),
 *                 cadencia (bool: inclui as tarefas automaticas da cadencia)
 * @return array<int, array> column_id => lista de tarefas
 */
function board_cards(array $f = []): array
{
    $cols = board_columns();
    if (!$cols) {
        return [];
    }
    // A coluna de conclusao mostra so o que fechou nos ultimos 30 dias: senao
    // ela cresce para sempre e o quadro fica impossivel de ler.
    //
    // A ordem dentro da coluna e pelo prazo (due_at) crescente: o que vence
    // primeiro aparece no topo. sort_order fica so como desempate entre cards
    // do mesmo prazo — por isso arrastar para reordenar DENTRO da coluna nao
    // muda mais a posicao na tela (mover ENTRE colunas continua valendo).
    $sql = 'SELECT t.*, l.company, u.name AS assignee_name
            FROM tasks t
            LEFT JOIN leads l ON l.id = t.lead_id
            LEFT JOIN users u ON u.id = t.assigned_to
            WHERE t.column_id IS NOT NULL
              AND (t.done_at IS NULL OR t.done_at >= DATE_SUB(NOW(), INTERVAL 30 DAY))
            ORDER BY t.due_at, t.sort_order, t.id';
    $out = array_fill_keys(array_column($cols, 'id'), []);
    foreach (rows($sql) as $t) {
        $cid = (int) $t['column_id'];
        if (!array_key_exists($cid, $out)) {
            continue;
        }
        // Os filtros nao removem o card da lista: marcam. A tela renderiza o
        // oculto como placeholder invisivel para o navegador conseguir mandar
        // a ORDEM COMPLETA da coluna ao arrastar — senao a renumeracao 1..N
        // atropelaria os cards que o filtro escondeu.
        $oculto = false;
        if (empty($f['cadencia']) && $t['kind'] === TASK_KIND_CADENCIA) {
            $oculto = true;
        }
        if (!empty($f['mine']) && (int) $t['assigned_to'] !== (int) $f['mine']) {
            $oculto = true;
        }
        $t['_oculto'] = $oculto;
        $out[$cid][] = $t;
    }
    return $out;
}
/**
 * Move o card para a coluna e reordena a coluna inteira.
 *
 * Recebe a ordem completa dos ids da coluna de destino e renumera de 1 a N
 * numa transacao — mais simples e sem deriva de arredondamento do que calcular
 * uma posicao fracionaria entre dois vizinhos.
 *
 * Soltar na coluna de conclusao fecha a tarefa (mesmo done_at do botao do
 * dashboard); tirar de la reabre.
 *
 * @param int[] $ordem ids das tarefas na coluna de destino, de cima para baixo
 */
function board_move(int $taskId, int $columnId, array $ordem): void
{
    $t = row('SELECT id, lead_id, done_at, column_id FROM tasks WHERE id = ?', [$taskId]);
    if ($t === null) {
        throw new InvalidArgumentException('Tarefa nao encontrada.');
    }
    $col = null;
    foreach (board_columns() as $c) {
        if ((int) $c['id'] === $columnId) {
            $col = $c;
        }
    }
    if ($col === null) {
        throw new InvalidArgumentException('Coluna nao encontrada.');
    }
    if ($t['lead_id'] !== null && lead_no_contact((int) $t['lead_id']) && (int) $col['is_done'] !== 1) {
        throw new InvalidArgumentException('Lead marcado como "nao contactar" — a tarefa so pode ser concluida.');
    }
    $mudouColuna = (int) $t['column_id'] !== $columnId;

    db()->beginTransaction();
    try {
        // FOR UPDATE dentro da transacao: dois arrastos simultaneos na mesma
        // coluna serializam em vez de gerar sort_order duplicado.
        $atual = array_map('intval', array_column(
            rows('SELECT id FROM tasks WHERE column_id = ? ORDER BY sort_order, id FOR UPDATE', [$columnId]),
            'id'
        ));
        $validos = $atual;
        $validos[] = $taskId;
        $ordem = array_values(array_unique(array_filter(
            array_map('intval', $ordem),
            fn ($id) => in_array($id, $validos, true)
        )));
        if (!in_array($taskId, $ordem, true)) {
            $ordem[] = $taskId;
        }
        // Quem nao veio na lista (o cliente nao podia ver) mantem o lugar.
        $faltando = array_values(array_diff($atual, $ordem));
        foreach ($faltando as $id) {
            $ordem[] = $id;
        }

        q('UPDATE tasks SET column_id = ? WHERE id = ?', [$columnId, $taskId]);
        foreach ($ordem as $i => $id) {
            q('UPDATE tasks SET sort_order = ? WHERE id = ?', [$i + 1, $id]);
        }
        if ((int) $col['is_done'] === 1) {
            q('UPDATE tasks SET done_at = NOW() WHERE id = ? AND done_at IS NULL', [$taskId]);
        } elseif ($mudouColuna) {
            // So reabre quando o card realmente trocou de coluna: reordenar
            // dentro da propria coluna nao pode apagar a conclusao.
            q('UPDATE tasks SET done_at = NULL WHERE id = ?', [$taskId]);
        }
        db()->commit();
    } catch (Throwable $e) {
        db()->rollBack();
        throw $e;
    }
}
/** Campos editaveis do card. Valores ja normalizados pelo chamador. */
function task_update(int $id, array $d): void
{
    if (scalar('SELECT id FROM tasks WHERE id = ?', [$id]) === null) {
        throw new InvalidArgumentException('Tarefa nao encontrada.');
    }
    if (array_key_exists('title', $d)) {
        $d['title'] = norm_text((string) $d['title'], 200);
        if ($d['title'] === '') {
            throw new InvalidArgumentException('De um titulo a tarefa.');
        }
    }
    $allowed = ['title', 'description', 'due_at', 'assigned_to'];
    $sets = [];
    $params = [];
    foreach ($allowed as $col) {
        if (array_key_exists($col, $d)) {
            $sets[] = "$col = ?";
            $params[] = $d[$col];
        }
    }
    if (!$sets) {
        return;
    }
    $params[] = $id;
    q('UPDATE tasks SET ' . implode(', ', $sets) . ' WHERE id = ?', $params);
}

/** Remove a tarefa. Card do quadro tem exclusao — diferente de lead. */
function task_delete(int $id): void
{
    q('DELETE FROM tasks WHERE id = ?', [$id]);
}

/* ---------- Colunas do quadro (configuraveis em settings.php) ---------- */

function board_column_add(string $name, string $cor): int
{
    $name = norm_text($name, 40);
    if ($name === '') {
        throw new InvalidArgumentException('De um nome a coluna.');
    }
    if (count(board_columns()) >= 12) {
        throw new InvalidArgumentException('Limite de 12 colunas — o quadro fica ilegivel com mais.');
    }
    $fim = (int) scalar('SELECT COALESCE(MAX(sort_order), 0) + 1 FROM board_columns');
    q('INSERT INTO board_columns (name, sort_order, is_done, color) VALUES (?,?,0,?)',
        [$name, $fim, in_array($cor, BOARD_CORES, true) ? $cor : 'cinza']);
    board_columns(true);
    return last_id();
}

function board_column_update(int $id, string $name, string $cor): void
{
    $name = norm_text($name, 40);
    if ($name === '') {
        throw new InvalidArgumentException('De um nome a coluna.');
    }
    q('UPDATE board_columns SET name = ?, color = ? WHERE id = ?',
        [$name, in_array($cor, BOARD_CORES, true) ? $cor : 'cinza', $id]);
    board_columns(true);
}

/** Reordena as colunas pela lista de ids informada. */
function board_columns_reorder(array $ids): void
{
    db()->beginTransaction();
    try {
        foreach (array_values(array_map('intval', $ids)) as $i => $id) {
            q('UPDATE board_columns SET sort_order = ? WHERE id = ?', [$i + 1, $id]);
        }
        db()->commit();
    } catch (Throwable $e) {
        db()->rollBack();
        throw $e;
    }
    board_columns(true);
}

/**
 * Troca qual coluna significa "concluido". Sempre exatamente uma: e ela que
 * liga o quadro ao done_at que o dashboard e o detalhe do lead ja usam.
 */
function board_column_set_done(int $id): void
{
    if (scalar('SELECT id FROM board_columns WHERE id = ?', [$id]) === null) {
        throw new InvalidArgumentException('Coluna nao encontrada.');
    }
    $antiga = board_column_done();
    $antigaId = $antiga !== null ? (int) $antiga['id'] : 0;
    if ($antigaId === $id) {
        return;
    }
    db()->beginTransaction();
    try {
        q('UPDATE board_columns SET is_done = 0');
        q('UPDATE board_columns SET is_done = 1 WHERE id = ?', [$id]);
        // Reabre APENAS o que estava na coluna que perdeu o papel de conclusao.
        // Um WHERE column_id <> ? apagaria o done_at de todo o historico do CRM.
        // Tarefa de cadencia nunca ressuscita: quem a fecha e a propria cadencia.
        if ($antigaId > 0) {
            q('UPDATE tasks SET done_at = NULL
               WHERE column_id = ? AND done_at IS NOT NULL AND kind <> ?', [$antigaId, TASK_KIND_CADENCIA]);
        }
        q('UPDATE tasks SET done_at = NOW() WHERE column_id = ? AND done_at IS NULL', [$id]);
        db()->commit();
    } catch (Throwable $e) {
        db()->rollBack();
        throw $e;
    }
    board_columns(true);
}
/**
 * Apaga a coluna e joga os cards dela na coluna informada (ou na de entrada).
 * A coluna de conclusao nao pode ser apagada, e nem a ultima que sobrou.
 */
function board_column_delete(int $id, ?int $paraId = null): void
{
    $cols = board_columns();
    if (count($cols) <= 1) {
        throw new InvalidArgumentException('O quadro precisa de pelo menos uma coluna.');
    }
    $alvo = null;
    foreach ($cols as $c) {
        if ((int) $c['id'] === $id) {
            $alvo = $c;
        }
    }
    if ($alvo === null) {
        throw new InvalidArgumentException('Coluna nao encontrada.');
    }
    if ((int) $alvo['is_done'] === 1) {
        throw new InvalidArgumentException('Essa e a coluna de conclusao — marque outra antes de apagar esta.');
    }
    $destino = null;
    foreach ($cols as $c) {
        if ((int) $c['id'] === $id) {
            continue;
        }
        if ($paraId !== null) {
            if ((int) $c['id'] === $paraId) {
                $destino = $c;
                break;
            }
            continue;
        }
        // Sem destino escolhido, cai na primeira coluna que NAO conclui:
        // apagar uma coluna nao pode fechar tarefa por acidente.
        if ((int) $c['is_done'] !== 1) {
            $destino = $c;
            break;
        }
    }
    if ($destino === null) {
        throw new InvalidArgumentException('Escolha uma coluna de destino valida para os cards.');
    }
    $destinoId = (int) $destino['id'];
    db()->beginTransaction();
    try {
        // Os cards que chegam entram DEPOIS dos que ja estavam, sem empatar.
        $fim = (int) scalar('SELECT COALESCE(MAX(sort_order), 0) FROM tasks WHERE column_id = ?', [$destinoId]);
        $vindos = rows('SELECT id FROM tasks WHERE column_id = ? ORDER BY sort_order, id', [$id]);
        foreach ($vindos as $i => $v) {
            q('UPDATE tasks SET column_id = ?, sort_order = ? WHERE id = ?', [$destinoId, $fim + $i + 1, (int) $v['id']]);
        }
        if ((int) $destino['is_done'] === 1) {
            q('UPDATE tasks SET done_at = NOW()
               WHERE column_id = ? AND done_at IS NULL AND kind <> ?', [$destinoId, TASK_KIND_CADENCIA]);
        }
        q('DELETE FROM board_columns WHERE id = ?', [$id]);
        db()->commit();
    } catch (Throwable $e) {
        db()->rollBack();
        throw $e;
    }
    board_columns(true);
}
/** Usuarios ativos, para o seletor de responsavel. */
function users_ativos(): array
{
    return rows('SELECT id, name FROM users WHERE is_active = 1 ORDER BY name');
}

/* ---------- Metricas do dashboard ---------- */

function metrics_status_counts(): array
{
    $counts = array_fill_keys(LEAD_STATUSES, 0);
    foreach (rows('SELECT status, COUNT(*) AS n FROM leads GROUP BY status') as $r) {
        $counts[$r['status']] = (int) $r['n'];
    }
    return $counts;
}

/** Demos do mês corrente: realizadas (interações type=demo) e agendadas (histórico → demo_agendada). */
function metrics_demos_mes(): array
{
    $inicioMes = date('Y-m-01 00:00:00');
    return [
        'realizadas' => (int) scalar("SELECT COUNT(*) FROM interactions WHERE type = 'demo' AND occurred_at >= ?", [$inicioMes]),
        'agendadas'  => (int) scalar("SELECT COUNT(*) FROM lead_status_history WHERE to_status = 'demo_agendada' AND changed_at >= ?", [$inicioMes]),
    ];
}

function leads_followups_vencidos(int $limit = 10): array
{
    return rows("SELECT id, company, contact_name, status, next_action_at, next_action_note
                 FROM leads
                 WHERE next_action_at IS NOT NULL AND next_action_at <= NOW()
                   AND status NOT IN ('cliente','perdido')
                 ORDER BY next_action_at ASC LIMIT $limit");
}

function leads_novos_parados(int $hours = 48, int $limit = 10): array
{
    return rows("SELECT id, company, contact_name, created_at
                 FROM leads
                 WHERE status = 'novo' AND created_at <= DATE_SUB(NOW(), INTERVAL $hours HOUR)
                 ORDER BY created_at ASC LIMIT $limit");
}

/* ---------- Contatos do lead (lead_contacts) ---------- */

/** Cargos que indicam poder de decisão (flag automática, editável na UI). */
function cargo_e_decisor(?string $cargo): bool
{
    if (!$cargo) {
        return false;
    }
    return (bool) preg_match(
        '/s[óo]cio|ceo\b|coo\b|cfo\b|cto\b|diretor|presidente|dono|propriet|founder|fundador|administrador|gerente geral|head\b/iu',
        $cargo
    );
}

function contacts_of(int $leadId): array
{
    return rows('SELECT * FROM lead_contacts WHERE lead_id = ? ORDER BY is_principal DESC, is_decisor DESC, id', [$leadId]);
}

/** Espelha o contato principal nos campos rápidos do lead (compatibilidade). */
function sync_principal_contact(int $leadId): void
{
    $p = row('SELECT name, email, whatsapp FROM lead_contacts WHERE lead_id = ? AND is_principal = 1 LIMIT 1', [$leadId]);
    if ($p !== null) {
        q('UPDATE leads SET contact_name = ?, email = ?, whatsapp = ? WHERE id = ?',
            [$p['name'], $p['email'], $p['whatsapp'], $leadId]);
    }
}

function contact_add(int $leadId, array $d): int
{
    $name = norm_text($d['name'] ?? '', 120);
    if (mb_strlen($name) < 2) {
        throw new InvalidArgumentException('Informe o nome do contato.');
    }
    $cargo = norm_text($d['cargo'] ?? '', 80) ?: null;
    $decisor = array_key_exists('is_decisor', $d) && $d['is_decisor'] !== null
        ? (int) (bool) $d['is_decisor']
        : (int) cargo_e_decisor($cargo);
    $principal = (int) (bool) ($d['is_principal'] ?? 0);
    // Primeiro contato do lead vira principal automaticamente
    if (!$principal && scalar('SELECT COUNT(*) FROM lead_contacts WHERE lead_id = ?', [$leadId]) == 0) {
        $principal = 1;
    }
    if ($principal) {
        q('UPDATE lead_contacts SET is_principal = 0 WHERE lead_id = ?', [$leadId]);
    }
    q('INSERT INTO lead_contacts (lead_id, name, cargo, email, whatsapp, phone, linkedin, is_principal, is_decisor, notes)
       VALUES (?,?,?,?,?,?,?,?,?,?)', [
        $leadId, $name, $cargo,
        $d['email'] ?? null, $d['whatsapp'] ?? null, $d['phone'] ?? null, $d['linkedin'] ?? null,
        $principal, $decisor,
        norm_text($d['notes'] ?? '', 255) ?: null,
    ]);
    $id = last_id();
    if ($principal) {
        sync_principal_contact($leadId);
    }
    return $id;
}


/**
 * Edita um contato. A flag de decisor tem botao proprio e nao e recalculada
 * aqui - mudar o cargo nao desfaz uma marcacao manual.
 */
function contact_update(int $id, array $d, ?int $expectLeadId = null): void
{
    $c = row('SELECT lead_id, is_principal FROM lead_contacts WHERE id = ?', [$id]);
    if ($c === null) {
        throw new InvalidArgumentException('Contato não encontrado.');
    }
    if ($expectLeadId !== null && (int) $c['lead_id'] !== $expectLeadId) {
        throw new InvalidArgumentException('Contato não pertence a este lead.');
    }
    $name = norm_text($d['name'] ?? '', 120);
    if (mb_strlen($name) < 2) {
        throw new InvalidArgumentException('Informe o nome do contato.');
    }
    // SET dinamico (mesmo padrao de lead_update): chave ausente nao apaga
    // a coluna. Sem isso, editar pela tela zerava o notes gravado pela API.
    $sets = ['name = ?', 'cargo = ?', 'email = ?', 'whatsapp = ?', 'phone = ?', 'linkedin = ?'];
    $params = [
        $name,
        norm_text($d['cargo'] ?? '', 80) ?: null,
        $d['email'] ?? null,
        $d['whatsapp'] ?? null,
        $d['phone'] ?? null,
        $d['linkedin'] ?? null,
    ];
    if (array_key_exists('notes', $d)) {
        $sets[] = 'notes = ?';
        $params[] = norm_text($d['notes'] ?? '', 255) ?: null;
    }
    $params[] = $id;
    q('UPDATE lead_contacts SET ' . implode(', ', $sets) . ' WHERE id = ?', $params);
    if ((int) $c['is_principal'] === 1) {
        sync_principal_contact((int) $c['lead_id']);
    }
}

function contact_delete(int $id, ?int $expectLeadId = null): void
{
    contact_assert_lead($id, $expectLeadId);
    q('DELETE FROM lead_contacts WHERE id = ?', [$id]);
}

/** Recusa agir sobre contato de outro lead quando o chamador informa qual espera. */
function contact_assert_lead(int $id, ?int $expectLeadId): void
{
    if ($expectLeadId === null) {
        return;
    }
    $dono = scalar('SELECT lead_id FROM lead_contacts WHERE id = ?', [$id]);
    if ($dono === null || (int) $dono !== $expectLeadId) {
        throw new InvalidArgumentException('Contato nao pertence a este lead.');
    }
}

/** Caminho inverso do sync: edição dos campos rápidos do lead atualiza o contato principal. */
function sync_lead_to_principal(int $leadId): void
{
    $l = row('SELECT contact_name, email, whatsapp FROM leads WHERE id = ?', [$leadId]);
    if ($l === null || ($l['contact_name'] === '' && !$l['email'] && !$l['whatsapp'])) {
        return;
    }
    $p = row('SELECT id FROM lead_contacts WHERE lead_id = ? AND is_principal = 1 LIMIT 1', [$leadId]);
    if ($p !== null) {
        q('UPDATE lead_contacts SET name = ?, email = ?, whatsapp = ? WHERE id = ?',
            [$l['contact_name'] ?: 'Contato', $l['email'], $l['whatsapp'], $p['id']]);
    } else {
        contact_add($leadId, [
            'name'         => $l['contact_name'] ?: 'Contato',
            'email'        => $l['email'],
            'whatsapp'     => $l['whatsapp'],
            'is_principal' => 1,
        ]);
    }
}

function contact_set_principal(int $id, ?int $expectLeadId = null): void
{
    contact_assert_lead($id, $expectLeadId);
    $c = row('SELECT lead_id FROM lead_contacts WHERE id = ?', [$id]);
    if ($c === null) {
        throw new InvalidArgumentException('Contato não encontrado.');
    }
    q('UPDATE lead_contacts SET is_principal = 0 WHERE lead_id = ?', [$c['lead_id']]);
    q('UPDATE lead_contacts SET is_principal = 1 WHERE id = ?', [$id]);
    sync_principal_contact((int) $c['lead_id']);
}

function contact_toggle_decisor(int $id, ?int $expectLeadId = null): void
{
    contact_assert_lead($id, $expectLeadId);
    q('UPDATE lead_contacts SET is_decisor = 1 - is_decisor WHERE id = ?', [$id]);
}

/** Map lead_id => true para leads (da página atual) que têm contato decisor. */
function leads_com_decisor(array $leadIds): array
{
    if (!$leadIds) {
        return [];
    }
    $marks = implode(',', array_fill(0, count($leadIds), '?'));
    $map = [];
    foreach (rows("SELECT DISTINCT lead_id FROM lead_contacts WHERE is_decisor = 1 AND lead_id IN ($marks)", $leadIds) as $r) {
        $map[(int) $r['lead_id']] = true;
    }
    return $map;
}

/** Contatos agregados por lead ("Nome (cargo) email fone; ...") para o export. */
function contacts_agregados(array $leadIds): array
{
    if (!$leadIds) {
        return [];
    }
    $marks = implode(',', array_fill(0, count($leadIds), '?'));
    $map = [];
    foreach (rows("SELECT lead_id, name, cargo, email, whatsapp, phone, linkedin, is_decisor
                   FROM lead_contacts WHERE lead_id IN ($marks)
                   ORDER BY lead_id, is_principal DESC, id", $leadIds) as $c) {
        $peca = $c['name']
            . ($c['cargo'] ? ' (' . $c['cargo'] . ($c['is_decisor'] ? ' — decisor' : '') . ')' : ($c['is_decisor'] ? ' (decisor)' : ''))
            . ($c['email'] ? ' ' . $c['email'] : '')
            . ($c['whatsapp'] ? ' ' . $c['whatsapp'] : '')
            . (!empty($c['phone']) ? ' fixo ' . $c['phone'] : '')
            . ($c['linkedin'] ? ' ' . $c['linkedin'] : '');
        $map[(int) $c['lead_id']][] = trim($peca);
    }
    return array_map(fn ($l) => implode(' | ', $l), $map);
}

/* ---------- Fila de prospecção (prospect_pool) ---------- */

const POOL_VERTICAIS = ['contabilidade', 'software_ti', 'advocacia', 'bpo_agencias', 'seguros_imob', 'servicos_prof'];
const POOL_VERTICAL_LABELS = [
    'contabilidade' => 'Contabilidade',
    'software_ti'   => 'Software/TI',
    'advocacia'     => 'Advocacia',
    'bpo_agencias'  => 'BPO/Agências',
    'seguros_imob'  => 'Seguros/Imobiliárias',
    'servicos_prof' => 'Consultoria/Engenharia',
];

/** Upsert por CNPJ (pipeline mensal). Preserva o estado de promoção. */
function pool_upsert(array $d): void
{
    q('INSERT INTO prospect_pool
         (cnpj, company, contact_name, contact_cargo, email, whatsapp, website, estacoes, vertical,
          score, uf, municipio, observacoes, mes_referencia)
       VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)
       ON DUPLICATE KEY UPDATE
         company = VALUES(company), contact_name = VALUES(contact_name),
         contact_cargo = VALUES(contact_cargo),
         email = VALUES(email), whatsapp = VALUES(whatsapp),
         website = VALUES(website),
         estacoes = VALUES(estacoes), vertical = VALUES(vertical),
         score = VALUES(score), uf = VALUES(uf), municipio = VALUES(municipio),
         observacoes = VALUES(observacoes), mes_referencia = VALUES(mes_referencia)', [
        $d['cnpj'], $d['company'], $d['contact_name'] ?? '',
        $d['contact_cargo'] ?? null, $d['email'] ?? null,
        $d['whatsapp'] ?? null, $d['website'] ?? null, $d['estacoes'] ?? null,
        in_enum($d['vertical'] ?? null, POOL_VERTICAIS, 'outro'),
        max(0, min(100, (int) ($d['score'] ?? 0))),
        $d['uf'] ?? null, $d['municipio'] ?? null, $d['observacoes'] ?? null,
        $d['mes_referencia'] ?? null,
    ]);
}

/** Disponíveis/promovidos por vertical + mês de referência mais recente. */
function pool_stats(): array
{
    $stats = ['verticais' => [], 'disponiveis' => 0, 'promovidos' => 0, 'mes' => null];
    foreach (rows('SELECT vertical,
                          SUM(promoted_at IS NULL) AS disponiveis,
                          SUM(promoted_at IS NOT NULL) AS promovidos
                   FROM prospect_pool GROUP BY vertical') as $r) {
        $stats['verticais'][$r['vertical']] = [
            'disponiveis' => (int) $r['disponiveis'],
            'promovidos'  => (int) $r['promovidos'],
        ];
        $stats['disponiveis'] += (int) $r['disponiveis'];
        $stats['promovidos'] += (int) $r['promovidos'];
    }
    $stats['mes'] = scalar('SELECT MAX(mes_referencia) FROM prospect_pool');
    return $stats;
}

/**
 * Promove os melhores da fila a leads (botão "Puxar leads").
 * Empresas que já viraram lead por outro caminho são reconciliadas
 * (marcadas como promovidas) sem criar duplicata, e não contam na cota.
 * @return array{criados:int, reconciliados:int, lead_ids:array}
 */
function pool_pull(int $qtd, ?string $vertical, int $userId): array
{
    $qtd = max(1, min(50, $qtd));
    $criados = 0;
    $reconciliados = 0;
    $leadIds = [];
    $vistos = 0;

    while ($criados < $qtd && $vistos < 500) { // trava de segurança
        $sqlVert = $vertical ? 'AND vertical = ?' : '';
        $params = $vertical ? [$vertical] : [];
        $lote = rows("SELECT * FROM prospect_pool
                      WHERE promoted_at IS NULL $sqlVert
                      ORDER BY score DESC, id ASC LIMIT 50", $params);
        if (!$lote) {
            break;
        }
        foreach ($lote as $p) {
            $vistos++;
            $dup = lead_find_duplicate($p['email'] ?: null, $p['whatsapp'] ?: null, $p['cnpj']);
            if ($dup !== null) {
                q('UPDATE prospect_pool SET promoted_lead_id = ?, promoted_at = NOW() WHERE id = ?',
                    [$dup, $p['id']]);
                $reconciliados++;
                continue;
            }
            $res = lead_create([
                'company'           => $p['company'],
                'cnpj'              => $p['cnpj'],
                'contact_name'      => $p['contact_name'],
                'contact_cargo'     => $p['contact_cargo'] ?? null,
                'email'             => $p['email'] ?: null,
                'whatsapp'          => $p['whatsapp'] ?: null,
                'website'           => $p['website'] ?? null,
                'estimated_devices' => $p['estacoes'] !== null ? (int) $p['estacoes'] : null,
                'source'            => 'prospeccao',
                'notes'             => $p['observacoes'] ?: null,
            ], $userId, 'ui');
            q('UPDATE prospect_pool SET promoted_lead_id = ?, promoted_at = NOW() WHERE id = ?',
                [$res['id'], $p['id']]);
            $leadIds[] = $res['id'];
            $criados++;
            if ($criados >= $qtd) {
                break;
            }
        }
    }
    return ['criados' => $criados, 'reconciliados' => $reconciliados, 'lead_ids' => $leadIds];
}

/* ---------- Log do intake público ---------- */

function intake_log(string $ip, string $outcome, ?int $leadId = null, ?string $detail = null): void
{
    q('INSERT INTO intake_log (ip, outcome, lead_id, detail) VALUES (?,?,?,?)',
        [$ip, $outcome, $leadId, $detail !== null ? mb_substr($detail, 0, 255) : null]);
}
