<?php
/**
 * Configurações operacionais gravadas em banco (app_settings), com defaults
 * no código. Enquanto a migration 007 não roda, tudo cai nos defaults e
 * nenhuma tela quebra — mesmo cuidado que o dashboard já toma com a fila.
 *
 * Cobre a cadência de e-mail: prazos em dias úteis e os 5 modelos que o
 * link "abrir no Outlook" usa (docs/comercial/templates-email.md).
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

/**
 * cadencia_email_N = dias úteis de espera DEPOIS do Nº e-mail enviado.
 * O 5º é a retomada (prazo longo). cadencia_hora = hora do vencimento (0-23).
 */
const SETTING_INT_DEFAULTS = [
    'cadencia_email_1' => 4,
    'cadencia_email_2' => 3,
    'cadencia_email_3' => 3,
    'cadencia_email_4' => 3,
    'cadencia_email_5' => 30,
    'cadencia_hora'    => 9,

    // --- Cadencia AUTOMATICA (migration 012) -------------------------------
    // auto_ligada e o interruptor geral: 0 nao envia nada, seja qual for o
    // resto. Os tetos seguem o aquecimento do playbook (15 / 20 / 30 por dia
    // e por caixa) e auto_max_tick limita cada execucao do cron.
    'auto_ligada'         => 0,
    'auto_sandbox'        => 1,
    'auto_teto_sem1'      => 15,
    'auto_teto_sem2'      => 20,
    'auto_teto'           => 30,
    'auto_max_tick'       => 5,
    'auto_dominio_dias'   => 7,
    'auto_avisa_email'    => 1,
    'auto_avisa_telegram' => 0,
    // Manda tambem a versao HTML (multipart/alternative). Desligado por padrao:
    // o playbook escolheu texto puro para a abordagem fria, e mudar isso e
    // decisao comercial, nao default tecnico.
    'auto_html'           => 0,
    'auto_resumo_hora'    => 17,
    'auto_resumo_minuto'  => 30,
    // Tarefas HUMANAS da cadencia (ficam visiveis no quadro, ao contrario das
    // tarefas automaticas de "cobrar retorno", que a automacao aposenta).
    'cadencia_ligacao_dias'  => 2,
    'cadencia_linkedin_dias' => 7,
];

/**
 * Configuracoes de texto que nao sao modelo de e-mail. Ficam separadas dos
 * modelos para a tela de Configuracoes conseguir renderizar cada grupo no seu
 * lugar, e para settings_save() aceitar as duas famílias.
 */
