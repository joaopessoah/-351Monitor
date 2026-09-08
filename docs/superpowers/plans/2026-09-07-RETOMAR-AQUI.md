# RETOMAR AQUI — fase F6 (produtividade macro no painel)

> Escrito em 07/09/2026 21:5x por sessão que estava com o contexto no fim. Leia este
> arquivo primeiro; ele diz o que está pronto, o que estava em voo e o que fazer a seguir.

**Spec (fonte da verdade do produto):** `docs/superpowers/specs/2026-09-07-produto-produtividade-design.md`
As nove decisões do dono estão na seção 7, todas APROVADAS com as recomendações.

**Plano da primeira onda:** `docs/superpowers/plans/2026-09-07-produtividade-f1-dados.md` (executado)

**Artefatos que o dono aprovou:**
- estudo e plano: https://claude.ai/code/artifact/9f5d1cc3-176f-4647-9e86-33199c35af3c
- mockup navegável (é o ALVO visual): https://claude.ai/code/artifact/e5a02426-7399-4f2a-a5a3-3cadfe5e8ced
- cópia local do mockup, com as três telas: `painel-proposta.html` no scratchpad da sessão
  (se não existir mais, o artefato acima é a referência)

---

## Estado em 07/09/2026

Tudo abaixo está em `main` e **em produção**: site publicado por FTPS e painel de staging
rodando em `painel.2-25-193-15.sslip.io` (bundle `index-DRxOXtS6.js`, `/healthz` 200).
Backend com **445 testes de integração verdes**; portal com typecheck e build limpos.

### Entregue
- **Dados**: balde `seconds_unclassified` separado do neutro; tabela `hourly_activity`
  preenchida na mesma transação da agregação diária; `GET /dashboard/overview` com índice e
  cobertura calculados no servidor; tabela `people` chaveada por `windows_sid` com
  `GET/PATCH /people`; `POST /reaggregation` até 366 dias; `classification_vocabulary` por
  organização; motor de alertas de gestão (`management_alerts` + job de 15 min + `GET /alerts`)
  com sete regras e as duas de pessoa como opt-in (`organizations.person_alerts_enabled`).
- **Portal**: Visão Geral nova com a faixa "Agora" como primeiro cabeçalho, seis KPIs com
  minigráfico, donut da composição, atividade por hora, anel do índice com meta e cobertura,
  cartão de alertas, equipes lado a lado com mínimo de 3 pessoas, resumo do período;
  Colaboradores; Linha do Tempo em três níveis; página da pessoa com indicadores e composição
  por dia; Classificação com fila por impacto, vocabulário e recalcular histórico.
- **Site**: vocabulário alinhado, `como-medimos.html`, seções repetidas fundidas.
- **Infra**: deploy de staging consertado (dois defeitos, ver seção de armadilhas).

### Atualização de 07/09 22:1x — as cinco lacunas do mockup foram FECHADAS e implantadas
Composição por dia com os quatro baldes ligada ao período global, aplicativos por período,
seletor de período personalizado, mapa do mês por DIA (com `GET /people/daily` novo) e busca
global com Ctrl+K. 449 testes verdes, bundle `index-BMaidHx2.js` no ar. Os dois botões do
cabeçalho (Resumo em PDF e Enviar por e-mail) ficaram DESABILITADOS com o motivo no title.

### Atualização final de 07/09 — TODAS AS FASES F0 A F8 ENTREGUES E IMPLANTADAS
Nada ficou em voo. Árvore limpa, `main` sincronizado, 479+ testes verdes, bundle
`index-CjfLFSQx.js` no ar. Entraram nesta última rodada:
- anotação de período e contestação de classificação com revisão do gestor (decisão 6);
- digest duplo (gestor agregado sem ranking + pessoal do colaborador) e resumo em PDF;
- equipes ligadas a pessoa, feriados nacionais fora do denominador da capacidade e
  classificação por equipe (equipe vence organização, ausência é herança);
- prints reais do painel no site, no lugar dos mockups em CSS;
- correção do gráfico por hora: o wrapper do ECharts registrava só barra e pizza, e o
  cartão de linha era descartado em silêncio. Ficou um guard que compara séries pedidas
  com desenhadas e avisa nomeando o import que falta.

**O que sobrou, tudo fora do código de produto:** revisão jurídica da Bruna sobre a
redação do vocabulário; CI sem acesso SSH ao servidor (bloqueio antiforça-bruta ao
runner); GHCR respondendo unauthorized, o que deixa o rollback por tag inoperante; e
`Demo__Slug` não configurado no staging, o que congela os dados da demo em 04/09.

