<?php
/**
 * Motor da cadencia automatica de e-mail.
 *
 * Duas regras estruturais que explicam o resto do arquivo:
 *
 * 1. UM e-mail pendente por lead de cada vez. O proximo so e planejado depois
 *    que o anterior sai. Assim as guardas sao sempre avaliadas com o mundo do
 *    dia do envio (o lead pode ter respondido, virado cliente ou pedido para
 *    sair entre o agendamento e o disparo), e o texto gravado no outbox e
 *    exatamente o que foi enviado.
 * 2. Na duvida, PARA. Parar sem necessidade custa um e-mail a menos; seguir
 *    escrevendo para quem pediu para sair custa a marca.
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

const CADENCIA_ESTADOS = ['ativa', 'pausada', 'concluida', 'interrompida'];

const CADENCIA_ESTADO_LABELS = [
    'ativa'         => 'Ativa',
    'pausada'       => 'Pausada',
    'concluida'     => 'Concluida',
    'interrompida'  => 'Interrompida',
];

const OUTBOX_STATUS_LABELS = [
    'aguardando' => 'Aguardando aprovacao',
    'agendado'   => 'Agendado',
    'enviando'   => 'Enviando',
    'enviado'    => 'Enviado',
    'falhou'     => 'Falhou',
    'cancelado'  => 'Cancelado',
    'pulado'     => 'Pulado',
];

/** Tarefas humanas que a cadencia cria (visiveis no quadro, ao contrario da automatica). */
const TASK_KIND_LIGACAO  = 'cadencia_ligacao';
const TASK_KIND_LINKEDIN = 'cadencia_linkedin';

/* ---------- Dias uteis com feriado ---------- */

/** N dias uteis a partir de $de, pulando fim de semana E feriado nacional. */
function cadencia_dia_util(string $de, int $dias): string
{
    return business_days_add($de, $dias, feriados_set());
}

/* ---------- Janelas de horario ---------- */

/** "08:30" -> 510 minutos. Hora invalida vira o default da chave. */
function cadencia_minutos(string $hhmm, int $default): int
{
    if (preg_match('/^(\d{1,2}):(\d{2})$/', trim($hhmm), $m)) {
        $h = (int) $m[1];
        $i = (int) $m[2];
        if ($h >= 0 && $h <= 23 && $i >= 0 && $i <= 59) {
            return $h * 60 + $i;
        }
    }
    return $default;
}

/**
 * Janelas de envio do dia, em minutos desde a meia-noite, ja ordenadas e sem
 * as invertidas (fim <= inicio some: janela vazia nao agenda nada).
 * @return array<int, array{0:int, 1:int}>
 */
function cadencia_janelas(): array
{
    $pares = [
        [cadencia_minutos(setting_str('auto_janela1_ini'), 510), cadencia_minutos(setting_str('auto_janela1_fim'), 660)],
        [cadencia_minutos(setting_str('auto_janela2_ini'), 840), cadencia_minutos(setting_str('auto_janela2_fim'), 1020)],
    ];
    $out = [];
    foreach ($pares as $p) {
        if ($p[1] > $p[0]) {
            $out[] = $p;
        }
    }
    usort($out, fn ($a, $b) => $a[0] <=> $b[0]);
    return $out ?: [[510, 660]];
}

/** Agora esta dentro de alguma janela de um dia util? */
function cadencia_dentro_da_janela(?int $agora = null): bool
{
    $agora = $agora ?? time();
    $dia = date('Y-m-d', $agora);
    if (cadencia_dia_util($dia, 0) !== $dia) {
        return false; // fim de semana ou feriado
    }
    $min = (int) date('G', $agora) * 60 + (int) date('i', $agora);
    foreach (cadencia_janelas() as [$ini, $fim]) {
        if ($min >= $ini && $min <= $fim) {
            return true;
        }
    }
    return false;
}

/**
 * Sorteia o horario do proximo envio a partir de $diaMinimo.
 *
 * Sortear em vez de enfileirar e o que evita 20 e-mails saindo no mesmo
 * minuto — o padrao que denuncia disparo em massa para qualquer filtro.
 *
 * @param callable(int,int):int|null $rand injetavel para o teste
 */
function cadencia_slot(string $diaMinimo, ?callable $rand = null, ?int $agora = null): string
{
    $rand = $rand ?? static fn (int $a, int $b): int => $b > $a ? random_int($a, $b) : $a;
    $agora = $agora ?? time();
    $dia = cadencia_dia_util($diaMinimo, 0);
    for ($tentativa = 0; $tentativa < 40; $tentativa++) {
        $meiaNoite = strtotime($dia . ' 00:00:00');
        foreach (cadencia_janelas() as [$ini, $fim]) {
            $abre = $meiaNoite + $ini * 60;
            $fecha = $meiaNoite + $fim * 60;
            if ($fecha <= $agora) {
                continue; // janela ja passou
            }
            $de = max($abre, $agora + 120); // nunca "agora mesmo": o cron roda a cada 10 min
            if ($de >= $fecha) {
                continue;
            }
            return date('Y-m-d H:i:s', $rand($de, $fecha));
        }
        $dia = cadencia_dia_util($dia, 1);
    }
    return date('Y-m-d H:i:s', $agora + 3600);
}

/* ---------- Opt-out assinado ---------- */

function optout_segredo(): string
{
    $s = (string) cfg('optout_secret');
    return $s !== '' ? $s : hash('sha256', (string) cfg('migrate_key') . '|optout');
}

function optout_token(int $leadId, ?int $contactId): string
{
    return substr(hash_hmac('sha256', $leadId . ':' . (int) $contactId, optout_segredo()), 0, 32);
}

function optout_confere(int $leadId, ?int $contactId, string $token): bool
{
    return hash_equals(optout_token($leadId, $contactId), $token);
}

function optout_url(int $leadId, ?int $contactId): string
{
    $base = rtrim((string) (cfg('site_url') ?: 'https://www.mais351monitor.com.br'), '/');
    return $base . '/crm/optout.php?l=' . $leadId . '&c=' . (int) $contactId
        . '&t=' . optout_token($leadId, $contactId);
}

/* ---------- Estado da cadencia ---------- */

