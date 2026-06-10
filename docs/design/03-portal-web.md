# Especificação do Portal Web do Cliente — SaaS de Monitoramento de Estações Windows

## 1. Personas e Jobs-to-be-Done

O portal serve três personas sem treinamento. O princípio de design: **a primeira tela responde a pergunta principal de cada persona em menos de 10 segundos**, e nenhuma tarefa frequente exige mais de 2 cliques a partir do dashboard.

| Persona | Pergunta central (JTBD) | Telas que vive | Frequência de uso | Dispositivo típico |
|---|---|---|---|---|
| **Gestor / dono de PME** | "Minha equipe está trabalhando agora? E essa semana?" | Visão Geral, Linha do Tempo, Apps | Diária, várias vezes ao dia | Desktop + celular (leitura) |
| **RH / Departamento Pessoal** | "Preciso do relatório de jornada do João de maio" / "Quem está fazendo hora além do expediente?" | Relatórios, Linha do Tempo | Semanal/mensal (fechamento) | Desktop |
| **TI / Admin** | "Instalar agentes, ver quais máquinas pararam de reportar, gerenciar acessos" | Onboarding, Configurações → Dispositivos/Chaves, alertas de agente parado | Intensa na implantação, depois pontual | Desktop |

Jobs secundários que o portal deve suportar sem fricção:
- Gestor: "Mostrar para o sócio um PDF do uso da semana" → export em 1 clique.
- RH: "Comprovar para o funcionário o que é e o que NÃO é coletado" → Página de Transparência imprimível.
- TI: "Trocar a chave de enrollment que vazou" → revogar/gerar chave em Configurações.

### Papéis de acesso (RBAC do MVP)

| Papel | Permissões |
|---|---|
| **Proprietário** | Tudo, inclusive billing e exclusão da organização. 1+ por tenant. |
| **Administrador** | Tudo exceto billing/exclusão da org. Gerencia usuários, chaves, privacidade. |
| **Gestor** | Leitura de dashboards, timeline, apps, relatórios e exports. Sem acesso a Configurações (exceto renomear devices — não). |
| **Visualizador** | Somente leitura de dashboards e relatórios, sem export. (Opcional no MVP; se cortar, manter os 3 primeiros.) |

Escopo por equipe/grupo (Gestor vê só sua equipe) fica **pós-MVP** — no MVP todo papel enxerga o tenant inteiro.

---

## 2. Mapa de Telas do MVP

### 2.0 Mapa de rotas

```
/login                          /convite/:token            /recuperar-senha
/redefinir-senha/:token
/onboarding                     (wizard, só org sem devices)
/                               Visão Geral (dashboard)
/linha-do-tempo                 Timeline (modo equipe e modo device)
/apps                           Detalhe de aplicativos
/relatorios                     Hub de relatórios
/relatorios/jornada             Relatório de jornada
/relatorios/exportacoes         Histórico de exports (jobs assíncronos)
/configuracoes/organizacao      Nome, fuso, semana de trabalho, feriados
/configuracoes/dispositivos     Renomear, agrupar, arquivar
/configuracoes/categorias       Categorias de apps e classificação
/configuracoes/usuarios         Usuários do portal e papéis
/configuracoes/chaves           Chaves de enrollment
/configuracoes/privacidade      Mascaramento, apps ignorados, limiar de ociosidade
/transparencia                  Página de transparência (+ versão pública /t/:tokenPublico)
```

Layout persistente: sidebar esquerda colapsável (Visão Geral, Linha do Tempo, Aplicativos, Relatórios, Configurações, Transparência), topbar com seletor de período global onde aplicável, nome do tenant, badge de fuso (ex.: "Horários em GMT-3 · São Paulo"), menu do usuário. Banner global vermelho quando ≥1 agente está "Sem comunicação" há >30 min em horário de trabalho.

---

### 2.1 Autenticação

**Login (`/login`)**
- Objetivo: entrar em <5s. E-mail + senha, "lembrar-me" (refresh token 30 dias), link "esqueci minha senha".
- Erros genéricos ("e-mail ou senha inválidos") — nunca revelar se o e-mail existe. Rate limit visível após 5 tentativas (cooldown com contador).
- CTA secundário "Criar conta da empresa" → fluxo de signup/onboarding.

**Convite (`/convite/:token`)**
- Token de 7 dias, single-use. Tela mostra: "Você foi convidado(a) para a organização **{nome}** como **{papel}**". Campos: nome completo, senha (medidor de força, mínimo 10 chars). Token expirado → tela com botão "Pedir novo convite" (notifica o admin que convidou).

**Reset de senha (`/recuperar-senha` → e-mail → `/redefinir-senha/:token`)**
- Resposta sempre "se este e-mail existir, enviamos instruções". Token 60 min. Após redefinir, invalida todas as sessões.

---

### 2.2 Onboarding (`/onboarding`)

- **Objetivo:** do signup à **primeira máquina reportando em <15 minutos**. Esta tela define a conversão do trial; é a segunda tela mais importante do produto.
- Wizard de 4 passos com progresso visível:

