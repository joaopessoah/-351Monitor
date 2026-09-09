# +351 CRM — CRM de leads (interno)

Ferramenta interna do time comercial (João e Bruna) para tratar leads do +351 Monitor.
Vive **fora do spec do produto** (`docs/PROMPT-DESENVOLVIMENTO.md`) e fora do PostgreSQL do SaaS:
é um app PHP 8.2+ vanilla + MySQL/MariaDB rodando na **mesma hospedagem compartilhada do site**
(Hostinger), servido em `https://www.mais351monitor.com.br/crm/`.

- Deploy: `.github/workflows/deploy-crm.yml` (push em `main` tocando `crm/**` → FTPS → `public_html/crm/`).
  Este `README.md` é excluído do deploy.
- O formulário do site (`site/index.html#contato`) envia para `crm/intake.php`.
- O Claude (assistente) acessa leads via `crm/api/index.php` com Bearer token.

## Setup único no hPanel (produção)

1. **Banco**: hPanel → Bancos de Dados → MySQL → criar banco `uXXXX_crm` + usuário com todos os
   privilégios nesse banco. Anote nome/usuário/senha (no servidor o host é `localhost`).
2. **PHP**: hPanel → configuração PHP do site → fixar **PHP 8.2+** (extensão `pdo_mysql` já vem ativa).
3. **Config**: File Manager → pasta do domínio (`domains/mais351monitor.com.br/`, a que **contém**
   `public_html`) → criar `crm_config.php` com o template abaixo. Nunca dentro de `public_html`.
4. **Sessões**: na mesma pasta, criar o diretório `crm_sessions` (o CRM o usa como save_path isolado).
5. **HTTPS**: conferir que "Forçar HTTPS" está ativo (o cookie de sessão é `Secure`).
6. Depois do primeiro deploy: abrir `https://www.mais351monitor.com.br/crm/migrate.php`, colar a
   `migrate_key` e aplicar. **Copie as senhas temporárias exibidas — elas não aparecem de novo.**
   No primeiro login a troca de senha é obrigatória.
7. **Backup**: hPanel → Arquivos → Backups (conferir a frequência do plano). Antes de cada migration
   nova, exporte o banco pelo phpMyAdmin. O "Exportar CSV" da tela de Leads é uma cópia operacional.

### Template do `crm_config.php`

```php
<?php
// FICA FORA DO GIT E FORA DO WEBROOT. Gere os segredos com:
// php -r "echo rtrim(strtr(base64_encode(random_bytes(32)),'+/','-_'),'=') . PHP_EOL;"
return [
    'db_host'       => 'localhost',
    'db_name'       => 'uXXXX_crm',
    'db_user'       => 'uXXXX_crm',
    'db_pass'       => 'SENHA-DO-BANCO',
    'migrate_key'    => 'GERE-UM-SEGREDO-43-CHARS',
    'api_tokens'     => ['claude' => 'GERE-OUTRO-SEGREDO-43-CHARS'],
    'analytics_salt' => 'GERE-MAIS-UM-SEGREDO-43-CHARS', // sal do hash de visitante
    'cookie_secure'  => true,   // exige HTTPS
    'app_env'        => 'prod', // 'dev' liga display_errors

    // --- Cadência automática de e-mail (opcional; sem isto o CRM funciona
    // --- como antes, só sem enviar nem ler caixa). Detalhes e passo a passo:
    // --- docs/runbooks/cadencia-automatica.md
    'mail' => [
        'bruna@mais351monitor.com.br' => [
            'nome'  => 'Bruna | +351 Monitor',
            'senha' => 'SENHA-DA-CAIXA-OU-SENHA-DE-APLICATIVO',
            // defaults: smtp.hostinger.com:587 (tls) e imap.hostinger.com:993
            // 'smtp_host' => '...', 'smtp_porta' => 465, 'smtp_seg' => 'ssl',
            // 'imap_host' => '...', 'imap_porta' => 993, 'imap_senha' => '...',
        ],
        'joao@mais351monitor.com.br' => ['nome' => 'João | +351 Monitor', 'senha' => '...'],
    ],
    'mail_helo'        => 'mais351monitor.com.br',
    'site_url'         => 'https://www.mais351monitor.com.br',
    'optout_secret'    => 'GERE-MAIS-UM-SEGREDO-43-CHARS', // assina o link de descadastro
    'telegram'         => ['token' => '', 'chat_id' => ''], // opcional
    'healthchecks_url' => '',                               // dead-man switch do cron
];
```

