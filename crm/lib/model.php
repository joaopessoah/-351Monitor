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
const DEMO_META_MES     = 10; // meta comercial: 10 demos/mês (docs/CONSIDERACOES-E-DECISOES.md:343)

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
    return $id === null ? null : (int) $id;
}

/** @return array{id:int, duplicate_of_lead_id:?int} */
function lead_create(array $d, ?int $userId, string $via): array
{
    $dup = lead_find_duplicate($d['email'] ?? null, $d['whatsapp'] ?? null, $d['cnpj'] ?? null);
    db()->beginTransaction();
    try {
        q('INSERT INTO leads (company, cnpj, contact_name, email, whatsapp, status, source,
                              utm_source, utm_medium, utm_campaign, estimated_devices, plan_interest,
                              next_action_at, next_action_note, notes, duplicate_of_lead_id, created_via, created_by)
           VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)', [
            $d['company'],
            $d['cnpj'] ?? null,
            $d['contact_name'] ?? '',
            $d['email'] ?? null,
            $d['whatsapp'] ?? null,
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
    return ['id' => $id, 'duplicate_of_lead_id' => $dup];
}

/** Atualiza campos editáveis (whitelist). Valores já normalizados pelo chamador. */
function lead_update(int $id, array $d): void
{
    $allowed = ['company', 'cnpj', 'contact_name', 'email', 'whatsapp', 'source', 'estimated_devices',
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

/** Eliminação definitiva (LGPD): CASCADE remove interações, tarefas e histórico. */
function lead_delete(int $id): void
{
    q('DELETE FROM leads WHERE id = ?', [$id]);
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

function interaction_add(int $leadId, string $type, string $summary, ?string $occurredAt, ?int $userId): int
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
    q('INSERT INTO interactions (lead_id, user_id, type, summary, occurred_at) VALUES (?,?,?,?,?)',
        [$leadId, $userId, $type, $summary, $occurredAt ?: date('Y-m-d H:i:s')]);
    return last_id();
}

function task_add(?int $leadId, string $title, string $dueAt, ?int $assignedTo, ?int $createdBy): int
{
    $title = trim($title);
    if ($title === '') {
        throw new InvalidArgumentException('Dê um título à tarefa.');
    }
    if ($leadId !== null && scalar('SELECT id FROM leads WHERE id = ?', [$leadId]) === null) {
        throw new InvalidArgumentException('Lead não encontrado.');
    }
    q('INSERT INTO tasks (lead_id, title, due_at, assigned_to, created_by) VALUES (?,?,?,?,?)',
        [$leadId, $title, $dueAt, $assignedTo, $createdBy]);
    return last_id();
}

function task_done(int $id): void
{
    q('UPDATE tasks SET done_at = NOW() WHERE id = ? AND done_at IS NULL', [$id]);
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

/* ---------- Métricas do dashboard ---------- */

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

/* ---------- Log do intake público ---------- */

function intake_log(string $ip, string $outcome, ?int $leadId = null, ?string $detail = null): void
{
    q('INSERT INTO intake_log (ip, outcome, lead_id, detail) VALUES (?,?,?,?)',
        [$ip, $outcome, $leadId, $detail !== null ? mb_substr($detail, 0, 255) : null]);
}
