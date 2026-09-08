<?php
/**
 * Descadastro de 1 clique. Publico e sem sessao: quem autentica e o token
 * assinado (HMAC do par lead+contato), que so nos sabemos gerar.
 *
 * POST age na hora — e o que o cabecalho List-Unsubscribe-Post exige para o
 * botao "cancelar inscricao" do Gmail e do Outlook aparecer no lugar do
 * "marcar como spam".
 * GET mostra um botao de confirmacao, porque antivirus de e-mail corporativo
 * abre todos os links da mensagem: um GET que ja removesse tiraria da lista
 * gente que nunca clicou.
 */

require __DIR__ . '/lib/bootstrap.php';

security_headers();
session_boot();

$leadId = (int) ($_REQUEST['l'] ?? 0);
$contactId = (int) ($_REQUEST['c'] ?? 0);
$token = (string) ($_REQUEST['t'] ?? '');
$ip = client_ip();

$estado = 'invalido'; // invalido | confirmar | pronto | erro
$empresa = '';

if ($leadId > 0 && $token !== '' && optout_confere($leadId, $contactId ?: null, $token)) {
    $lead = row('SELECT id, company, no_contact FROM leads WHERE id = ?', [$leadId]);
    if ($lead === null) {
        $estado = 'pronto'; // lead ja apagado: para quem pediu, o efeito e o mesmo
    } else {
        $empresa = (string) $lead['company'];
        $estado = !empty($lead['no_contact']) ? 'pronto' : 'confirmar';
        if ($_SERVER['REQUEST_METHOD'] === 'POST' && $estado === 'confirmar') {
            try {
                if (throttle_blocked('optout', $ip, 20, 60)) {
                    $estado = 'erro';
                } else {
                    throttle_add('optout', $ip);
                    lead_set_no_contact($leadId, true, 'descadastro pelo link do e-mail');
                    $c = $contactId > 0 ? row('SELECT email FROM lead_contacts WHERE id = ?', [$contactId]) : null;
                    interaction_add($leadId, 'email',
                        'Descadastro pelo link do e-mail' . (!empty($c['email']) ? ' (' . $c['email'] . ')' : '') . '.',
                        date('Y-m-d H:i:s'), null, null);
                    $estado = 'pronto';
                }
            } catch (Throwable $e) {
                error_log('optout: ' . $e->getMessage());
                $estado = 'erro';
            }
        }
    }
}

http_response_code($estado === 'invalido' ? 404 : 200);
?>
<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>Cancelar contato — +351 Monitor</title>
<style>
  :root { color-scheme: dark; }
  body {
    margin: 0; min-height: 100vh; display: grid; place-items: center;
    background: #0B0F1A; color: #9AA6B8;
    font: 16px/1.6 'Segoe UI', system-ui, -apple-system, sans-serif;
    padding: 24px;
  }
  .caixa {
    max-width: 520px; width: 100%; background: #101724; border: 1px solid #1A2230;
    border-radius: 16px; padding: 32px;
  }
  .marca { font-weight: 700; font-size: 1.05rem; color: #F2F5FA; margin-bottom: 24px; }
  .marca em { color: #B6FF3C; font-style: normal; }
  h1 { color: #F2F5FA; font-size: 1.45rem; line-height: 1.2; margin: 0 0 14px; }
  p { margin: 0 0 14px; }
  strong { color: #F2F5FA; }
  button {
    font: inherit; font-weight: 600; cursor: pointer; margin-top: 10px;
    background: #B6FF3C; color: #0B0F1A; border: 0; border-radius: 10px; padding: 11px 20px;
  }
  button:hover { filter: brightness(1.08); }
  button:focus-visible { outline: 2px solid #F2F5FA; outline-offset: 2px; }
  a { color: #B6FF3C; }
  .pe { font-size: 0.85rem; color: #5C6879; margin-top: 26px; }
</style>
</head>
<body>
<main class="caixa">
  <div class="marca"><em>+</em>351 Monitor</div>

  <?php if ($estado === 'confirmar'): ?>
    <h1>Cancelar o contato comercial?</h1>
    <p>Confirmando, paramos de escrever para você agora e não voltamos depois.</p>
    <form method="post">
      <input type="hidden" name="l" value="<?= (int) $leadId ?>">
      <input type="hidden" name="c" value="<?= (int) $contactId ?>">
      <input type="hidden" name="t" value="<?= esc($token) ?>">
      <button type="submit">Confirmar e me remover</button>
    </form>
  <?php elseif ($estado === 'pronto'): ?>
    <h1>Pronto, você saiu da lista.</h1>
    <p>Não vamos mais enviar e-mails comerciais<?= $empresa !== '' ? ' sobre a <strong>' . esc($empresa) . '</strong>' : '' ?>.
       Guardamos apenas o registro do seu pedido, que é o que garante que o contato não volte
       por uma nova importação.</p>
    <p>Se isso foi engano ou se um dia fizer sentido conversar, responda o último e-mail que
       enviamos e a gente reativa.</p>
  <?php elseif ($estado === 'erro'): ?>
    <h1>Não deu para concluir agora.</h1>
    <p>Tente de novo em alguns minutos ou responda <strong>SAIR</strong> ao nosso e-mail — funciona igual.</p>
  <?php else: ?>
    <h1>Link inválido ou expirado.</h1>
    <p>Responda <strong>SAIR</strong> ao nosso e-mail que removemos seu contato do mesmo jeito.</p>
  <?php endif; ?>

  <p class="pe">+351 Monitor ·
    <a href="<?= esc(rtrim((string) (cfg('site_url') ?: 'https://www.mais351monitor.com.br'), '/')) ?>/privacidade.html">Política de privacidade</a>
  </p>
</main>
</body>
</html>