### Histórico: em voo na rodada anterior (tudo commitado)
Se os arquivos abaixo estiverem modificados e não commitados, o trabalho chegou pela metade.

| Agente | Entrega | Arquivos que possui |
|---|---|---|
| Anotação e contestação (F6, decisão 6, DIFERENCIAL) | tabela `person_notes`, `/api/v1/people/{sid}/notes` com GET/POST/PATCH (gestor revisa), bloco na página da pessoa. Contestação aceita NÃO altera agregado | migration nova, `PersonNotesController.cs`, `PersonNoteContracts.cs`, `AuditLogEntry.cs`, testes, `pages/PessoaPage.tsx`, `components/pessoa/**` |
| Digest duplo e PDF (F6, DIFERENCIAL) | evolui o `WeeklyDigestService` (índice, variação, quatro baldes, cobertura, alertas, sem ranking), cria digest PESSOAL do colaborador, e o kind `resumo_pdf` no `/exports` com QuestPDF | `Infrastructure/Digest/**`, `Worker/**`, `Email/**`, `Exports/**`, `ExportsController.cs`, testes |
| Equipes, feriados e regra por equipe (F5 + F7) | escopo de EQUIPE na regra de classificação (equipe vence organização), tabela `teams` ligada a PESSOA, feriados nacionais semeados, jornada por equipe, tela de Equipes em Configurações | migrations, `CategoriesController.cs`, `AppCatalogController.cs`, `OrganizationController.cs`, `TeamsController.cs`, `DailyAggregationService.cs`, `pages/configuracoes/**`, `AppsPage.tsx` |
| Prints reais no site (F8) | substitui os mockups em CSS por capturas do painel novo, criando e APAGANDO um usuário temporário no staging | `site/index.html`, `site/assets/css/home.css`, `site/assets/img/**` |

**Se algum não terminou:** o brief está resumido acima; o alvo visual é o mockup. Prioridade se
faltar tempo: prints do site (destrava venda), depois anotação e contestação (diferencial),
depois digest, e por último feriados e regra por equipe (refinamento).

**Cuidado com o agente do site:** ele cria um usuário temporário no staging para capturar as
telas. Se ele não terminou, CONFIRA no banco `m351_staging` se sobrou usuário de teste e apague.

### Em voo na leva anterior (já commitado, mantido como histórico)
Se os arquivos abaixo estiverem modificados e NÃO commitados, o trabalho chegou pela metade.
Verifique com `git status` e decida: terminar ou reverter o arquivo.

| Agente | Entrega | Arquivos que ele possui |
|---|---|---|
| Visão Geral | composição por dia com os 4 baldes ligada ao período global; top de apps por período; seletor de período personalizado; botões "Resumo em PDF" e "Enviar por e-mail" DESABILITADOS com title | `portal/src/pages/VisaoGeralPage.tsx`, novos em `portal/src/components/dashboard/`, `portal/src/lib/period.ts` |
| Mapa do mês | `GET /api/v1/people/daily?from&to[&tag]` com testes; mapa do mês passa de célula por SEMANA para célula por DIA | `backend/src/M351.Api/Controllers/PeopleController.cs`, `PeopleContracts.cs`, testes novos, `portal/src/components/timeline/MonthHeatmap.tsx`, `portal/src/pages/LinhaDoTempoPage.tsx` |
| Busca global | campo na barra superior com Ctrl+K / ⌘K buscando pessoas, apps, dispositivos e telas | `portal/src/components/layout/AppShell.tsx`, novos em `portal/src/components/search/` |

**Como retomar cada um:** o brief completo de cada agente está no histórico da sessão anterior.
O essencial está na tabela acima e no mockup. Nenhum deles precisa de decisão do dono.

---

## O que fazer a seguir, em ordem

1. **`git status`**. Se houver mudança não commitada, rode a verificação (seção abaixo) e
   commite o que passar; reverta o arquivo que estiver truncado.
2. **Deploy**, se houver commit novo (procedimento na seção seguinte).
3. **Pendências conhecidas** (nenhuma bloqueia uso, todas estão comentadas no código):
   - feriados não entram na capacidade utilizada das equipes (fase F7): semana com feriado
     aparece com capacidade subestimada;
   - prints reais do produto no site: o lugar está marcado em comentário no `site/index.html`
     e os mockups em CSS continuam lá até alguém tirar as capturas do painel novo;
   - a página da pessoa calcula índice e cobertura no cliente porque
     `GET /dashboard/summary` é anterior à fase e não devolve os indicadores prontos; a
     fórmula é a mesma do servidor e o comentário no código pede para remover a duplicação
     quando o endpoint evoluir;
   - `management_alerts` não aparece no dossiê de Conformidade (lista de jobs).