function cadencia_estado(int $leadId): ?array
{
    try {
        return row('SELECT * FROM lead_cadence WHERE lead_id = ?', [$leadId]);
    } catch (Throwable $e) {
        return null; // migration 012 ainda nao aplicada
    }
}

function cadencia_auto_ativa(int $leadId): bool
{
    $c = cadencia_estado($leadId);
    return $c !== null && $c['state'] === 'ativa';
}

/** Contato que recebe os e-mails: o escolhido, senao o decisor, senao o principal. */
function cadencia_contato_alvo(int $leadId, ?int $contactId = null): ?array
{
    if ($contactId) {
        $c = row('SELECT * FROM lead_contacts WHERE id = ? AND lead_id = ?', [$contactId, $leadId]);
        if ($c !== null) {
            return $c;
        }
    }
    foreach (contacts_of($leadId) as $c) {
        if (!empty($c['email']) && (int) $c['is_decisor'] === 1) {
            return $c;
        }
    }
    foreach (contacts_of($leadId) as $c) {
        if (!empty($c['email'])) {
            return $c;
        }
    }
    return null;
}

/**
 * O contato tem nome de gente? "Contato", vazio ou o proprio nome da empresa
 * nao servem: o 1o e-mail abre com "Oi, {primeiro_nome}".
 */
function cadencia_nome_ok(?array $contato, array $lead): bool
{
    $nome = trim((string) ($contato['name'] ?? ''));
    if (mb_strlen($nome) < 3) {
        return false;
    }
    $baixo = mb_strtolower($nome);
    if (in_array($baixo, ['contato', 'contatos', 'financeiro', 'comercial', 'empresa'], true)) {
        return false;
    }
    return $baixo !== mb_strtolower(trim((string) $lead['company']));
}

/**
 * O lead pode ENTRAR na cadencia?
 * @return string|null motivo do impedimento, ou null quando pode
 */
function cadencia_pode_iniciar(array $lead, ?array $contato): ?string
{
    if (!empty($lead['no_contact'])) {
        return 'marcado como "nao contactar"';
    }
    if (!in_array($lead['status'], ['novo', 'contato_feito'], true)) {
        return 'status ' . (STATUS_LABELS[$lead['status']] ?? $lead['status']) . ' (fora do topo do funil)';
    }
    if ($lead['duplicate_of_lead_id']) {
        return 'marcado como duplicado do lead #' . (int) $lead['duplicate_of_lead_id'];
    }
    if ($contato === null || empty($contato['email'])) {
        return 'sem contato com e-mail';
    }
    $check = email_check((string) $contato['email']);
    if (in_array($check['status'], ['invalido', 'descartavel', 'bounce'], true)) {
        return 'e-mail ' . $check['status'] . ' (' . $check['detail'] . ')';
    }
    if (!cadencia_nome_ok($contato, $lead)) {
        return 'contato sem nome de pessoa';
    }
    $c = cadencia_estado((int) $lead['id']);
    if ($c !== null && $c['state'] === 'ativa') {
        return 'ja esta em cadencia';
    }
    return null;
}

/**
 * Previa do "Iniciar cadencia" em lote: por que cada lead entra ou nao.
 * @return array{aptos:array, barrados:array}
 */
function cadencia_auto_previa(array $leadIds): array
{
    $aptos = [];
    $barrados = [];
    foreach (array_unique(array_map('intval', $leadIds)) as $id) {
        $lead = row('SELECT * FROM leads WHERE id = ?', [$id]);
        if ($lead === null) {
            continue;
        }
        $contato = cadencia_contato_alvo($id);
        $motivo = cadencia_pode_iniciar($lead, $contato);
        $linha = [
            'lead'    => $lead,
            'contato' => $contato,
            'motivo'  => $motivo,
            'check'   => $contato && !empty($contato['email']) ? email_check((string) $contato['email']) : null,
            // Cadencia anterior encerrada nao impede recomecar, mas quem
            // confirma o lote precisa VER que esse lead ja recebeu a sequencia
            // — senao a lista da semana reenvia o 1o e-mail para quem ja leu.
            'anterior' => cadencia_estado($id),
        ];
        if ($motivo === null) {
            $aptos[] = $linha;
        } else {
            $barrados[] = $linha;
        }
    }
    return ['aptos' => $aptos, 'barrados' => $barrados];
}

/**
 * Coloca o lead em cadencia e planeja o 1o e-mail.
 * @return array{ok:bool, motivo:?string, outbox_id:?int, quando:?string}
 */
function cadencia_auto_iniciar(int $leadId, ?int $senderId, ?int $contactId, ?int $userId, ?string $linhaPessoal = null): array
{
    $lead = row('SELECT * FROM leads WHERE id = ?', [$leadId]);
    if ($lead === null) {
        return ['ok' => false, 'motivo' => 'lead nao encontrado', 'outbox_id' => null, 'quando' => null];
    }
    $contato = cadencia_contato_alvo($leadId, $contactId);
    $motivo = cadencia_pode_iniciar($lead, $contato);
    if ($motivo !== null) {
        return ['ok' => false, 'motivo' => $motivo, 'outbox_id' => null, 'quando' => null];
    }
    if ($senderId !== null && mail_conta_usuario($senderId) === null) {
        return ['ok' => false, 'motivo' => 'remetente sem caixa configurada', 'outbox_id' => null, 'quando' => null];
    }
    $linha = norm_text((string) $linhaPessoal, 300);

    q('INSERT INTO lead_cadence (lead_id, state, current_seq, sender_user_id, contact_id,
                                 personal_line, personal_line_state, started_by, started_at, stop_reason, stopped_at)
       VALUES (?, ?, 0, ?, ?, ?, ?, ?, NOW(), NULL, NULL)
       ON DUPLICATE KEY UPDATE state = VALUES(state), current_seq = 0,
           sender_user_id = VALUES(sender_user_id), contact_id = VALUES(contact_id),
           personal_line = VALUES(personal_line), personal_line_state = VALUES(personal_line_state),
           started_by = VALUES(started_by), started_at = NOW(), stop_reason = NULL,
           stopped_at = NULL, last_sent_at = NULL',
        [$leadId, 'ativa', $senderId, (int) $contato['id'], $linha ?: null,
            $linha !== '' ? 'aprovada' : 'nenhuma', $userId]);

    email_status_gravar((int) $contato['id'], email_check((string) $contato['email'])['status']);
    $out = cadencia_auto_planejar($leadId);
    if ($out !== null) {
        return ['ok' => true, 'motivo' => null,
            'outbox_id' => (int) $out['id'], 'quando' => $out['scheduled_for']];
    }
    // Planejar devolve null tambem quando ELE MESMO encerrou a cadencia (sem
    // caixa configurada, contato sem e-mail). Sem ler o estado de volta, a tela
    // diria "nao ha etapa ativa" — um motivo errado, que manda procurar no
    // lugar errado.
    $depois = cadencia_estado($leadId);
    return [
        'ok'        => false,
        'motivo'    => $depois !== null && !empty($depois['stop_reason'])
            ? (string) $depois['stop_reason']
            : 'nao ha etapa ativa para enviar',
        'outbox_id' => null,
        'quando'    => null,
    ];
}

