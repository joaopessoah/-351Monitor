<?php
/**
 * Retorno: le a caixa de quem envia, classifica o que chegou e age no lead.
 *
 * Vies deliberado: na duvida, PARA a cadencia. Parar sem necessidade custa um
 * e-mail a menos; continuar escrevendo para quem respondeu (ou pediu para
 * sair) custa a marca.
 *
 * Privacidade: guardamos assunto + 400 caracteres de trecho. A mensagem
 * inteira continua na caixa de e-mail, que ja e o registro dela, e o trecho
 * some junto com o lead (CASCADE na migration 014).
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

const INBOUND_KIND_LABELS = [
    'humana'    => 'Resposta',
    'auto'      => 'Resposta automatica',
    'bounce'    => 'Devolvido',
    'optout'    => 'Pediu para sair',
    'ignorada'  => 'Ignorada',
];

/* ---------- Parsing (puro) ---------- */

/**
 * Cabecalhos crus -> mapa minusculo => valor, com as linhas dobradas juntadas.
 * Cabecalho repetido mantem a PRIMEIRA ocorrencia (a mais externa).
 */
function inbound_headers(string $raw): array
{
    $raw = preg_replace('/\r\n|\r/', "\n", $raw);
    $raw = preg_replace('/\n[ \t]+/', ' ', $raw); // unfolding
    $out = [];
    foreach (explode("\n", $raw) as $linha) {
        if (!preg_match('/^([A-Za-z0-9\-_]+):\s?(.*)$/', $linha, $m)) {
            continue;
        }
        $k = strtolower($m[1]);
        if (!array_key_exists($k, $out)) {
            $out[$k] = trim($m[2]);
        }
    }
    return $out;
}

/** Decodifica encoded-words (=?UTF-8?B?...?=) de assunto e nome do remetente. */
function inbound_decode_mime(string $s): string
{
    // O espaco ENTRE dois encoded-words nao faz parte do texto (RFC 2047) e
    // some antes da decodificacao — depois dela nao da mais para distinguir
    // esse espaco de um que estava dentro do proprio texto codificado.
    $s = (string) preg_replace('/(\?=)[ \t\r\n]+(=\?)/', '$1$2', $s);
    $out = preg_replace_callback(
        '/=\?([A-Za-z0-9\-_]+)\?([BbQq])\?([^?]*)\?=/',
        static function (array $m): string {
            $charset = strtoupper($m[1]);
            $txt = strtoupper($m[2]) === 'B'
                ? (base64_decode($m[3], false) ?: '')
                : quoted_printable_decode(str_replace('_', ' ', $m[3]));
            if ($charset !== 'UTF-8' && $charset !== 'US-ASCII' && function_exists('mb_convert_encoding')) {
                $conv = @mb_convert_encoding($txt, 'UTF-8', $charset);
                if (is_string($conv) && $conv !== '') {
                    $txt = $conv;
                }
            }
            return $txt;
        },
        $s
    );
    // Espaco entre dois encoded-words adjacentes nao conta (RFC 2047)
    return trim((string) $out);
}

/** "Fulano <a@b.com>" -> ['nome' => 'Fulano', 'email' => 'a@b.com'] */
function inbound_endereco(string $header): array
{
    $header = inbound_decode_mime(trim($header));
    if (preg_match('/^(.*)<([^>]+)>\s*$/s', $header, $m)) {
        $nome = trim(trim($m[1]), '"\' ');
        $email = mb_strtolower(trim($m[2]));
    } else {
        $nome = '';
        $email = mb_strtolower(trim($header, '<> '));
    }
    return ['nome' => mb_substr($nome, 0, 120), 'email' => filter_var($email, FILTER_VALIDATE_EMAIL) ? $email : ''];
}

/** Corpo decodificado conforme o Content-Transfer-Encoding declarado. */
function inbound_decode_corpo(string $corpo, string $cte, string $charset): string
{
    $cte = strtolower(trim($cte));
    if ($cte === 'base64') {
        $corpo = (string) base64_decode(preg_replace('/\s+/', '', $corpo), false);
    } elseif ($cte === 'quoted-printable') {
        $corpo = quoted_printable_decode($corpo);
    }
    $charset = strtoupper(trim($charset)) ?: 'UTF-8';
    if ($charset !== 'UTF-8' && $charset !== 'US-ASCII' && function_exists('mb_convert_encoding')) {
        $conv = @mb_convert_encoding($corpo, 'UTF-8', $charset);
        if (is_string($conv) && $conv !== '') {
            $corpo = $conv;
        }
    }
    return $corpo;
}