| Passo | Conteúdo | Dados/Ações |
|---|---|---|
| 1. Sua organização | Nome da empresa, CNPJ (opcional), fuso padrão (default `America/Sao_Paulo`), horário de trabalho default (seg–sex 08:00–18:00, editável) | `POST /api/v1/orgs` |
| 2. Chave de instalação | Chave de enrollment gerada automaticamente (`ENRL-XXXX-XXXX-XXXX`), botão copiar, botão "Baixar instalador (.msi)" | Bloco de código pronto para colar: `msiexec /i AgenteMonitor.msi /qn ENROLLMENT_KEY=ENRL-XXXX-XXXX-XXXX` + variante GPO/Intune (link doc) |
| 3. Aguardando a primeira máquina | Spinner + texto "Instale o agente em uma máquina. Ela aparecerá aqui em até 2 minutos." Polling de 10s em `GET /api/v1/devices?status=any`. Quando o primeiro device chega: card verde com hostname, usuário Windows, status, animação de sucesso, botão "Ver no dashboard". | Link "Está demorando? Checklist de firewall/proxy" (porta 443 de saída, domínio da API) |
| 4. Próximos passos | Checklist: convidar colegas do portal, revisar categorias de apps, revisar configurações de privacidade, publicar a Página de Transparência para os funcionários | Pode pular tudo |

- O checklist do passo 4 persiste como card dispensável no topo da Visão Geral até ser completado ou dispensado.
- Org sem nenhum device **sempre** redireciona `/` → `/onboarding` (retomável no passo onde parou).

---

### 2.3 Visão Geral (`/`) — Dashboard

- **Objetivo:** responder "quem está fazendo o quê agora" e "como foi a semana" sem nenhum clique.
- **Atualização:** polling de 45s (badge discreto "Atualizado há 12s"; pausa quando a aba perde foco).

**Linha 1 — cards de presença agora (dados de `GET /api/v1/dashboard/presence`):**
| Card | Definição |
|---|---|
| 🟢 Ativos | Input de teclado/mouse nos últimos N min (N = limiar de ociosidade do tenant, default 5) |
| 🟡 Ociosos | Máquina ligada e desbloqueada, sem input ≥ N min |
| 🔵 Bloqueados | Sessão bloqueada / sem usuário logado |
| ⚪ Desligadas | Agente reportou shutdown/suspensão (estado esperado) |
| 🔴 Sem comunicação | Sem heartbeat >10 min **sem** evento de desligamento — possível agente parado. Card com tom de alerta, clicável → lista filtrada |

**Linha 2 — "Equipe agora":** tabela ao vivo. Colunas: status (dot colorido + label), Dispositivo/Usuário, Aplicativo em foco (respeitando mascaramento; mostra só o nome do app se títulos mascarados), "neste app há Xmin", último evento. Ordenação default: Sem comunicação primeiro, depois Ativos. Clique na linha → Linha do Tempo daquele device hoje.

**Linha 3 — dois gráficos lado a lado (período: semana atual, seletor semana anterior):**
- **Horas ativas por dia** (barras empilhadas ativo/ocioso por dia útil; linha pontilhada de referência = jornada esperada do tenant, ex. 8h; sábado/domingo/feriado com fundo levemente hachurado).
- **Top 10 aplicativos da semana** (barras horizontais, tempo ativo; cor pela classificação da categoria — ver §3.3; clique → `/apps` filtrado).

**Filtros:** sem filtros complexos no MVP — apenas seletor de semana nos gráficos. Densidade > flexibilidade aqui.

---

### 2.4 Linha do Tempo (`/linha-do-tempo`) — **A tela do produto**

- **Objetivo:** reconstruir visualmente o dia de uma pessoa/máquina em 5 segundos de olhar. É a tela de maior valor percebido e a que fecha vendas em demo.

**Modos:**
1. **Modo equipe (default):** um dia, uma linha (swimlane) de 28px por dispositivo, eixo X = horas do dia. Permite varrer a equipe inteira. Virtualização vertical para >30 devices.
2. **Modo dispositivo:** um device, um dia, faixa principal de estados (48px) + sub-faixa de apps (32px, blocos coloridos por categoria do app em foco). Acesso por clique na lane do modo equipe ou seletor.

**Componentes:**
- Cabeçalho: date picker (default hoje; atalhos "Hoje / Ontem / ◀ ▶"; teclas ← → mudam o dia), seletor de device (modo device), toggle "Horário de trabalho / 24h" (default: janela de trabalho do tenant com folga de 1h para cada lado; eventos fora da janela são o motivo do toggle existir).
- Eixo de tempo com marcações de hora; linha vertical "agora" quando o dia é hoje.
- **Estados e cores (com textura, não só cor — daltonismo):**

| Estado | Cor | Textura/borda |
|---|---|---|
| Ativo | Verde 600 | sólido |
| Ocioso | Âmbar 400 | sólido |
| Bloqueado/sem sessão | Azul-acinzentado 300 | sólido |
| Desligada/suspensa | Cinza 100 | sem preenchimento, contorno |
| Sem dados (antes da instalação do agente / dia futuro) | Cinza 50 | hachura diagonal |
| Sem comunicação (agente parado) | Vermelho 300 | hachura diagonal vermelha + ícone ⚠ no início do intervalo |

- **Hover:** tooltip com intervalo (`09:14 – 09:41 · 27min`), estado, app em foco + título de janela (se não mascarado), categoria. Hit-testing por busca binária no array de intervalos (ver §5.1).
- **Clique/drag:** clique em bloco abre painel lateral direito com o detalhamento do intervalo (apps usados e duração); drag horizontal seleciona faixa e o painel agrega a seleção. Scroll/pinch = zoom (dia → hora → minuto), com re-fetch de resolução maior.
- **Rodapé do modo device (resumo do dia):** Primeiro evento `08:02` · Último evento `17:48` · Ligada `9h 12min` · Ativa `6h 40min` · Ociosa `1h 05min` · Bloqueada `1h 27min`. (Mesmos números do relatório de jornada — consistência absoluta entre telas.)
- **Indicador de fuso do device:** se o fuso reportado pelo device divergir do tenant, badge "Máquina em GMT-4" ao lado do nome (horários sempre exibidos no fuso do tenant — ver §3.1).
- **Acessibilidade:** alternativa tabular ("Ver como tabela") com os mesmos intervalos — também é o fallback mobile e de screen reader.

