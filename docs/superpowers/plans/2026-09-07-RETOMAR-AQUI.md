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

### Em voo quando o contexto acabou (três agentes)
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
