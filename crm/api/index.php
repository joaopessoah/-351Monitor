<?php
/**
 * API do CRM para o Claude (assistente do time) tratar leads em sessões futuras.
 * Auth: Authorization: Bearer <token> (fallback X-Api-Key), tokens no crm_config.php.
 * Rotas via ?r= — GET leitura, POST escrita (body JSON). Erros: {"error":{code,message}}.
 *
 * Exemplos:
 *   curl -H "Authorization: Bearer $T" ".../crm/api/index.php?r=leads&status=novo"
 *   curl -H "Authorization: Bearer $T" -X POST -d '{"lead_id":1,"type":"demo","summary":"Demo feita"}' ".../crm/api/index.php?r=interactions"
 *   (type=email aceita "email_seq":1..5 — agenda sozinho a tarefa da proxima etapa)
 */

require dirname(__DIR__) . '/lib/bootstrap.php';

security_headers(false);
header('Content-Type: application/json; charset=utf-8');

function api_out(int $code, array $body): never
{
    http_response_code($code);
    echo json_encode($body, JSON_UNESCAPED_UNICODE);
    exit;
}

function api_err(int $http, string $code, string $msg): never
{
    api_out($http, ['error' => ['code' => $code, 'message' => $msg]]);
}

// ---------- Autenticação por token ----------
$authHeader = $_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? '';
$token = '';
if (preg_match('/^Bearer\s+(.+)$/i', $authHeader, $m)) {
    $token = trim($m[1]);
}
if ($token === '') {
    $token = trim($_SERVER['HTTP_X_API_KEY'] ?? '');
}
$tokenOk = false;
foreach ((array) cfg('api_tokens') as $t) {
    if (is_string($t) && strlen($t) >= 32 && $token !== '' && hash_equals($t, $token)) {
        $tokenOk = true;
        break;
    }
}
if (!$tokenOk) {
    api_err(401, 'unauthorized', 'Token inválido.');
}

$ip = client_ip();
if (throttle_blocked('api', $ip, 120, 1)) {
    api_err(429, 'rate_limited', 'Limite de requisições excedido (120/min).');
}
throttle_add('api', $ip);

// ---------- Roteamento ----------
$r = (string) ($_GET['r'] ?? '');
$method = $_SERVER['REQUEST_METHOD'];
$body = [];
if ($method === 'POST') {
    $raw = (string) file_get_contents('php://input');
    $body = $raw === '' ? [] : json_decode($raw, true);
    if (!is_array($body)) {
        api_err(400, 'bad_json', 'Body JSON inválido.');
    }
}

/** DATETIME local (-03:00 fixo) → ISO 8601 com offset explícito. */
function api_dt(?string $s): ?string
{
    return $s === null ? null : str_replace(' ', 'T', $s) . '-03:00';
}

function api_lead_row(array $l): array
{
    return [
        'id'                   => (int) $l['id'],
        'company'              => $l['company'],
        'cnpj'                 => $l['cnpj'],
        'cnpj_razao_social'    => $l['cnpj_razao_social'],
        'cnpj_situacao'        => $l['cnpj_situacao'],
        'contact_name'         => $l['contact_name'],
        'email'                => $l['email'],
        'whatsapp'             => $l['whatsapp'],
        'website'              => $l['website'],
        'linkedin'             => $l['linkedin'],
        'status'               => $l['status'],
        'lost_reason'          => $l['lost_reason'],
        'source'               => $l['source'],
        'plan_interest'        => $l['plan_interest'],
        'estimated_devices'    => $l['estimated_devices'] !== null ? (int) $l['estimated_devices'] : null,
        'next_action_at'       => api_dt($l['next_action_at']),
        'next_action_note'     => $l['next_action_note'],
        'duplicate_of_lead_id' => $l['duplicate_of_lead_id'] !== null ? (int) $l['duplicate_of_lead_id'] : null,
        'created_at'           => api_dt($l['created_at']),
        'updated_at'           => api_dt($l['updated_at']),
    ];
}