const SETTING_STR_DEFAULTS = [
    'auto_modo'         => 'aprovacao', // 'aprovacao' | 'automatico'
    'auto_janela1_ini'  => '08:30',
    'auto_janela1_fim'  => '11:00',
    'auto_janela2_ini'  => '14:00',
    'auto_janela2_fim'  => '17:00',
    'auto_sandbox_para' => '',
    'auto_etapas'       => '1,2,3,4,5',
    'auto_inicio'       => '',
    'auto_cc_avisos'    => '',
    // Rodape de descadastro, colado no fim de todo e-mail da cadencia.
    // {link} e a unica chave. Vazio = nenhum rodape no corpo (o cabecalho
    // List-Unsubscribe continua indo, e o Gmail/Outlook mostram o botao
    // proprio deles) — mas ai o "responda SAIR" tem que estar no modelo.
    'auto_optout_texto' => "Não quer mais receber? Responda SAIR, ou cancele em um clique:\n{link}",

    // Assinatura acrescentada ao fim de TODO e-mail da cadencia, num lugar so
    // em vez de repetida nos cinco modelos. Vazia por padrao: quem ja tem a
    // assinatura escrita dentro dos modelos nao ganha uma segunda.
    'auto_assinatura'      => '',
    // Versao visual da MESMA assinatura, usada quando auto_html esta ligado.
    //
    // E a assinatura que o time ja usa, com duas mudancas obrigatorias:
    //
    // 1. O logo era um data:image/png;base64 de 22 KB embutido. O Gmail REMOVE
    //    imagem em data: URI das mensagens recebidas, entao no cliente mais
    //    comum do Brasil o logo simplesmente nao apareceria — e os 22 KB iriam
    //    em cada e-mail assim mesmo. Agora e um arquivo hospedado no proprio
    //    site (site/assets/img/), que carrega, fica em cache e nao pesa.
    // 2. Os tamanhos vieram de uma assinatura de tela (logo de 250px, nome de
    //    31px). Em e-mail isso estoura a largura no celular; aqui esta na
    //    escala que cabe em cliente de e-mail sem rolagem lateral.
    //
    // Tabela e nao flex/grid porque cliente de e-mail ainda e HTML de 2005.
    // So entra em uso quando auto_assinatura (texto) tambem estiver preenchida.
    'auto_assinatura_html' => '<table role="presentation" cellpadding="0" cellspacing="0" border="0"'
        . ' style="border-collapse:collapse;font-family:Arial,Helvetica,sans-serif">'
        . '<tr>'
        . '<td valign="middle" style="padding:0 20px 0 0;border-right:1px solid #d8dde3">'
        . '<a href="https://www.mais351monitor.com.br" target="_blank" style="text-decoration:none;border:0">'
        . '<img src="https://www.mais351monitor.com.br/assets/img/assinatura-351monitor.png"'
        . ' alt="+351 Monitor" width="128" height="104"'
        . ' style="display:block;width:128px;height:104px;border:0;outline:none;text-decoration:none">'
        . '</a>'
        . '</td>'
        . '<td valign="top" style="padding:2px 0 0 20px">'
        . '<div style="font-size:20px;line-height:25px;font-weight:700;color:#111b2a;margin:0 0 3px">Bruna Rondelli</div>'
        . '<div style="font-size:13px;line-height:17px;font-weight:700;color:#59bd2b;margin:0 0 13px">COO</div>'
        . '<div style="width:48px;border-top:2px solid #172334;margin:0 0 13px"></div>'
        . '<table role="presentation" cellpadding="0" cellspacing="0" border="0"'
        . ' style="border-collapse:collapse;font-family:Arial,Helvetica,sans-serif">'
        . '<tr><td style="padding:0 16px 5px 0;font-size:13px;line-height:18px;color:#a3adb8;white-space:nowrap">Telefone</td>'
        . '<td style="padding:0 0 5px 0;font-size:13px;line-height:18px;white-space:nowrap">'
        . '<a href="tel:+5511992209235" style="color:#183550;text-decoration:none">+55 11 99220-9235</a></td></tr>'
        . '<tr><td style="padding:0 16px 5px 0;font-size:13px;line-height:18px;color:#a3adb8;white-space:nowrap">E-mail</td>'
        . '<td style="padding:0 0 5px 0;font-size:13px;line-height:18px;white-space:nowrap">'
        . '<a href="mailto:bruna@mais351monitor.com.br" style="color:#183550;text-decoration:none">bruna@mais351monitor.com.br</a></td></tr>'
        . '<tr><td style="padding:0 16px 0 0;font-size:13px;line-height:18px;color:#a3adb8;white-space:nowrap">Site</td>'
        . '<td style="padding:0;font-size:13px;line-height:18px;white-space:nowrap">'
        . '<a href="https://www.mais351monitor.com.br" target="_blank" style="color:#183550;text-decoration:none">mais351monitor.com.br</a></td></tr>'
        . '</table>'
        . '<div style="margin-top:14px;font-size:10px;line-height:14px;font-weight:700;'
        . 'letter-spacing:2.4px;color:#9ba6b2;white-space:nowrap">PRODUTIVIDADE EM TEMPO REAL</div>'
        . '</td>'
        . '</tr></table>',
];

