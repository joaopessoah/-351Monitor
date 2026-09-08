# Produto de produtividade: redesenho do painel, modelo de métricas e plano

Data: 2026-09-07 · Status: **APROVADO pelo dono em 07/09/2026** (as 9 decisões da seção 7 foram aprovadas com as recomendações; implementação em curso na branch `feat/produtividade-f6`)
Autor: análise assistida (Claude) a pedido de João Pessoa · Revisão jurídica pendente: Bruna Rondelli

Artefatos que acompanham este spec:
- Estudo completo (diagnóstico, benchmark, plano): https://claude.ai/code/artifact/9f5d1cc3-176f-4647-9e86-33199c35af3c
- Mockup navegável das três telas propostas: https://claude.ai/code/artifact/e5a02426-7399-4f2a-a5a3-3cadfe5e8ced (#visao, #tempo, #pessoas)

---

## 1. Problema

O site vende **software de monitoramento de produtividade** com "indicadores de atividade, produtividade e ociosidade reunidos em um só painel", "análise por colaborador", "análise por equipe: padrões comparados entre áreas" e "capacidade utilizada e disponível". O painel entrega uma tela de **presença agora** (contagem de dispositivos por estado técnico), horas ativas/ociosas da semana e top 10 apps. Não existe, em lugar nenhum do portal, um indicador de produtividade, uma lista de colaboradores nem comparação entre equipes.

Os dados para entregar isso **já existem**: `daily_device_summaries` guarda, por dia × dispositivo × usuário, `seconds_on`, `seconds_active`, `seconds_idle`, `seconds_locked`, `seconds_work_related`, `seconds_neutral`, `seconds_not_work_related`. O que falta é (a) um quarto balde explícito para "sem classificação" (hoje fundido em `seconds_neutral`), (b) agregações por pessoa, equipe e período, (c) as telas.

### 1.1 Diagnóstico da Visão Geral atual (9 blocos, 3 públicos misturados)

| Bloco | Para quem serve | Veredito |
|---|---|---|
| 5 cards de presença (Ativos, Ociosos, Bloqueados/sem usuário, Desligadas, Sem comunicação) | TI (3 de 5 são estado técnico da máquina) | Vira a faixa "Agora", primeiro cabeçalho da tela |
| Faixa de saúde da frota | TI | Vai para Administração → Dispositivos; permanece só como alerta |
| Meta da semana (só se configurada) | Gestor | Único indicador de produtividade do portal, escondido por padrão. Vira a barra de meta do índice |
| Atividade fora do horário | Gestor | Mantém, vira tile com seletor de período |
| Medidor do plano | Proprietário | Vai para Cobrança |
| Tabela "Equipe agora" (hostname, usuário Windows, app em foco) | TI/Gestor | Demovida para "Agora" (Linha do Tempo → Hoje) |
| Checklist de onboarding | Admin | Vai para Configurações, aparece só até concluir |
| Horas ativas por dia (ativo/ocioso) | Gestor | Vira "Composição por dia" com os 4 baldes + ocioso |
| Top 10 apps | Gestor | Mantém, dentro do bloco Aplicativos da Visão Geral |

### 1.2 Repetições

- "Sem comunicação / precisa de atenção" aparece em 5 lugares: card, faixa de saúde, sino, chips de Dispositivos e destaque de linha.
- Categorização de apps existe em dois lugares com fluxos diferentes: `/apps` (inline, lote, sugestões do dicionário) e `Configurações → Categorias → Mapeamento de apps` (busca, lote).
- "Fora do horário" é widget da Visão Geral e aba dentro do relatório "Uso de aplicativos" (não é sobre apps).
- Ocioso tem três cores: âmbar na timeline (`#d97706`), cinza-azulado no gráfico semanal (`vizOcioso`), e no site âmbar significa "Improdutivo". Semântica cruzada.

### 1.3 O que falta (prometido no site ou pedido pelo dono)

Índice de produtividade; horas produtivas / neutras / improdutivas / sem classificação por dia, semana e mês; horas com a máquina ligada como KPI; lista de colaboradores; comparação entre equipes; alertas de gestão (hoje só alertas de frota, plano Pro, e-mail); atividade ao longo do dia (endpoint `activity-by-hour` não existe); capacidade utilizada; visão mensal (API limitada a 92 dias, sem rollups).

