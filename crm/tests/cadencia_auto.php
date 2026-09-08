<?php
/** Testes das funcoes puras da cadencia AUTOMATICA (migrations 012-014). */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

define('CRM', 1);
date_default_timezone_set('America/Sao_Paulo');

$CRM = dirname(__DIR__);

// Stubs: sem banco. settings_all()/feriados_set() caem nos defaults quando
// rows() explode — o mesmo caminho de "migration ainda nao aplicada".
function rows(string $sql, array $p = []): array { throw new RuntimeException('sem banco no teste'); }
function q(string $sql, array $p = []) { throw new RuntimeException('sem banco no teste'); }
function scalar(string $sql, array $p = []) { throw new RuntimeException('sem banco no teste'); }
function row(string $sql, array $p = []) { throw new RuntimeException('sem banco no teste'); }
function last_id(): int { return 1; }
function esc(?string $s): string { return htmlspecialchars((string) $s, ENT_QUOTES, 'UTF-8'); }
function db() { throw new RuntimeException('sem banco no teste'); }
function cfg(string $k) { return ['optout_secret' => str_repeat('s', 43), 'site_url' => 'https://www.mais351monitor.com.br'][$k] ?? null; }

require $CRM . '/lib/validate.php';
require $CRM . '/lib/settings.php';
require $CRM . '/lib/model.php';
require $CRM . '/lib/smtp.php';
require $CRM . '/lib/mailer.php';
require $CRM . '/lib/email_check.php';
require $CRM . '/lib/cadencia.php';
require $CRM . '/lib/render.php';

$falhas = 0;
$total = 0;
function check(bool $cond, string $msg): void
{
    global $falhas, $total;
    $total++;
    if (!$cond) { $falhas++; echo "  FALHOU: $msg\n"; }
}

/** Minuto do dia de um 'Y-m-d H:i:s'. */
function minutoDo(string $dt): int
{
    return (int) date('G', strtotime($dt)) * 60 + (int) date('i', strtotime($dt));
}

echo "== business_days_add com feriado ==\n";
$feriados = ['2026-09-07' => 'Independencia', '2026-11-02' => 'Finados', '2026-11-20' => 'Consciencia Negra'];
$casos = [
    // 4 de setembro e sexta; 7 (segunda) e feriado -> +1 dia util = terca 8
    ['2026-09-04', 1, '2026-09-08'],
    // 4 de setembro + 0 continua no proprio dia (e dia util)
    ['2026-09-04', 0, '2026-09-04'],
    // sabado 5 + 0 cai na segunda 7, que e feriado -> terca 8
    ['2026-09-05', 0, '2026-09-08'],
    // 30 de outubro (sexta) + 1 pula sabado, domingo e o feriado de 2/11
    ['2026-10-30', 1, '2026-11-03'],
    // 19 de novembro (quinta) + 1 pula o feriado de 20 -> segunda 23
    ['2026-11-19', 1, '2026-11-23'],
];
foreach ($casos as [$de, $d, $esperado]) {
    $r = business_days_add($de, $d, $feriados);
    check($r === $esperado, "$de +{$d}du com feriado: esperado $esperado, veio $r");
    echo "  $de +{$d}du -> $r\n";
}
check(business_days_add('2026-09-04', 1) === '2026-09-07',
    'sem a lista de feriados o comportamento antigo tem que continuar igual');

echo "== cadencia_minutos ==\n";
check(cadencia_minutos('08:30', 0) === 510, '08:30 = 510 minutos');
check(cadencia_minutos('00:00', 999) === 0, 'meia-noite e zero, nao o default');
check(cadencia_minutos('23:59', 0) === 1439, '23:59 = 1439');
check(cadencia_minutos('25:00', 42) === 42, 'hora invalida cai no default');
check(cadencia_minutos('abc', 42) === 42, 'lixo cai no default');

echo "== cadencia_janelas (defaults) ==\n";
$janelas = cadencia_janelas();
check(count($janelas) === 2, 'duas janelas por padrao, veio ' . count($janelas));
check($janelas[0] === [510, 660], '1a janela 08:30-11:00');
check($janelas[1] === [840, 1020], '2a janela 14:00-17:00');

echo "== cadencia_slot: cai sempre em dia util, dentro de uma janela ==\n";
$cedo = static fn (int $a, int $b): int => $a;      // sempre o inicio possivel
$tarde = static fn (int $a, int $b): int => $b;     // sempre o fim da janela
$segunda0900 = strtotime('2026-09-14 09:00:00');    // segunda-feira
$slot = cadencia_slot('2026-09-14', $cedo, $segunda0900);
check(substr($slot, 0, 10) === '2026-09-14', "mesma manha deveria servir: $slot");
check(minutoDo($slot) === 542, "com agora=09:00 o piso e 09:02, veio " . minutoDo($slot));