Rotacionar o token da API = trocar o valor aqui. O token dá acesso a dados pessoais de leads —
trate como credencial de operador (LGPD).

### Uma caixa em `mail` não basta para ela virar remetente

Quem assina o e-mail é um **usuário do CRM** cujo login é a própria caixa. O seletor "Quem assina"
(em Leads, no detalhe do lead e na Fila) é a **interseção** entre a chave `mail` e a tabela `users`,
e `mail_remetentes()` percorre `users` — endereço que só existe no `crm_config.php` nunca é olhado.

Depois de acrescentar um endereço a `mail`, abra **Configurações → Usuários e remetentes**: a caixa
nova aparece em "Caixas configuradas que ainda não assinam nada", com um botão que cria o usuário e
mostra a senha temporária uma única vez. A mesma tela reativa quem foi desativado e gera senha nova.
Nada disso precisa de phpMyAdmin.

## Dev local (Windows)

1. PHP 8.2+ portátil (com `pdo_mysql` habilitado no `php.ini`) e um MySQL/MariaDB local **ou**
   o banco `uXXXX_crmdev` da Hostinger via hPanel → MySQL Remoto (libere o IP da sua máquina
   **apenas para o usuário do banco de dev**; use o host externo mostrado nessa tela).
2. Criar `crm_config.php` na **raiz do repo** (está no `.gitignore`) apontando para o banco de dev,
   com `cookie_secure => false` e `app_env => 'dev'`.
3. Rodar na raiz do repo: `php -S localhost:8080 -t crm` → `http://localhost:8080/migrate.php`.

## API para o Claude

Auth: `Authorization: Bearer <token>` (fallback: header `X-Api-Key`). Limite: 120 req/min.
Base: `https://www.mais351monitor.com.br/crm/api/index.php`

| Rota | Método | Entrada | Saída |
|---|---|---|---|
| `?r=leads` | GET | `status`, `source`, `q`, `so_vencidos=1`, `page` | `{items, page, total}` (25/pág) |
| `?r=lead&id=N` | GET | — | lead completo + `interactions` + `tasks` + `history` |
| `?r=leads` | POST | `{company*, cnpj, contact_name, email, whatsapp, source, estimated_devices, plan_interest, next_action_at, next_action_note, notes}` | `{id, duplicate_of_lead_id}` |
| `?r=lead-update` | POST | `{id*, ...campos acima}` | `{ok}` |
| `?r=lead-status` | POST | `{id*, status*, lost_reason (se perdido)}` | `{ok}` |
| `?r=interactions` | POST | `{lead_id*, type*, summary*, occurred_at}` | `{id}` |
| `?r=tasks` | GET | `due=hoje\|atrasadas\|abertas` | `{items}` |
| `?r=tasks` | POST | `{title*, due_at*, lead_id}` | `{id}` |
| `?r=task-done` | POST | `{id*}` | `{ok}` |
| `?r=cnpj-lookup&cnpj=` | GET | consulta pura, não grava | `{cnpj, data}` |
| `?r=cnpj-enrich` | POST | `{lead_id*}` — consulta e grava no lead | `{ok, data}` |
| `?r=analytics` | GET | `d=7\|30\|90` (padrão 30) | `{resumo, por_dia, paginas, origens, eventos, dispositivos, funil}` |
| `?r=analytics-visita&ref=` | GET | código de 6 chars da visita | jornada completa: `views` + `events` |
| `?r=analytics-vincular` | POST | `{ref*, lead_id*}` | `{ok}` |
| `?r=cadence-start` | POST | `{lead_id*, sender_user_id\|sender, contact_id, linha_pessoal}` | `{ok, outbox_id, scheduled_for}` |
| `?r=cadence-stop` | POST | `{lead_id*, estado: interrompida\|pausada, motivo}` | `{ok}` |
| `?r=cadence-line` | POST | `{lead_id*, linha_pessoal, estado: rascunho\|aprovada}` | `{ok, estado}` |
| `?r=cadence-report` | GET | `mes=AAAA-MM` | taxas por etapa, resposta, bounce, opt-out, motivos de saída |
| `?r=outbox` | GET | `status`, `lead_id`, `dia=hoje` | fila de saída (100 últimos) |
| `?r=inbox` | GET | `status=nao_tratado\|todos`, `kind` | retornos classificados |
| `?r=inbox-handled` | POST | `{id*}` | `{ok}` |
| `?r=email-check` | POST | `{email*, forcar}` | `{status, mx_ok, detail}` |

