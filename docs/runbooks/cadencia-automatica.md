# Runbook — ligar a cadência automática de e-mail

> Par de `docs/comercial/prospeccao.md` (a estratégia) e de `crm/README.md` (o CRM).
> Código: `crm/lib/cadencia.php`, `crm/lib/mailer.php`, `crm/lib/inbound.php`, `crm/cron/tick.php`.
> Migrations **012, 013 e 014**.

O motor faz três coisas, nesta ordem, a cada 10 minutos: envia o que venceu (depois
de reavaliar todas as guardas), lê as caixas de quem assina e age no lead conforme o
retorno, e no fim do dia manda o resumo. Nada disso é IA: é PHP determinístico com
horário sorteado e teto diário.

**Ordem obrigatória:** F0 (preparar) → F1 (sandbox) → F2 (retornos) → F3 (ligar de
verdade). Não pule o sandbox: o primeiro e-mail que sair errado sai para um prospect.

---

## F0 — Preparar (meia hora, tudo no hPanel)

### 1. Conferir SPF, DKIM e DMARC

hPanel → E-mails → `mais351monitor.com.br` → **Configurações DNS/Registros**. Os três
precisam existir *antes* do primeiro envio automático. Sem DKIM, uma parte dos e-mails
vai direto para spam e as taxas do playbook não medem mais nada.

### 2. Descobrir os limites atuais da caixa

hPanel → E-mails → **Estatísticas / Limites**. Em setembro de 2026 o plano Business
Starter dava 1.000 mensagens por dia por caixa e 500 por hora via SMTP. Nosso teto é
30 por dia por caixa — 3% do limite. Se a Hostinger mudar os números, o que manda é o
painel, não este texto.

### 3. Senha de aplicativo por caixa

Se a caixa tiver 2FA, crie uma senha de aplicativo (hPanel → E-mails → a caixa →
Segurança). Ela vale para SMTP **e** IMAP. Sem 2FA, a senha normal serve.

### 4. Escrever a configuração

File Manager → a pasta que **contém** `public_html` → editar `crm_config.php`.
Acrescente as chaves abaixo às que já existem (banco, `migrate_key`, `api_tokens`…):

```php
    // Caixas que assinam a cadência. A chave é o próprio endereço.
    'mail' => [
        // Institucional: os e-mails automáticos sem dono saem daqui.
        'contato@mais351monitor.com.br' => [
            'nome'       => '+351 Monitor',
            'senha'      => 'SENHA-OU-SENHA-DE-APLICATIVO',
            // Os campos abaixo já têm estes valores por padrão:
            // 'smtp_host' => 'smtp.hostinger.com', 'smtp_porta' => 587, 'smtp_seg' => 'tls',
            // 'imap_host' => 'imap.hostinger.com', 'imap_porta' => 993,
        ],
        // Pessoais: quem assina a cadência (a resposta cai no Outlook da pessoa).
        'bruna@mais351monitor.com.br' => [
            'nome'  => 'Bruna | +351 Monitor',
            'senha' => 'OUTRA-SENHA',
        ],
        'joao@mais351monitor.com.br' => [
            'nome'  => 'João | +351 Monitor',
            'senha' => 'OUTRA-SENHA',
        ],
    ],
    'mail_helo'        => 'mais351monitor.com.br',
    'site_url'         => 'https://www.mais351monitor.com.br',
    // Gere com: php -r "echo rtrim(strtr(base64_encode(random_bytes(32)),'+/','-_'),'=') . PHP_EOL;"
    'optout_secret'    => 'GERE-UM-SEGREDO-43-CHARS',
    // Opcionais
    'telegram'         => ['token' => '', 'chat_id' => ''],
    'healthchecks_url' => '',
```

`smtp_seg` aceita `tls` (porta 587, STARTTLS — o padrão) ou `ssl` (porta 465, cifra
desde o primeiro byte). Se a hospedagem bloquear a 587, troque as duas chaves juntas.

> **Não precisa da extensão `imap` do PHP.** O CRM fala IMAP direto no socket TLS
> (`crm/lib/imapmin.php`), abrindo a caixa em modo leitura (EXAMINE + BODY.PEEK):
> nenhuma mensagem é marcada como lida e o Outlook continua idêntico.

### 5. Aplicar as migrations

`https://www.mais351monitor.com.br/crm/migrate.php` → colar a `migrate_key` →
aplicar. Devem entrar **012, 013 e 014**. Antes disso, exporte o banco pelo phpMyAdmin
(regra da casa para toda migration nova).

### 6. Criar o cron

hPanel → Avançado → **Cron Jobs** → novo, **a cada 10 minutos**:

```
/usr/bin/php /home/uXXXX/domains/mais351monitor.com.br/public_html/crm/cron/tick.php
```

