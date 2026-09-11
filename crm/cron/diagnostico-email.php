<?php
/**
 * Diagnostico do encanamento de e-mail do CRM. NAO envia nada e NAO escreve no
 * banco: abre o socket, faz a conversa ate o AUTH e desliga com QUIT.
 *
 *   php /caminho/para/crm/cron/diagnostico-email.php
 *
 * Existe porque "a cadencia esta dando tudo erro" tem cinco causas possiveis e
 * quatro delas sao invisiveis pela tela: a caixa nao esta no crm_config.php, a
 * senha esta errada, a caixa nao tem usuario ativo (e por isso nunca aparece em
 * "Quem assina"), ou a 587 esta bloqueada na hospedagem. Cada linha abaixo
 * separa uma da outra, com o codigo que o servidor devolveu.
 *
 * So CLI, como o tick: o .htaccess desta pasta nega a web, e o guard cobre o
 * caso de a negacao falhar.
 */

if (PHP_SAPI !== 'cli') {
    http_response_code(403);
    exit("So na linha de comando.\n");
}

require dirname(__DIR__) . '/lib/bootstrap.php';

$falhas = 0;

function diag(string $rotulo, string $estado, string $detalhe = ''): void
{
    printf("  %-24s %-6s %s\n", $rotulo, $estado, $detalhe);
}

/**
 * Conversa SMTP ate o AUTH, sem MAIL FROM: nada sai da caixa.
 *
 * @return array{ok:bool, erro:string, passos:string[]}
 */
function diag_smtp(array $conta, string $helo): array
{
    $passos = [];
    try {
        $s = SmtpSocket::abrir(
            (string) $conta['smtp_host'],
            (int) $conta['smtp_porta'],
            (string) $conta['smtp_seg']
        );
    } catch (Throwable $e) {
        return ['ok' => false, 'erro' => $e->getMessage(), 'passos' => $passos];
    }

    $desligar = function (string $erro) use ($s, &$passos): array {
        @smtp_cmd($s, 'QUIT');
        $s->fechar();
        return ['ok' => false, 'erro' => $erro, 'passos' => $passos];
    };

    $r = smtp_resposta($s);
    $passos[] = 'banner ' . $r['code'];
    if ($r['code'] !== 220) {
        return $desligar('servidor recusou a conexao: ' . $r['texto']);
    }

    $r = smtp_cmd($s, 'EHLO ' . $helo);
    $passos[] = 'EHLO ' . $r['code'];
    if ($r['code'] !== 250) {
        return $desligar('EHLO recusado: ' . $r['texto']);
    }
    $cap = strtoupper($r['texto']);

    if ((string) $conta['smtp_seg'] === 'tls') {
        if (!str_contains($cap, 'STARTTLS')) {
            return $desligar('servidor nao oferece STARTTLS (use smtp_seg "ssl" na porta 465)');
        }
        $r = smtp_cmd($s, 'STARTTLS');
        $passos[] = 'STARTTLS ' . $r['code'];
        if ($r['code'] !== 220 || !$s->ligarTls()) {
            return $desligar('STARTTLS falhou: ' . $r['texto']);
        }
        $r = smtp_cmd($s, 'EHLO ' . $helo);
        $passos[] = 'EHLO/TLS ' . $r['code'];
        if ($r['code'] !== 250) {
            return $desligar('EHLO pos-TLS recusado: ' . $r['texto']);
        }
        $cap = strtoupper($r['texto']);
    }

    if ((string) $conta['senha'] === '') {
        return $desligar('senha vazia no crm_config.php');
    }

    // Mesma escolha de mecanismo do envio real (lib/smtp.php), para o
    // diagnostico nao passar por um caminho que o envio nao usa.
    if (str_contains($cap, 'AUTH') && str_contains($cap, 'PLAIN')) {
        $r = smtp_cmd($s, 'AUTH PLAIN ' . base64_encode(
            "\0" . (string) $conta['usuario'] . "\0" . (string) $conta['senha']
        ));
        $passos[] = 'AUTH PLAIN ' . $r['code'];
    } else {
        $r = smtp_cmd($s, 'AUTH LOGIN');
        $passos[] = 'AUTH LOGIN ' . $r['code'];
        if ($r['code'] === 334) {
            $r = smtp_cmd($s, base64_encode((string) $conta['usuario']));
        }
        if ($r['code'] === 334) {
            $r = smtp_cmd($s, base64_encode((string) $conta['senha']));
        }
        $passos[] = 'AUTH fim ' . $r['code'];
    }
    if ($r['code'] !== 235) {
        // 535 = usuario/senha. Com 2FA na caixa a senha normal NAO serve:
        // hPanel -> E-mails -> a caixa -> Seguranca -> senha de aplicativo.
        return $desligar('autenticacao recusada (codigo ' . $r['code'] . ')');
    }

    smtp_cmd($s, 'QUIT');
    $s->fechar();
    return ['ok' => true, 'erro' => '', 'passos' => $passos];
}