$slot = cadencia_slot('2026-09-14', $cedo, strtotime('2026-09-14 12:00:00'));
check(minutoDo($slot) === 840, "meio-dia deve cair na 2a janela (14:00), veio $slot");

$slot = cadencia_slot('2026-09-14', $cedo, strtotime('2026-09-14 18:00:00'));
check(substr($slot, 0, 10) === '2026-09-15', "depois das 17h vai para o dia seguinte, veio $slot");
check(minutoDo($slot) === 510, "e abre na 1a janela, veio $slot");

$slot = cadencia_slot('2026-09-18', $cedo, strtotime('2026-09-18 18:00:00')); // sexta a noite
check(substr($slot, 0, 10) === '2026-09-21', "sexta a noite vai para segunda, veio $slot");

$slot = cadencia_slot('2026-09-19', $cedo, strtotime('2026-09-19 09:00:00')); // sabado
check(substr($slot, 0, 10) === '2026-09-21', "sabado vai para segunda, veio $slot");

$slot = cadencia_slot('2026-09-14', $tarde, $segunda0900);
check(minutoDo($slot) === 660, "com rand no teto, o slot e o fim da janela (11:00), veio $slot");

echo "== cadencia_slot: propriedade sobre 240 instantes ==\n";
$base = strtotime('2026-09-01 00:00:00');
$ruins = 0;
for ($i = 0; $i < 240; $i++) {
    $agora = $base + $i * 3607 * 7; // ~7h de passo, cobre semanas e fins de semana
    $s = cadencia_slot(date('Y-m-d', $agora), null, $agora);
    $ts = strtotime($s);
    $dow = (int) date('N', $ts);
    $min = minutoDo($s);
    $dentro = false;
    foreach (cadencia_janelas() as [$ini, $fim]) {
        if ($min >= $ini && $min <= $fim) { $dentro = true; }
    }
    if ($dow > 5 || !$dentro || $ts < $agora) {
        $ruins++;
        if ($ruins <= 3) { echo "  FALHOU: agora=" . date('c', $agora) . " -> $s\n"; }
    }
}
check($ruins === 0, "$ruins slots fora de janela, em fim de semana ou no passado");

echo "== cadencia_nome_ok ==\n";
$lead = ['company' => 'Contabilidade Alfa', 'id' => 1];
check(cadencia_nome_ok(['name' => 'Maria Souza'], $lead), 'nome de pessoa passa');
check(!cadencia_nome_ok(['name' => 'Contato'], $lead), '"Contato" nao e nome de gente');
check(!cadencia_nome_ok(['name' => 'contabilidade alfa'], $lead), 'nome igual ao da empresa nao serve');
check(!cadencia_nome_ok(['name' => 'Jo'], $lead), 'nome curto demais nao serve');
check(!cadencia_nome_ok(null, $lead), 'contato ausente nao serve');
check(!cadencia_nome_ok(['name' => 'FINANCEIRO'], $lead), 'caixa de papel nao e nome de gente');

echo "== etapas ativas ==\n";
check(cadencia_etapas_ativas() === [1, 2, 3, 4, 5], 'default sao as 5 etapas');
check(cadencia_etapa_seguinte(0) === 1, 'da etapa 0 a proxima e a 1');
check(cadencia_etapa_seguinte(2) === 3, 'da 2 a proxima e a 3');
check(cadencia_etapa_seguinte(5) === null, 'depois da ultima nao ha proxima');

echo "== linha pessoal no modelo ==\n";
$leadFull = ['company' => 'Contabilidade Alfa', 'contact_name' => 'Maria Souza', 'estimated_devices' => 42];
$contato = ['name' => 'Maria Souza', 'cargo' => 'Socia'];
$comLinha = cadencia_email_modelo(1, $leadFull, $contato, 'Bruna', 'Vi que voces abriram a filial de Campinas.');
check(str_contains($comLinha['corpo'], 'filial de Campinas'), 'a linha pessoal entrou no corpo');
check(!str_contains($comLinha['corpo'], '{'), 'nao sobrou chave com linha pessoal');
$semLinha = cadencia_email_modelo(1, $leadFull, $contato, 'Bruna', null);
check(!str_contains($semLinha['corpo'], '{'), 'nao sobrou chave sem linha pessoal');
check(!str_contains($semLinha['corpo'], "\n\n\n"), 'sem linha pessoal nao pode sobrar buraco de 3 quebras');
check(str_contains($semLinha['corpo'], 'Oi, Maria,'), 'a saudacao continua inteira: '
    . mb_substr($semLinha['corpo'], 0, 24));