/** Encerra a cadencia e cancela o que estava agendado. */
function cadencia_auto_parar(int $leadId, string $motivo, string $estado = 'interrompida'): void
{
    $c = cadencia_estado($leadId);
    if ($c === null) {
        return;
    }
    if (!in_array($estado, CADENCIA_ESTADOS, true)) {
        $estado = 'interrompida';
    }
    q('UPDATE lead_cadence SET state = ?, stop_reason = ?, stopped_at = NOW(), next_send_at = NULL WHERE lead_id = ?',
        [$estado, norm_text($motivo, 180), $leadId]);
    q("UPDATE email_outbox SET status = 'cancelado', skip_reason = ?
        WHERE lead_id = ? AND status IN ('aguardando','agendado')",
        [norm_text($motivo, 180), $leadId]);
}

function cadencia_auto_pausar(int $leadId, string $motivo = 'pausada na tela'): void
{
    if (cadencia_estado($leadId) === null) {
        return;
    }
    q("UPDATE lead_cadence SET state = 'pausada', stop_reason = ?, next_send_at = NULL WHERE lead_id = ?",
        [norm_text($motivo, 180), $leadId]);
    q("UPDATE email_outbox SET status = 'cancelado', skip_reason = 'cadencia pausada'
        WHERE lead_id = ? AND status IN ('aguardando','agendado')", [$leadId]);
}

/** Retoma mantendo a etapa: o proximo e-mail e o seguinte ao ultimo enviado. */
function cadencia_auto_retomar(int $leadId): array
{
    $c = cadencia_estado($leadId);
    if ($c === null) {
        return ['ok' => false, 'motivo' => 'lead nunca entrou em cadencia'];
    }
    if ($c['state'] === 'ativa') {
        // Duplo clique, botao "voltar" do navegador, POST repetido: sem esta
        // porta a funcao planejaria um SEGUNDO e-mail da mesma etapa e o lead
        // receberia a mesma mensagem duas vezes.
        $pendente = outbox_pendente_do_lead($leadId);
        return ['ok' => true, 'motivo' => null, 'quando' => $pendente['scheduled_for'] ?? null];
    }
    $lead = row('SELECT * FROM leads WHERE id = ?', [$leadId]);
    $contato = cadencia_contato_alvo($leadId, $c['contact_id'] !== null ? (int) $c['contact_id'] : null);
    if ($lead === null) {
        return ['ok' => false, 'motivo' => 'lead nao encontrado'];
    }
    if (!empty($lead['no_contact'])) {
        return ['ok' => false, 'motivo' => 'lead marcado como "nao contactar"'];
    }
    if (!in_array($lead['status'], ['novo', 'contato_feito'], true)) {
        return ['ok' => false, 'motivo' => 'status fora do topo do funil'];
    }
    if ($contato === null || empty($contato['email'])) {
        return ['ok' => false, 'motivo' => 'sem contato com e-mail'];
    }
    q("UPDATE lead_cadence SET state = 'ativa', stop_reason = NULL, stopped_at = NULL, contact_id = ? WHERE lead_id = ?",
        [(int) $contato['id'], $leadId]);
    $out = cadencia_auto_planejar($leadId);
    return ['ok' => true, 'motivo' => null, 'quando' => $out['scheduled_for'] ?? null];
}

/* ---------- Planejamento do proximo e-mail ---------- */

/** Ultimo e-mail que saiu para o lead (para encadear a conversa). */
function cadencia_ultimo_enviado(int $leadId): ?array
{
    return row("SELECT * FROM email_outbox WHERE lead_id = ? AND status = 'enviado'
                ORDER BY sent_at DESC, id DESC LIMIT 1", [$leadId]);
}

/**
 * Texto final do e-mail: modelo da etapa + linha pessoal + rodape de opt-out.
 * @return array{assunto:string, corpo:string}
 */
function cadencia_auto_render(int $seq, array $lead, ?array $contato, ?string $meuNome, ?string $linhaPessoal, string $optoutUrl): array
{
    $m = cadencia_email_modelo($seq, $lead, $contato, $meuNome, $linhaPessoal);
    $m['corpo'] = rtrim($m['corpo']) . "\n\n" . str_replace('{link}', $optoutUrl, CADENCIA_OPTOUT_RODAPE);
    return $m;
}

/**
 * Cria a linha do outbox da PROXIMA etapa. Devolve a linha criada, ou null
 * quando a cadencia acabou (nesse caso ela e concluida aqui mesmo).
 */
function cadencia_auto_planejar(int $leadId, ?callable $rand = null): ?array
{
    $c = cadencia_estado($leadId);
    if ($c === null || $c['state'] !== 'ativa') {
        return null;
    }
    $atual = (int) $c['current_seq'];
    $proxima = cadencia_etapa_seguinte($atual);
    if ($proxima === null) {
        cadencia_auto_concluir($leadId, $c);
        return null;
    }
    $lead = row('SELECT * FROM leads WHERE id = ?', [$leadId]);
    $contato = cadencia_contato_alvo($leadId, $c['contact_id'] !== null ? (int) $c['contact_id'] : null);
    if ($lead === null || $contato === null || empty($contato['email'])) {
        cadencia_auto_parar($leadId, 'sem contato com e-mail');
        return null;
    }
    $senderId = $c['sender_user_id'] !== null ? (int) $c['sender_user_id'] : null;
    $conta = mail_conta_usuario($senderId);
    if ($conta === null) {
        $contas = mail_contas();
        $conta = $contas ? reset($contas) : null;
    }
    if ($conta === null) {
        cadencia_auto_parar($leadId, 'nenhuma caixa de e-mail configurada');
        return null;
    }
    $remetente = $senderId !== null ? row('SELECT name FROM users WHERE id = ?', [$senderId]) : null;
    // {meu_nome} vazio assina o e-mail com " | +351 Monitor" e um pipe solto na
    // frente. Sem usuario, o primeiro nome da caixa resolve ("Bruna | +351...").
    $meuNome = trim((string) ($remetente['name'] ?? ''));
    if ($meuNome === '') {
        $meuNome = trim(explode('|', (string) $conta['nome'])[0]) ?: '+351 Monitor';
    }

    // Espera: dias uteis DEPOIS da etapa que acabou de sair (0 quando e a 1a).
    $base = $c['last_sent_at'] ?: date('Y-m-d H:i:s');
    $dias = $atual > 0 ? setting_int('cadencia_email_' . $atual) : 0;
    $dia = cadencia_dia_util(date('Y-m-d', strtotime((string) $base)), $dias);
    if ($dia < date('Y-m-d')) {
        $dia = date('Y-m-d'); // etapa atrasada nunca nasce no passado
    }
    $quando = cadencia_slot($dia, $rand);

    $anterior = cadencia_ultimo_enviado($leadId);
    $optout = optout_url($leadId, (int) $contato['id']);
    $render = cadencia_auto_render($proxima, $lead, $contato, $meuNome,
        $c['personal_line_state'] === 'aprovada' ? (string) $c['personal_line'] : null, $optout);

    $refs = null;
    if ($anterior !== null) {
        $refs = trim((string) ($anterior['references_hdr'] ?? '') . ' ' . $anterior['message_id']);
    }

    // A invariante "um e-mail pendente por lead" e garantida AQUI, no unico
    // ponto que insere no outbox. Qualquer caminho que replaneje (retomar,
    // trocar a linha pessoal, destravar) passa por esta linha, entao nenhum
    // deles consegue deixar dois e-mails da mesma etapa na fila.
    q("UPDATE email_outbox SET status = 'cancelado', skip_reason = 'substituido por novo planejamento'
        WHERE lead_id = ? AND status IN ('aguardando','agendado')", [$leadId]);

    q('INSERT INTO email_outbox (lead_id, contact_id, seq, sender_user_id, from_email, from_name,
                                 to_email, to_name, subject, body, message_id, in_reply_to,
                                 references_hdr, status, scheduled_for)
       VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)', [
        $leadId,
        (int) $contato['id'],
        $proxima,
        $senderId,
        (string) $conta['email'],
        (string) $conta['nome'],
        (string) $contato['email'],
        (string) $contato['name'],
        mb_substr($render['assunto'], 0, 255),
        $render['corpo'],
        mail_message_id(mail_dominio((string) $conta['email'])),
        $anterior['message_id'] ?? null,
        $refs,
        setting_str('auto_modo') === 'automatico' ? 'agendado' : 'aguardando',
        $quando,
    ]);
    $id = last_id();
    q('UPDATE lead_cadence SET next_send_at = ? WHERE lead_id = ?', [$quando, $leadId]);
    return row('SELECT * FROM email_outbox WHERE id = ?', [$id]);
}

