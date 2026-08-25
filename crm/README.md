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
    'migrate_key'   => 'GERE-UM-SEGREDO-43-CHARS',
    'api_tokens'    => ['claude' => 'GERE-OUTRO-SEGREDO-43-CHARS'],
    'cookie_secure' => true,   // exige HTTPS
    'app_env'       => 'prod', // 'dev' liga display_errors
];
```

Rotacionar o token da API = trocar o valor aqui. O token dá acesso a dados pessoais de leads —
trate como credencial de operador (LGPD).

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

## Testes

`php crm/tests/run.php` — suítes das funções puras (dias úteis da cadência, modelos de
e-mail e o link mailto, parser das migrations, regressões de code review). Não tocam no
banco nem sobem servidor: os stubs de `rows/q/scalar/row` estouram de propósito, o que
também exercita o caminho "migration ainda não aplicada". Rode antes de subir para a
Hostinger — a hospedagem compartilhada não é lugar de descobrir erro de sintaxe.

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