echo "== render nao pode deixar duas assinaturas ==\n";
// O modelo padrao traz a assinatura antiga colada no fim. Com auto_assinatura
// configurada, o e-mail sairia com duas assinaturas e dois "responda SAIR".
$corpoPadrao = setting_str('cadencia_email_corpo_1');
check(str_contains($corpoPadrao, 'responda SAIR'),
    'pre-condicao: o modelo padrao ainda tem o bloco antigo');
$padraoSubstituido = str_replace('{meu_nome}', 'Bruna', CADENCIA_EMAIL_ASSINATURA);
$corpoRenderizado = cadencia_email_modelo(1, $leadFull, $contato, 'Bruna', null)['corpo'];
check(str_contains($corpoRenderizado, $padraoSubstituido),
    'pre-condicao: o corpo renderizado carrega a assinatura padrao');
$semDupla = str_replace($padraoSubstituido, '', $corpoRenderizado);
check(!str_contains($semDupla, 'responda SAIR'),
    'tirar o bloco conhecido resolve o SAIR duplicado');
check(str_contains($semDupla, 'demonstração de 10 minutos'),
    'e o texto do modelo continua inteiro depois de tirar o bloco');

echo "== render com rodape de descadastro ==\n";
$render = cadencia_auto_render(1, $leadFull, $contato, 'Bruna', null, 'https://ex.com/optout?t=1');
check(str_contains($render['corpo'], 'https://ex.com/optout?t=1'), 'o link de descadastro entrou');
check(str_contains($render['corpo'], 'SAIR'), 'o rodape de opt-out por resposta continua');
check(!str_contains($render['corpo'], '{link}'), 'a chave {link} foi substituida');
check(!str_contains($render['assunto'] . $render['corpo'], '{'), 'nada de chave sobrando no render');
// O rodape default tem que caber em duas linhas: e o que o destinatario ve
// depois da assinatura, e um paragrafo ali parece formulario, nao e-mail.
check(substr_count(setting_str('auto_optout_texto'), "\n") <= 1,
    'o rodape default passou de duas linhas');
check(str_contains(setting_str('auto_optout_texto'), '{link}'),
    'o rodape default precisa ter {link}');

echo "== token de descadastro ==\n";
$t = optout_token(42, 7);
check(strlen($t) === 16, 'token com 16 chars (link curto no corpo), veio ' . strlen($t));
check(ctype_xdigit($t), 'token so com hexadecimal');
check(optout_confere(42, 7, $t), 'o token confere para o par certo');
check(!optout_confere(42, 8, $t), 'token de outro contato nao confere');
check(!optout_confere(43, 7, $t), 'token de outro lead nao confere');
check(!optout_confere(42, 7, str_repeat('0', 32)), 'token forjado nao confere');
check(str_contains(optout_url(42, 7), '/crm/optout.php?l=42&c=7&t=' . $t), 'URL montada: ' . optout_url(42, 7));

echo "== teto do aquecimento ==\n";
check(cadencia_teto_hoje() === 15, 'sem data de inicio o teto e o da semana 1');

echo "== matriz das guardas ==\n";
/** Cenario base: tudo em ordem, o e-mail deve sair. */
function cenarioBase(): array
{
    return [
        'lead' => [
            'id' => 1, 'company' => 'Contabilidade Alfa', 'status' => 'novo',
            'no_contact' => 0, 'duplicate_of_lead_id' => null,
        ],
        'contato' => ['id' => 9, 'name' => 'Maria Souza', 'email' => 'maria@alfa.com.br'],
        'out' => [
            'lead_id' => 1, 'contact_id' => 9, 'sender_user_id' => 2, 'seq' => 1,
            'to_email' => 'maria@alfa.com.br', 'subject' => 'Horas da equipe na Alfa',
            'body' => 'Oi, Maria, tudo bem?',
        ],
        'ctx' => [
            'cadencia' => ['state' => 'ativa'], 'respondeu' => false,
            'check_status' => 'valido', 'check_detail' => '', 'na_janela' => true,
            'enviados_hoje' => 3, 'teto' => 15, 'dominio_quente' => false, 'dominio_dias' => 7,
        ],
    ];
}

/** Aplica uma modificacao no cenario base e devolve a decisao. */
function decide(array $mudancas): ?array
{
    $c = cenarioBase();
    foreach ($mudancas as $caminho => $valor) {
        [$onde, $chave] = explode('.', $caminho, 2);
        if ($valor === '__remover__') {
            $c[$onde] = null;
        } else {
            $c[$onde][$chave] = $valor;
        }
    }
    return cadencia_guardas_puras($c['lead'], $c['contato'], $c['out'], $c['ctx']);
}

check(decide([]) === null, 'cenario em ordem: o e-mail sai');