**API:** `GET /api/v1/timeline?deviceId=&date=2026-06-09&resolutionSec=60` e `GET /api/v1/timeline/team?date=&resolutionSec=90`. Servidor devolve intervalos já mesclados na resolução pedida, máx. ~3.000 intervalos por resposta (ver §5.1).

---

### 2.5 Detalhe de Aplicativos (`/apps`)

- **Objetivo:** "onde o tempo foi gasto", por app e categoria, com drill-down.
- **Filtros (barra superior):** período (presets Hoje/Ontem/Esta semana/Semana passada/Este mês + range custom até 92 dias), dispositivo/usuário (multi-select), categoria, classificação.
- **Componentes:**
  - Donut "tempo por classificação" (Relacionado ao trabalho / Neutro / Não relacionado / Não categorizado) + barras "tempo por categoria".
  - Tabela principal: App (ícone + nome de exibição), Categoria (editável inline — dropdown; a edição vale para o tenant inteiro e reprocessa relatórios futuros, não retroage agregados históricos no MVP), Tempo ativo, % do tempo ativo total, Nº de dispositivos, sparkline 14 dias. Ordenável; paginação 50.
  - **Drill-down:** expandir linha do app → top títulos de janela com tempo (ex.: Chrome → "Jira — PROJ-123", "YouTube — …"). Se mascaramento de títulos ativo: linha única "Títulos mascarados pela política de privacidade da organização" com link para `/configuracoes/privacidade` (visível só para admin).
  - Ações por app: Recategorizar · Ignorar app (vai para a lista de ignorados — deixa de aparecer em relatórios; coleta futura descartada).
- Apps "Não categorizados" com badge contador no topo ("12 apps sem categoria — revisar") para puxar o admin à curadoria.

---

### 2.6 Relatórios (`/relatorios`)

- **Objetivo:** RH/gestor gera documento confiável por período, por pessoa ou equipe, em CSV/PDF.
- **Hub:** dois relatórios no MVP — **Jornada** e **Uso de aplicativos** (o segundo é a tela `/apps` com export). Card de cada um + atalho para `/relatorios/exportacoes`.

**Relatório de Jornada (`/relatorios/jornada`)**
- Filtros: período custom (máx. 92 dias por export), dispositivos/usuários (multi), incluir fins de semana/feriados (toggle, default incluir com marcação).
- Tabela: uma linha por device × dia:

| Data | Dia | Dispositivo/Usuário | Primeiro evento | Último evento | Tempo ligada | Tempo ativo | Tempo ocioso | Tempo bloqueado | Observação |
|---|---|---|---|---|---|---|---|---|---|
| 08/06/2026 | seg | NB-JOAO / joao.silva | 08:02 | 17:48 | 9h 12min | 6h 40min | 1h 05min | 1h 27min | — |
| 12/06/2026 | sex | NB-JOAO / joao.silva | — | — | — | — | — | — | Feriado (Corpus Christi)* |

- Linha de totais por device no rodapé do grupo; dias sem dados em dia útil destacados ("Sem dados — máquina desligada o dia todo" vs "⚠ Agente sem comunicação").
- **Banner fixo não-dispensável no topo da tela e rodapé de todo PDF/CSV:** "Relatório gerencial de uso de estação de trabalho. **Não constitui registro de ponto** nos termos da Portaria MTE 671/2021 e não substitui o controle de jornada exigido pelo art. 74 da CLT." (ver §7).
- **Export:** botão "Exportar" → CSV (UTF-8 **com BOM**, separador `;` — Excel pt-BR) ou PDF (gerado no servidor). Exports são jobs assíncronos: `POST /api/v1/reports/jornada/export` → toast "Gerando…" → notificação + download em `/relatorios/exportacoes` (histórico de 30 dias, com quem gerou e com quais filtros — trilha de auditoria LGPD).

---

### 2.7 Configurações

**Organização (`/configuracoes/organizacao`)** — nome, fuso padrão (IANA, default `America/Sao_Paulo`), semana de trabalho (dias + horário, ex. seg–sex 08:00–18:00; usado como referência visual nos gráficos e na janela default da timeline), feriados: tabela de feriados nacionais BR pré-carregada por ano + CRUD de feriados do tenant (estaduais/municipais/pontes).

**Dispositivos (`/configuracoes/dispositivos`)** — tabela: nome de exibição (editável; default = hostname), hostname, usuário Windows mais frequente, grupo (tag livre no MVP — ex. "Comercial", "Dev"; vira entidade Equipe pós-MVP), versão do agente, fuso do device, último heartbeat, status. Ações: Renomear · Atribuir grupo · **Arquivar** (para de contar no limite do plano e some dos dashboards; histórico preservado e acessível em relatórios com toggle "incluir arquivados") · Reexibir. Filtros: status, grupo, versão do agente. Linha vermelha para "Sem comunicação".