---

## Verificação (sempre, antes de commitar)

```
cd C:\dev\351-monitor\backend && dotnet build && dotnet test
cd C:\dev\351-monitor\portal && npx tsc --noEmit -p tsconfig.json && npm run build
```
Referência: **445 testes verdes**. PostgreSQL 16 local em `localhost:5432`, `postgres`/`postgres`.

## Deploy

O job `deploy-staging` do CI **falha** por bloqueio de rede: a proteção antiforça-bruta do
servidor barrou o endereço do runner do GitHub em 07/09. Enquanto isso, o deploy é manual e
funciona:

```bash
cd /c/dev/351-monitor
export SSH_KEY_FILE="$HOME/.ssh/ci_deploy_351monitor" STAGING_SSH_USER=deploy
bash infra/scripts/deploy-staging.sh
```
Sucesso = `[deploy] health-gate ok`, `[deploy] remote-ok` e `[deploy] concluído`.
O site institucional é publicado sozinho por FTPS a cada push em `main` que toque `site/**`.

---

## Armadilhas já pagas (não repita)

- **Migration escrita à mão não é reconhecida.** O EF exige o `.Designer.cs` com o atributo
  `[Migration]`. Gere com
  `dotnet ef migrations add <Nome> --project src/M351.Infrastructure --startup-project src/M351.Infrastructure --output-dir Data/Migrations`
  — o startup é a própria Infrastructure porque a Api não referencia `EntityFrameworkCore.Design`.
  Tabelas `daily_*`, `hourly_*`, `people` e `management_alerts` não são entidades do EF: o corpo
  da migration é DDL cru.
- **Dapper não converte** `smallint`→`int`, `numeric`→`double` nem `text[]`→`string[]` no
  construtor de record. Use `::int`, `::double precision` e `array_to_string(..., chr(31))`.
- **Teste com Admin ou Owner** precisa de `mfaEnabled: true` no `CreateUserAsync`.
- **Evento de teste com 600 s ou mais de intervalo** cai na regra de lacuna N7 e vira
  `no_data`: espace os eventos em 5 minutos.
- **Raw string interpolada do C#** com um `$` não aceita `{{` como chave literal: use
  `ARRAY[]::text[]` em vez de `'{}'::text[]`.
- **`docker compose exec` drena a entrada padrão** do chamador, mesmo com `-T`. Dentro de um
  bloco remoto de ssh isso engole o resto do script. Sempre `< /dev/null`.
- **ssh não preserva a lista de argumentos**: argumento vazio desaparece e os seguintes
  escorregam de posição. Passe valores como atribuições de ambiente.

## Aberto do lado da infraestrutura (não é código)

- CI não consegue mais chegar ao servidor por SSH (bloqueio antiforça-bruta ao runner).
  Solução: liberar os endereços do GitHub Actions ou reduzir a agressividade do fail2ban.
- `docker compose pull` responde `unauthorized` no registro GHCR a partir do servidor, então o
  rollback por tag imutável que o próprio script sugere **não está operante**.
- O usuário `deploy` foi criado em 07/09 e recebeu: chave do CI em `authorized_keys`, grupo
  `docker`, `safe.directory` do git, grupo e escrita nas pastas versionadas de
  `/opt/351monitor` (nunca em volume de container) e `/var/backups/m351`.

## Primeira coisa a olhar no painel depois de qualquer deploy

O histórico anterior mostra "sem classificação" perto de zero e o neutro inflado, porque a
coluna nasceu com valor padrão. Isso se corrige em **Configurações › Classificação ›
Recalcular histórico**, escolhendo a janela. É assíncrono, roda em ciclos de 15 min e pode
levar horas numa frota grande.

---

## Estado em 08/09/2026 — os três itens do estudo estão FEITOS

Os três itens que a sessão anterior deixou na fila foram implementados, testados e commitados
em `main`. **524 testes de integração verdes**; portal com typecheck e build limpos.

| Item do estudo | O que entrou |
|---|---|
| **1. Índice explicável** | `GET /dashboard/index-explained` decompõe a variação do índice por aplicativo, equipe e dia com uma identidade **aditiva exata** — as parcelas somam a variação total, sem fatia "outros". Painel "Por que o índice mudou" na Visão Geral, com barras divergentes e a soma no rodapé. |
| **2. Transparência (visão do colaborador)** | `GET /people/{sid}/self-view` e o bloco "O que mostramos a esta pessoa" na página da pessoa. `resumo_pdf` ganhou `params.windows_sid` e virou também a variante pessoal. **Nenhum acesso novo foi criado para o colaborador** (decisão 10, ver abaixo). |
| **3. Agregados mensais** | Tabela `monthly_summaries` + marca-d'água em `monthly_rollup_state`, job de hora em hora no worker, purga junto com a diária aos 24 meses, `?grain=month` no `/dashboard/overview` com teto de 24 meses e os presets "3 meses" e "12 meses" na Visão Geral. |