O caminho exato do PHP e do site aparece na própria tela do hPanel. A política de uso
da hospedagem compartilhada não permite intervalo menor que 5 minutos nem mais de 5
crons simultâneos — 10 minutos está dentro.

O `tick.php` só roda em linha de comando (três guardas: `.htaccess` da pasta, checagem
de `PHP_SAPI` e um lock no MariaDB). Rodar duas vezes ao mesmo tempo não duplica envio.

### 7. Testar o encanamento

CRM → **Configurações** → *Cadência automática* → **Enviar teste para mim**, em cada
caixa. Se chegar, o SMTP funciona a partir da hospedagem. Depois clique em
**Rodar agora** na tela de Envios: a coluna "Último UID lido" em Configurações deve
sair de `—` para `<uidvalidity>:<uid>`, o que prova que o IMAP também funciona.

**Aceite da F0:** e-mail de teste recebido e o UID inicial gravado nas duas caixas.

---

## F1 — Sandbox (uma semana de conferência, sem risco)

Em **Configurações → Cadência automática**:

| Campo | Valor |
|---|---|
| Motor ligado | ✅ |
| Sandbox | ✅ |
| E-mail do sandbox | o seu (ou deixe vazio: vai para a própria caixa remetente) |
| Modo | Aprovação |
| Etapas ativas | `1,2,3,4,5` (ou `1,2,5` para os 3 toques do playbook) |

Com o sandbox ligado **nada chega a um lead**: o e-mail sai de verdade pelo SMTP, com
o texto final, mas endereçado a você, com `[SANDBOX]` no assunto e a primeira linha
dizendo para quem iria.

Escolha 5 a 10 leads em **Leads**, marque as caixas e clique **Iniciar cadência…**.
A prévia mostra quem entra, quem fica de fora e por quê. Confirme.

Em **Envios**: aprove, use **Enviar agora** e confira o que chegou na sua caixa.

**Aceite da F1:**

- [ ] O 1º e-mail chega com o nome certo do contato e sem nenhuma chave `{...}`.
- [ ] O 2º chega **na mesma conversa** do 1º (o cliente de e-mail agrupa).
- [ ] A timeline do lead ganhou a interação "Primeiro e-mail enviado automaticamente".
- [ ] Nasceram as tarefas humanas de ligação (D+2) e LinkedIn (D+7) no quadro.
- [ ] O lead saiu de "Novo" para "Contato feito".
- [ ] Cada guarda foi provada uma vez: marque um lead como "não contactar", avance
      outro para "Demo agendada", aponte um terceiro para um domínio inexistente, e
      confira que os três aparecem em **Não saíram** com o motivo escrito.

---

## F2 — Retornos (ainda em sandbox)

Responda um dos e-mails do sandbox e rode o tick.

**Aceite da F2:**

- [ ] Resposta normal → cadência encerrada, tarefa "Responder …" criada, aviso por
      e-mail, contador no menu.
- [ ] Resposta contendo **SAIR** na primeira linha → lead marcado como "não contactar"
      e confirmação automática enviada a quem pediu.
- [ ] Resposta **citando** o nosso rodapé (que contém a palavra SAIR) → continua sendo
      tratada como resposta humana, **não** como descadastro. Essa é a armadilha que a
      suíte `crm/tests/inbound.php` guarda.
- [ ] E-mail para um endereço inexistente → devolução detectada, endereço marcado como
      `bounce`, cadência parada, tarefa "Corrigir o e-mail".
- [ ] Auto-resposta de férias → registrada, **sem** parar a cadência.
- [ ] O link de descadastro do rodapé abre a página, pede confirmação e remove.

---

## F3 — Ligar de verdade

1. **Publicar a política de privacidade** com a seção de prospecção B2B
   (`site/privacidade.html`, seção "Prospecção comercial"). Isto é pré-requisito, não
   recomendação: o texto anterior dizia "nada de listas de e-mail em massa" e não
   mencionava contato a partir de dados públicos da Receita.
2. Em Configurações: **desmarcar Sandbox**, manter **modo Aprovação**, preencher
   **Início do aquecimento** com a data de hoje (o campo se preenche sozinho ao
   desligar o sandbox com o motor ligado).
3. Semana 1: teto 15/dia por caixa. Semana 2: 20. Depois: 30. O motor troca sozinho
   pela data de início.
4. Ao fim das duas semanas, se o bounce estiver abaixo de 3% e não houver reclamação,
   trocar o modo para **Automático**.

**Aceite da F3:** duas semanas sem envio fora de janela ou acima do teto, bounce < 3%,
opt-out < 1%, zero reclamação, taxa de resposta em torno dos 5% do playbook.

---

## Operação do dia a dia