**Categorias de apps (`/configuracoes/categorias`)** — duas abas:
- *Categorias:* lista com classificação por tenant. Defaults de fábrica (editáveis): Desenvolvimento, Escritório/Documentos, Comunicação, Reuniões, Navegação, Design, ERP/Sistemas internos, Sistema/Utilitários → **Relacionado ao trabalho**; Música/Streaming de áudio → **Neutro**; Jogos, Redes sociais, Vídeo/Streaming → **Não relacionado**; Não categorizado → **Neutro**. Classificações possíveis: `Relacionado ao trabalho` / `Neutro` / `Não relacionado ao trabalho` (vocabulário deliberado — ver §3.3).
- *Mapeamento de apps:* app (por nome de executável + nome amigável vindo de catálogo global mantido por nós) → categoria, com override por tenant. Busca, contagem de "não categorizados", recategorização em lote.

**Usuários do portal (`/configuracoes/usuarios`)** — lista (nome, e-mail, papel, último acesso, status convite pendente/ativo), Convidar (e-mail + papel), Reenviar convite, Alterar papel, Desativar. Regra: sempre ≥1 Proprietário ativo.

**Chaves de enrollment (`/configuracoes/chaves`)** — lista: chave (mascarada `ENRL-XXXX-····-····` + copiar), apelido ("Matriz", "Filial SP"), criada em, devices registrados por ela, status. Ações: Gerar nova · **Revogar** (modal: "Devices já registrados continuam funcionando; novas instalações com esta chave serão recusadas"). Bloco com o comando `msiexec` pronto por chave.

**Privacidade (`/configuracoes/privacidade`)** — a tela que materializa o posicionamento "transparente, não spyware":
- **Mascarar títulos de janela** (on/off, tenant-wide): quando on, o agente envia apenas o nome do app; títulos nunca chegam ao servidor (enforcement no agente via config remota, não só filtro de exibição — deixar isso escrito na própria UI).
- **Apps ignorados:** lista de executáveis cuja atividade é descartada (defaults sugeridos: gerenciadores de senha, apps de saúde, bancos — lista editável).
- **Limiar de ociosidade:** 3–15 min, default 5, com explicação do efeito.
- **Coleta fora do horário de trabalho:** Coletar sempre (default) / Coletar apenas na janela de trabalho ± 2h. (decisão sensível — ver Perguntas Abertas).
- Toda alteração aqui é registrada em log de auditoria (quem, quando, de→para) exibido no rodapé da tela.

---

### 2.8 Página de Transparência (`/transparencia`)

- **Objetivo:** dar ao cliente um artefato pronto para cumprir o dever de informação da LGPD perante os funcionários — diferencial comercial explícito.
- Documento gerado a partir de template + **estado real das configurações** do tenant: "O que é coletado" (apps abertos, app/janela em foco com título *[ou: sem título — mascarado]*, horários de login/logoff/bloqueio, tempo de ociosidade) e "O que NUNCA é coletado" (conteúdo digitado, capturas de tela, conteúdo de arquivos/e-mails/mensagens, webcam/microfone, navegação em modo detalhado de URL). Campos editáveis do tenant: finalidade declarada, contato do encarregado/DPO, data de vigência.
- Ações: Visualizar como funcionário · Baixar PDF · Imprimir · **Gerar link público** (`/t/:tokenPublico`, sem login, para colar no comunicado interno/intranet).
- O agente instalado exibe ícone na bandeja do Windows cujo "Sobre o monitoramento" abre exatamente este link público do tenant — o portal mostra esse vínculo ("Este texto é o que seus funcionários veem ao clicar no ícone do agente").

---

## 3. Decisões de UX específicas do domínio

### 3.1 Fusos horários
- **Armazenar tudo em UTC.** Exibir **sempre no fuso padrão do tenant** (IANA), nunca no fuso do navegador — RH em SP olhando máquina em Manaus precisa de números consistentes entre si e com os relatórios.
- Topbar mostra permanentemente "Horários em GMT-3 (São Paulo)". Device com fuso divergente ganha badge "Máquina em GMT-4" na timeline e na lista de devices; tooltip explica "Os horários exibidos foram convertidos para o fuso da organização".
- Agente reporta timestamps em UTC + offset/IANA local; servidor corrige clock skew (diferença entre hora do device e hora de recepção > 2 min → ajustar e sinalizar device com "relógio dessincronizado" na tela de Dispositivos).
- Relatório de jornada: o "dia" é cortado à meia-noite **do fuso do tenant**.

### 3.2 Sem dados ≠ desligada ≠ agente parado
Três situações que viram a mesma coisa em produtos mal feitos e geram chamados de suporte:
| Situação | Como o sistema sabe | Tratamento visual |
|---|---|---|
| **Desligada/suspensa** | Agente enviou evento de shutdown/suspend antes de sumir (flush no `SessionEnding`/`PowerModeChanged`) | Estado neutro, cinza contornado. Normal. |
| **Sem comunicação (agente parado/bloqueado por rede)** | Sumiu sem evento de desligamento; sem heartbeat >10 min | Vermelho hachurado na timeline; card de alerta no dashboard; banner global se >30 min em horário de trabalho. É problema de TI, não de pessoa. |
| **Sem dados** (antes da instalação, device arquivado, dia futuro) | Não existem eventos no range | Hachura cinza-claríssima + label "Sem dados". Nunca pintar como ocioso. |
- Heartbeat do agente: 60s. Pipeline de ingestão tolera atraso (buffer offline do agente até 24h) — a UI marca trechos "recebidos com atraso" com sutileza apenas no tooltip, e o dashboard "agora" considera dado fresco apenas com heartbeat <3 min.