/**
 * HTML -> texto preservando as quebras de linha.
 *
 * strip_tags() sozinho cola tudo numa linha, e e a QUEBRA que o corte de
 * citacao procura: "<div>De: Bruna</div><div>Enviada em: ...</div>" viraria
 * "De: BrunaEnviada em: ..." e a citacao inteira (com o nosso rodape) entraria
 * no texto tratado como se a pessoa tivesse escrito. Resposta de Outlook e
 * quase sempre HTML, entao este e o caminho comum, nao a excecao.
 */
function inbound_html_para_texto(string $html): string
{
    // O que o cliente marcou como citacao ja sai daqui.
    $html = (string) preg_replace('#<blockquote\b[^>]*>.*?</blockquote>#is', "\n", $html);
    $html = (string) preg_replace('#<(script|style)\b[^>]*>.*?</\1>#is', ' ', $html);
    $html = (string) preg_replace('#<br\s*/?>#i', "\n", $html);
    $html = (string) preg_replace('#</(p|div|tr|li|h[1-6]|table|blockquote)\s*>#i', "\n", $html);
    $html = (string) preg_replace('#<(hr|p|div|tr|li)\b[^>]*>#i', "\n", $html);
    $txt = html_entity_decode(strip_tags($html), ENT_QUOTES | ENT_HTML5, 'UTF-8');
    $txt = str_replace("\xC2\xA0", ' ', $txt); // nbsp
    return trim((string) preg_replace("/\n{3,}/", "\n\n", $txt));
}

/**
 * Texto legivel do corpo. Em multipart, procura a primeira parte text/plain,
 * inclusive dentro de multipart aninhado (o mixed/alternative que todo cliente
 * gera quando ha anexo); em text/html, tira as tags preservando as quebras.
 */
function inbound_texto(string $corpoBruto, array $headers, int $profundidade = 0): string
{
    // O TIPO e comparado em minusculas, mas o BOUNDARY sai do valor original:
    // ele diferencia maiuscula de minuscula (o "----=_Part_123_AbC" do Outlook
    // e o caso comum), e um boundary rebaixado nao casa com nada — a mensagem
    // inteira viraria "uma parte so", HTML e citacao incluidos.
    $ctBruto = (string) ($headers['content-type'] ?? 'text/plain');
    $ct = strtolower($ctBruto);
    $cte = $headers['content-transfer-encoding'] ?? '';
    $charset = preg_match('/charset\s*=\s*"?([A-Za-z0-9\-_]+)"?/i', $ctBruto, $m) ? $m[1] : 'UTF-8';

    if (str_contains($ct, 'multipart/') && $profundidade < 4
        && preg_match('/boundary\s*=\s*"?([^";\s]+)"?/i', $ctBruto, $b)) {
        $partes = explode('--' . $b[1], preg_replace('/\r\n|\r/', "\n", $corpoBruto));
        $sobra = null;
        foreach ($partes as $parte) {
            $parte = ltrim($parte, "-\n");
            if (trim($parte) === '' || str_starts_with(trim($parte), '--')) {
                continue;
            }
            [$h, $c] = array_pad(preg_split("/\n\n/", $parte, 2), 2, '');
            $ph = inbound_headers($h);
            $pctBruto = (string) ($ph['content-type'] ?? 'text/plain');
            $pct = strtolower($pctBruto);
            // Aninhado (mixed contendo alternative): desce um nivel.
            if (str_contains($pct, 'multipart/')) {
                $dentro = inbound_texto($c, $ph, $profundidade + 1);
                if (trim($dentro) !== '') {
                    return $dentro;
                }
                continue;
            }
            // Anexo nao e corpo.
            if (stripos((string) ($ph['content-disposition'] ?? ''), 'attachment') === 0) {
                continue;
            }
            $pcs = preg_match('/charset\s*=\s*"?([A-Za-z0-9\-_]+)"?/i', $pctBruto, $mm) ? $mm[1] : 'UTF-8';
            $decodificado = inbound_decode_corpo($c, $ph['content-transfer-encoding'] ?? '', $pcs);
            if (str_starts_with($pct, 'text/plain') || (!isset($ph['content-type']) && trim($c) !== '')) {
                return $decodificado;
            }
            if ($sobra === null && str_starts_with($pct, 'text/html') && trim($decodificado) !== '') {
                $sobra = inbound_html_para_texto($decodificado); // plano B: so havia HTML
            }
        }
        return (string) $sobra;
    }

    $texto = inbound_decode_corpo($corpoBruto, $cte, $charset);
    return str_contains($ct, 'text/html') ? inbound_html_para_texto($texto) : $texto;
}