---

## 2. Decisões de produto propostas

### 2.1 Vocabulário (precisa da revisão jurídica)

- **Estados de máquina continuam neutros**: Ativo, Ocioso, Bloqueado, Desligada, Sem comunicação. Ocioso **nunca** é contado como improdutivo (concorrente brasileiro faz isso; reuniões e chamadas viram "improdutivo", o que é falso e juridicamente frágil).
- **Classificação de aplicativos** passa a usar os rótulos **Produtivo / Neutro / Improdutivo / Sem classificação**, sempre acompanhados do enquadramento "classificação definida pela sua empresa". O conjunto "Relacionado ao trabalho / Neutro / Não relacionado" fica disponível como opção da organização. Motivo: é o padrão da categoria (ActivTrak, DeskTime, Insightful, Time Doctor, Monitoo, fSense), é o que o site já promete e o julgamento continua sendo do cliente, sobre apps, nunca sobre pessoas. O Princípio 8 do spec mestre é atualizado nesse ponto.
- Continua **proibido**: ranking público, "leaderboard", exposição de comparação entre pessoas fora do gestor autorizado, linguagem de ponto (entrada/saída, hora extra, banco de horas).

### 2.2 Modelo de métricas (definições publicadas na interface e numa página "Como medimos")

| Métrica | Definição | Fonte hoje |
|---|---|---|
| Horas com a máquina ligada | ativo + ocioso + bloqueado | `seconds_on` |
| Horas ativas | uso de teclado/mouse | `seconds_active` |
| Produtivo / Neutro / Improdutivo | tempo ativo em apps de categorias com classificação +1 / 0 / −1 | `seconds_work_related`, `seconds_neutral` (parcial), `seconds_not_work_related` |
| Sem classificação | tempo ativo em apps sem categoria | **novo** `seconds_unclassified` (hoje dentro de `seconds_neutral`) |
| Índice de produtividade | produtivo ÷ (produtivo + neutro + improdutivo) | calculado, novo |
| Cobertura da classificação | (ativo − sem classificação) ÷ ativo | calculado, novo |
| Ociosidade | ocioso ÷ ligada | calculado |
| Aproveitamento | ativo ÷ ligada | calculado |
| Capacidade utilizada | ativo ÷ (jornada declarada × dias úteis × pessoas) | calculado; precisa de feriados e jornada por equipe |
| Atividade fora do horário | já existe | `reports/fora-do-horario` |

Decisões embutidas: o índice exclui "sem classificação" do denominador para não punir a organização por curadoria incompleta; a cobertura mostra esse buraco explicitamente e leva à fila de classificação. Cadências: dia (linha do agregado), semana e mês (soma); trimestre exige rollups mensais (fase posterior).

### 2.3 Identidades

- **Pessoa**: hoje `device_users` é (dispositivo, SID); a mesma pessoa em duas máquinas são dois registros. Criar entidade `people` por tenant, com vínculo automático por `windows_sid` e mesclagem manual pelo admin.
- **Equipe**: hoje é `devices.tags[]`. Criar entidade `teams` vinculada a pessoa (não a dispositivo), mantendo a tag como atalho de migração.
- **Mínimo de grupo**: agregados de equipe com menos de 3 pessoas não exibem médias comparativas (só totais).

### 2.4 Classificação 2.0

- Regras por equipe (o mesmo app pode ser produtivo no Marketing e improdutivo no Financeiro). Sem regra por pessoa na primeira versão.
- Cobertura como KPI + fila de classificação ordenada por impacto em horas + sugestões do dicionário BR (`apps-br.csv`, 343 apps) aplicáveis em lote com prévia.
- Reagregação retroativa sob demanda até 12 meses (hoje fixa em 30 dias), como job assíncrono.
- Curadoria num só lugar: `Configurações → Classificação` (categorias, mapeamento, dicionário). A tela `/apps` deixa de existir como menu; a análise de apps entra na Visão Geral e em `Relatórios → Aplicativos`.

---

## 3. Arquitetura de informação