### 3.3 "Ativo" ≠ "produtivo" — vocabulário
- Os **estados** da timeline/dashboard são fisiológicos e neutros: Ativo, Ocioso, Bloqueado, Desligada. Nunca "trabalhando", "parado", "improdutivo" em estado de máquina.
- Julgamento de valor existe **apenas** na camada de categorias, configurada pelo próprio cliente, com rótulos `Relacionado ao trabalho / Neutro / Não relacionado ao trabalho` (não "produtivo/improdutivo" — evita vender julgamento moral e reduz atrito jurídico/sindical; o cliente que quiser dureza configura as categorias dele).
- Tooltip pedagógico fixo no card "Ociosos": "Ocioso significa sem uso de teclado/mouse. Reuniões presenciais, chamadas e leitura podem aparecer como ociosidade." — essa frase evita a interpretação errada nº 1 do domínio e deve aparecer também no PDF de jornada.
- Nunca exibir ranking de pessoas "menos ativas" no MVP. Ordenações existem, "leaderboard de vergonha" não.

### 3.4 Semana de trabalho e feriados
- Semana de trabalho do tenant (dias + horário) alimenta: linha de referência nos gráficos de horas, janela default da timeline, definição de "horário de trabalho" para alertas de agente parado, e marcação de dias no relatório de jornada.
- Feriados nacionais pré-carregados (tabela mantida pelo produto, anos correntes +1); tenant adiciona os locais. Dia de feriado: fundo diferenciado nos gráficos, linha do relatório marcada "Feriado (nome)", excluído do denominador de "média de horas por dia útil".
- Pós-MVP: jornada esperada por pessoa/grupo (escalas, meio período).

---

## 4. Stack front-end

### Recomendação: **Opção A — React + TypeScript + Vite** (SPA servida pelo ASP.NET Core), com Tailwind CSS + shadcn/ui + ECharts + TanStack Query + TanStack Table + React Router.

| Critério | React+TS (A) | Blazor Server/WASM (B) |
|---|---|---|
| Componente de timeline custom (canvas, zoom, hit-test) | Ecossistema maduro; controle total do canvas via hooks; exemplos abundantes | Possível, mas via JS interop de qualquer jeito — você escreve o JS/TS que tentou evitar |
| Gráficos | ECharts/Recharts gratuitos e excelentes | Opções fortes são pagas (Syncfusion/Telerik/DevExpress ~US$ 900–1.500/dev/ano) ou wrappers de libs JS |
| Contratação no Brasil | Mercado enorme de devs React | Raríssimo achar dev Blazor; o time .NET atual aprende React em semanas, o inverso (contratar Blazor) não escala |
| Latência/infra | SPA + REST stateless, CDN-friendly | Blazor Server: estado por circuito SignalR no servidor (memória + sticky session — ruim para SaaS multi-tenant); WASM: payload inicial 2–6 MB e interop para tudo que é gráfico |
| Dashboards "vivos" (polling, cache, dedupe) | TanStack Query resolve de graça (refetchInterval, cache, retry, pausa em aba oculta) | Implementação manual com timers/`PersistentComponentState` |
| Língua única com o back | Não (TS no front) | Sim — o real benefício do Blazor |
| Compartilhar contratos/DTOs | Mitigável: gerar tipos TS do OpenAPI do ASP.NET (NSwag/openapi-typescript) no build — elimina 80% do benefício do Blazor | Nativo |

**Custo honesto de não escolher Blazor:** o time aprende uma segunda língua e mantém dois mundos de tooling; primeiras 3–4 semanas de produtividade reduzida no front. Mitigação: shadcn/ui + um template de admin dashboard encurtam o caminho; geração automática de client TS a partir do OpenAPI mantém o contrato com o back em C#.

**Custo de escolher Blazor (por que não):** este produto **é** um front de visualização de dados — timeline canvas, gráficos, tabelas densas, atualização periódica. É exatamente o pior caso do Blazor (interop pesado, charts pagos, contratação difícil) e o melhor caso do React. Blazor seria defensável se o produto fosse CRUD interno; não é.

Complementos: Vitest + Testing Library; Playwright para smoke E2E dos 3 fluxos críticos (login→dashboard, onboarding, export); date-fns + `@date-fns/tz`; ESLint/Prettier; build do Vite publicado como assets estáticos do ASP.NET Core (deploy único no MVP).

---

## 5. Componentes críticos

### 5.1 Timeline do dia (componente `DayTimeline`)
- **Renderização: Canvas 2D** (uma `<canvas>` por viewport, não por lane).
  - Por quê não SVG: dia detalhado de um device = centenas a milhares de intervalos; modo equipe = 30 lanes × centenas = dezenas de milhares de nós SVG → DOM degrada em zoom/pan e em re-render de polling. SVG fica aceitável até ~2–3 mil nós; estouramos isso no modo equipe.
  - Por quê não divs virtualizadas: virtualização resolve eixo Y (lanes), não a densidade horizontal de blocos de 1px; e tooltips/hover em milhares de divs custam caro.
  - Canvas: um único elemento, redraw <16ms para dezenas de milhares de retângulos, hit-testing por busca binária no array de intervalos ordenados (mouse X → tempo → intervalo). DPI-aware (`devicePixelRatio`). Acessibilidade via camada paralela: tabela alternativa (mesma fonte de dados) + navegação por teclado que move um cursor desenhado no canvas (←/→ intervalo a intervalo, Enter abre painel).