/** Quantos e-mails a cadência acompanha (1º ao 5º). */
const CADENCIA_EMAIL_PASSOS = 5;

/** Chaves aceitas nos modelos. Ver cadencia_email_vars(). */
const CADENCIA_EMAIL_CHAVES = ['{empresa}', '{contato}', '{primeiro_nome}', '{cargo}', '{estacoes}',
    '{meu_nome}', '{linha_pessoal}'];

/** Rodapé obrigatório: remetente real + opt-out (LGPD do outbound). */
const CADENCIA_EMAIL_ASSINATURA =
    "{meu_nome} | +351 Monitor\n"
    . "Monitoramento transparente de produtividade (sem keylogger, sem prints)\n"
    . "www.mais351monitor.com.br\n\n"
    . "Se não quiser receber meus e-mails, responda SAIR que removo seu contato.";

/** Modelos default da cadência — texto puro, curto, 1 pergunta no fim. */
const CADENCIA_EMAIL_DEFAULTS = [
    1 => [
        'assunto' => 'Horas da equipe na {empresa}: quantas viram produção?',
        // {linha_pessoal} e a unica frase escrita para AQUELA empresa (site,
        // noticia, indicacao). Vazia, a linha some sem deixar buraco.
        'corpo'   => "Oi, {primeiro_nome}, tudo bem?\n\n"
            . "{linha_pessoal}\n\n"
            . "No trabalho híbrido quase ninguém consegue responder uma pergunta simples: "
            . "das 8h do time, quantas viram entrega de verdade?\n\n"
            . "O +351 Monitor mostra horas ativas, ociosidade e os sistemas usados em cada "
            . "estação Windows, sem print de tela e sem keylogger, com kit LGPD pronto "
            . "(termo de ciência incluído) e dados hospedados no Brasil.\n\n"
            . "Vale uma demonstração de 10 minutos pelo WhatsApp esta semana?",
    ],
    2 => [
        'assunto' => 'RE: Horas da equipe na {empresa}: quantas viram produção?',
        'corpo'   => "{primeiro_nome}, complemento rápido do meu e-mail anterior.\n\n"
            . "O que coletamos: horas ativas, ociosidade, aplicativo/janela em uso, sessões.\n"
            . "O que nunca coletamos: teclas digitadas, prints de tela, arquivos, e-mails.\n"
            . "LGPD: termo de ciência pronto, ícone visível na máquina, dados no Brasil.\n"
            . "Preço: em real, com Pix ou boleto. Sem dólar na fatura.\n"
            . "Implantação: instalamos junto com a sua TI, com onboarding assistido.\n\n"
            . "A demonstração leva 10 minutos, pelo WhatsApp, no seu horário. Consigo te mostrar esta semana?",
    ],
    3 => [
        'assunto' => 'O que a {empresa} enxergaria na primeira semana',
        'corpo'   => "{primeiro_nome}, tudo certo?\n\n"
            . "Na prática, o que aparece no painel logo na primeira semana é: quais estações "
            . "ficam ociosas em que faixa de horário, quais sistemas realmente consomem o dia "
            . "do time e onde o retrabalho está escondido.\n\n"
            . "É a informação que embasa conversa de feedback sem achismo, e sem transformar "
            . "a empresa em vigilância.\n\n"
            . "Te mostro em 10 minutos pelo WhatsApp? Pode ser no fim do dia, se for melhor.",
    ],
    4 => [
        'assunto' => 'Ainda faz sentido para a {empresa}?',
        'corpo'   => "{primeiro_nome}, imagino que a agenda esteja corrida.\n\n"
            . "Para eu não te tomar tempo à toa: visibilidade de produtividade das estações "
            . "Windows é tema para agora, ou está fora de prioridade?\n\n"
            . "Uma palavra já me basta como resposta.",
    ],
    5 => [
        'assunto' => 'Fecho seu contato, {primeiro_nome}?',
        'corpo'   => "{primeiro_nome}, este é meu último e-mail sobre o assunto.\n\n"
            . "Se não é prioridade agora, tudo certo: guardo seu contato e não insisto. "
            . "Se for tema para outro momento, me responde com um \"depois\" que eu volto "
            . "no trimestre que vem.\n\n"
            . "E se quiser resolver logo: 10 minutos no WhatsApp e você vê o painel funcionando.",
    ],
];