echo "\n== crm_config.php ==\n";
$contas = mail_contas();
if (!$contas) {
    echo "  A chave 'mail' esta vazia ou ausente: o CRM nao envia nem le caixa.\n";
    echo "  Template em crm/README.md (o arquivo fica ACIMA do public_html).\n\n";
    exit(1);
}
diag('caixas configuradas', (string) count($contas), implode(', ', array_keys($contas)));
diag('mail_helo', (string) (cfg('mail_helo') ?: '(padrao: dominio da caixa)'));
diag(
    'optout_secret',
    cfg('optout_secret') ? 'ok' : 'ERRO',
    cfg('optout_secret') ? '' : 'sem ele o link de descadastro nao e assinado'
);

echo "\n== usuarios que podem assinar ==\n";
try {
    $remetentes = mail_remetentes();
    foreach ($remetentes as $u) {
        diag((string) $u['email'], 'ok', 'usuario #' . $u['id'] . ' (' . $u['name'] . ')');
    }
    if (!$remetentes) {
        diag('(nenhum)', 'ERRO', 'nenhuma caixa do crm_config.php tem usuario ativo');
        $falhas++;
    }
    foreach (mail_caixas_sem_usuario() as $email => $_conta) {
        diag($email, 'ERRO', 'caixa sem usuario: Configuracoes -> Usuarios e remetentes');
        $falhas++;
    }
} catch (Throwable $e) {
    diag('banco', 'ERRO', $e->getMessage());
    $falhas++;
}

echo "\n== SMTP (conversa ate o AUTH; nenhuma mensagem e enviada) ==\n";
foreach ($contas as $email => $conta) {
    $helo = (string) (cfg('mail_helo') ?: mail_dominio((string) $conta['email']));
    $r = diag_smtp($conta, $helo);
    echo '  ' . $email . '  ' . $conta['smtp_host'] . ':' . $conta['smtp_porta']
        . ' (' . $conta['smtp_seg'] . ")\n";
    diag('  passos', $r['ok'] ? 'ok' : 'ERRO', implode(' -> ', $r['passos']));
    if (!$r['ok']) {
        diag('  motivo', 'ERRO', $r['erro']);
        $falhas++;
    }
}

echo "\n== IMAP (login e logout; nada e marcado como lido) ==\n";
foreach ($contas as $email => $conta) {
    if ((string) $conta['imap_senha'] === '') {
        diag($email, 'ERRO', 'sem senha IMAP: os retornos nunca sao lidos');
        $falhas++;
        continue;
    }
    try {
        $imap = ImapMin::abrir((string) $conta['imap_host'], (int) $conta['imap_porta']);
        $ok = $imap->login((string) $conta['imap_usuario'], (string) $conta['imap_senha']);
        $imap->fechar();
        diag($email, $ok ? 'ok' : 'ERRO', $ok ? '' : 'login recusado (senha ou senha de aplicativo)');
        if (!$ok) {
            $falhas++;
        }
    } catch (Throwable $e) {
        diag($email, 'ERRO', $e->getMessage());
        $falhas++;
    }
}

echo "\n== interruptores ==\n";
try {
    $ligada = setting_bool('auto_ligada');
    $sandbox = setting_bool('auto_sandbox');
    diag('motor ligado', $ligada ? 'sim' : 'NAO', $ligada ? '' : 'nada sai enquanto estiver assim');
    diag('sandbox', $sandbox ? 'SIM' : 'nao', $sandbox ? 'o lead nao recebe: tudo volta para a nossa caixa' : '');
    diag('max por tick', (string) setting_int('auto_max_tick'));
} catch (Throwable $e) {
    diag('settings', 'ERRO', $e->getMessage());
    $falhas++;
}

echo "\n" . ($falhas === 0
    ? "Encanamento ok. Se mesmo assim nada sai, o motivo esta em Envios -> Nao sairam.\n\n"
    : $falhas . " problema(s) acima. Cada linha ERRO nomeia o que corrigir.\n\n");

exit($falhas === 0 ? 0 : 1);