- **Agregação no servidor por zoom (decisão de arquitetura, não só de front):**
  - O agente amostra janela ativa/idle a cada 5–15s; o servidor consolida em **intervalos de estado** (mesmo estado + mesmo app contíguos = 1 intervalo) numa tabela `activity_intervals` — essa é a fonte da timeline, nunca eventos crus.
  - Endpoint recebe `resolutionSec`; servidor mescla intervalos menores que a resolução na regra "estado dominante do bucket" e devolve no máx. ~3.000 intervalos. Front pede resolução ≈ `(janelaVisívelSegundos / larguraPx) × 2`. Zoom out dia inteiro em 1.200px → ~60s/bucket (1.440 intervalos worst case); zoom 1h → 5s (intervalos praticamente crus).
  - Payload compacto: `{ "lanes": [{ "deviceId": "...", "intervals": [[startEpochSec, durationSec, state, appId|null], ...] }], "apps": { "17": {"name":"Chrome","categoryId":3} } }` — arrays posicionais + dicionário de apps deduplicado; dia de equipe ~50–200 KB gzipped.
  - Cache HTTP: dias passados são imutáveis → `Cache-Control: max-age=86400` + ETag; só "hoje" é dinâmico.

### 5.2 Atualização quase-em-tempo-real
- **Polling, não WebSocket/SignalR, no MVP.** Justificativa: (a) o frescor do dado é limitado pela ingestão — agentes sobem lotes a cada 30–60s, então push entregaria a mesma latência percebida; (b) dashboard agrega N máquinas — o cliente quer "foto de agora", não stream de eventos; (c) SignalR em produção multi-tenant exige backplane (Redis) e sticky sessions — infra e superfície de bug que um time pequeno não deve pagar para ganhar 30s; (d) polling é trivialmente cacheável e degradável.
- Implementação: TanStack Query com `refetchInterval: 45_000` no dashboard/presença e timeline de "hoje"; `refetchOnWindowFocus: true`; pausa automática em aba oculta (`refetchIntervalInBackground: false`); onboarding passo 3 usa 10s. Endpoints de "agora" devolvem agregado pré-computado (cache servidor 15s) — custo por poll ~irrelevante.
- Migração futura: se surgir caso real (alertas instantâneos, tela de parede/TV), adotar SSE antes de WebSocket — unidirecional resolve e é mais simples.

### 5.3 Estados vazios (empty states desenhados, não acidentais)
| Contexto | Conteúdo |
|---|---|
| Org sem nenhum device | Tela inteira = passo 2–3 do onboarding embutido: chave, comando de instalação, "aguardando primeira máquina" com polling. Nunca um dashboard zerado. |
| Device sem dados hoje (mas com histórico) | Timeline com hachura "Sem dados" + frase contextual: "NB-JOAO não ligou hoje. Último dado: ontem às 18:32." + botão "Ver ontem" |
| Device "Sem comunicação" | Estado de erro: "Sem dados desde 09:14. A máquina pode estar ligada com o agente parado." + link "Como diagnosticar" |
| Período sem dados em relatório | "Nenhum dado no período selecionado" + sugestão do último período com dados |
| Apps não categorizados (primeira semana) | Card no `/apps`: "Categorize seus apps para que os gráficos de classificação façam sentido" + CTA |
| Busca/filtros sem resultado | "Nenhum resultado para estes filtros" + botão "Limpar filtros" |

### 5.4 Loading e skeletons
- Skeleton específico por componente (cards retangulares pulsando com a geometria final: 5 cards, tabela de 8 linhas, área de gráfico) — nunca spinner de página inteira após o login.
- Timeline: desenhar eixo + lanes cinza imediatamente, preencher blocos quando os dados chegam (perceived performance).
- Polling em background **nunca** mostra skeleton de novo (TanStack Query: `placeholderData: keepPreviousData`); apenas o badge "Atualizando…" pisca.
- Erro de fetch: estado de erro inline por widget com "Tentar novamente", sem derrubar a página.

---

## 6. Design

- **Light mode default** (dark mode pós-MVP via tokens — shadcn/ui já estrutura). Estética: profissional/sóbria, densidade de dados estilo Linear/Stripe Dashboard, sem ilustrações infantis.
- **Tipografia:** Inter (UI) + tabular figures (`font-variant-numeric: tabular-nums`) em toda coluna numérica e durações — alinhamento de tabelas é o que faz o produto parecer sério.
- **Cor:** neutros frios como base; cor reservada para semântica de estado (verde/âmbar/cinza-azul/vermelho conforme §2.4) e uma cor de marca para ações. Estados sempre com redundância não-cromática (ícone/textura/label) — daltonismo.
- **Formatos pt-BR:** datas `dd/mm/aaaa`, horários `HH:mm` (24h), durações como `6h 40min` (nunca decimal na UI; CSV traz coluna adicional `horas_decimais` para Excel). Números com vírgula decimal.
- **Acessibilidade (alvo WCAG 2.1 AA):** contraste ≥4.5:1 em texto; foco visível em tudo; tabelas com navegação por teclado (Tab para a tabela, setas entre linhas, Enter abre detalhe), `aria-sort`, cabeçalhos corretos; gráficos ECharts com `aria` habilitado + tabela de dados alternativa acessível ("Ver dados"); timeline com fallback tabular (§5.1); formulários com labels reais e erros associados.
- **Responsividade pragmática:** breakpoints — desktop (≥1280, alvo principal), tablet (≥768, dashboards ok), mobile (≥360): **leitura sim, administração não**. No mobile: Visão Geral vira coluna única (cards de presença + lista da equipe + gráficos empilhados); timeline mostra a versão tabular/resumo do dia com mini-barra de proporções por padrão (canvas pan-and-zoom é hostil ao toque); telas de Configurações exibem aviso "Melhor experiência no desktop" mas não bloqueiam. Sem app nativo, sem PWA no MVP (PWA instalável é quick-win pós-MVP).
- Sidebar colapsável, conteúdo máx. ~1440px centralizado, tabelas com linhas de 36–40px (densas), zebra sutil opcional.