/** Fim da sequencia: conclui e deixa a tarefa de retomada, como na cadencia manual. */
function cadencia_auto_concluir(int $leadId, ?array $c = null): void
{
    $c = $c ?? cadencia_estado($leadId);
    q("UPDATE lead_cadence SET state = 'concluida', stop_reason = 'sequencia concluida',
              stopped_at = NOW(), next_send_at = NULL WHERE lead_id = ?", [$leadId]);
    if (lead_no_contact($leadId)) {
        return;
    }
    $dias = setting_int('cadencia_email_' . CADENCIA_EMAIL_PASSOS);
    $hora = max(0, min(23, setting_int('cadencia_hora')));
    $due = cadencia_dia_util(date('Y-m-d'), $dias) . sprintf(' %02d:00:00', $hora);
    $userId = $c && $c['sender_user_id'] !== null ? (int) $c['sender_user_id'] : null;
    try {
        task_add($leadId, 'Cadencia concluida sem resposta — retomar ou marcar perdido', $due,
            $userId, $userId, TASK_KIND_CADENCIA);
    } catch (Throwable $e) {
        error_log('cadencia concluir: ' . $e->getMessage());
    }
}

/**
 * O humano registrou "3o e-mail enviado" na mao num lead que esta em cadencia
 * automatica: o motor adota a etapa dele e replaneja em vez de duplicar.
 */
function cadencia_sincronizar_manual(int $leadId, int $seq, ?string $ocorridaEm): ?string
{
    q("UPDATE email_outbox SET status = 'cancelado', skip_reason = 'etapa registrada a mao'
        WHERE lead_id = ? AND status IN ('aguardando','agendado')", [$leadId]);
    q('UPDATE lead_cadence SET current_seq = GREATEST(current_seq, ?), last_sent_at = ? WHERE lead_id = ?',
        [$seq, $ocorridaEm ?: date('Y-m-d H:i:s'), $leadId]);
    $out = cadencia_auto_planejar($leadId);
    return $out['scheduled_for'] ?? null;
}

/* ---------- Tetos e ritmo ---------- */

/** Teto do dia por caixa, seguindo o aquecimento (semana 1, semana 2, regime). */
function cadencia_teto_hoje(?int $agora = null): int
{
    $agora = $agora ?? time();
    $inicio = trim(setting_str('auto_inicio'));
    if ($inicio === '') {
        return max(1, setting_int('auto_teto_sem1'));
    }
    $ts = strtotime($inicio);
    if ($ts === false) {
        return max(1, setting_int('auto_teto'));
    }
    $dias = (int) floor(($agora - $ts) / 86400);
    if ($dias < 7) {
        return max(1, setting_int('auto_teto_sem1'));
    }
    if ($dias < 14) {
        return max(1, setting_int('auto_teto_sem2'));
    }
    return max(1, setting_int('auto_teto'));
}

/**
 * Quantos e-mails a CAIXA ja mandou hoje.
 *
 * Conta por from_email, nao por usuario: dois usuarios sem caixa propria caem
 * no mesmo fallback de remetente, e contando por usuario cada um levaria o
 * teto inteiro — o dobro do volume saindo de uma caixa so, com o painel
 * mostrando "30/30". Quem tem limite na Hostinger e a caixa.
 */
function cadencia_enviados_hoje(?string $fromEmail): int
{
    if ($fromEmail === null || $fromEmail === '') {
        return (int) scalar("SELECT COUNT(*) FROM email_outbox
                             WHERE status = 'enviado' AND DATE(sent_at) = CURDATE()");
    }
    return (int) scalar("SELECT COUNT(*) FROM email_outbox
                         WHERE status = 'enviado' AND DATE(sent_at) = CURDATE() AND from_email = ?",
        [mb_strtolower($fromEmail)]);
}

/** Outro lead ja recebeu e-mail neste dominio nos ultimos N dias? */
function cadencia_dominio_quente(string $email, int $leadId): bool
{
    $dominio = mail_dominio($email);
    if ($dominio === '') {
        return false;
    }
    $dias = max(0, setting_int('auto_dominio_dias'));
    if ($dias === 0) {
        return false;
    }
    $n = (int) scalar("SELECT COUNT(*) FROM email_outbox
                       WHERE status = 'enviado' AND lead_id <> ?
                         AND sent_at >= DATE_SUB(NOW(), INTERVAL ? DAY)
                         AND SUBSTRING_INDEX(to_email, '@', -1) = ?",
        [$leadId, $dias, $dominio]);
    return $n > 0;
}

/* ---------- Guardas do envio ---------- */

/**
 * A DECISAO das guardas, pura: recebe tudo o que ja foi lido do banco e diz o
 * que fazer. Fica separada da leitura porque e a logica que impede escrever
 * para quem pediu para sair — e isso precisa de teste, nao de confianca.
 *
 * A ordem importa: primeiro tudo que ENCERRA (o lead nao devia mais receber),
 * depois o que so ADIA (hoje nao da, amanha da).
 *
 * @param array{cadencia:?array, respondeu:bool, check_status:string,
 *              check_detail:string, na_janela:bool, enviados_hoje:int,
 *              teto:int, dominio_quente:bool, dominio_dias:int} $ctx
 * @return array{acao:string, motivo:string}|null acao: 'parar' | 'adiar'
 */
function cadencia_guardas_puras(?array $lead, ?array $contato, array $out, array $ctx): ?array
{
    $parar = static fn (string $m): array => ['acao' => 'parar', 'motivo' => $m];
    $adiar = static fn (string $m): array => ['acao' => 'adiar', 'motivo' => $m];

    if ($lead === null) {
        return $parar('lead removido');
    }
    if (!empty($lead['no_contact'])) {
        return $parar('lead pediu para nao ser contactado');
    }
    if (!in_array($lead['status'], ['novo', 'contato_feito'], true)) {
        return $parar('status avancou para ' . (STATUS_LABELS[$lead['status']] ?? $lead['status']));
    }
    if (!empty($lead['duplicate_of_lead_id'])) {
        return $parar('lead duplicado do #' . (int) $lead['duplicate_of_lead_id']);
    }
    $estado = $ctx['cadencia']['state'] ?? null;
    if ($estado !== 'ativa') {
        return $parar('cadencia ' . ($estado ?? 'inexistente'));
    }
    if (!empty($ctx['respondeu'])) {
        return $parar('resposta ja recebida');
    }
    if (in_array($ctx['check_status'] ?? '', ['invalido', 'descartavel', 'bounce'], true)) {
        return $parar('e-mail ' . $ctx['check_status'] . ' (' . ($ctx['check_detail'] ?? '') . ')');
    }
    if (!cadencia_nome_ok($contato, $lead)) {
        return $parar('contato sem nome de pessoa');
    }
    if (str_contains((string) $out['subject'] . (string) $out['body'], '{')) {
        // PAUSA, nao encerra: uma chave sobrando quase sempre e a linha pessoal
        // com uma chave digitada por engano, e isso conserta em 10 segundos.
        // Encerrar a cadencia por causa disso seria perder o lead por um typo.
        return ['acao' => 'pausar', 'motivo' => 'modelo com chave nao substituida'];
    }
    if (empty($ctx['na_janela'])) {
        return $adiar('fora da janela de envio');
    }
    if ((int) ($ctx['enviados_hoje'] ?? 0) >= (int) ($ctx['teto'] ?? 0)) {
        return $adiar('teto diario da caixa');
    }
    if (!empty($ctx['dominio_quente'])) {
        return $adiar('outro lead do mesmo dominio recebeu e-mail nos ultimos '
            . (int) ($ctx['dominio_dias'] ?? 0) . ' dias');
    }
    return null;
}

/** Le o mundo no instante do envio e entrega a decisao a cadencia_guardas_puras(). */
function cadencia_auto_guardas(array $out, ?int $agora = null): ?array
{
    $agora = $agora ?? time();
    $leadId = (int) $out['lead_id'];
    $lead = row('SELECT * FROM leads WHERE id = ?', [$leadId]);
    if ($lead === null) {
        return ['acao' => 'parar', 'motivo' => 'lead removido'];
    }
    $check = email_check((string) $out['to_email']);
    $contato = $out['contact_id'] !== null
        ? row('SELECT * FROM lead_contacts WHERE id = ?', [(int) $out['contact_id']])
        : null;

    return cadencia_guardas_puras($lead, $contato, $out, [
        'cadencia'       => cadencia_estado($leadId),
        'respondeu'      => cadencia_resposta_recebida($leadId),
        'check_status'   => $check['status'],
        'check_detail'   => $check['detail'],
        'na_janela'      => cadencia_dentro_da_janela($agora),
        'enviados_hoje'  => cadencia_enviados_hoje((string) $out['from_email']),
        'teto'           => cadencia_teto_hoje($agora),
        'dominio_quente' => cadencia_dominio_quente((string) $out['to_email'], $leadId),
        'dominio_dias'   => setting_int('auto_dominio_dias'),
    ]);
}

/** Ja chegou resposta humana (ou opt-out) deste lead? */
function cadencia_resposta_recebida(int $leadId): bool
{
    try {
        return (int) scalar("SELECT COUNT(*) FROM email_inbound
                             WHERE lead_id = ? AND kind IN ('humana','optout')", [$leadId]) > 0;
    } catch (Throwable $e) {
        return false; // migration 014 ainda nao aplicada
    }
}

/* ---------- Envio ---------- */

/**
 * Envia UMA linha do outbox, ja reservada por quem chamou.
 * @return array{resultado:string, motivo:string}
 */
function cadencia_enviar_linha(array $out): array
{
    $id = (int) $out['id'];
    $leadId = (int) $out['lead_id'];

    $guarda = cadencia_auto_guardas($out);
    if ($guarda !== null) {
        if ($guarda['acao'] === 'parar') {
            q("UPDATE email_outbox SET status = 'pulado', skip_reason = ? WHERE id = ?",
                [$guarda['motivo'], $id]);
            cadencia_auto_parar($leadId, $guarda['motivo']);
            if (str_contains($guarda['motivo'], 'contato sem nome')) {
                cadencia_tarefa_humana($leadId, 'Conseguir o nome do contato (cadencia parou sem ele)', 1,
                    $out['sender_user_id'] !== null ? (int) $out['sender_user_id'] : null, 'manual');
            }
            if (str_contains($guarda['motivo'], 'e-mail invalido') || str_contains($guarda['motivo'], 'e-mail bounce')) {
                cadencia_tarefa_humana($leadId, 'Corrigir o e-mail do contato', 1,
                    $out['sender_user_id'] !== null ? (int) $out['sender_user_id'] : null, 'manual');
            }
            return ['resultado' => 'parado', 'motivo' => $guarda['motivo']];
        }
        if ($guarda['acao'] === 'pausar') {
            q("UPDATE email_outbox SET status = 'pulado', skip_reason = ? WHERE id = ?",
                [$guarda['motivo'], $id]);
            cadencia_auto_pausar($leadId, $guarda['motivo']);
            cadencia_tarefa_humana($leadId, 'Corrigir o texto do e-mail e retomar a cadencia', 1,
                $out['sender_user_id'] !== null ? (int) $out['sender_user_id'] : null, 'manual');
            return ['resultado' => 'pausado', 'motivo' => $guarda['motivo']];
        }
        // adiar: volta para a fila no proximo slot util.
        //
        // "Fora da janela" pode ser resolvido HOJE mesmo (o cron das 11:04
        // ainda tem a janela da tarde), e cadencia_slot ja sabe pular as
        // janelas que fecharam. Mandar tudo para amanha empurraria um dia
        // inteiro os ultimos minutos de cada janela, em silencio. Teto batido e
        // dominio quente, ao contrario, so mudam com a virada do dia.
        $de = $guarda['motivo'] === 'fora da janela de envio'
            ? date('Y-m-d')
            : cadencia_dia_util(date('Y-m-d'), 1);
        $novo = cadencia_slot($de);
        q("UPDATE email_outbox SET status = 'agendado', scheduled_for = ?, skip_reason = ? WHERE id = ?",
            [$novo, $guarda['motivo'], $id]);
        q('UPDATE lead_cadence SET next_send_at = ? WHERE lead_id = ?', [$novo, $leadId]);
        return ['resultado' => 'adiado', 'motivo' => $guarda['motivo']];
    }

    $conta = mail_conta((string) $out['from_email']);
    if ($conta === null) {
        q("UPDATE email_outbox SET status = 'falhou', last_error = ? WHERE id = ?",
            ['caixa ' . $out['from_email'] . ' sem credencial no crm_config.php', $id]);
        return ['resultado' => 'falhou', 'motivo' => 'caixa sem credencial'];
    }

    // Sandbox: o e-mail sai de verdade, mas para a nossa propria caixa.
    $paraReal = (string) $out['to_email'];
    $para = $paraReal;
    $assunto = (string) $out['subject'];
    $corpo = (string) $out['body'];
    if (setting_bool('auto_sandbox')) {
        $destinoSandbox = norm_email(setting_str('auto_sandbox_para'));
        // norm_email devolve null (vazio) ou false (invalido): os dois caem na
        // propria caixa remetente, que sempre existe.
        $para = is_string($destinoSandbox) && $destinoSandbox !== ''
            ? $destinoSandbox
            : (string) $conta['email'];
        $assunto = '[SANDBOX] ' . $assunto;
        $corpo = "[SANDBOX] este e-mail iria para {$paraReal} (lead #{$leadId}).\n\n" . $corpo;
    }

    $r = mail_enviar($conta, [
        'de_nome'         => (string) $out['from_name'],
        'de_email'        => (string) $conta['email'],
        'para_nome'       => (string) $out['to_name'],
        'para_email'      => $para,
        'assunto'         => $assunto,
        'corpo'           => $corpo,
        'message_id'      => (string) $out['message_id'],
        'in_reply_to'     => $out['in_reply_to'],
        'references'      => $out['references_hdr'],
        'unsubscribe_url' => optout_url($leadId, $out['contact_id'] !== null ? (int) $out['contact_id'] : null),
    ]);

    if (!$r['ok']) {
        if (!empty($r['incerto'])) {
            // A mensagem chegou a ser entregue ao servidor e a confirmacao nao
            // voltou. A linha FICA em 'enviando' e aparece em "Travados em
            // envio", onde alguem confere a caixa de Enviados e decide. Um
            // retry automatico aqui manda o mesmo e-mail duas vezes.
            q("UPDATE email_outbox SET last_error = ? WHERE id = ?",
                [mb_substr('entrega incerta: ' . $r['erro'], 0, 255), $id]);
            return ['resultado' => 'incerto', 'motivo' => $r['erro']];
        }
        $tentativas = (int) $out['attempts'];
        if ($tentativas < 3) {
            q("UPDATE email_outbox SET status = 'agendado', scheduled_for = DATE_ADD(NOW(), INTERVAL 30 MINUTE),
                      last_error = ? WHERE id = ?", [mb_substr($r['erro'], 0, 255), $id]);
            return ['resultado' => 'retentar', 'motivo' => $r['erro']];
        }
        q("UPDATE email_outbox SET status = 'falhou', last_error = ? WHERE id = ?",
            [mb_substr($r['erro'], 0, 255), $id]);
        cadencia_tarefa_humana($leadId, 'Envio de e-mail falhou 3 vezes — verificar', 1,
            $out['sender_user_id'] !== null ? (int) $out['sender_user_id'] : null, 'manual');
        return ['resultado' => 'falhou', 'motivo' => $r['erro']];
    }

    // Sucesso: registra a interacao pela mesma porta que a tela usa.
    $seq = (int) $out['seq'];
    $resumo = (CADENCIA_EMAIL_LABELS[$seq] ?? ('E-mail ' . $seq))
        . ' enviado automaticamente para ' . $paraReal . '. Assunto: ' . $out['subject'];
    $interactionId = null;
    try {
        $interactionId = interaction_add($leadId, 'email', $resumo, date('Y-m-d H:i:s'),
            $out['sender_user_id'] !== null ? (int) $out['sender_user_id'] : null, $seq);
    } catch (Throwable $e) {
        error_log('cadencia interacao: ' . $e->getMessage());
    }
    q("UPDATE email_outbox SET status = 'enviado', sent_at = NOW(), interaction_id = ?, last_error = NULL
        WHERE id = ?", [$interactionId, $id]);
    q('UPDATE lead_cadence SET current_seq = ?, last_sent_at = NOW() WHERE lead_id = ?', [$seq, $leadId]);
    email_status_gravar($out['contact_id'] !== null ? (int) $out['contact_id'] : null, 'valido');

    // O 1o e-mail tira o lead de "Novo" e abre os toques humanos do playbook.
    if ($seq === 1) {
        $lead = row('SELECT status FROM leads WHERE id = ?', [$leadId]);
        if ($lead !== null && $lead['status'] === 'novo') {
            try {
                lead_set_status($leadId, 'contato_feito', null,
                    $out['sender_user_id'] !== null ? (int) $out['sender_user_id'] : null);
            } catch (Throwable $e) {
                error_log('cadencia status: ' . $e->getMessage());
            }
        }
        $sender = $out['sender_user_id'] !== null ? (int) $out['sender_user_id'] : null;
        $nome = (string) $out['to_name'];
        cadencia_tarefa_humana($leadId, 'Ligar para ' . $nome . ' — script de 30s',
            max(1, setting_int('cadencia_ligacao_dias')), $sender, TASK_KIND_LIGACAO);
        cadencia_tarefa_humana($leadId, 'LinkedIn: conexao + nota curta para ' . $nome,
            max(1, setting_int('cadencia_linkedin_dias')), $sender, TASK_KIND_LINKEDIN);
    }

    cadencia_auto_planejar($leadId);
    return ['resultado' => 'enviado', 'motivo' => ''];
}

/**
 * Tarefa humana da cadencia, em N dias uteis. Nunca derruba o envio.
 *
 * A deduplicacao olha o TITULO junto com o tipo: com kind 'manual' (o caso de
 * "Responder fulano"), so o tipo faria uma tarefa manual qualquer ja aberta
 * no lead engolir o pedido novo.
 */
function cadencia_tarefa_humana(int $leadId, string $titulo, int $diasUteis, ?int $userId, string $kind): void
{
    try {
        $titulo = norm_text($titulo, 200);
        if ((int) scalar('SELECT COUNT(*) FROM tasks
                          WHERE lead_id = ? AND kind = ? AND title = ? AND done_at IS NULL',
            [$leadId, $kind, $titulo]) > 0) {
            return; // ja existe uma aberta igual
        }
        if ($diasUteis <= 0) {
            // "para hoje": daqui a duas horas, senao a tarefa ja nasce vencida
            // (a hora padrao da cadencia e 09:00) e polui a lista de atrasadas.
            $due = date('Y-m-d H:i:s', time() + 7200);
        } else {
            $hora = max(0, min(23, setting_int('cadencia_hora')));
            $due = cadencia_dia_util(date('Y-m-d'), $diasUteis) . sprintf(' %02d:00:00', $hora);
        }
        task_add($leadId, $titulo, $due, $userId, $userId, $kind);
    } catch (Throwable $e) {
        error_log('cadencia tarefa: ' . $e->getMessage());
    }
}

/* ---------- Execucao (chamada pelo cron) ---------- */

/**
 * Uma passada do motor de ENVIO. A leitura da caixa fica em inbound.php e o
 * cron chama as duas.
 * @return array{enviados:int, adiados:int, parados:int, falhas:int, detalhes:string[]}
 */
function cadencia_auto_tick(): array
{
    $r = ['enviados' => 0, 'adiados' => 0, 'parados' => 0, 'falhas' => 0, 'detalhes' => []];
    if (!setting_bool('auto_ligada')) {
        $r['detalhes'][] = 'interruptor geral desligado';
        return $r;
    }
    $max = max(1, setting_int('auto_max_tick'));
    // Pega mais linhas do que o teto do tick: pular/adiar nao consome cota.
    // LIMIT vai interpolado (int) de proposito: com EMULATE_PREPARES desligado
    // o MySQL recusa um LIMIT vindo de placeholder, que chega como string.
    $teto = $max * 4;
    $fila = rows("SELECT * FROM email_outbox
                  WHERE status = 'agendado' AND scheduled_for <= NOW()
                  ORDER BY scheduled_for LIMIT $teto");
    foreach ($fila as $out) {
        if ($r['enviados'] >= $max) {
            break;
        }
        // Reserva atomica: dois ticks simultaneos nunca enviam a mesma linha.
        $st = q("UPDATE email_outbox SET status = 'enviando', attempts = attempts + 1
                 WHERE id = ? AND status = 'agendado'", [(int) $out['id']]);
        if ($st->rowCount() !== 1) {
            continue;
        }
        $out['attempts'] = (int) $out['attempts'] + 1;
        try {
            $res = cadencia_enviar_linha($out);
        } catch (Throwable $e) {
            error_log('cadencia envio: ' . $e->getMessage());
            q("UPDATE email_outbox SET status = 'falhou', last_error = ? WHERE id = ?",
                [mb_substr($e->getMessage(), 0, 255), (int) $out['id']]);
            $res = ['resultado' => 'falhou', 'motivo' => $e->getMessage()];
        }
        $r['detalhes'][] = '#' . $out['id'] . ' ' . $res['resultado']
            . ($res['motivo'] !== '' ? ' — ' . $res['motivo'] : '');
        match ($res['resultado']) {
            'enviado'           => $r['enviados']++,
            'adiado'            => $r['adiados']++,
            'parado', 'pausado' => $r['parados']++,
            'falhou', 'incerto' => $r['falhas']++,
            default             => null,
        };
    }
    return $r;
}

/* ---------- Consultas para as telas e a API ---------- */

function outbox_do_lead(int $leadId, int $limite = 20): array
{
    return rows('SELECT * FROM email_outbox WHERE lead_id = ? ORDER BY id DESC LIMIT ' . max(1, $limite), [$leadId]);
}

function outbox_pendente_do_lead(int $leadId): ?array
{
    return row("SELECT * FROM email_outbox WHERE lead_id = ? AND status IN ('aguardando','agendado')
                ORDER BY scheduled_for LIMIT 1", [$leadId]);
}

/**
 * Estado da cadencia de varios leads de uma vez (a lista de Leads mostra um
 * selo por linha; uma consulta por lead deixaria a pagina lenta a toa).
 * @return array<int, array{state:string, seq:int, next:?string}>
 */
function cadencias_dos_leads(array $leadIds): array
{
    $ids = array_values(array_unique(array_map('intval', $leadIds)));
    if (!$ids) {
        return [];
    }
    $marks = implode(',', array_fill(0, count($ids), '?'));
    $out = [];
    try {
        foreach (rows("SELECT lead_id, state, current_seq, next_send_at FROM lead_cadence
                       WHERE lead_id IN ($marks)", $ids) as $r) {
            $out[(int) $r['lead_id']] = [
                'state' => (string) $r['state'],
                'seq'   => (int) $r['current_seq'],
                'next'  => $r['next_send_at'],
            ];
        }
    } catch (Throwable $e) {
        return []; // migration 012 ainda nao aplicada
    }
    return $out;
}

/** Contadores do dashboard e da tela de envios. */
function cadencia_painel(): array
{
    $p = ['aguardando' => 0, 'hoje' => 0, 'enviados_hoje' => 0, 'falhas' => 0,
        'retornos' => 0, 'teto' => 0, 'ultimo_tick' => '', 'ligada' => false,
        'modo' => 'aprovacao', 'por_caixa' => []];
    try {
        $p['aguardando']    = (int) scalar("SELECT COUNT(*) FROM email_outbox WHERE status = 'aguardando'");
        $p['hoje']          = (int) scalar("SELECT COUNT(*) FROM email_outbox WHERE status = 'agendado' AND DATE(scheduled_for) = CURDATE()");
        $p['enviados_hoje'] = (int) scalar("SELECT COUNT(*) FROM email_outbox WHERE status = 'enviado' AND DATE(sent_at) = CURDATE()");
        // O teto e POR CAIXA: com duas caixas, o total do dia nao cabe num
        // "12/15" so. A quebra por caixa e o que diz se alguma esta no limite.
        foreach (rows("SELECT from_email, COUNT(*) AS n FROM email_outbox
                       WHERE status = 'enviado' AND DATE(sent_at) = CURDATE()
                       GROUP BY from_email ORDER BY n DESC") as $r) {
            $p['por_caixa'][(string) $r['from_email']] = (int) $r['n'];
        }
        $p['falhas']        = (int) scalar("SELECT COUNT(*) FROM email_outbox WHERE status = 'falhou'");
        $p['teto']          = cadencia_teto_hoje();
        $p['ligada']        = setting_bool('auto_ligada');
        $p['modo']          = setting_str('auto_modo');
        $p['ultimo_tick']   = state_get('cron_ultimo');
    } catch (Throwable $e) {
        // migration 012 ainda nao aplicada
    }
    try {
        $p['retornos'] = (int) scalar("SELECT COUNT(*) FROM email_inbound
                                       WHERE handled_at IS NULL AND kind IN ('humana','optout','bounce')");
    } catch (Throwable $e) {
    }
    return $p;
}

/**
 * Relatorio de calibracao do mes (rota cadence-report da API): o que o ritual
 * de segunda e o relatorio mensal do Claude leem.
 */
function cadencia_auto_relatorio(string $mes): array
{
    if (!preg_match('/^\d{4}-(0[1-9]|1[0-2])$/', $mes)) {
        throw new InvalidArgumentException('Mes invalido — use AAAA-MM.');
    }
    $ini = $mes . '-01 00:00:00';
    $fim = date('Y-m-t 23:59:59', strtotime($ini));
    $porEtapa = [];
    foreach (rows("SELECT seq, COUNT(*) AS n FROM email_outbox
                   WHERE status = 'enviado' AND sent_at BETWEEN ? AND ? GROUP BY seq ORDER BY seq",
        [$ini, $fim]) as $r) {
        $porEtapa[(int) $r['seq']] = (int) $r['n'];
    }
    $enviados = array_sum($porEtapa);
    $conta = function (string $kind) use ($ini, $fim): int {
        try {
            return (int) scalar('SELECT COUNT(DISTINCT lead_id) FROM email_inbound
                                 WHERE kind = ? AND received_at BETWEEN ? AND ? AND lead_id IS NOT NULL',
                [$kind, $ini, $fim]);
        } catch (Throwable $e) {
            return 0;
        }
    };
    $respostas = $conta('humana');
    $bounces = $conta('bounce');
    $optouts = $conta('optout');
    $motivos = [];
    foreach (rows('SELECT stop_reason, COUNT(*) AS n FROM lead_cadence
                   WHERE stopped_at BETWEEN ? AND ? GROUP BY stop_reason ORDER BY n DESC',
        [$ini, $fim]) as $r) {
        $motivos[(string) ($r['stop_reason'] ?? 'sem motivo')] = (int) $r['n'];
    }
    $pct = fn (int $n): float => $enviados > 0 ? round($n * 100 / $enviados, 2) : 0.0;
    return [
        'mes'            => $mes,
        'enviados'       => $enviados,
        'por_etapa'      => $porEtapa,
        'respostas'      => $respostas,
        'bounces'        => $bounces,
        'optouts'        => $optouts,
        'taxa_resposta'  => $pct($respostas),
        'taxa_bounce'    => $pct($bounces),
        'taxa_optout'    => $pct($optouts),
        'demos'          => (int) scalar("SELECT COUNT(*) FROM interactions WHERE type = 'demo' AND occurred_at BETWEEN ? AND ?", [$ini, $fim]),
        'saidas'         => $motivos,
    ];
}