```
Visão Geral        macro: índice, composição, tiles, atividade por hora, composição por dia,
                   alertas e pendências, equipes lado a lado, aplicativos, faixa "Agora"
Linha do Tempo     Mês (mapa de calor pessoa × dia) → Dia (faixas por pessoa + resumo)
                   → Pessoa/dia (canvas atual) · "Hoje" abriga a antiga tabela "Equipe agora"
Colaboradores      lista com métricas do período (ordem alfabética padrão) → Pessoa
Relatórios         Jornada · Aplicativos · Fora do horário · Exportações · Resumos enviados
Administração      Dispositivos · Configurações (Classificação, Alertas, Equipes, ...) ·
                   Transparência · Cobrança
```

Controles globais na barra superior: período (Hoje / Esta semana / Este mês / Personalizado até 92 dias), equipe (todas / uma / comparar), comparação com o período anterior de mesma duração e mesmos dias da semana.

Telas em detalhe: ver mockup navegável. Referência visual escolhida pelo dono (07/09/2026, substituindo a anterior): Dribbble 27479829 (Insoftex, "RevenuePulse AI"): painel escuro e denso, linha de seis KPIs compactos com minigráficos, donut com total ao centro, anel de nota com marca da meta, cartão de insight com brilho em gradiente, tabela com sparklines, sidebar agrupada por seções. As cores continuam as dos tokens da marca em `portal/src/lib/brandTheme.ts`; a referência entra na estrutura, densidade e componentes. A faixa "Agora" (ativos, ociosos, bloqueados, desligados, sem comunicação) é o primeiro cabeçalho da Visão Geral, por pedido do dono. Ajuste de paleta de dataviz (validado): "sem classificação" sobe para `#8593AD`; ocioso mantém `#3A455C` com hachura a 45° para se distinguir; ocioso deixa de ser âmbar na timeline.

---

## 4. Alertas e pendências

Central única ("Alertas e pendências") na Visão Geral e no sino, com duas abas: **gestão** (novo) e **administração** (as pendências atuais: apps sem categoria, dispositivos com alerta, exportações, convites). Regras avaliadas no worker sobre os agregados diários, com estado e cooldown (reuso de `device_alert_state` com novos `kind`), entrega no painel e por e-mail (digest diário/semanal), plano Pro.

| # | Regra | Dado |
|---|---|---|
| 1 | Dispositivo sem comunicação em horário de trabalho (existe) | `device_current_state` |
| 2 | Cobertura da classificação abaixo de 85% | novo balde |
| 3 | Equipe com tempo ativo 15% abaixo da média das 4 semanas anteriores | `daily_device_summaries` |
| 4 | Pessoa com mais de X h de atividade fora do horário na semana (equilíbrio) | `fora-do-horario` |
| 5 | Dias longos: ligada acima de 10 h em 3 ou mais dias na semana | `seconds_on` |
| 6 | Meta da semana em risco (projeção pelo ritmo dos dias registrados) | metas + agregados |
| 7 | Improdutivo acima de Y% do tempo ativo na equipe | novo balde |
| 8 | Dia útil sem dados em dispositivo ativo | `jornada` note `sem_dados` |

Vocabulário dos alertas: sempre variação e contexto, nunca julgamento ("Equipe Comercial com 18% menos tempo ativo que a média das 4 semanas anteriores"). Alertas por pessoa são opt-in da organização; por equipe são o padrão.

---

## 5. Diferenciais a construir (ordem de valor ÷ esforço)

1. **Índice explicável**: "por que mudou" decomposto por app, equipe e dia. Nenhum concorrente pesquisado faz.
2. **Cobertura da classificação como KPI** com fila por impacto e dicionário brasileiro. Concorrentes exibem "não categorizado" mas não guiam a curadoria.
3. **Transparência como produto**: página do colaborador (link tokenizado já existe) com os próprios dados, anotação de período ("reunião presencial") e contestação de classificação com revisão do gestor. Ninguém do conjunto oferece anotação/contestação.
4. **Digest duplo**: gestor (agregado, sem ranking) e colaborador (pessoal). Ninguém combina os dois.
5. **Definições publicadas** ("Como medimos") e mínimo de grupo. Só RescueTime/Controlio publicam fórmula; só RescueTime Teams/Viva agregam com mínimo.
6. **Capacidade utilizada vs contratada** para responder "preciso contratar?" (promessa do site).
7. **Resumo semanal em português** gerado a partir dos agregados (heurístico primeiro, LLM depois, sem dado pessoal).