---

## 7. Relatório de jornada — enquadramento jurídico (requisito de produto, não rodapé)

- **O produto NÃO é registro eletrônico de ponto.** A Portaria MTE 671/2021 regula REP-C/REP-A/REP-P com requisitos específicos (registro fiel e imutável de marcações, comprovante ao trabalhador, AEJ/AFD, registro do desenvolvedor no MTE). Posicionar o relatório como ponto = risco regulatório e trabalhista para nós e para o cliente.
- Implicações concretas na UI:
  1. Nome oficial: **"Relatório gerencial de jornada de uso"** — nunca "espelho de ponto", "folha de ponto", "marcações".
  2. Disclaimer fixo (não-dispensável) na tela e em todo PDF/CSV exportado: "Relatório gerencial de uso da estação de trabalho, baseado em eventos de sessão do Windows. **Não constitui registro de ponto** (Portaria MTE 671/2021) e não substitui o controle de jornada do art. 74 da CLT."
  3. Vocabulário das colunas: "Primeiro evento" / "Último evento" (nunca "Entrada"/"Saída").
  4. Não calcular nem exibir: horas extras, adicional noturno, banco de horas, atrasos — qualquer um desses cria a aparência de sistema de ponto. Se o cliente pedir, a resposta de produto é integração futura com sistemas de ponto homologados, não cálculo próprio.
  5. Material de marketing/onboarding repete o enquadramento — protege o cliente de passivo trabalhista e nos protege de mau uso.
- LGPD no portal: base legal típica é legítimo interesse do empregador (monitoramento de recursos corporativos com transparência); o produto entrega os instrumentos — Página de Transparência (§2.8), configurações de minimização (§ privacidade: mascaramento, apps ignorados, janela de coleta), log de auditoria de quem exportou relatórios e de mudanças de política. Retenção default sugerida: dados detalhados (intervalos/títulos) 90 dias, agregados diários 13 meses — exposta na Página de Transparência (número final: ver Perguntas Abertas).

---

## 8. Roadmap de telas pós-MVP (ordem sugerida)

| Fase | Entrega | Telas/Componentes |
|---|---|---|
| **+1 (0–3 meses pós-GA)** | **Alertas configuráveis** | `/configuracoes/alertas`: regras (device ocioso > X min em horário de trabalho; agente sem comunicação > X; app de categoria proibida aberto > X min), canais e-mail + webhook; central de notificações no topbar |
| +1 | **Equipes/grupos de verdade** | Entidade Equipe (substitui tag livre), Gestor com escopo por equipe, filtro de equipe em todas as telas, comparativo entre equipes no dashboard |
| **+2 (3–6 meses)** | **Comparativo entre períodos** | "Esta semana vs anterior" em todos os gráficos (delta %), tendências de 13 semanas por device/equipe |
| +2 | **Relatórios agendados** | Export recorrente (semanal/mensal) por e-mail; templates salvos de filtros |
| +2 | Dashboard de TV / tela de parede | Visão "agora" full-screen com token de leitura (motivador do upgrade polling→SSE) |
| **+3 (6–12 meses)** | **Screenshots opt-in** | Capturas por amostragem/sob demanda com: consentimento explícito registrado por funcionário, blur default, retenção curta (7–30 dias), trilha de auditoria de visualização, atualização automática da Página de Transparência. Feature flag por tenant; carga jurídica/LGPD inteira tratada como projeto próprio |
| +3 | **API pública + webhooks** | `/configuracoes/api`: tokens com escopo de leitura, docs OpenAPI, webhooks de eventos (device offline, alerta disparado) — destrava integrações com BI (Power BI) e ponto homologado |
| +3 | SSO corporativo | Entra ID / Google Workspace (SAML/OIDC) — requisito de mid-market |
| Contínuo | Dark mode, PWA instalável, en/es se houver demanda | — |

---

## Apêndice: Decisões-chave recomendadas

- React + TypeScript + Vite (com Tailwind, shadcn/ui, ECharts, TanStack Query) em vez de Blazor — o produto é essencialmente visualização de dados (timeline canvas, gráficos, tabelas densas), o pior caso do Blazor (charts pagos, JS interop, contratação rara) e o melhor caso do React; o gap de língua se mitiga gerando tipos TS do OpenAPI do ASP.NET.
- Timeline renderizada em Canvas 2D com agregação server-side por nível de zoom (parâmetro resolutionSec, máx. ~3.000 intervalos por resposta, payload posicional compacto) — SVG/divs degradam com dezenas de milhares de blocos no modo equipe; dias passados são imutáveis e cacheáveis com ETag.
- Polling de 45s via TanStack Query no MVP, sem WebSocket/SignalR — o frescor é limitado pela ingestão em lotes do agente (30–60s), push não melhoraria a latência percebida e SignalR exige backplane/sticky sessions que um time pequeno não deve pagar; SSE antes de WebSocket se surgir caso real.
- Vocabulário em duas camadas: estados de máquina neutros (Ativo/Ocioso/Bloqueado/Desligada) e julgamento de valor apenas em categorias configuráveis pelo cliente com rótulos 'Relacionado ao trabalho/Neutro/Não relacionado' — nunca 'produtivo/improdutivo' em estado de máquina, e sem rankings de pessoas no MVP.
- Tudo armazenado em UTC e exibido sempre no fuso padrão do tenant (nunca no fuso do navegador), com badge de divergência quando o device está em outro fuso e correção server-side de clock skew.
- Distinção explícita entre 'Desligada' (agente enviou evento de shutdown), 'Sem comunicação' (sumiu sem evento — alerta de TI, vermelho hachurado) e 'Sem dados' (hachura neutra) — confundir esses três é a fonte nº 1 de chamados de suporte no domínio.
- Relatório de jornada posicionado como gerencial com disclaimer fixo não-dispensável (Portaria MTE 671/2021 / art. 74 CLT) em tela e em todo export, colunas 'Primeiro/Último evento' (nunca Entrada/Saída) e zero cálculo de horas extras/banco de horas.
- Privacidade como enforcement no agente, não filtro de exibição: mascaramento de títulos impede o título de chegar ao servidor; apps ignorados descartados na coleta; tudo com log de auditoria — sustenta o posicionamento 'transparente, não spyware'.
- Onboarding como funil de conversão: signup → chave de enrollment → comando msiexec pronto → primeira máquina visível com polling de 10s em <15 minutos; org sem devices nunca vê dashboard vazio, sempre o wizard.
- Exports como jobs assíncronos com histórico auditável (quem exportou, quando, com quais filtros); CSV em UTF-8 com BOM e separador ';' para Excel pt-BR; PDF gerado no servidor (QuestPDF encaixa no stack .NET).
- Página de Transparência gerada do estado real das configurações do tenant, com link público que o ícone do agente na bandeja abre — fecha o ciclo de transparência funcionário↔empresa e vira argumento de venda.