/**
 * Tira a citacao da mensagem anterior: o que interessa e o que a PESSOA
 * escreveu, e o e-mail citado abaixo contem o nosso proprio texto (inclusive
 * a palavra "SAIR" do rodape, que sem isso viraria opt-out em toda resposta).
 */
function inbound_limpar_citacao(string $texto): string
{
    $texto = preg_replace('/\r\n|\r/', "\n", $texto);
    $cortes = [
        '/\n\s*Em .{0,80}escreveu\s*:/isu',
        '/\n\s*On .{0,120}wrote\s*:/isu',
        '/\n\s*-{2,}\s*(Mensagem original|Original Message|Forwarded message)/isu',
        '/\n\s*De\s*:\s*.{0,120}\n\s*Enviad[ao]\s*(em|:)/isu',
        '/\n\s*From\s*:\s*.{0,120}\n\s*Sent\s*:/isu',
        '/\n_{5,}\n/u',
    ];
    foreach ($cortes as $re) {
        if (preg_match($re, $texto, $m, PREG_OFFSET_CAPTURE)) {
            $texto = substr($texto, 0, $m[0][1]);
        }
    }
    $linhas = [];
    foreach (explode("\n", $texto) as $l) {
        if (str_starts_with(ltrim($l), '>')) {
            continue;
        }
        $linhas[] = $l;
    }
    return trim(preg_replace("/\n{3,}/", "\n\n", implode("\n", $linhas)));
}

/**
 * Classifica o retorno. Ordem importa: devolucao primeiro, resposta
 * automatica antes de opt-out (um "estou de ferias, volto dia 20" nao pode
 * virar pedido de descadastro) e o resto e gente.
 *
 * @return array{kind:string, code:string}
 */