Enums — status: `novo, contato_feito, demo_agendada, demo_realizada, trial, cliente, perdido` ·
origem: `site, whatsapp, email, indicacao, lista_50, outro` · interação: `whatsapp, email, ligacao,
demo, reuniao, outro` · plano: `essencial, pro, indefinido`. Datas: `YYYY-MM-DD HH:MM`
(fuso America/Sao_Paulo; a API responde ISO 8601 com `-03:00`).

```bash
# Tarefas de hoje
curl -s -H "Authorization: Bearer $T" \
  "https://www.mais351monitor.com.br/crm/api/index.php?r=tasks&due=hoje"

# Registrar uma demo realizada
curl -s -X POST -H "Authorization: Bearer $T" -H "Content-Type: application/json" \
  -d '{"lead_id":1,"type":"demo","summary":"Demo de 10min no WhatsApp. Interessados no Essencial."}' \
  "https://www.mais351monitor.com.br/crm/api/index.php?r=interactions"
```

## CNPJ e enriquecimento (Receita Federal)

- **Fluxo recomendado de cadastro**: na tela "Novo lead", comece pelo CNPJ ("Buscar na Receita") —
  o formulário volta preenchido (empresa = nome fantasia ou razão social) com o resumo da RFB;
  se o CNPJ não constar na base pública, o cadastro segue manual com o CNPJ preservado.
- O CNPJ do lead é validado por dígito verificador (`norm_cnpj`), já com suporte ao
  **CNPJ alfanumérico** emitido pela RFB desde jul/2026. Dedupe considera CNPJ além de e-mail/fone.
- "Consultar na Receita" (detalhe do lead, API `cnpj-enrich`) busca **dados abertos da RFB**
  via fontes públicas gratuitas, sempre **server-side**: BrasilAPI → fallback minhareceita.org
  (`crm/lib/cnpj.php`). Traz razão social, situação cadastral, CNAE, porte, município/UF,
  abertura, capital social e sócios; snapshot fica em `leads.cnpj_json` + colunas de exibição.
- CNPJ recém-emitido pode demorar a aparecer nas bases públicas (dumps mensais da RFB).
- Import CSV: coluna opcional `cnpj` no fim (`empresa;contato;email;whatsapp;estacoes;origem;observacoes;cnpj`).

## Quadro de tarefas (board.php)

Kanban do trabalho do time, separado do Kanban do funil (`kanban.php`, que move *leads*).

**O card é a tarefa.** Não existe tabela de cards: `tasks` ganhou `column_id`, `sort_order`
e `description`. Por isso o botão ✓ do dashboard, a tarefa no detalhe do lead e o card do
quadro são o mesmo registro — a coluna marcada com `is_done = 1` é a que grava `done_at`.
Sempre existe exatamente uma; trocar qual é reconcilia o estado das tarefas em transação.

**Arrastar.** É o único `fetch` de tela do CRM. O JS move o card no DOM primeiro e manda
para `board.php?r=move` a **ordem completa dos ids da coluna de destino**; o servidor
renumera de 1 a N numa transação (sem posição fracionária, sem rebalanceamento depois).
Se a resposta falhar, o card volta para a posição exata de onde saiu. O corpo vai como
`x-www-form-urlencoded` de propósito: assim `$_POST` popula e o `csrf_check()` de sempre vale.

**Sem JavaScript** cada card mostra um `<select>` “mover para” que posta como qualquer outro
form (`.board.has-dnd .board-card form { display: none }` some com ele quando o arrasto sobe).

Tarefas geradas pela cadência de e-mail ficam ocultas por padrão — com 30 leads ativos são
dezenas de cards automáticos. O filtro “Mostrar cadência” liga. A coluna de conclusão mostra
só os últimos 30 dias, senão cresce para sempre.

As colunas (nome, cor, ordem, qual conclui) são editadas em **Configurações**. Não dá para
apagar a coluna de conclusão nem a última que sobrou; apagar qualquer outra exige escolher
para onde os cards dela vão.

## Cadência automática de e-mail (migrations 012-014)

Passo a passo para ligar, aceite de cada fase e diagnóstico:
**`docs/runbooks/cadencia-automatica.md`**. Aqui fica só o desenho.

**Um e-mail pendente por lead.** O próximo só é planejado depois que o anterior sai —
assim as guardas são avaliadas com o mundo do dia do envio (o lead pode ter respondido,
virado cliente ou pedido para sair entre o agendamento e o disparo), e o texto gravado
no `email_outbox` é exatamente o que foi enviado. A tela mostra esse texto e deixa
editar enquanto o e-mail não saiu.