| Onde | O que olhar |
|---|---|
| Menu | O número ao lado de **Envios** é retorno não tratado. É a única coisa que precisa de gente hoje. |
| Dashboard | Enviados hoje / teto, aguardando aprovação, retornos, última execução do cron. |
| Envios | Fila, texto exato de cada e-mail, motivo de tudo que não saiu, retornos com trecho. |
| 17h30 | Resumo do dia por e-mail: o que saiu, o que não saiu e por quê, retornos. |

### Botão de pânico

**Configurações → Cadência automática → desmarcar "Motor ligado" → Salvar.** Para o
envio na hora; a leitura da caixa continua (respostas e descadastros seguem sendo
honrados, que é o comportamento certo).

---

## Quando alguma coisa não funciona

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| Acrescentei um e-mail em `mail` e ele não aparece em "Quem assina" | o seletor é a interseção entre a chave `mail` e a tabela `users`: falta o usuário ativo com esse login | **Configurações → Usuários e remetentes** → "Caixas configuradas que ainda não assinam nada" → *Criar usuário para esta caixa*. A senha temporária aparece uma única vez |
| "o cron parou de rodar" no dashboard | cron desligado ou caminho do PHP errado | hPanel → Cron Jobs; rode o comando manualmente com `-v` |
| Nada sai, fila cheia de "Agendado" | motor desligado, fora da janela, teto batido | Configurações; o motivo aparece em **Não saíram** |
| Falha de autenticação no teste | senha errada, ou 2FA sem senha de aplicativo | gere a senha de aplicativo no hPanel |
| "servidor não oferece STARTTLS" | porta 587 bloqueada | troque para `'smtp_seg' => 'ssl'` e `'smtp_porta' => 465` |
| Retornos não aparecem | senha IMAP ausente, ou o cursor pulou | Configurações mostra "sem senha" e o último UID lido |
| Tudo virou "resposta humana" | classificador conservador de propósito | é o viés escolhido: na dúvida, para |
| "Travados em envio" na tela de Envios | o PHP foi encerrado, **ou a conexão caiu depois de o corpo já ter sido entregue** ao servidor (o erro diz "entrega incerta") | **confira a caixa de Enviados do remetente** e escolha "Já saiu" ou "Não saiu — reenfileirar". O CRM não adivinha nem tenta de novo sozinho, porque o palpite errado manda o mesmo e-mail duas vezes |
| Cadência "Pausada" com motivo "modelo com chave não substituída" | sobrou um `{alguma coisa}` no texto — quase sempre uma chave digitada errado na linha pessoal | corrija a linha pessoal no lead e clique em Retomar. A cadência **pausa**, não é encerrada: um typo não custa o lead |
| Um lead recebeu duas vezes | não deveria: há reserva atômica + lock | mande o `id` do outbox e o log; é bug, não configuração |

Diagnóstico manual, com saída detalhada:

```bash
# PRIMEIRO: encanamento (não envia nada, não escreve no banco). Testa cada caixa
# do crm_config.php: SMTP até o AUTH, login IMAP, usuário ativo, interruptores.
php /caminho/para/crm/cron/diagnostico-email.php

php /caminho/para/crm/cron/tick.php -v
php /caminho/para/crm/cron/tick.php --so-ler   # só lê a caixa, não envia nada
```

Rode o `diagnostico-email.php` antes de mexer em qualquer coisa: ele distingue
"senha errada" de "caixa sem usuário" de "porta bloqueada" de "motor desligado",
que na tela de Envios aparecem todas como a mesma fila parada.

---

## O que o motor nunca faz

- Enviar fora das janelas, em fim de semana ou em feriado nacional (tabela `feriados`,
  carregada com 2026 e 2027 — **acrescente 2028 antes de janeiro**).
- Enviar para quem está em "não contactar", para lead duplicado, para lead que já saiu
  do topo do funil ou para contato sem nome de pessoa.
- Enviar dois e-mails para o mesmo domínio dentro de 7 dias (configurável).
- Passar do teto diário por caixa.
- Enviar um texto com chave `{...}` não substituída.
- Rastrear abertura: não existe pixel nem link mascarado. A métrica é a resposta.

---

## LGPD

- Base legal: legítimo interesse em contato B2B com dados públicos (art. 7º, IX),
  com remetente real, assunto honesto, opt-out em todo e-mail e oposição honrada na hora.
- O opt-out é **lista de supressão**: o registro é mantido de propósito, é ele que
  impede o contato de voltar pela fila, pelo import ou pelo site.
- Retenção: corpo do e-mail no outbox e trecho do retorno são apagados junto com o
  lead (CASCADE), dentro dos 12 meses da política.
- Do retorno guardamos assunto e 400 caracteres. A mensagem inteira fica na caixa de
  e-mail, que já é o registro dela.
- Nenhum dado de lead sai para terceiro: o envio é SMTP próprio, a leitura é IMAP
  próprio, e não há serviço de disparo no meio.