$matriz = [
    // [mudanca, acao esperada, pedaco do motivo]
    [['lead.no_contact' => 1],                       'parar', 'nao ser contactado'],
    [['lead.status' => 'demo_agendada'],             'parar', 'status avancou'],
    [['lead.status' => 'cliente'],                   'parar', 'status avancou'],
    [['lead.status' => 'perdido'],                   'parar', 'status avancou'],
    [['lead.duplicate_of_lead_id' => 7],             'parar', 'duplicado'],
    [['lead.x' => '__remover__'],                    'parar', 'lead removido'],
    [['ctx.cadencia' => ['state' => 'pausada']],     'parar', 'cadencia pausada'],
    [['ctx.cadencia' => null],                       'parar', 'cadencia inexistente'],
    [['ctx.respondeu' => true],                      'parar', 'resposta ja recebida'],
    [['ctx.check_status' => 'bounce'],               'parar', 'e-mail bounce'],
    [['ctx.check_status' => 'invalido'],             'parar', 'e-mail invalido'],
    [['ctx.check_status' => 'descartavel'],          'parar', 'e-mail descartavel'],
    [['contato.name' => 'Contato'],                  'parar', 'sem nome de pessoa'],
    [['contato.x' => '__remover__'],                 'parar', 'sem nome de pessoa'],
    // Chave sobrando PAUSA (da para corrigir e retomar), nao encerra o lead.
    [['out.body' => 'Oi, {primeiro_nome}'],          'pausar', 'chave nao substituida'],
    [['out.subject' => 'Horas na {empresa}'],        'pausar', 'chave nao substituida'],
    [['ctx.na_janela' => false],                     'adiar', 'fora da janela'],
    [['ctx.enviados_hoje' => 15],                    'adiar', 'teto diario'],
    [['ctx.enviados_hoje' => 99],                    'adiar', 'teto diario'],
    [['ctx.dominio_quente' => true],                 'adiar', 'mesmo dominio'],
];
foreach ($matriz as [$mudanca, $acao, $trecho]) {
    $r = decide($mudanca);
    $rotulo = json_encode($mudanca, JSON_UNESCAPED_UNICODE);
    check($r !== null, "$rotulo deveria bloquear e nao bloqueou");
    if ($r !== null) {
        check($r['acao'] === $acao, "$rotulo: esperava $acao, veio {$r['acao']}");
        check(str_contains($r['motivo'], $trecho), "$rotulo: motivo '{$r['motivo']}' nao fala de '$trecho'");
    }
}

echo "== o generico passa, o status generico nao bloqueia ==\n";
check(decide(['ctx.check_status' => 'generico']) === null,
    'caixa de papel (contato@) entra na cadencia — com prioridade menor, mas entra');
check(decide(['ctx.check_status' => 'nao_verificado']) === null,
    'endereco ainda nao verificado nao bloqueia (a verificacao acontece antes)');

echo "== quem para, para de vez; quem adia, so espera ==\n";
check(decide(['ctx.enviados_hoje' => 15])['acao'] === 'adiar', 'teto nao encerra a cadencia');
check(decide(['lead.no_contact' => 1])['acao'] === 'parar', 'opt-out encerra a cadencia');
// A ordem tambem e um contrato: opt-out vence teto batido.
check(decide(['lead.no_contact' => 1, 'ctx.enviados_hoje' => 99])['acao'] === 'parar',
    'com opt-out E teto batido, o que vale e o opt-out (parar), nunca adiar para amanha');

echo "== o motivo de 'fora da janela' e um contrato ==\n";
// cadencia_enviar_linha compara com esta string exata para decidir se reagenda
// para HOJE (proxima janela) ou para amanha. Mudar o texto sem mudar la
// empurraria em silencio um dia inteiro os ultimos minutos de cada janela.
check(decide(['ctx.na_janela' => false])['motivo'] === 'fora da janela de envio',
    'o texto do motivo mudou: ajuste tambem o if de cadencia_enviar_linha');
$fonteCadencia = str_replace("\r\n", "\n", (string) file_get_contents($CRM . '/lib/cadencia.php'));
check(str_contains($fonteCadencia, "=== 'fora da janela de envio'"),
    'cadencia_enviar_linha nao compara mais com o motivo da janela');
check(str_contains($fonteCadencia, "WHERE lead_id = ? AND status IN ('aguardando','agendado')\", [\$leadId]);"),
    'cadencia_auto_planejar precisa cancelar o pendente antes de inserir (um por lead)');

echo "\n";
echo $falhas === 0 ? "TODOS OS $total TESTES PASSARAM\n" : "$falhas FALHAS de $total testes\n";
exit($falhas === 0 ? 0 : 1);