/** Defaults completos: números + textos soltos + os 5 modelos com assinatura. */
function setting_defaults(): array
{
    static $d = null;
    if ($d !== null) {
        return $d;
    }
    $d = SETTING_INT_DEFAULTS + SETTING_STR_DEFAULTS;
    foreach (CADENCIA_EMAIL_DEFAULTS as $n => $t) {
        $d['cadencia_email_assunto_' . $n] = $t['assunto'];
        $d['cadencia_email_corpo_' . $n]   = $t['corpo'] . "\n\n" . CADENCIA_EMAIL_ASSINATURA;
    }
    return $d;
}

/** @param bool $refresh relê do banco (usado depois de gravar) */
function settings_all(bool $refresh = false): array
{
    static $cache = null;
    if ($refresh) {
        $cache = null;
    }
    if ($cache !== null) {
        return $cache;
    }
    $cache = setting_defaults();
    try {
        foreach (rows('SELECT k, v FROM app_settings') as $r) {
            $cache[$r['k']] = $r['v'];
        }
    } catch (Throwable $e) {
        // migration 007 ainda não aplicada — segue nos defaults
    }
    return $cache;
}

function setting_int(string $key): int
{
    $v = filter_var(settings_all()[$key] ?? null, FILTER_VALIDATE_INT);
    return $v === false || $v === null ? (int) (SETTING_INT_DEFAULTS[$key] ?? 0) : $v;
}

function setting_str(string $key): string
{
    return (string) (settings_all()[$key] ?? setting_defaults()[$key] ?? '');
}

function setting_bool(string $key): bool
{
    return setting_int($key) === 1;
}

/**
 * Modelos que ainda carregam a assinatura/despedida ANTIGA dentro do corpo.
 *
 * Antes a assinatura vivia colada no fim de cada um dos cinco modelos. Agora
 * ela e o rodape de descadastro sao configuracoes proprias, entao o que sobrou
 * dentro do modelo vira texto repetido — o "responda SAIR" chega duas vezes no
 * mesmo e-mail, que e o tipo de detalhe que faz o leitor achar que e robo.
 *
 * @return int[] numeros das etapas que precisam de limpeza
 */
function cadencia_modelos_sujos(): array
{
    $sujos = [];
    for ($n = 1; $n <= CADENCIA_EMAIL_PASSOS; $n++) {
        $corpo = setting_str('cadencia_email_corpo_' . $n);
        if (str_contains($corpo, CADENCIA_EMAIL_ASSINATURA)
            || preg_match('/responda\s+SAIR/iu', $corpo) === 1) {
            $sujos[] = $n;
        }
    }
    return $sujos;
}

/**
 * Tira do fim dos modelos exatamente os blocos que hoje moram em outro lugar.
 * So remove texto conhecido, palavra por palavra — nada escrito pelo time some.
 *
 * @return int quantos modelos foram alterados
 */
function cadencia_limpar_modelos(): int
{
    $mexidos = 0;
    $kv = [];
    for ($n = 1; $n <= CADENCIA_EMAIL_PASSOS; $n++) {
        $original = setting_str('cadencia_email_corpo_' . $n);
        $corpo = str_replace(CADENCIA_EMAIL_ASSINATURA, '', $original);
        // A linha solta do opt-out, com ou sem a assinatura em volta.
        $corpo = (string) preg_replace(
            '/^\s*Se n[ãa]o quiser receber meus e-mails, responda SAIR que removo seu contato\.\s*$/miu',
            '', $corpo);
        $corpo = rtrim((string) preg_replace("/\n{3,}/", "\n\n", $corpo));
        if ($corpo !== rtrim($original)) {
            $kv['cadencia_email_corpo_' . $n] = $corpo;
            $mexidos++;
        }
    }
    if ($kv) {
        settings_save($kv);
    }
    return $mexidos;
}