**O cron faz tudo.** `crm/cron/tick.php`, a cada 10 minutos: envia o que venceu, lê as
caixas por IMAP, age nos retornos, manda o resumo às 17h30 e pinga o healthchecks.
Só CLI (guarda de `PHP_SAPI` + `.htaccess` da pasta) e com `GET_LOCK` no MariaDB, então
duas execuções simultâneas não duplicam envio. A reserva de cada linha é atômica
(`UPDATE ... WHERE status = 'agendado'`).

**Sem biblioteca de terceiro.** `lib/smtp.php` e `lib/imapmin.php` falam os dois
protocolos direto no socket TLS — a hospedagem não tem Composer, o resto do repo é PHP
puro, e vendorizar um cliente de e-mail traria dezenas de arquivos para o deploy FTPS
por causa de oito comandos. O diálogo SMTP é separado do socket (`interface SmtpStream`),
o que deixa `tests/mailer.php` roteirizar as respostas do servidor e exercitar AUTH,
dot-stuffing e todos os caminhos de erro sem subir nada. A extensão `imap` do PHP **não**
é necessária; a caixa é aberta em modo leitura (EXAMINE + BODY.PEEK), então nada é
marcado como lido no Outlook.

**Guardas reavaliadas em cada envio** (o motivo de cada pulo fica gravado): status ainda
no topo do funil, sem opt-out, sem duplicado, sem resposta já recebida, e-mail válido,
contato com nome de pessoa, texto sem chave `{...}`, dentro da janela, abaixo do teto
diário **da caixa** (contado por `from_email`, não por usuário — quem tem limite na
Hostinger é a caixa) e nenhum outro lead do mesmo domínio nos últimos 7 dias. A decisão
é uma função pura (`cadencia_guardas_puras`) separada da leitura do banco, e a matriz
inteira dela está em `tests/cadencia_auto.php`. Cada guarda **encerra**, **pausa** (só a
chave não substituída, que conserta em dez segundos) ou **adia**: teto e domínio quente
adiam para amanhã; fora da janela adia para a próxima janela do mesmo dia.

**Duas incertezas tratadas como incerteza, não como palpite.** Se a conexão cair depois
de o corpo já ter sido entregue ao servidor, ou se o PHP morrer antes de gravar, a linha
fica em "Travados em envio" esperando alguém conferir a caixa de Enviados — o CRM não
reenvia sozinho, porque o palpite errado manda o mesmo e-mail duas vezes. E o cursor do
IMAP só avança depois que a mensagem foi processada de ponta a ponta: uma falha de
leitura deixa o UID para o próximo tick, em vez de perder a resposta para sempre.

**Retorno.** O classificador (`lib/inbound.php`) separa devolução, resposta automática,
pedido de saída e gente — nesta ordem, e com viés para **parar** a cadência na dúvida.
A citação da mensagem anterior é removida antes de procurar "SAIR": sem isso, toda
resposta que citasse o nosso rodapé viraria um descadastro.

**Ritmo.** Horário sorteado dentro de 8h30–11h e 14h–17h (configurável), só dias úteis,
feriado nacional na tabela `feriados`, teto por caixa com aquecimento 15 → 20 → 30 e no
máximo 5 envios por execução do cron. Sem pixel de abertura e sem link rastreado: a
métrica de engajamento é a resposta.

**Opt-out.** Todo e-mail leva `List-Unsubscribe` + `List-Unsubscribe-Post` (é o que faz
o Gmail mostrar "cancelar inscrição" em vez de "marcar como spam") e um link assinado
por HMAC. O POST remove na hora (exigência do padrão de 1 clique); o GET pede
confirmação, porque antivírus corporativo abre todos os links da mensagem.

**Interruptores.** `Motor ligado` (para o envio; a leitura continua), `Sandbox` (o
e-mail sai de verdade, mas para a própria caixa) e `Modo aprovação` (nada sai sem alguém
liberar em Envios). Todos em Configurações.

## Analytics do site (views e cliques)

Medição própria do `www.mais351monitor.com.br`, sem Google Analytics e sem terceiros.
Painel em **`/crm/analytics.php`** (aba "Site" do menu).

- **Como funciona:** `site/assets/js/track.js` (~6 KB, sem dependência) manda `pv` (pageview),
  `ev` (clique) e `end` (tempo de leitura + scroll) para `crm/collect.php`, que grava em
  `site_visits` / `site_views` / `site_events`. Mesmo domínio, mesmo banco dos leads.