try {
    switch ($method . ' ' . $r) {
        case 'GET leads': {
            $res = leads_search([
                'status'      => $_GET['status'] ?? '',
                'source'      => $_GET['source'] ?? '',
                'q'           => norm_text($_GET['q'] ?? '', 120),
                'so_vencidos' => !empty($_GET['so_vencidos']),
                'so_decisor'  => !empty($_GET['so_decisor']),
            ], max(1, (int) ($_GET['page'] ?? 1)));
            api_out(200, [
                'items' => array_map('api_lead_row', $res['items']),
                'page'  => $res['page'],
                'total' => $res['total'],
            ]);
        }
        case 'GET lead': {
            $id = (int) ($_GET['id'] ?? 0);
            $l = row('SELECT * FROM leads WHERE id = ?', [$id]);
            if ($l === null) {
                api_err(404, 'not_found', 'Lead não encontrado.');
            }
            $out = api_lead_row($l);
            $out['notes'] = $l['notes'];
            $out['utm'] = ['source' => $l['utm_source'], 'medium' => $l['utm_medium'], 'campaign' => $l['utm_campaign']];
            $out['cnpj_data'] = $l['cnpj_json'] !== null ? json_decode((string) $l['cnpj_json'], true) : null;
            $out['cnpj_checked_at'] = api_dt($l['cnpj_checked_at']);
            $out['contacts'] = array_map(fn ($c) => [
                'id' => (int) $c['id'], 'name' => $c['name'], 'cargo' => $c['cargo'],
                'email' => $c['email'], 'whatsapp' => $c['whatsapp'], 'phone' => $c['phone'] ?? null,
                'linkedin' => $c['linkedin'], 'notes' => $c['notes'] ?? null,
                'principal' => (bool) $c['is_principal'], 'decisor' => (bool) $c['is_decisor'],
            ], contacts_of($id));
            $out['interactions'] = array_map(fn ($i) => [
                'id' => (int) $i['id'], 'type' => $i['type'], 'summary' => $i['summary'],
                'occurred_at' => api_dt($i['occurred_at']), 'user' => $i['user_name'],
            ], rows('SELECT i.*, u.name AS user_name FROM interactions i LEFT JOIN users u ON u.id = i.user_id WHERE i.lead_id = ? ORDER BY i.occurred_at DESC', [$id]));
            $out['tasks'] = array_map(fn ($t) => [
                'id' => (int) $t['id'], 'title' => $t['title'],
                'due_at' => api_dt($t['due_at']), 'done_at' => api_dt($t['done_at']),
            ], rows('SELECT * FROM tasks WHERE lead_id = ? ORDER BY due_at', [$id]));
            $out['history'] = array_map(fn ($h) => [
                'from' => $h['from_status'], 'to' => $h['to_status'], 'changed_at' => api_dt($h['changed_at']),
            ], rows('SELECT * FROM lead_status_history WHERE lead_id = ? ORDER BY changed_at DESC', [$id]));
            api_out(200, $out);
        }
        case 'POST leads': {
            $company = norm_text($body['company'] ?? '', 160);
            if (mb_strlen($company) < 2) {
                api_err(422, 'invalid', 'company é obrigatório (mínimo 2 caracteres).');
            }
            $cnpj = norm_cnpj($body['cnpj'] ?? '');
            if ($cnpj === false) {
                api_err(422, 'invalid', 'cnpj inválido.');
            }
            $email = norm_email($body['email'] ?? '');
            if ($email === false) {
                api_err(422, 'invalid', 'email inválido.');
            }
            $fone = norm_whatsapp($body['whatsapp'] ?? '');
            if ($fone === false) {
                api_err(422, 'invalid', 'whatsapp inválido.');
            }
            $est = norm_int($body['estimated_devices'] ?? null, 1, 10000);
            if ($est === false) {
                api_err(422, 'invalid', 'estimated_devices inválido.');
            }
            $na = norm_dt_api($body['next_action_at'] ?? '');
            if ($na === false) {
                api_err(422, 'invalid', 'next_action_at inválido (use YYYY-MM-DD HH:MM).');
            }
            $site = norm_url($body['website'] ?? '');
            if ($site === false) {
                api_err(422, 'invalid', 'website inválido.');
            }
            $li = norm_url($body['linkedin'] ?? '');
            if ($li === false) {
                api_err(422, 'invalid', 'linkedin inválido.');
            }
            $res = lead_create([
                'company'           => $company,
                'cnpj'              => $cnpj,
                'contact_name'      => norm_text($body['contact_name'] ?? '', 120),
                'email'             => $email,
                'whatsapp'          => $fone,
                'website'           => $site,
                'linkedin'          => $li,
                'estimated_devices' => $est,
                'source'            => in_enum($body['source'] ?? null, LEAD_SOURCES, 'outro'),
                'plan_interest'     => in_enum($body['plan_interest'] ?? null, LEAD_PLANS, 'indefinido'),
                'next_action_at'    => $na,
                'next_action_note'  => norm_text($body['next_action_note'] ?? '', 255) ?: null,
                'notes'             => trim((string) ($body['notes'] ?? '')) ?: null,
            ], null, 'api');
            api_out(200, ['id' => $res['id'], 'duplicate_of_lead_id' => $res['duplicate_of_lead_id']]);
        }
        case 'POST lead-update': {
            $id = (int) ($body['id'] ?? 0);
            if (row('SELECT id FROM leads WHERE id = ?', [$id]) === null) {
                api_err(404, 'not_found', 'Lead não encontrado.');
            }
            $d = [];
            if (array_key_exists('company', $body)) {
                $c = norm_text($body['company'], 160);
                if (mb_strlen($c) < 2) {
                    api_err(422, 'invalid', 'company inválido.');
                }
                $d['company'] = $c;
            }
            if (array_key_exists('contact_name', $body)) {
                $d['contact_name'] = norm_text($body['contact_name'], 120);
            }
            if (array_key_exists('cnpj', $body)) {
                $c = norm_cnpj($body['cnpj']);
                if ($c === false) {
                    api_err(422, 'invalid', 'cnpj inválido.');
                }
                $d['cnpj'] = $c;
            }
            if (array_key_exists('email', $body)) {
                $e = norm_email($body['email']);
                if ($e === false) {
                    api_err(422, 'invalid', 'email inválido.');
                }
                $d['email'] = $e;
            }
            if (array_key_exists('whatsapp', $body)) {
                $w = norm_whatsapp($body['whatsapp']);
                if ($w === false) {
                    api_err(422, 'invalid', 'whatsapp inválido.');
                }
                $d['whatsapp'] = $w;
            }
            if (array_key_exists('estimated_devices', $body)) {
                $n = norm_int($body['estimated_devices'], 1, 10000);
                if ($n === false) {
                    api_err(422, 'invalid', 'estimated_devices inválido.');
                }
                $d['estimated_devices'] = $n;
            }
            if (array_key_exists('website', $body)) {
                $u = norm_url($body['website']);
                if ($u === false) {
                    api_err(422, 'invalid', 'website inválido.');
                }
                $d['website'] = $u;
            }
            if (array_key_exists('linkedin', $body)) {
                $u = norm_url($body['linkedin']);
                if ($u === false) {
                    api_err(422, 'invalid', 'linkedin inválido.');
                }
                $d['linkedin'] = $u;
            }
            if (array_key_exists('plan_interest', $body)) {
                $d['plan_interest'] = in_enum($body['plan_interest'], LEAD_PLANS, 'indefinido');
            }
            if (array_key_exists('source', $body)) {
                $d['source'] = in_enum($body['source'], LEAD_SOURCES, 'outro');
            }
            if (array_key_exists('next_action_at', $body)) {
                $na = norm_dt_api($body['next_action_at']);
                if ($na === false) {
                    api_err(422, 'invalid', 'next_action_at inválido.');
                }
                $d['next_action_at'] = $na;
            }
            if (array_key_exists('next_action_note', $body)) {
                $d['next_action_note'] = norm_text($body['next_action_note'], 255) ?: null;
            }
            if (array_key_exists('notes', $body)) {
                $d['notes'] = trim((string) $body['notes']) ?: null;
            }
            lead_update($id, $d);
            if (array_key_exists('contact_name', $d) || array_key_exists('email', $d) || array_key_exists('whatsapp', $d)) {
                sync_lead_to_principal($id);
            }
            api_out(200, ['ok' => true]);
        }
        case 'POST contacts': {
            $leadId = (int) ($body['lead_id'] ?? 0);
            if (row('SELECT id FROM leads WHERE id = ?', [$leadId]) === null) {
                api_err(404, 'not_found', 'Lead não encontrado.');
            }
            $email = norm_email($body['email'] ?? '');
            if ($email === false) {
                api_err(422, 'invalid', 'email inválido.');
            }
            $fone = norm_whatsapp($body['whatsapp'] ?? '');
            if ($fone === false) {
                api_err(422, 'invalid', 'whatsapp inválido.');
            }
            $li = norm_url($body['linkedin'] ?? '');
            if ($li === false) {
                api_err(422, 'invalid', 'linkedin inválido.');
            }
            $cid = contact_add($leadId, [
                'name'         => (string) ($body['name'] ?? ''),
                'cargo'        => $body['cargo'] ?? null,
                'email'        => $email,
                'whatsapp'     => $fone,
                'linkedin'     => $li,
                'is_principal' => !empty($body['principal']),
                'is_decisor'   => array_key_exists('decisor', $body) ? (bool) $body['decisor'] : null,
                'notes'        => $body['notes'] ?? null,
            ]);
            api_out(200, ['id' => $cid]);
        }
        case 'POST contact-delete': {
            contact_delete((int) ($body['id'] ?? 0));
            api_out(200, ['ok' => true]);
        }
        case 'POST lead-status': {
            lead_set_status((int) ($body['id'] ?? 0), (string) ($body['status'] ?? ''), $body['lost_reason'] ?? null, null);
            api_out(200, ['ok' => true]);
        }
        case 'POST interactions': {
            $oc = norm_dt_api($body['occurred_at'] ?? '');
            if ($oc === false) {
                api_err(422, 'invalid', 'occurred_at inválido (use YYYY-MM-DD HH:MM).');
            }
            $leadId = (int) ($body['lead_id'] ?? 0);
            $tipo = (string) ($body['type'] ?? '');
            // email_seq (1 a 5) liga a interacao a cadencia e agenda a proxima tarefa
            $seq = $tipo === 'email' ? (int) ($body['email_seq'] ?? 0) : 0;
            $id = interaction_add($leadId, $tipo, (string) ($body['summary'] ?? ''), $oc, null, $seq > 0 ? $seq : null);
            $due = $seq > 0 ? cadencia_email_agendar($leadId, $seq, null, $oc) : null;
            api_out(200, ['id' => $id, 'next_task_due_at' => $due]);
        }
        case 'GET tasks': {
            $due = in_enum($_GET['due'] ?? null, ['hoje', 'atrasadas', 'abertas'], 'abertas');
            api_out(200, ['items' => array_map(fn ($t) => [
                'id'      => (int) $t['id'],
                'lead_id' => $t['lead_id'] !== null ? (int) $t['lead_id'] : null,
                'company' => $t['company'],
                'title'   => $t['title'],
                'due_at'  => api_dt($t['due_at']),
            ], tasks_lista($due))]);
        }
        case 'POST tasks': {
            $due = norm_dt_api($body['due_at'] ?? '');
            if ($due === null || $due === false) {
                api_err(422, 'invalid', 'due_at é obrigatório (YYYY-MM-DD HH:MM).');
            }
            $leadId = isset($body['lead_id']) && $body['lead_id'] !== null ? (int) $body['lead_id'] : null;
            $id = task_add($leadId, (string) ($body['title'] ?? ''), $due, null, null);
            api_out(200, ['id' => $id]);
        }
        case 'POST task-done': {
            task_done((int) ($body['id'] ?? 0));
            api_out(200, ['ok' => true]);
        }
        case 'GET cnpj-lookup': {
            // Consulta pura (não grava nada) — útil para checar uma empresa antes de criar o lead.
            $c = norm_cnpj($_GET['cnpj'] ?? '');
            if ($c === null || $c === false) {
                api_err(422, 'invalid', 'Informe um CNPJ válido em ?cnpj=.');
            }
            $data = cnpj_lookup($c);
            if ($data === null) {
                api_err(404, 'not_found', 'CNPJ não encontrado na base pública (ou consulta indisponível).');
            }
            api_out(200, ['cnpj' => $c, 'data' => $data]);
        }
        case 'POST cnpj-enrich': {
            // Consulta o CNPJ já salvo no lead e grava o snapshot no cadastro.
            $data = lead_enrich_cnpj((int) ($body['lead_id'] ?? 0));
            api_out(200, ['ok' => true, 'data' => $data]);
        }
        case 'POST pool-upsert': {
            // Carga mensal da fila de prospecção (pipeline tools/leadgen).
            $itens = $body['items'] ?? null;
            if (!is_array($itens) || count($itens) === 0 || count($itens) > 500) {
                api_err(422, 'invalid', 'items deve ser uma lista de 1 a 500 empresas.');
            }
            $ok = 0;
            $ignorados = 0;
            foreach ($itens as $item) {
                if (!is_array($item)) {
                    $ignorados++;
                    continue;
                }
                $cnpj = norm_cnpj($item['cnpj'] ?? '');
                $company = norm_text($item['company'] ?? '', 160);
                if ($cnpj === null || $cnpj === false || mb_strlen($company) < 2) {
                    $ignorados++;
                    continue;
                }
                $email = norm_email($item['email'] ?? '');
                $fone = norm_whatsapp($item['whatsapp'] ?? '');
                $site = norm_url($item['website'] ?? '');
                $est = norm_int($item['estacoes'] ?? null, 1, 10000);
                pool_upsert([
                    'cnpj'           => $cnpj,
                    'company'        => $company,
                    'contact_name'   => norm_text($item['contact_name'] ?? '', 120),
                    'contact_cargo'  => norm_text($item['contact_cargo'] ?? '', 80) ?: null,
                    'email'          => $email !== false ? $email : null,
                    'whatsapp'       => $fone !== false ? $fone : null,
                    'website'        => $site !== false ? $site : null,
                    'estacoes'       => $est !== false ? $est : null,
                    'vertical'       => $item['vertical'] ?? null,
                    'score'          => (int) ($item['score'] ?? 0),
                    'uf'             => norm_text($item['uf'] ?? '', 2) ?: null,
                    'municipio'      => norm_text($item['municipio'] ?? '', 120) ?: null,
                    'observacoes'    => trim((string) ($item['observacoes'] ?? '')) ?: null,
                    'mes_referencia' => norm_text($item['mes_referencia'] ?? '', 7) ?: null,
                ]);
                $ok++;
            }
            api_out(200, ['ok' => true, 'gravados' => $ok, 'ignorados' => $ignorados]);
        }
        case 'GET pool-stats': {
            api_out(200, pool_stats());
        }
        case 'GET analytics': {
            // Números do site (crm/collect.php). ?d=7|30|90, padrão 30.
            $dias = (int) in_enum((string) ($_GET['d'] ?? '30'), ['7', '30', '90'], '30');
            $resumo = analytics_resumo($dias);
            api_out(200, [
                'periodo_dias' => $dias,
                'resumo'       => array_map('intval', $resumo),
                'por_dia'      => analytics_por_dia($dias),
                'paginas'      => analytics_paginas($dias),
                'origens'      => analytics_origens($dias),
                'eventos'      => analytics_eventos($dias),
                'dispositivos' => analytics_dispositivos($dias),
                'funil'        => array_map('intval', analytics_funil($dias)),
            ]);
        }
        case 'GET analytics-visita': {
            // Jornada completa de uma visita pelo código que chega no WhatsApp.
            $ref = ref_code_norm($_GET['ref'] ?? '');
            if ($ref === null) {
                api_err(422, 'invalid', 'Informe o código de 6 caracteres em ?ref=.');
            }
            $v = analytics_visita_por_ref($ref);
            if ($v === null) {
                api_err(404, 'not_found', 'Visita não encontrada (visitas sem lead expiram em 365 dias).');
            }
            api_out(200, [
                'ref'          => $v['ref_code'],
                'started_at'   => api_dt($v['started_at']),
                'last_seen_at' => api_dt($v['last_seen_at']),
                'landing_path' => $v['landing_path'],
                'origem'       => $v['utm_source'] ?? $v['referrer_host'],
                'utm'          => [
                    'source' => $v['utm_source'], 'medium' => $v['utm_medium'],
                    'campaign' => $v['utm_campaign'], 'content' => $v['utm_content'], 'term' => $v['utm_term'],
                ],
                'device'       => $v['device'],
                'browser'      => $v['browser'],
                'os'           => $v['os'],
                'lead_id'      => $v['lead_id'] !== null ? (int) $v['lead_id'] : null,
                'lead_company' => $v['lead_company'],
                'views'        => array_map(fn ($w) => [
                    'path' => $w['path'], 'title' => $w['title'],
                    'seconds' => $w['seconds'] !== null ? (int) $w['seconds'] : null,
                    'scroll_pct' => $w['scroll_pct'] !== null ? (int) $w['scroll_pct'] : null,
                    'at' => api_dt($w['created_at']),
                ], $v['views_list']),
                'events'       => array_map(fn ($e) => [
                    'name' => $e['name'], 'path' => $e['path'], 'label' => $e['label'],
                    'target' => $e['target'],
                    'value' => $e['value_num'] !== null ? (int) $e['value_num'] : null,
                    'at' => api_dt($e['created_at']),
                ], $v['events_list']),
            ]);
        }
        case 'POST analytics-vincular': {
            $ref = ref_code_norm($body['ref'] ?? '');
            if ($ref === null) {
                api_err(422, 'invalid', 'ref inválido (6 caracteres).');
            }
            visit_link_lead($ref, (int) ($body['lead_id'] ?? 0));
            api_out(200, ['ok' => true]);
        }
        default:
            api_err(404, 'not_found', 'Rota desconhecida. Rotas: leads, lead, lead-update, lead-status, interactions, tasks, task-done, cnpj-lookup, cnpj-enrich, pool-upsert, pool-stats, analytics, analytics-visita, analytics-vincular.');
    }
} catch (InvalidArgumentException $e) {
    api_err(422, 'invalid', $e->getMessage());
} catch (Throwable $e) {
    error_log('api: ' . $e->getMessage());
    api_err(500, 'internal', 'Erro interno.');
}