/** Upsert das chaves informadas. Só aceita chaves conhecidas. */
function settings_save(array $kv): void
{
    $conhecidas = setting_defaults();
    foreach ($kv as $k => $v) {
        if (!array_key_exists($k, $conhecidas)) {
            throw new InvalidArgumentException('Configuração desconhecida: ' . $k);
        }
        q('INSERT INTO app_settings (k, v) VALUES (?, ?) ON DUPLICATE KEY UPDATE v = VALUES(v)',
            [$k, (string) $v]);
    }
    settings_all(true);
}

/* ---------- Estado interno (nao aparece em Configuracoes) ---------- */

/**
 * Cursor do IMAP, data do ultimo resumo e afins. Mora no mesmo app_settings,
 * com prefixo '_' para nunca colidir com uma configuracao de verdade e para
 * o settings_save() (que so aceita chaves conhecidas) continuar servindo de
 * guarda contra chave digitada errada na tela.
 */
function state_get(string $key, string $default = ''): string
{
    $k = '_' . $key;
    $all = settings_all();
    if (array_key_exists($k, $all)) {
        return (string) $all[$k];
    }
    try {
        $v = scalar('SELECT v FROM app_settings WHERE k = ?', [$k]);
    } catch (Throwable $e) {
        return $default; // migration 007 ainda nao aplicada
    }
    return $v === null ? $default : (string) $v;
}

function state_set(string $key, string $value): void
{
    q('INSERT INTO app_settings (k, v) VALUES (?, ?) ON DUPLICATE KEY UPDATE v = VALUES(v)',
        ['_' . $key, $value]);
    settings_all(true);
}

/**
 * Feriados nacionais (migration 012) como mapa 'Y-m-d' => nome.
 *
 * Mora aqui, junto de settings_all(), porque tem exatamente a mesma natureza:
 * leitura de banco com fallback silencioso para quando a migration ainda nao
 * rodou. business_days_add() continua puro e recebe este conjunto por
 * parametro — e o que deixa a suite de dias uteis rodar sem banco nenhum.
 */
function feriados_set(bool $refresh = false): array
{
    static $cache = null;
    if ($refresh) {
        $cache = null;
    }
    if ($cache !== null) {
        return $cache;
    }
    $cache = [];
    try {
        foreach (rows('SELECT dia, nome FROM feriados') as $r) {
            $cache[(string) $r['dia']] = (string) $r['nome'];
        }
    } catch (Throwable $e) {
        // migration 012 ainda nao aplicada: so fim de semana e pulado
    }
    return $cache;
}

/**
 * Etapas da cadencia que a automacao envia, na ordem. Desligar a 3 e a 4
 * (deixando "1,2,5") transforma os 5 e-mails nos 3 toques do playbook sem
 * mexer em modelo nenhum.
 *
 * @return int[]
 */
function cadencia_etapas_ativas(): array
{
    $out = [];
    foreach (explode(',', setting_str('auto_etapas')) as $p) {
        $n = (int) trim($p);
        if ($n >= 1 && $n <= CADENCIA_EMAIL_PASSOS && !in_array($n, $out, true)) {
            $out[] = $n;
        }
    }
    sort($out);
    return $out ?: range(1, CADENCIA_EMAIL_PASSOS);
}

/** Proxima etapa ativa depois de $seq, ou null quando a cadencia acabou. */
function cadencia_etapa_seguinte(int $seq): ?int
{
    foreach (cadencia_etapas_ativas() as $n) {
        if ($n > $seq) {
            return $n;
        }
    }
    return null;
}
