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
];

/** Quantos e-mails a cadência acompanha (1º ao 5º). */
const CADENCIA_EMAIL_PASSOS = 5;

/** Chaves aceitas nos modelos. Ver cadencia_email_vars(). */
const CADENCIA_EMAIL_CHAVES = ['{empresa}', '{contato}', '{primeiro_nome}', '{cargo}', '{estacoes}', '{meu_nome}'];

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
        'corpo'   => "Oi, {primeiro_nome}, tudo bem?\n\n"
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

/** Defaults completos: números + os 5 modelos, cada corpo já com a assinatura. */
function setting_defaults(): array
{
    static $d = null;
    if ($d !== null) {
        return $d;
    }
    $d = SETTING_INT_DEFAULTS;
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