function inbound_classificar(array $h, string $texto): array
{
    $de = strtolower($h['from'] ?? '');
    $assunto = mb_strtolower(inbound_decode_mime($h['subject'] ?? ''));
    $ct = strtolower($h['content-type'] ?? '');
    $limpo = inbound_limpar_citacao($texto);

    // 1. Devolucao (DSN)
    $ehBounce = str_contains($ct, 'report-type=delivery-status')
        || str_contains($ct, 'multipart/report')
        || isset($h['x-failed-recipients'])
        || preg_match('/(mailer-daemon|postmaster)@/i', $de) === 1
        || preg_match('/(undeliverable|delivery status notification|delivery failure|returned mail|mail delivery (failed|system)|nao entregue|não entregue|devolucao|devolução|falha na entrega)/iu', $assunto) === 1;
    if ($ehBounce) {
        $code = '';
        if (preg_match('/\b([45]\.\d{1,3}\.\d{1,3})\b/', $texto, $m)) {
            $code = $m[1];
        } elseif (preg_match('/\b(5\d{2}|4\d{2})\b[ -]/', $texto, $m)) {
            $code = $m[1];
        }
        return ['kind' => 'bounce', 'code' => $code];
    }

    // 2. Resposta automatica
    $auto = (isset($h['auto-submitted']) && strtolower($h['auto-submitted']) !== 'no')
        || isset($h['x-autoreply']) || isset($h['x-autorespond'])
        || isset($h['x-auto-response-suppress'])
        || preg_match('/^(auto_reply|bulk|junk|list)$/i', trim($h['precedence'] ?? '')) === 1
        || preg_match('/(resposta autom|automatic reply|out of office|ausencia|ausência|estou fora|de ferias|de férias|retorno (no dia|em) )/iu', $assunto) === 1;
    if ($auto) {
        return ['kind' => 'auto', 'code' => ''];
    }

    // 3. Pedido de descadastro
    //
    // As URLs saem do texto ANTES de procurar o pedido. A razao e concreta: o
    // link do nosso proprio rodape e ".../crm/optout.php?...", e a palavra
    // "unsubscribe" aparece em rodape de newsletter. Se a citacao escapar do
    // corte (resposta em HTML, encaminhamento), o texto do NOSSO e-mail viraria
    // um pedido de descadastro da PESSOA. Ninguem precisa de uma URL para pedir
    // para sair, entao tirar todas nao custa nada e fecha a porta.
    $semUrl = (string) preg_replace('#\b(?:https?://|www\.)\S+#i', ' ', $limpo);
    $semUrl = (string) preg_replace('/\S+@\S+\.\w+/', ' ', $semUrl);
    $explicito = preg_match('/(descadastr\w*|unsubscribe|opt[-\s]out|n[aã]o quero (mais )?receber|pare de (me )?(enviar|mandar)|tire meu e-?mail|remov[ae]r? (o )?meu (e-?mail|contato|cadastro)|cancelar (o )?(recebimento|as mensagens))/iu', $semUrl) === 1;
    $curtoSair = mb_strlen(trim($semUrl)) <= 200
        && preg_match('/^\W*(sair|remover|remova|cancelar|descadastrar)\b/iu', trim($semUrl)) === 1;
    if ($explicito || $curtoSair) {
        return ['kind' => 'optout', 'code' => ''];
    }

    return ['kind' => 'humana', 'code' => ''];
}

/** Devolucao definitiva? 4.x.x e temporario; o resto tratamos como definitivo. */
function inbound_bounce_definitivo(string $code): bool
{
    return !str_starts_with($code, '4');
}

/* ---------- Vinculo com o lead ---------- */

/**
 * Tres tentativas, da mais forte para a mais fraca: o Message-ID que nos
 * mesmos geramos, depois o endereco do remetente, depois o dominio (so quando
 * ele identifica um unico lead em cadencia).
 *
 * @return array{lead_id:?int, outbox_id:?int}
 */