### DECISÃO 10 do dono (08/09/2026): o colaborador não é usuário do painel

Perguntado como o colaborador veria os próprios números, o dono respondeu: **"eu quero o
colaborador no painel! só não quero que crie nenhum acesso para colaborador"**. Ou seja, ele é
ASSUNTO do painel, nunca usuário — sem login, token pessoal, magic link ou rota pública com dado
dele. Está registrada na **seção 7.2 do spec** e emenda a decisão 6.

Consequência prática, para quem retomar: **não proponha credencial nem rota pública de dado
pessoal neste repo.** `PublicTransparencyController` e `/t/{token}` continuam **só com a política
de coleta**, e o comentário do controller que exclui dado pessoal está certo — o `transparency_token`
é do DISPOSITIVO, não da pessoa, e numa máquina com dois usuários do Windows não há resposta para
"os números de quem?".

### Pendências antigas que estes itens fecharam de passagem

- a página da pessoa **não recalcula mais** índice e cobertura no cliente (`personIndicators()` foi
  apagada — o `self-view` devolve os dois prontos);
- o cartão que **explicava a ausência** do uso por aplicativo por pessoa virou o uso de verdade: o
  impedimento era o `/reports/usage` só aceitar `device_ids`, e o `self-view` recorta por TITULAR;
- a tela de Exportações não tinha rótulo para `resumo_pdf` e o nome de fallback dele saía `.csv`.

---

## COMECE POR AQUI numa sessão nova (08/09/2026)

Nada do produto está em voo. O que resta, em ordem de valor:

1. **Recorte por equipe para líder** — hoje qualquer Viewer enxerga a organização inteira, e o
   dono citou cinco níveis de acesso (TI, admin, gerente, diretor, líder) onde o sistema tem três
   papéis (`UserRole`: Owner/Admin/Viewer). "Líder que vê só a própria equipe" não é exprimível.
   O `DashboardController` já registra isso como "papel Manager-por-equipe adiado para a v1.1".
   **Precisa de decisão do dono** sobre os níveis e o que cada um enxerga.
2. **Feriados na capacidade utilizada das equipes** (fase F7): semana com feriado ainda aparece
   com capacidade subestimada.
3. **`management_alerts` no dossiê de Conformidade** — a lista de jobs não menciona o motor de
   alertas.
4. **Blocos da Visão Geral no grão mensal**: atividade por hora, aplicativos do período e a
   decomposição do índice somem em janela longa, porque só existem no grão diário. Se trimestre e
   ano virarem uso frequente, vale dar a eles uma versão mensal em vez de escondê-los.

## Fora do código (segue igual)

- revisão jurídica da Bruna sobre a redação do vocabulário;
- CI sem acesso SSH ao servidor (bloqueio antiforça-bruta ao runner do GitHub) — **o deploy é
  manual**, procedimento na seção "Deploy" acima;
- GHCR respondendo `unauthorized` a partir do servidor, o que deixa o rollback por tag inoperante;
- `Demo__Slug` não configurado no staging, o que congela os dados da demo em 04/09.

## Armadilhas novas (somam às da seção acima)

- **Npgsql devolve `DateTime` para `timestamptz`**, não `DateTimeOffset`, quando se lê por
  `ExecuteScalarAsync` cru (o Dapper converte; o comando cru não). E `'-infinity'` não tem
  equivalente em `DateTime`: no `MonthlyRollupService` a marca-d'água entra por SUBCONSULTA, sem
  ida e volta pelo .NET, justamente por isso.
- **`dotnet ef migrations add` gera o arquivo VAZIO** neste repo (as tabelas de agregado não são
  entidades do EF). O comando serve para produzir o par `.cs` + `.Designer.cs` com o atributo
  `[Migration]`; o corpo é DDL cru escrito à mão DEPOIS.
- **Teste que passa sem a implementação não testa nada.** O primeiro teste do PDF pessoal passou
  antes de existir a variante, porque `windows_sid` era ignorado em silêncio — só ficou honesto
  quando passou a exigir que o `row_count` contasse os apps DAQUELA pessoa.