---

## 6. Plano por fases (1 dev; com 2 devs, ~60% do prazo)

| Fase | Entrega | Backend | Portal | Prazo |
|---|---|---|---|---|
| F0 | Decisões (vocabulário, fórmulas, lista de pessoas, comparação de equipes, mínimo de grupo) + revisão jurídica | – | – | 1 sem |
| F1 | Fundamentos de dados | `seconds_unclassified` + reagregação; `hourly_activity` (job) e `GET /dashboard/activity-by-hour`; `GET /dashboard/overview` (KPIs, composição, deltas); entidade `people` com vínculo por SID | – | 2–3 sem |
| F2 | Visão Geral nova | ajustes de contrato | hero, tiles, gráficos, alertas (v1 client-side), equipes lado a lado, apps, faixa Agora; período e comparação globais; blocos de admin movidos | 2–3 sem |
| F3 | Colaboradores + Pessoa | usage por pessoa × dia; top apps por pessoa | lista, flags, página da pessoa reformada | 2 sem |
| F4 | Linha do Tempo dia a dia | daily por pessoa para o mês; team timeline agrupada por pessoa com resumo | mapa de calor, faixas por pessoa, canvas mantido | 2–3 sem |
| F5 | Classificação 2.0 | regras por equipe; reagregação até 12 meses; rótulos por org | cobertura, fila por impacto, curadoria num só lugar | 2 sem |
| F6 | Alertas e resumos | motor de regras no worker; digest gestor + colaborador; PDF do resumo | central de alertas, configurações | 3 sem |
| F7 | Capacidade e equipes | entidade `teams`, feriados, jornada por equipe | KPIs de capacidade, comparação com mínimo de grupo | 2 sem |
| F8 | Site | – | prints reais, copy alinhada às métricas reais, página "Como medimos", ajustes de layout pela referência | 1–2 sem |

Total: 17–21 semanas com 1 dev; 10–12 com 2. A ordem F1→F2 entrega a Visão Geral macro em 4–6 semanas.

---

## 7. Decisões tomadas (F0 concluída em 07/09/2026)

O dono aprovou o plano inteiro e as nove decisões abaixo **com as recomendações**. Ficam registradas
como decisão de produto; qualquer mudança futura exige nova decisão dele.

| # | Pergunta | Decisão |
|---|---|---|
| 1 | Rótulos de classificação | **Produtivo / Neutro / Improdutivo / Sem classificação** como padrão, sempre com o enquadramento "classificação definida pela sua empresa". O conjunto "Relacionado ao trabalho / Neutro / Não relacionado" fica como opção da organização (`organizations.classification_vocabulary`). Estados de máquina seguem neutros. **Atualiza o Princípio 8 e a Seção 8.7 do spec mestre.** Revisão jurídica da redação: pendente com Bruna, não bloqueia o código. |
| 2 | Lista de colaboradores | **Existe**, com métricas do período e ordenação por coluna, restrita a Gestor/Admin/Owner e **auditada** (`view_report` por pessoa). Ordem alfabética por padrão; sem a palavra ranking, sem gamificação, sem publicação. |
| 3 | Comparação de equipes | **Permitida lado a lado**, com **mínimo de 3 pessoas** por equipe para exibir médias comparativas (abaixo disso, só totais). Substitui a regra anterior de "uma equipe por vez". |
| 4 | Fórmula do índice | **produtivo ÷ (produtivo + neutro + improdutivo)**, com a **cobertura da classificação** sempre exibida ao lado. Nunca um sem o outro. |
| 5 | Alertas por pessoa | **Opt-in da organização**, registrado em auditoria. Padrão é equipe. |
| 6 | Página do colaborador | **Entra**, com os próprios dados, anotação de período e contestação de classificação com revisão do gestor (fase F6). ⚠️ **Emendada pela decisão 10** (seção 7.2): "os próprios dados" são exibidos DENTRO do painel, para quem já tem acesso — o colaborador não recebe acesso nenhum. |
| 7 | Reagregação retroativa | **Sob demanda até 12 meses**, como job assíncrono, com aviso de custo. Default automático segue 30 dias. |
| 8 | Máquina ligada sem usuário | **Fora** das horas por pessoa; aparece só como estado do dispositivo em Administração. Mantém a derrogação já documentada no `DailyAggregationService`. |
| 9 | Site | **Evoluir** a home atual com prints reais e a página "Como medimos"; sem redesenho. |