## Apêndice: Riscos

- Performance da timeline se construída sobre eventos crus: sem a tabela de intervalos consolidados (activity_intervals) e agregação por resolução no servidor, a tela principal do produto fica lenta com poucas semanas de dados — essa decisão é de back-end e precisa existir antes do front.
- Títulos de janela podem conter dados pessoais e até sensíveis de terceiros (assunto de e-mail, nome de paciente em prontuário, conversas) — risco LGPD real para o cliente e reputacional para o produto; o default do mascaramento e a lista default de apps ignorados precisam de revisão jurídica antes do GA.
- Confusão com ponto eletrônico: se vendas/marketing ou a UI insinuarem 'controle de ponto', cria-se passivo trabalhista para clientes e risco regulatório (Portaria 671); o disclaimer precisa ser inegociável em tela e exports, e o time comercial treinado.
- Interpretação errada de 'ocioso' (reuniões, chamadas, leitura aparecem como ociosidade) é a causa nº 1 de conflito gestor-funcionário e de churn — o tooltip pedagógico e o vocabulário neutro mitigam, mas não eliminam; considerar integração futura com calendário.
- Tentação de escolher Blazor pelo conforto do time .NET: o custo aparece tarde (componente de timeline via JS interop, bibliotecas de gráficos pagas, contratação difícil) e a migração depois é reescrita do front inteiro.
- Clock skew e buffer offline dos agentes geram timelines incoerentes (intervalos sobrepostos, dados chegando horas depois) — o pipeline precisa de correção de relógio e a UI de tolerância a dados atrasados desde o dia 1.
- Custo de polling em escala: N tenants com dashboards abertos a cada 45s exige endpoints de agregados pré-computados com cache curto no servidor; se cada poll fizer query pesada, o banco sofre cedo.
- CSV sem BOM/; quebra no Excel pt-BR (acentuação e colunas) — bug bobo que destrói credibilidade com RH no primeiro export.
- Multiusuário em uma máquina (PC compartilhado, terminal server): se a modelagem for só por device, o relatório de jornada por pessoa fica errado — decidir identidade (device × usuário Windows) antes de congelar o schema do front.
- Acessibilidade do canvas: sem o fallback tabular e a navegação por teclado planejados desde o início, a timeline vira retrofit caro em vez de feature.

## Apêndice: Perguntas abertas (dependem do dono do produto)

- Identidade de monitoramento: a unidade primária é a máquina (device) ou o usuário Windows? Como tratar PCs compartilhados e múltiplas máquinas por pessoa (afeta timeline, jornada e pricing por seat vs por device)?
- Default do mascaramento de títulos de janela para tenants novos: ligado (privacidade primeiro, menos valor demonstrado) ou desligado (mais valor na demo, mais exposição LGPD)?
- Retenção oficial de dados: confirmar 90 dias de dados detalhados + 13 meses de agregados, ou outra política — afeta date pickers, pricing por plano e o texto da Página de Transparência.
- Coleta fora do horário de trabalho: coletar 24/7 por padrão (com exibição opcional) ou oferecer/forçar janela de coleta? Há posição jurídica/comercial da empresa sobre monitorar uso pessoal fora do expediente?
- Pricing e limites por plano (nº de devices, retenção, papéis): quais recursos do portal ficam atrás de paywall e como exibir limites/upsell na UI?
- SSO (Entra ID/Google Workspace) entra no MVP por exigência de algum cliente-âncora, ou e-mail+senha basta para o segmento PME inicial?
- Nome do produto e identidade visual — afeta a Página de Transparência, o ícone do agente na bandeja e o template de PDF dos relatórios.
- Internacionalização: pt-BR hardcoded ou strings externalizadas desde já prevendo es/en (custo baixo agora, alto depois)? Há ambição LatAm no horizonte de 18 meses?
- O catálogo global de apps→categoria (mantido por nós, com override por tenant) terá curadoria de quem? Precisa de processo/ferramenta interna desde o MVP?
- Visualizador (4º papel) entra no MVP ou cortamos para 3 papéis (Proprietário/Administrador/Gestor)?