- **Sem cookie e sem IP:** o visitante é `sha256(sal + data + IP + user-agent)` truncado em 32
  chars — irreversível e trocado toda meia-noite. Visita = 30 min sem atividade. Consequência a
  saber: "visitantes" num período de 30 dias é a **soma dos únicos de cada dia**, e dois
  computadores atrás do mesmo IP corporativo com o mesmo navegador viram uma visita só.
  O sal vem de `analytics_salt` no `crm_config.php`; sem ele, é derivado dos outros segredos.
- **O track.js precisa carregar ANTES do home.js.** Ele instala um `dataLayer` de verdade e o
  `trackCalc()` que já existia no home.js passa a cair aqui — por isso os 8 eventos da
  calculadora funcionam sem uma linha alterada naquele arquivo. Plugar um GTM por cima depois
  continua funcionando.
- **Eventos automáticos:** `whatsapp` (os 8 CTAs — o texto pré-preenchido identifica qual),
  `email`, `outbound`, `anchor`, mais `calculator_interaction` / `calculator_calculate` /
  `calculator_demo_click` vindos da calculadora.
- **Código da visita (`ref_code`):** cada visita ganha um código de 6 chars que o track.js
  pendura no fim do texto dos links `wa.me` (`… #K7M2Q9`). Quando a conversa chega no WhatsApp,
  cole o código no painel para ver a jornada e **vincular a visita ao lead** — daí saem as
  colunas "Leads" por origem e o card "Jornada no site" no detalhe do lead.
- **Bots** são barrados por user-agent no servidor; o **Global Privacy Control** do visitante é
  respeitado (o track.js nem roda).
- **Retenção:** visitas sem lead são apagadas com 365 dias (poda oportunista em ~0,5% das
  escritas). As vinculadas a um lead ficam com ele.
- **Rate limit:** só a *criação* de visita passa pelo `throttle_events` (30/h e 120/dia por IP);
  os hits seguintes não gravam nada lá. Tetos por visita: 200 views e 300 eventos.

## Testes

`php crm/tests/run.php` — suítes das funções puras (dias úteis da cadência, modelos de
e-mail e o link mailto, parser das migrations, regressões de code review, e as quatro
da cadência automática: janelas e sorteio de horário, montagem MIME e diálogo SMTP
inteiro contra um servidor de mentira, classificador de retorno com fixtures reais de
devolução e auto-resposta, camadas da validação de e-mail com o DNS injetado). Não
tocam no banco nem sobem servidor: os stubs de `rows/q/scalar/row` estouram de
propósito, o que também exercita o caminho "migration ainda não aplicada". Rode antes
de subir para a Hostinger — a hospedagem compartilhada não é lugar de descobrir erro
de sintaxe.

## Segurança e LGPD (resumo)

- Login com rate limit (8/15min por IP), senha `password_hash()`, troca forçada no 1º acesso,
  sessão httpOnly/Secure/SameSite=Lax em save_path próprio, CSRF em todo POST.
- `/crm/` fora de índices: `robots.txt` + `X-Robots-Tag: noindex`.
- Intake público com honeypot, time-trap, limite 5/h e 20/dia por IP; IPs de envio ficam 90 dias
  no `intake_log` (poda automática) — retenção declarada na política de privacidade do site.
- Leads sem avanço: eliminar em até 12 meses. **Não há botão de excluir na tela** (decisão de
  produto) — a limpeza é feita pelo phpMyAdmin; `lead_delete()` continua em `lib/model.php` para
  quem precisar do caminho programático (CASCADE apaga interações, tarefas e histórico).
- Opt-out ("não me contacte"): marcar no detalhe do lead. O registro é **mantido** de propósito —
  é a lista de supressão que impede o contato de voltar pela fila, pelo import ou pelo site
  (`lead_create()` herda o flag do duplicado). Marcar encerra as tarefas abertas e bloqueia a cadência.
  A cadência automática marca sozinha quando alguém responde "SAIR" ou usa o link de
  descadastro, e responde confirmando a remoção.
- Prospecção outbound: base legal é legítimo interesse em contato B2B com dados públicos
  (art. 7º, IX), declarada na seção "Prospecção comercial" da política de privacidade do
  site. Corpo do e-mail no outbox e trecho do retorno somem junto com o lead (CASCADE).
- Analytics do site sem cookie, sem storage no navegador e **sem IP gravado** (hash diário
  irreversível); GPC respeitado; retenção de 365 dias — tudo declarado na seção "Medição de
  audiência deste site" da política de privacidade.