### 7.2 Decisão 10 — o colaborador não é usuário do painel (07/09/2026, fim do dia)

Ao retomar o item "transparência como produto", a implementação esbarrou numa ambiguidade da
decisão 6: *onde* o colaborador veria os próprios dados. O caminho descrito na seção 5 era o link
tokenizado `/t/{token}`, mas esse token é do **dispositivo**, não da pessoa — numa máquina com dois
usuários do Windows não há resposta para "os números de quem?" — e o `PublicTransparencyController`
exclui dado pessoal por decisão documentada, porque a URL é uma capability sem autenticação.

**Decisão do dono:** o colaborador **aparece no painel como assunto, nunca como usuário**. Não se
cria login, token pessoal, magic link nem rota pública com os números dele. Quem entra no painel é
TI, admin, gerente, diretor e líder.

Consequências, todas dentro do que já existe:

| Onde | O que muda |
|---|---|
| `PublicTransparencyController` e `/t/{token}` | **Nada.** Continuam só com a política de coleta. O comentário que exclui dado pessoal está certo e permanece. |
| Página da pessoa | Ganha a **visão do colaborador**: os mesmos números que a pessoa veria sobre si, no enquadramento "isto é o que mostramos a você", aberta por quem já tem acesso. |
| `/exports` | O `resumo_pdf` ganha a variante **pessoal**, para o número chegar à mão da pessoa por papel, no 1:1. |
| Digest pessoal por e-mail | **Permanece como está.** É o canal automático que sustenta a obrigação de transparência sem dar acesso ao sistema. |
| Anotação e contestação (`person_notes`) | **Permanecem como estão**, escritas por quem tem acesso (Viewer+), revisadas por Admin+. |

Fica aberto, sem decisão e fora desta rodada: hoje um Viewer enxerga a organização inteira, então
"líder que vê só a própria equipe" não é exprimível nos três papéis atuais (Owner/Admin/Viewer).

### 7.1 Perguntas originais (histórico)


1. Rótulos Produtivo / Neutro / Improdutivo como padrão (recomendado), com o conjunto neutro como opção?
2. Lista de colaboradores com métricas e ordenação por coluna, restrita ao gestor e auditada (recomendado), ou só agregados?
3. Comparação de equipes lado a lado com mínimo de 3 pessoas (recomendado)?
4. Fórmula do índice: produtivo ÷ classificado, com cobertura ao lado (recomendado), ou produtivo ÷ ativo?
5. Alertas por pessoa como opt-in da organização (recomendado) ou nunca?
6. Página do colaborador com os próprios dados entra na F6 (recomendado)?
7. Reagregação retroativa sob demanda até 12 meses (recomendado)?
8. "Ligada sem usuário logado" fica fora das horas por pessoa e aparece só como estado do dispositivo (recomendado)?
9. Site: evoluir a home atual com prints reais (recomendado) ou refazer?

---

## 8. Linhas que não cruzamos (do benchmark jurídico)

Não copiar dos concorrentes: "conformidade LGPD garantida/100%", ranking como feature de destaque, "ranking + custo em R$ por colaborador", "recomendado (não existe lei) avisar os colaboradores", gravação 24x7, modo oculto, teclas, webcam, "relatório para TRT com hash", "detecção de duplo CLT", "+42% de produtividade em 90 dias", "menos horas extras". Base: TST Ag-ED-RR-871-71.2013.5.03.0129 (ranking na intranet, R$ 50 mil) e RR-1001166-22.2016.5.02.003 (ranking por e-mail, dano moral presumido); LGPD arts. 6, 9 e 18; CLT art. 6º § único; Lei 14.442/2022.