function inbound_vincular(array $h, string $fromEmail): array
{
    $refs = trim(($h['in-reply-to'] ?? '') . ' ' . ($h['references'] ?? ''));
    if ($refs !== '' && preg_match_all('/<[^>]+>/', $refs, $m)) {
        $ids = array_reverse($m[0]);
        foreach ($ids as $mid) {
            $o = row('SELECT id, lead_id FROM email_outbox WHERE message_id = ?', [$mid]);
            if ($o !== null) {
                return ['lead_id' => (int) $o['lead_id'], 'outbox_id' => (int) $o['id']];
            }
        }
    }
    if ($fromEmail === '') {
        return ['lead_id' => null, 'outbox_id' => null];
    }
    $o = row("SELECT id, lead_id FROM email_outbox WHERE to_email = ? AND status = 'enviado'
              ORDER BY sent_at DESC LIMIT 1", [$fromEmail]);
    if ($o !== null) {
        return ['lead_id' => (int) $o['lead_id'], 'outbox_id' => (int) $o['id']];
    }
    $c = row('SELECT lead_id FROM lead_contacts WHERE email = ? ORDER BY id LIMIT 1', [$fromEmail]);
    if ($c !== null) {
        return ['lead_id' => (int) $c['lead_id'], 'outbox_id' => null];
    }
    $l = row('SELECT id FROM leads WHERE email = ? ORDER BY id LIMIT 1', [$fromEmail]);
    if ($l !== null) {
        return ['lead_id' => (int) $l['id'], 'outbox_id' => null];
    }
    // Dominio: so vale se apontar para UM lead em cadencia ativa (senao um
    // "financeiro@" de uma holding responderia pela empresa errada).
    $dominio = mail_dominio($fromEmail);
    if ($dominio !== '') {
        $cands = rows("SELECT c.lead_id FROM lead_cadence c
                       JOIN email_outbox o ON o.lead_id = c.lead_id AND o.status = 'enviado'
                       WHERE c.state = 'ativa' AND SUBSTRING_INDEX(o.to_email, '@', -1) = ?
                       GROUP BY c.lead_id LIMIT 2", [$dominio]);
        if (count($cands) === 1) {
            return ['lead_id' => (int) $cands[0]['lead_id'], 'outbox_id' => null];
        }
    }
    return ['lead_id' => null, 'outbox_id' => null];
}

/* ---------- Acao ---------- */

/**
 * Age no lead conforme a classificacao. Recebe a linha ja gravada em
 * email_inbound. Nunca lanca: um retorno estranho nao pode derrubar o cron.
 */
function inbound_agir(array $reg): void
{
    $leadId = $reg['lead_id'] !== null ? (int) $reg['lead_id'] : null;
    if ($leadId === null) {
        return;
    }
    $lead = row('SELECT * FROM leads WHERE id = ?', [$leadId]);
    if ($lead === null) {
        return;
    }
    $quando = (string) ($reg['received_at'] ?? date('Y-m-d H:i:s'));
    $trecho = trim((string) ($reg['snippet'] ?? ''));

    switch ($reg['kind']) {
        case 'bounce':
            $codigo = (string) ($reg['bounce_code'] ?? '');
            $alvo = $reg['outbox_id'] !== null
                ? row('SELECT to_email, contact_id FROM email_outbox WHERE id = ?', [(int) $reg['outbox_id']])
                : null;
            $email = (string) ($alvo['to_email'] ?? $lead['email'] ?? '');
            if (inbound_bounce_definitivo($codigo)) {
                if ($email !== '') {
                    email_marcar_bounce($email, $codigo);
                }
                cadencia_auto_parar($leadId, 'e-mail devolvido' . ($codigo !== '' ? ' (' . $codigo . ')' : ''));
                interaction_add($leadId, 'email',
                    'E-mail devolvido pelo servidor' . ($codigo !== '' ? ' (' . $codigo . ')' : '')
                    . '. Endereco marcado como invalido: ' . $email, $quando, null, null);
                cadencia_tarefa_humana($leadId, 'Corrigir o e-mail de ' . ($email ?: 'contato') . ' (devolvido)', 1, null, 'manual');
            } else {
                // 4.x.x e temporario: adia o proximo envio em um dia util.
                $novo = cadencia_slot(cadencia_dia_util(date('Y-m-d'), 1));
                q("UPDATE email_outbox SET scheduled_for = ?, skip_reason = 'devolucao temporaria'
                    WHERE lead_id = ? AND status IN ('aguardando','agendado')", [$novo, $leadId]);
                interaction_add($leadId, 'email',
                    'Devolucao temporaria (' . $codigo . '). Proxima tentativa em ' . fmt_dt($novo) . '.',
                    $quando, null, null);
            }
            break;

        case 'optout':
            interaction_add($leadId, 'email',
                'Pediu para nao receber mais e-mails. Trecho: ' . mb_substr($trecho, 0, 300), $quando, null, null);
            lead_set_no_contact($leadId, true, 'respondeu pedindo para sair');
            inbound_confirmar_optout($reg, $lead);
            break;

        case 'humana':
            cadencia_auto_parar($leadId, 'resposta recebida', 'interrompida');
            interaction_add($leadId, 'email',
                'Resposta recebida de ' . ($reg['from_email'] ?: 'contato')
                . '. Assunto: ' . $reg['subject'] . "\n" . mb_substr($trecho, 0, 300), $quando, null, null);
            lead_update($leadId, [
                'next_action_at'   => date('Y-m-d H:i:s'),
                'next_action_note' => 'Responder ' . mb_substr((string) ($reg['from_name'] ?: $reg['from_email']), 0, 120),
            ]);
            cadencia_tarefa_humana($leadId,
                'Responder ' . mb_substr((string) ($reg['from_name'] ?: $reg['from_email']), 0, 120), 0, null, 'manual');
            break;

        case 'auto':
        default:
            // So fica registrado: nao conta como resposta e nao para a cadencia.
            break;
    }
}

/** Confirma a remocao, como o playbook promete no rodape de todo e-mail. */
function inbound_confirmar_optout(array $reg, array $lead): void
{
    $para = (string) $reg['from_email'];
    if ($para === '') {
        return;
    }
    $conta = null;
    $c = cadencia_estado((int) $lead['id']);
    if ($c !== null && $c['sender_user_id'] !== null) {
        $conta = mail_conta_usuario((int) $c['sender_user_id']);
    }
    if ($conta === null) {
        $todas = mail_contas();
        $conta = $todas ? reset($todas) : null;
    }
    if ($conta === null) {
        return;
    }
    $corpo = "Removido, e obrigado por avisar.\n\n"
        . "Seu contato saiu da nossa lista agora e nao vamos mais escrever.\n\n"
        . "Se um dia fizer sentido falar sobre visibilidade de produtividade nas estacoes "
        . "Windows, e so responder este e-mail.\n\n"
        . "+351 Monitor\nwww.mais351monitor.com.br";
    mail_enviar($conta, [
        'de_nome'    => (string) $conta['nome'],
        'de_email'   => (string) $conta['email'],
        'para_email' => $para,
        'assunto'    => 'Removido da lista',
        'corpo'      => $corpo,
        'message_id' => mail_message_id(mail_dominio((string) $conta['email'])),
        'auto'       => true,
    ]);
}

/* ---------- Leitura das caixas ---------- */

/**
 * Le todas as caixas configuradas e processa o que chegou desde o ultimo UID.
 * @return array{lidas:int, humanas:int, bounces:int, optouts:int, autos:int, erros:string[]}
 */
function inbound_processar(int $maxPorCaixa = 60): array
{
    $r = ['lidas' => 0, 'humanas' => 0, 'bounces' => 0, 'optouts' => 0, 'autos' => 0, 'erros' => []];
    $nossas = array_keys(mail_contas());

    foreach (mail_contas() as $endereco => $conta) {
        if ((string) $conta['imap_senha'] === '') {
            continue;
        }
        try {
            $imap = ImapMin::abrir((string) $conta['imap_host'], (int) $conta['imap_porta']);
        } catch (Throwable $e) {
            $r['erros'][] = $endereco . ': ' . $e->getMessage();
            continue;
        }
        try {
            if (!$imap->login((string) $conta['imap_usuario'], (string) $conta['imap_senha'])) {
                $r['erros'][] = $endereco . ': login IMAP recusado';
                $imap->fechar();
                continue;
            }
            $caixa = $imap->examine('INBOX');
            $chave = 'imap_' . $endereco;
            [$validadeAnt, $ultimo] = array_pad(explode(':', state_get($chave), 2), 2, '');
            $validade = (string) $caixa['uidvalidity'];

            if ($validadeAnt !== $validade || $ultimo === '') {
                // Primeira execucao (ou caixa recriada): comeca do fim. Sem
                // isto o primeiro tick processaria a caixa inteira e criaria
                // uma avalanche de tarefas por e-mails velhos.
                state_set($chave, $validade . ':' . max(0, (int) $caixa['uidnext'] - 1));
                $imap->fechar();
                continue;
            }

            $uids = $imap->uidsDepoisDe((int) $ultimo);
            $uids = array_slice($uids, 0, $maxPorCaixa);
            $maiorUid = (int) $ultimo;

            foreach ($uids as $uid) {
                // O cursor so avanca DEPOIS de processar. Marcar o UID antes e
                // perder a mensagem quando o FETCH falha — e a que falha pode
                // ser exatamente a resposta que a cadencia esta esperando.
                $cabecalho = $imap->parte($uid, 'HEADER');
                if ($cabecalho === null) {
                    $r['erros'][] = $endereco . ': falha ao ler o UID ' . $uid . ' — fica para o proximo tick';
                    break;
                }
                if ($cabecalho === '') {
                    $maiorUid = $uid; // mensagem sumiu entre a busca e a leitura
                    continue;
                }
                $h = inbound_headers($cabecalho);
                $de = inbound_endereco($h['from'] ?? '');
                if ($de['email'] !== '' && in_array($de['email'], $nossas, true)) {
                    $maiorUid = $uid;
                    continue; // aviso nosso voltando para a propria caixa
                }
                $corpoBruto = $imap->parte($uid, 'TEXT', 8192);
                if ($corpoBruto === null) {
                    $r['erros'][] = $endereco . ': falha ao ler o corpo do UID ' . $uid . ' — fica para o proximo tick';
                    break;
                }
                $texto = inbound_texto($corpoBruto, $h);
                $classe = inbound_classificar($h, $texto);
                $vinculo = inbound_vincular($h, $de['email']);

                $snippet = mb_substr(inbound_limpar_citacao($texto), 0, 380);
                $st = q('INSERT IGNORE INTO email_inbound
                     (mailbox, uid, message_id, in_reply_to, from_email, from_name, subject,
                      kind, bounce_code, lead_id, outbox_id, snippet, received_at)
                   VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)', [
                    $endereco, $uid,
                    mb_substr((string) ($h['message-id'] ?? ''), 0, 190) ?: null,
                    mb_substr((string) ($h['in-reply-to'] ?? ''), 0, 190) ?: null,
                    $de['email'], $de['nome'],
                    mb_substr(inbound_decode_mime((string) ($h['subject'] ?? '')), 0, 255),
                    $classe['kind'], $classe['code'] ?: null,
                    $vinculo['lead_id'], $vinculo['outbox_id'],
                    $snippet,
                    isset($h['date']) && strtotime($h['date']) ? date('Y-m-d H:i:s', strtotime($h['date'])) : date('Y-m-d H:i:s'),
                ]);
                // rowCount, nao last_id: com INSERT IGNORE que nao inseriu, o
                // lastInsertId ainda devolve o id do insert ANTERIOR desta
                // conexao — e reprocessariamos um retorno velho.
                if ($st->rowCount() === 0) {
                    $maiorUid = $uid;
                    continue; // ja processado (chave unica mailbox+uid)
                }
                $id = last_id();
                $reg = row('SELECT * FROM email_inbound WHERE id = ?', [$id]);
                if ($reg === null) {
                    $maiorUid = $uid;
                    continue;
                }
                $r['lidas']++;
                if ($classe['kind'] === 'humana') {
                    $r['humanas']++;
                } elseif ($classe['kind'] === 'bounce') {
                    $r['bounces']++;
                } elseif ($classe['kind'] === 'optout') {
                    $r['optouts']++;
                } else {
                    $r['autos']++;
                }
                try {
                    inbound_agir($reg);
                } catch (Throwable $e) {
                    error_log('inbound agir #' . $id . ': ' . $e->getMessage());
                    $r['erros'][] = 'acao no retorno #' . $id . ': ' . $e->getMessage();
                }
                if (in_array($classe['kind'], ['humana', 'bounce', 'optout'], true)) {
                    try {
                        notificar_retorno($reg);
                    } catch (Throwable $e) {
                        error_log('inbound aviso #' . $id . ': ' . $e->getMessage());
                    }
                }
                $maiorUid = $uid; // processado de ponta a ponta: agora sim
                state_set($chave, $validade . ':' . $maiorUid);
            }
            state_set($chave, $validade . ':' . $maiorUid);
        } catch (Throwable $e) {
            $r['erros'][] = $endereco . ': ' . $e->getMessage();
        }
        $imap->fechar();
    }
    return $r;
}

/** Marca o retorno como tratado (botao da tela de Envios). */
function inbound_tratar(int $id, ?int $userId): void
{
    q('UPDATE email_inbound SET handled_at = NOW(), handled_by = ? WHERE id = ? AND handled_at IS NULL',
        [$userId, $id]);
}

/** Retornos que ainda pedem alguma coisa de gente. */
function inbound_pendentes(int $limite = 50): array
{
    try {
        $limite = max(1, min(200, $limite));
        return rows("SELECT i.*, l.company FROM email_inbound i
                     LEFT JOIN leads l ON l.id = i.lead_id
                     WHERE i.handled_at IS NULL AND i.kind IN ('humana','optout','bounce')
                     ORDER BY i.received_at DESC LIMIT $limite");
    } catch (Throwable $e) {
        return [];
    }
}
