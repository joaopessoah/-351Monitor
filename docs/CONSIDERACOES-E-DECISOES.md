# Considerações e Decisões — antes de escrever código

> **Para quem é este documento:** para você, João — dono do produto. Leia antes de abrir o editor.
> **O que ele NÃO é:** o spec técnico. Os "comos" estão no `PROMPT-DESENVOLVIMENTO.md`. Aqui estão os **porquês**, os riscos, as obrigações legais, o negócio e os próximos passos.

---

## 1. Resumo executivo

O produto é um **SaaS brasileiro de monitoramento transparente de estações Windows para PMEs**: um agente leve instalado nas máquinas coleta eventos de sessão, janela ativa e ociosidade (nunca conteúdo — sem keylog, sem screenshot), um backend multi-tenant transforma esses eventos em timelines e relatórios, e um portal web responde às perguntas do gestor ("minha equipe está trabalhando agora? e essa semana?") com posicionamento explícito de **gestão transparente, não vigilância** — LGPD como argumento de venda, não como obstáculo. O estado atual: **a especificação está completa e reconciliada**. As cinco análises de design (`docs/design/01` a `05`) foram escritas em paralelo, a crítica adversarial (`docs/design/06`) listou todas as contradições entre elas, e as decisões finais foram consolidadas numa tabela canônica única. Nenhuma linha de código foi escrita ainda — e isso é proposital: este produto tem mais risco jurídico e de posicionamento do que risco técnico, e essas decisões precisavam vir primeiro.

**Como os documentos se relacionam:**

| Documento | Papel | Quando consultar |
|---|---|---|
| **Este documento** (`CONSIDERACOES-E-DECISOES.md`) | Os porquês: decisões, riscos, LGPD, negócio, próximos passos | Antes de começar; ao tomar qualquer decisão de produto |
| **`PROMPT-DESENVOLVIMENTO.md`** | O spec técnico mestre: contratos de API, schema, algoritmos, critérios de pronto por fase | Durante o desenvolvimento, todo dia |
| **`docs/design/01-06`** | Análises de referência que geraram tudo isso | Quando precisar do raciocínio detalhado por trás de uma decisão |

Regra de ouro: **em caso de divergência entre qualquer documento de design e a tabela canônica (refletida aqui e no spec), a tabela canônica vence.** As seções 01–05 contêm contradições entre si — foram resolvidas, não apague isso da cabeça ao relê-las.

---

## 2. Decisões de arquitetura e stack já tomadas — e por quê

| Decisão | Alternativas consideradas | Por que | Custo de mudar depois |
|---|---|---|---|
| **Agente híbrido: serviço Windows (LocalSystem) + helper por sessão de usuário** | Só serviço; só app na sessão do usuário | Session 0 Isolation: serviço não enxerga a janela ativa do usuário; app de sessão sozinho morre no logoff e é matável sem rastro. O híbrido é **obrigatório**, não preferência | Altíssimo — é a arquitetura do agente inteiro |
| **.NET 8 LTS, self-contained, single-file** (serviço Worker + helper WinForms tray) | Rust, Go, C++/Win32 | Time já domina .NET; P/Invoke maduro (CsWin32); self-contained elimina dependência de runtime na frota do cliente | Médio — retarget para .NET 10 LTS antes de nov/2026 já está no plano (trivial); trocar de linguagem = reescrita |
| **PostgreSQL 16 único, gerenciado, em região BR, particionado por tempo** | TimescaleDB, ClickHouse, SQL Server, Postgres self-host | O MVP dimensiona para ~2.500 devices — Postgres puro atende com folga enorme; um só sistema para operar e fazer backup; gerenciado tira PITR/failover das costas de 2 devs | Baixo→médio — TimescaleDB é extensão do mesmo banco (degrau natural de evolução); ClickHouse só acima de ~50 M eventos/dia |
| **Portal: React + TypeScript + Vite + Tailwind + shadcn/ui + ECharts + TanStack Query** | Blazor Server / Blazor WASM | O produto **é** visualização de dados (timeline em canvas, gráficos, tabelas densas, polling) — o pior caso do Blazor: charts bons são pagos (~US$ 1.000/dev/ano), o canvas exige JS interop de qualquer jeito, e contratar dev Blazor no Brasil é raríssimo. Detalhe na seção 03 do design | Alto — trocar de framework de front depois é reescrever o portal |
| **Hospedagem 100% Brasil** (ex.: Azure Brazil South), incluindo backups | Hetzner/Contabo na Europa (5–10× mais barato) | Residência nacional não é exigência da LGPD (transferência com salvaguardas é lícita), mas **é exigência comercial**: RH/jurídico do cliente B2B pergunta isso em todo procurement. Vira selo de venda | Médio para migrar de cloud; **irreversível** comercialmente se a promessa de residência for quebrada |
| **Deploy: 1 VM com Docker Compose + Caddy; Serilog→Seq + Sentry** | Kubernetes, App Service/AKS, stack completa de observabilidade | 2 devs não devem operar orquestrador; Caddy dá TLS automático; Seq+Sentry cobrem diagnóstico do MVP | Médio — Compose→k8s é migração conhecida, feita quando (se) houver necessidade real |
| **Multi-tenant lógico (pool model)**: `tenant_id` em toda tabela desde a 1ª migration + filtro obrigatório + teste automatizado de isolamento desde F0 | Schema por tenant; banco por tenant | Migrations e operação simples para time pequeno; queries internas cross-tenant (billing, saúde) fáceis. Banco dedicado fica reservado para futuro plano enterprise | **Retrofit de isolamento é o retrabalho mais caro possível neste produto** — por isso o teste de isolamento é gate desde F0 |
| **"Agente burro, servidor inteligente"**: agente só emite eventos idempotentes; backend deriva intervalos e durações | Agente calcula durações/sessões localmente | Idempotência e reenvio seguros; correção de relógio centralizada; bug em regra de negócio se corrige no servidor **sem redeploy de frota** | Alto se invertido — lógica embarcada em frota desatualizada é dívida permanente |
| **Polling de 5 s da janela ativa (com dedupe)** | `SetWinEventHook` (orientado a evento) | Polling é simples, robusto e suficiente para relatório gerencial; hooks são otimização pós-MVP com polling como fallback | Baixo — é mudança interna do helper, sem alteração de contrato |
| **Config e comandos via ack do `POST /api/v1/ingest/batch`** (pull; sem endpoint separado, sem assinatura de config no MVP) | Endpoint de policy com ETag; WebSocket/push | Um canal só; lote a cada ≤30 s garante propagação rápida; menos superfície e menos estado | Baixo — push (SSE) entra se surgir caso real |
| **Device token opaco, revogável manualmente; SEM rotação automática no MVP** | JWT; rotação automática a cada 90 dias | Opaco = revogação instantânea por lookup de hash; rotação automática é complexidade (carência, token anterior) que o risco do MVP não justifica | Baixo — rotação automática entra na v1.1 sem quebrar contrato |

Quatro dessas decisões merecem prosa, porque vão ser questionadas (por você mesmo, daqui a 3 meses):

**O agente híbrido não é escolha, é física do Windows.** Desde o Vista, serviços rodam na Session 0, isolada de qualquer sessão interativa. `GetForegroundWindow` e `GetLastInputInfo` chamados do serviço retornam dados de uma sessão vazia. Então a coleta roda num helper dentro da sessão do usuário (com o token dele, baixo privilégio), e o serviço — que precisa ser LocalSystem porque `WTSQueryUserToken` exige o privilégio TCB — cuida de fila, envio, watchdog e eventos de sessão/energia. O usuário pode matar o helper (roda como ele); o watchdog relança e registra `AGENT_TAMPER`. Isso é aceitável e até desejável: tamper-proofing absoluto contra admin local é impossível e tentar parecer rootkit destruiria o posicionamento.

**"Agente burro" é a decisão que protege seu cronograma.** Toda a inteligência (fechar intervalos, corrigir relógio, tratar idle retroativo, detectar gaps) vive no servidor, onde um bug se corrige com um deploy e um reprocessamento idempotente. Se a lógica estivesse no agente, cada correção dependeria do auto-update chegar a 100% da frota — e relatórios errados conviveriam por semanas.

**React em vez de Blazor vai doer nas primeiras 3–4 semanas — e é a escolha certa mesmo assim.** O custo honesto: o time .NET aprende uma segunda língua e mantém dois tooling. A mitigação: shadcn/ui encurta o caminho e a geração de tipos TS a partir do OpenAPI do ASP.NET elimina ~80% do benefício real do Blazor (contrato compartilhado). O que o Blazor não resolve de jeito nenhum: timeline em canvas (JS interop de qualquer forma), gráficos gratuitos de qualidade, contratação futura.

**Hospedar no Brasil custa mais e é a decisão de venda mais barata que você vai tomar.** ~R$ 3–5 mil/mês em Azure/AWS BR contra €50–100 na Europa. A diferença se paga na primeira venda em que o jurídico do cliente perguntar "onde ficam os dados?" — e em produto de monitoramento de funcionário, **todos** perguntam. Atenção operacional: redundância geográfica default da nuvem pode replicar para fora do país silenciosamente; conferir região de réplicas e do bucket de backup.

---

## 3. Tabela de números canônicos (referência rápida)

Estes são os números finais. **Qualquer divergência em `docs/design/` está superada por esta tabela.** O spec (`PROMPT-DESENVOLVIMENTO.md`) usa exatamente estes valores.

| Parâmetro | Valor canônico |
|---|---|
| Contrato agente↔backend | `POST /api/v1/agent/enroll` e `POST /api/v1/ingest/batch` (únicos) |
| Envelope de evento | `event_id` UUIDv7 + `seq` + `occurred_at` UTC + `tz_offset_min` + `mono_ms` + `boot_id` |
| Canal de config | Somente via ack do batch (devolve config quando há versão nova); sem endpoint separado; sem assinatura de config no MVP |
| Tipos de evento | **17 canônicos**: AGENT_START/STOP, SESSION_START/END, LOCK, UNLOCK, ACTIVE_WINDOW_CHANGED, IDLE_START (com `last_input_at`)/IDLE_END, HEARTBEAT, SYSTEM_SUSPEND/RESUME, TIME_CHANGED, EVENTS_DROPPED, AGENT_TAMPER, NOTICE_ACK, POLICY_APPLIED. **APPS_SNAPSHOT foi cortado** (sem consumidor + minimização) |
| Polling de janela ativa | 5 s (com dedupe e anti-flapping) |
| Heartbeat | 60 s |
| Envio em lote | a cada 30 s **ou** 500 eventos (o que vier primeiro) |
| Limiar de ociosidade | 300 s default; UI permite 3–15 min (protocolo aceita 60–1800 s) |
| Semântica do idle | `IDLE_START` fecha o intervalo ativo **retroativamente em `last_input_at`** (sem isso, todo ciclo de idle ganharia ~5 min de "ativo" falso) |
| Device "online agora" | último contato ≤ 180 s |
| Gap fecha intervalo | 600 s sem eventos |
| Buffer offline do agente | 7 dias **ou** 50.000 eventos **ou** 100 MB (o que estourar primeiro; descarte FIFO sinalizado por EVENTS_DROPPED) |
| Janela de aceitação do ingest | rejeita eventos com `occurred_at` < now−14d ou > now+5min |
| Retenção (fixa no MVP) | brutos **90 dias** · intervalos **12 meses** · agregados diários **24 meses** · auditoria **24 meses** |
| Estados da timeline | `active`, `idle`, `locked`, `off_clean` (desligada/suspensa limpa), `no_data` (sem comunicação) |
| Política de títulos (default) | `MASKED_PATTERNS` (lista de fábrica) + `APP_ONLY` automático em navegação anônima |
| Janela de coleta | `collection_window` suportada no MVP, com escolha **explícita** do tenant no onboarding (registrada em auditoria) |
| Tokens | enrollment key `ek_...` por tenant; device token opaco revogável manualmente; sem rotação automática no MVP; UNENROLL via ack descarta a fila local |
| RBAC | Owner / Admin / Viewer; MFA TOTP **obrigatória** para Owner e Admin; senha ≥ 12 chars, Argon2id |
| Signup | Sem self-service: org criada via backoffice, trial assistido |
| Dimensionamento | ~2.500 devices (não 10.000 — corte da crítica) |
| Preço (hipótese) | Essencial R$ 19,90/device/mês · Pro R$ 34,90 · piso 10 devices (R$ 199) · anual ~2 meses grátis · piloto assistido de até 25 devices, sem prazo fixo |
| Roadmap | F0 2 sem → F1 3 sem → F2 4 sem → F3 4 sem → F4 5 sem → F5 6 sem (parcialmente paralela) ≈ **20–24 semanas com 2 devs** |

---

## 4. O corte do MVP

Princípio do corte (da seção 05): **o MVP é o menor produto pelo qual uma PME paga R$ 500–1.500/mês e renova no mês seguinte.** Tudo que não contribui para "o gestor olha o dashboard toda semana e acha que vale o preço" sai.

| Capacidade | MVP | v1.1 (60–90 dias pós-GA) | Depois — ou nunca |
|---|---|---|---|
| Agente Windows 10 (1809+)/11 x64, serviço+helper, MSI/WiX assinado | ✅ | | |
| Agente macOS / Linux | | | v2+, só com demanda paga comprovada |
| Suporte a Windows Server / RDS / Citrix | | | **Não suportado** (corte confirmado; reavaliar com demanda) |
| Coleta: 17 eventos canônicos (sessão, janela ativa, idle, energia, tamper) | ✅ | | |
| APPS_SNAPSHOT (apps visíveis sem foco) | | | Cortado — sem consumidor + minimização LGPD |
| Ícone visível + toast de 1º logon + **NOTICE_ACK** registrado | ✅ (inegociável) | | |
| `window_title_policy` com default MASKED_PATTERNS + APP_ONLY em navegação anônima | ✅ | | |
| `collection_window` (janela de coleta) com escolha do tenant no onboarding | ✅ | | |
| Timeline device/dia + **timeline de equipe** | ✅ | | |
| Zoom multi-resolução na timeline (dia→hora→minuto) | | ✅ | |
| Dashboard (presença "agora", horas ativas/ociosas, top apps) | ✅ | | |
| Relatórios por período + export **CSV** (UTF-8 BOM, separador `;`) | ✅ | PDF/XLSX + relatórios agendados | |
| Categorias de apps por tenant + catálogo padrão | ✅ | regras por equipe | auto-categorização (ML) |
| **Seed de tenant demo** (30 devices, 60 dias sintéticos passando pelo pipeline real) | ✅ (F3 — requisito de vendas) | | |
| Multi-tenant lógico + teste de isolamento no CI | ✅ (desde F0) | | |
| RBAC Owner/Admin/Viewer + MFA TOTP obrigatória (Owner/Admin) | ✅ | Manager com escopo por equipe | SAML/SCIM (enterprise) |
| **DSR** — export e exclusão de dados de um titular, auditados | ✅ (gate de lançamento, F4) | | |
| Log de auditoria "quem viu o quê" | ✅ | | |
| Auto-update do agente | ✅ (canal único + rollback por manifesto) | anéis canary/5%/100% | |
| Rotação automática de device token | | ✅ | |
| Painel de saúde dos agentes (heartbeat, versão) | ✅ (lista + status) | alertas de agente parado | |
| Alertas (ociosidade, app proibido, offline) | | ✅ (e-mail) | webhooks/Slack/Teams |
| Billing | Manual: Pix/boleto + NFS-e | gateway recorrente (>30 contas) | self-service completo |
| Signup self-service | | | Avaliar — backoffice + trial assistido funciona até dezenas de contas |
| SSO (Google/Microsoft OAuth) | | ✅ | SAML (enterprise) |
| Página de transparência pública por slug (`/transparencia/:slug` — só a política de coleta, sem dados pessoais) + kit LGPD em PDF | ✅ | versão tokenizada com preview "ver como funcionário" | |
| API pública / webhooks / white-label MSP / app mobile | | | v2+ |
| Coleta de URLs/domínios de navegação | | | Avaliar (somente domínio, nunca URL completa) |
| **Screenshots** | | | **Decisão de marca: fora — talvez nunca** (se um dia, opt-in contratual + blur + retenção curtíssima) |
| Keylogging, clipboard, webcam/mic, leitura de conteúdo, modo oculto | | | **NUNCA** (linha vermelha — seção 5) |
| pg_partman / dimensionamento p/ 10k devices / observabilidade completa (OTel+Grafana) | | quando houver >1 instância | gatilhos objetivos no spec |

### O que foi conscientemente adiado — e o risco disso

Estes adiamentos foram decisões, não esquecimentos. Cada um tem um risco assumido:

1. **Manager com escopo por equipe (v1.1)** — é uma **derrogação consciente do REQ-PRIV-07** (granularidade de acesso da análise LGPD). Risco: um cliente com RH maduro pode exigir que gestor veja só a própria equipe já no lançamento. Mitigação: o enum de papéis e o hook de filtro no contexto de tenant já nascem extensíveis; **registre a derrogação formalmente** (decisão + justificativa + data) — isso importa se um DPO de cliente perguntar.
2. **Rotação automática de device token (v1.1)** — risco baixo: revogação manual cobre o cenário real (máquina comprometida/desligada). O contrato já comporta a rotação futura.
3. **Anéis de auto-update (v1.1)** — risco real: um update ruim atinge 100% da frota de uma vez. Mitigação no MVP: validação SHA-256 + Authenticode no cliente, rollback por manifesto, e disciplina de testar todo update na frota interna antes de publicar.
4. **Pentest não é gate de GA** — é gate **antes da primeira conta grande**. Risco: vender para uma conta grande antes do pentest. A regra de negócio é simples: não venda para conta grande antes do pentest. Anote isso onde o futuro você comercial vá ler.
5. **PDF de relatório (v1.1)** — RH gosta de PDF. CSV com BOM + disclaimer trabalhista cobre o caso de uso; se virar objeção de venda recorrente no piloto, antecipe.
6. **Assinatura de config (cortada do MVP)** — o agente aplica a config que vem no ack autenticado por TLS + device token; assinatura adicional foi removida da spec (e da alegação de "config assinada" dos textos antigos). Risco baixo; reavaliar se surgir requisito de cliente regulado.
7. **Retenção fixa (sem configuração por tenant/plano no MVP)** — simplifica purge, partições, texto de transparência e pricing. Risco: cliente de compliance pedir retenção maior — resposta padrão: roadmap, mediante aditivo, v1.1+.

---

## 5. LGPD e trabalhista — o que você PRECISA saber

Esta é a seção mais importante do documento. O maior risco do produto não é técnico — é jurídico e de imagem. A boa notícia: a lei está do seu lado **se** o produto for desenhado como está especificado.

### 5.1 Controlador vs. operador — e o que muda na prática

- **O cliente (empregador) é o CONTROLADOR**: ele decide finalidade e meios — quem monitorar, quais relatórios usar, como comunicar os empregados, como atender direitos dos titulares.
- **Você (fornecedor SaaS) é o OPERADOR**: trata dados exclusivamente sob instrução documentada do controlador. Na prática, **a configuração do tenant no portal É a instrução documentada** — por isso toda mudança de configuração de coleta é registrada em auditoria (incluindo a escolha da `collection_window` no onboarding).

Consequências práticas de ser operador:
- **Você não pode usar os dados para finalidades próprias** (benchmarks entre clientes, treinar modelos) sem previsão explícita no DPA. Qualquer feature analítica cross-tenant passa por revisão jurídica antes do design.
- **Responsabilidade solidária existe** (art. 42, §1º): se você descumprir a LGPD ou as instruções do controlador, responde junto. Por isso o produto deve **impedir tecnicamente** configurações ilegais (ex.: modo oculto não existe no binário), não apenas desaconselhá-las.
- **DPA assinado é pré-condição de provisionamento**: sem DPA, tenant não é criado. Como a criação de org é via backoffice (sem signup self-service), isso é trivial de garantir no processo.

### 5.2 Por que consentimento NÃO é a base legal

Intuição errada comum: "o funcionário assina um termo consentindo". **Não.** O art. 5º, XII exige manifestação **livre** — e a subordinação da relação de emprego vicia a liberdade (o empregado não pode recusar sem temer represália). Pior: consentimento é revogável a qualquer tempo, o que inviabilizaria a operação. O **Termo de Ciência** que o produto entrega é instrumento de **transparência**, não de consentimento — e o texto do termo diz isso explicitamente.

### 5.3 A base legal real: legítimo interesse + transparência

A base é o **legítimo interesse do controlador** (art. 7º, IX c/c art. 10): gestão de produtividade, segurança da informação e gestão de ativos de TI. Mas legítimo interesse não é cheque em branco — é condicionado a:

1. **LIA documentada** (teste de balanceamento) — você fornece o modelo, o cliente preenche;
2. **Transparência total ao empregado** — ícone sempre visível, tela "o que está sendo coletado agora", toast de primeiro logon com registro `NOTICE_ACK` (evidência de ciência para o controlador);
3. **Proporcionalidade e minimização** — lista de coleta exaustiva e fechada; defaults conservadores;
4. **Expectativa legítima** — equipamento corporativo + política comunicada previamente.

A jurisprudência trabalhista (TST/TRTs) consolida o mesmo: monitoramento de ferramenta corporativa é aceito com **política clara e prévia + ciência inequívoca + proporcionalidade**. O que gera condenação por dano moral: monitoramento **oculto**, acesso a **conteúdo pessoal**, e uso de dados para **exposição vexatória**. O produto bloqueia os três por arquitetura e vocabulário (estados neutros, sem ranking de pessoas, perfis de acesso auditados).

**E o ponto eletrônico:** o produto **NÃO é registro de ponto** (Portaria MTE 671/2021, art. 74 CLT). O relatório se chama "Relatório gerencial de jornada de uso", as colunas são "Primeiro/Último evento" (nunca "Entrada/Saída"), há disclaimer fixo não-dispensável em tela e em todo export, e **jamais** se calcula hora extra, banco de horas ou atraso. Quebrar qualquer um desses quatro itens cria aparência de sistema de ponto não homologado — passivo para o cliente e para você.

### 5.4 Títulos de janela: o risco nº 1 — e a mitigação

Os dados coletados são pessoais, mas não sensíveis por natureza. **Exceto títulos de janela**, que vazam dado sensível acidentalmente: `"Resultado_Exame_HIV.pdf - Adobe Reader"`, `"Sindicato dos Metalúrgicos - Filiação"`, nome de paciente em assunto de e-mail. Como operador, um vazamento disso é incidente LGPD grave **seu**, não só do cliente.

Mitigação em camadas, todas no MVP:
- Default de fábrica **`MASKED_PATTERNS`**: lista de padrões (saúde, sindical, religioso, financeiro pessoal) mascarada **no agente, antes de persistir em disco** — o título sensível nunca toca a fila local nem a rede;
- **`APP_ONLY` automático em navegação anônima/privada** (título descartado, só o nome do app);
- Lista de **apps ignorados** (gerenciadores de senha, apps bancários/saúde) — o tempo conta, o conteúdo não;
- Títulos **nunca** aparecem em logs (do agente nem do servidor);
- Retenção de brutos limitada a 90 dias;
- Ampliar a coleta (ex.: títulos completos) exige ação explícita do admin do tenant, **logada em auditoria** — vira instrução documentada dele, não default seu.

### 5.5 O que JAMAIS implementar (mesmo sob pedido de cliente, com recusa registrada)

- Keylogger / captura de teclas / leitura de clipboard;
- Qualquer modo oculto/stealth (ícone invisível, processo disfarçado) — **não existe a flag no binário, por arquitetura**;
- Webcam ou microfone;
- Leitura de conteúdo de e-mails, mensagens, documentos;
- Captura de tela contínua/oculta;
- Burla de janela anônima do navegador;
- Venda/uso secundário de dados de monitoramento.

Racional: além do passivo solidário, um único caso público de "software brasileiro usado como spyware" destrói a marca. A postura "não construímos isso nem por dinheiro" é ativo de vendas — alguns prospects vão condicionar a compra a screenshots/keylog; perder essas vendas é parte da estratégia, e o time comercial (você, no início) precisa da política escrita para não corroer a linha negócio a negócio.

### 5.6 Artefatos jurídicos a produzir/contratar (com advogado — os modelos não vão a mercado sem revisão formal)

| # | Artefato | O que é | Pronto até |
|---|---|---|---|
| 1 | **DPA** (Acordo de Tratamento Operador↔Controlador) | Anexo contratual: instruções, suboperadores, segurança, prazo de notificação de incidente, término/devolução em 30 dias | Antes do 1º contrato (F5) |
| 2 | **Termo de Ciência do Empregado** (modelo) | Cliente adapta e colhe aceite; texto explicita que é ciência, não consentimento | Kit de onboarding (F4) |
| 3 | **Política de Privacidade do produto** | Página pública: o que coleta/não coleta, papéis, subprocessadores, residência, retenção, contato do Encarregado | Antes do 1º cliente |
| 4 | **AUP** (Política de Uso Aceitável, modelo) | Sustenta a expectativa de privacidade reduzida em equipamento corporativo | Kit de onboarding (F4) |
| 5 | **RoT** (Registro de Operações de Tratamento — seu, art. 37) | Inventário das suas operações como operador (e controlador dos dados de conta/billing) | Antes do lançamento |
| 6 | **LIA** (modelo de avaliação de legítimo interesse) | Template preenchível pelo cliente, citando as salvaguardas do produto | Kit de onboarding (F4) |
| 7 | Plano de Resposta a Incidentes + runbook | Prazo de notificação ao controlador definido em contrato (proposta: 24–48 h; o controlador tem 3 dias úteis para a ANPD — Resolução CD/ANPD 15/2024 — seu prazo precisa deixar folga) | Antes do lançamento |
| 8 | Nomeação do **Encarregado (DPO)** + canal `privacidade@` | Pode ser sócio acumulando ou serviço externo — mas publicado | Antes do lançamento |
| 9 | Kit de comunicação interna + FAQ de privacidade | Texto de intranet/e-mail para o cliente avisar os funcionários; FAQ para os dois públicos | Kit de onboarding (F4) |

### 5.7 Checklist de conformidade pré-lançamento

**Jurídico/documental**
- [ ] DPA modelo revisado por advogado e integrado ao fluxo (sem DPA → sem tenant)
- [ ] Política de Privacidade publicada (`/privacidade`)
- [ ] Termo de Ciência + AUP + LIA modelo no kit de onboarding
- [ ] RoT preenchido e versionado
- [ ] Encarregado (DPO) nomeado, canal `privacidade@` publicado
- [ ] Lista de subprocessadores publicada
- [ ] Plano de Resposta a Incidentes escrito, prazo de notificação em contrato
- [ ] Cláusula de offboarding: export + exclusão em 30 dias

**Produto/agente**
- [ ] Ícone visível sem flag de ocultação; tela "o que é coletado" funcional
- [ ] Toast de 1º logon com `NOTICE_ACK` registrado
- [ ] `MASKED_PATTERNS` default, aplicado no agente (client-side)
- [ ] Navegação anônima → `APP_ONLY` automático
- [ ] `collection_window` com escolha explícita do tenant no onboarding, logada em auditoria
- [ ] Revisão de código confirmando: zero código de keylog/screenshot/webcam/mic/conteúdo
- [ ] Binário assinado (Authenticode; EV se possível) e submetido ao Microsoft Defender

**Portal/backend**
- [ ] Retenção 90 d / 12 m / 24 m / 24 m com job de purga rodando e logado
- [ ] DSR funcional e auditado: export e exclusão por titular (gate de lançamento, F4)
- [ ] Full export + exclusão de tenant (processo pode ser manual, mas o export precisa existir)
- [ ] Revogação de device token individual e em massa funcional; UNENROLL descarta fila local
- [ ] Log de auditoria cobrindo visualização individual, exports, exclusões, mudanças de config e permissões
- [ ] RBAC Owner/Admin/Viewer; derrogação do REQ-PRIV-07 registrada formalmente
- [ ] MFA TOTP obrigatória para Owner/Admin; senha ≥ 12, Argon2id

**Segurança/infra**
- [ ] TLS 1.2+ ponta a ponta; HSTS; sem HTTP
- [ ] Criptografia em repouso (banco + backups) verificada
- [ ] Hospedagem **e backups** confinados a região Brasil (replicação geográfica conferida)
- [ ] Suíte de testes de isolamento cross-tenant passando no CI (gate de release)
- [ ] Teste de restore de backup executado e documentado
- [ ] Pentest externo **agendado para antes da primeira conta grande** (não é gate de GA — decisão consciente)

---

## 6. Negócio

### 6.1 ICP — para quem vender (e para quem não vender)

**PME brasileira, 10–200 funcionários**, trabalho de escritório/híbrido em estações Windows: contabilidades, advocacia, BPO pequeno, agências, distribuidoras com backoffice, software houses, corretoras. **Sweet spot: 20–80 dispositivos** — dor real ("não sei o que a equipe faz no híbrido"), decisão rápida (1–2 decisores, ciclo de 1–4 semanas), TI mínima capaz de rodar um MSI.

**Anti-ICP no MVP** (recusar educadamente, anotar para depois): enterprise 500+ (SSO/SAML, questionário de 200 itens, ciclo de 6–12 meses — mataria um time de 2 devs), empresas que condicionam compra a screenshots/keylog (dizer não é parte da marca), operações sem estação Windows individual, órgãos públicos (licitação).

### 6.2 Concorrência e posicionamento

A categoria tem dois polos: **workforce analytics** (ActivTrak, DeskTime, Hubstaff, Time Doctor — "produtividade", linguagem amigável) e **insider threat** (Teramind, Veriato — screenshots, keylog, gravação de sessão). O produto pertence ao primeiro polo e o material de vendas diz isso com todas as letras: **gestão transparente de produtividade ≠ vigilância**. Inclua uma seção "o que NÃO somos": não somos ponto eletrônico, não somos antivírus, não somos spyware.

Onde um entrante brasileiro pequeno ganha dos globais:
1. **Preço em real** — ActivTrak a ~US$ 10–15/usuário vira R$ 55–85; você cobra R$ 19,90–34,90/dispositivo;
2. **LGPD-first como feature** — termo de ciência pronto, minimização por design, DSR no produto: nenhum global entrega isso em português com linguagem jurídica brasileira;
3. **Suporte local em português** (WhatsApp, horário BR) contra chamado em inglês com SLA de 48 h;
4. **Simplicidade radical** — instalação em 10 minutos, dashboard que o dono entende sem treinamento;
5. **Pix/boleto e contrato em português**.

O concorrente direto BR é o **fSense** (~R$ 10–25/máquina/mês, UX datada) — a diferenciação é experiência de produto + narrativa LGPD, não preço de guerra.

### 6.3 Preço (hipótese a validar no piloto) e racional

| | Essencial | Pro |
|---|---|---|
| Preço/dispositivo/mês | **R$ 19,90** | **R$ 34,90** |
| Diferenciação | dashboard, timeline, relatórios CSV, categorias | + relatórios avançados e alertas (quando lançarem, v1.1) + suporte WhatsApp prioritário |

- **Por dispositivo, não por usuário**: auditável (nº de agentes ativos), evita discussão de turnos/máquinas compartilhadas, e é o modelo do concorrente BR.
- **Piso de 10 dispositivos (R$ 199/mês)**: filtra micro-contas que consomem suporte e não renovam.
- **Anual com ~2 meses grátis**: melhora caixa e trava churn.
- **Piloto sem cartão e sem prazo fixo, com onboarding assistido obrigatório** (call de 30 min instalando os 5 primeiros agentes): resolve ativação e qualifica o lead ao mesmo tempo. (A oferta pública de "14 dias" foi descontinuada em 08/2026; a duração do piloto é controle comercial, caso a caso.)
- **Billing manual (Pix/boleto + NFS-e) até ~30 contas**: 10–30 faturas/mês são 2 h de trabalho; integrar gateway custa 2–4 semanas de dev. O financeiro manual ainda funciona como sensor de churn. Nota: a retenção é fixa no MVP, então a diferenciação do Pro no lançamento é suporte + acesso prioritário ao que vier na v1.1 — diferenciação por retenção entra junto com o gateway.
- Unit economics alvo: ticket médio ~40 devices × R$ 25 ≈ **R$ 1.000/mês por conta**; margem bruta > 85% (eventos de metadados são leves); CAC < 3 meses de receita.

### 6.4 Métricas (instrumentar desde F2)

| Métrica | Alvo | Por quê |
|---|---|---|
| TTFD (criação do tenant → 1º evento) | < 24 h (idealmente na call) | Conversão do trial nasce aqui |
| % de trials com ≥ 5 devices na semana 1 | ≥ 60% | Melhor preditor de conversão; < 5 devices = piloto de brincadeira |
| WAU de gestores por tenant | ≥ 75% das semanas | O dashboard precisa virar hábito; 2 semanas sem login = risco, acionar CS |
| Exports/mês por tenant | crescente | Proxy de uso em decisões reais de RH |
| Net device expansion | NRR > 100% só com devices | Expansão sem venda nova |
| Churn lógico mensal de contas | < 3% após estabilização | — |
| **Agentes silenciosos** (> 7 dias sem heartbeat, sem volta) | alarme automático para CS | Cliente desinstalando aos poucos é churn em câmera lenta — o sinal mais precoce que existe |

### 6.5 Riscos honestos de negócio

1. **Imagem — "software de espionar funcionário"**: o maior risco da categoria no Brasil. Um único caso público de mau uso por um cliente queima a marca. Mitigação: ícone inegociável, página pública do que NUNCA coletamos, contrato obrigando o cliente a informar os funcionários (transferência de responsabilidade documentada), recusa registrada de keylog/screenshot.
2. **Concentração de receita**: com 5–10 contas, perder 1 conta de 100 devices = 20–30% da MRR. Regra: nenhuma conta acima de ~25% da MRR sem plano de diversificação; preferir 10 contas de 40 devices a 2 de 200.
3. **Suporte como gargalo**: parque Windows de PME é heterogêneo (AV agressivo, proxy com inspeção TLS, máquina fora de domínio, usuário sem admin). Cada instalação falha consome horas de um time de 2. Mitigação: MSI robusto com log local, kit de instalação para a TI do cliente, diagnóstico de proxy/TLS claro no agente (é o principal motivo previsto de chamado "agente não reporta").
4. **Antivírus/code signing**: o agente tem assinatura comportamental de spyware (enumera janelas, lança processo em sessão de usuário, roda como SYSTEM). Falso positivo de AV é **quase certo** sem Authenticode (idealmente EV) + submissão prévia ao Microsoft Defender. O certificado EV é emitido para a razão social e tem semanas de lead time — por isso o nome do produto é pergunta que **bloqueia o início** (seção 9).
5. **Dependência da plataforma Windows**: mudança de API/política da Microsoft pode quebrar coleta ou reputação do binário de um dia para o outro — auto-update funcionando antes do GA é o seguro contra isso (por isso está na F4, não no "depois").
6. **Churn estrutural de PME**: a própria empresa cliente encolhe ou fecha. O modelo exige motor de aquisição contínuo (conteúdo/SEO "monitoramento LGPD", parcerias com MSPs e contabilidades), não só indicação.
7. **Pressão comercial por features invasivas**: virá cedo. Sem política de recusa escrita, a linha vermelha será corroída negócio a negócio.

---

## 7. Roadmap e estimativas

Estimativas em semanas-calendário **com 2 devs experientes .NET em tempo integral** (com 1 dev: 8–9 meses — F1/F2 não paralelizam bem sozinho). Critérios de pronto completos no `PROMPT-DESENVOLVIMENTO.md`; aqui o resumo verificável:

| Fase | Conteúdo | Critério de pronto (resumo) | Estimativa |
|---|---|---|---|
| **F0 — Fundação** | Monorepo, CI/CD, esqueleto multi-tenant (`tenant_id` desde a 1ª migration), auth do portal, ambientes | `git push` → deploy em staging; criar tenant + logar + convidar funciona; **teste automatizado prova que tenant A não lê dado do tenant B** | 2 sem |
| **F1 — Ingestão fim-a-fim** | Agente mínimo (serviço+helper, janela ativa/sessão/idle), enroll, heartbeat 60 s, batch 30 s, ingest idempotente, fila offline básica (WAL + retry; caps de 7 d/50k/100 MB e expurgo FIFO finalizam na F4) | Instalar agente numa VM limpa → eventos no banco do tenant certo em < 2 min; derrubar a rede 10 min → eventos chegam depois **sem perda** | 3 sem |
| **F2 — Pipeline + Timeline** | Intervalização (máquina de estados, idle retroativo, gaps, off_clean vs no_data), timeline device/dia no portal, lista de devices | Timeline de um dia real de uso bate com a realidade observada (validação manual de 8 h); **demo de 10 min possível para um estranho** | 4 sem |
| **F3 — Dashboard + relatórios + categorias** | Dashboard, agregados diários, categorias + catálogo, relatório CSV, timeline de equipe, **seed do tenant demo** (30 devices, 60 dias sintéticos no pipeline real), relatório interno de devices cobráveis | "Quem da equipe ficou mais ocioso esta semana?" em < 3 cliques; CSV abre certo no Excel pt-BR; tenant demo navegável ponta a ponta | 4 sem |
| **F4 — Hardening + LGPD** | MSI silencioso/GPO, auto-update canal único, code signing, NOTICE_ACK, **DSR (export/exclusão de titular)**, purga de retenção, log de auditoria, painel de saúde, backup/restore testado | Atualizar 10 agentes remotamente; MSI via GPO em máquina de domínio; expurgo comprovado; restore executado em staging; **checklist da seção 5.7 (produto) completo** | 5 sem |
| **F5 — Piloto** | 2–3 empresas amigas, 30–60 dias de uso real; correções de campo; materiais da seção 8; preço validado em proposta real | 2 pilotos com ≥ 10 devices rodando 30 dias com < 5% de devices silenciosos; ≥ 1 piloto converte em contrato pago | 6 sem (parcialmente paralela ao fim da F4) |
| **GA** | Site no ar, contrato padrão, cobrança manual, suporte | Primeira conta paga não-amiga fechada | — |

**Total: ~20–24 semanas (5–6 meses) até GA com 2 devs.**

**Caminho crítico:** F0 → F1 → F2 → F3 → F4 (MSI + auto-update) → F5. Não comece F3 antes de validar a F2 com dados reais — dashboard bonito sobre pipeline de intervalos errado é retrabalho garantido. **A F2 é o coração técnico do produto e o maior risco de estouro de prazo** (regras de borda: lock vs. idle, idle retroativo, múltiplas sessões, relógio errado na máquina do cliente) — reserve buffer mental para ela, não para o dashboard.

**Marcos de venda:**
- **Fim da F2** = primeira demo a prospects (com dados da própria equipe de vocês);
- **Fim da F3** = demo com tenant seed para qualquer prospect (ninguém vê dados reais de outro cliente);
- **Fim da F4** = pode instalar na máquina de um cliente de verdade (antes disso, jamais).

---

## 8. Preparação além do código

Trabalho seu (dono do produto/comercial), em paralelo às fases de dev. Nada disso é opcional — sem material, cada venda vira projeto.

| Entregável | Conteúdo mínimo | Pronto até |
|---|---|---|
| **Landing page** | Proposta de valor em 1 frase, 3 telas (do tenant demo), seção "Transparência e LGPD" (o que coletamos / o que NUNCA coletamos), preços públicos, CTA "agendar demonstração"; domínio + e-mail profissional | fim da F3 |
| **One-pager comercial (PDF)** | Dor → solução → 3 prints → preço → diferenciais (LGPD, real, suporte BR); versão encaminhável no WhatsApp | fim da F3 |
| **Script de demo (10 min)** | Roteiro fixo sobre o tenant seed: dashboard → drill-down em 1 pessoa → timeline de 1 dia → CSV → painel de agentes → fechar na transparência LGPD. + As 5 objeções e respostas: é legal? o funcionário sabe? pega o que digito? pesa na máquina? e home office? | fim da F3 |
| **Kit de instalação para a TI do cliente** | Pré-requisitos (SO, portas/domínios de saída), passo a passo MSI manual e via GPO, troubleshooting dos 5 erros mais comuns (AV, proxy TLS, etc.), como validar que o agente reporta | fim da F4 |
| **Kit LGPD para o cliente** | Comunicado interno modelo, Termo de Ciência, FAQ jurídico, descrição técnica dos dados (para o DPO do cliente) — **com revisão de advogado** | fim da F4 |
| **Contrato/termos** | Termos de uso + contrato B2B + DPA + SLA simples, revisados por advogado com prática LGPD/trabalhista | antes do 1º piloto pago (F5) |
| **Processo de suporte** | Canal único (e-mail + WhatsApp Business), SLA público (4 h úteis Pro / 1 dia útil Essencial), board de tickets simples, página de status básica | início da F5 |
| **Processo de cobrança manual** | Rotina mensal: contagem de devices cobráveis (relatório interno — backlog F3), NFS-e (contratar contabilidade que emita NFS-e de SaaS), Pix/boleto, régua de inadimplência (D+3 e-mail, D+10 contato, D+20 suspensão) | início da F5 |
| **Pipeline de vendas fundador-led** | Lista de 50 empresas-alvo da rede, CRM leve, meta: 10 demos no 1º mês pós-GA | GA |

---

## 9. PERGUNTAS ABERTAS que só você pode responder

Consolidadas das cinco análises, deduplicadas, sem as já resolvidas pela tabela canônica. Organizadas por urgência.

### BLOQUEIA INÍCIO (responda na semana 1)

1. **Nome do produto e marca.** Afeta domínio, contrato, registro de marca e — crítico — o **certificado de code signing EV**, que é emitido para a razão social e leva semanas para sair. Sem ele, o agente é falso positivo de AV quase certo. Decidir nome → iniciar emissão do certificado imediatamente.
   **→ RESPONDIDA (09/06/2026): o produto se chama "+351 Monitor".** O certificado pode (e deve) ser emitido já — ele sai em nome da razão social, não do produto. Empresa definida: CNPJ 60.352.161/0001-76 — iniciar a emissão do certificado Authenticode (idealmente EV) para esta razão social. Pendências derivadas: registrar domínio (o caractere "+" não existe em domínio — ex.: `351monitor.com.br` / `mais351.com.br`) e verificar disponibilidade de marca no INPI.
2. **Os 2 devs estão de fato em tempo integral pelos ~6 meses até o GA**, ou dividem tempo com outros produtos da empresa? As estimativas mudam quase linearmente (com 1 dev: 8–9 meses).
   **→ RESPONDIDA (09/06/2026): o desenvolvimento será feito pelo João usando o Claude Code** (sem segundo dev). Implicação: o gargalo deixa de ser escrever código e passa a ser **revisar, testar e validar** o que foi gerado — manter os critérios de "pronto" de cada fase como gate inegociável; tratar as estimativas como faixas a recalibrar ao fim da F1.
3. **Cloud definitiva e orçamento de infra mensal.** A tabela canônica fixa região BR com Postgres gerenciado (ex.: Azure Brazil South); falta a escolha final Azure vs. AWS (afeta DPA e lista de subprocessadores) e o teto de custo aceitável (~R$ 3–5 mil/mês no perfil recomendado). A F0 provisiona ambientes — precisa disso decidido.
   **→ RESPONDIDA PARCIALMENTE (09/06/2026): dev/staging na Hostinger por custo** — VPS com PostgreSQL em Docker (datacenter São Paulo, coerente com a Q4). Condição: o banco É PostgreSQL (a hospedagem compartilhada da Hostinger só tem MySQL — não serve; a spec não muda). Em VPS autogerido, backups/PITR viram responsabilidade nossa — script de backup diário + teste de restore entram na F0/F4. A cloud de PRODUÇÃO (gerenciada) fica para decidir antes da F5/GA.
4. **Compromisso público de datacenter no Brasil** (selo no site, cláusula em contrato) ou apenas prática interna? Recomendação forte: compromisso público — é o argumento de venda mais barato do produto. Afeta configuração de backup/réplicas desde F0.
   **→ RESPONDIDA (09/06/2026): SIM, compromisso público.** Todos os ambientes (incl. backups e réplicas) devem ficar em datacenter no Brasil; vira cláusula de DPA e selo no site.
5. **Orçamento jurídico e quem contrata.** Parecer LGPD + contrato + DPA + kit revisados por advogado especializado (LGPD/trabalhista) custam dinheiro real e têm lead time — a revisão precisa estar pronta até fim da F4/início da F5. Contratar cedo, não na véspera.
   **→ RESPONDIDA (09/06/2026): advogado da família fará sem custo.** Atenção ao que importa mais que o custo: (a) a especialidade necessária é **LGPD + trabalhista** — se o advogado atuar em outra área, pedir que valide os pontos críticos com um colega da especialidade; (b) o lead time continua valendo — entregar o briefing (seção 5 deste doc + `docs/design/04-lgpd-seguranca.md`) **agora**, com prazo combinado para o kit até o fim da F4.
6. **SO mínimo confirmado: Windows 10 1809+.** Existe demanda real da sua base-alvo por Windows 7/8.1 (ainda comum em PME BR) que justificasse build legado? Recomendação: não — confirme e siga.
   **→ RESPONDIDA (09/06/2026): Windows 10 1809+ e Windows 11 mantidos.** Cogitou-se só-Win11 "se facilitar", mas não facilita: .NET 8 e as APIs do agente são idênticas nos dois — restringir só encolheria o mercado. Custo real é apenas a matriz de testes da F4.

### BLOQUEIA GA (responda durante F3–F4)

7. **Quem são as 2–3 empresas amigas do piloto (F5)** e qual contrapartida: 50–70% de desconto por 3 meses vs. gratuidade, em troca de feedback quinzenal + logo/case. Comece a conversa no fim da F2 (quando já existe demo).
8. **Encarregado (DPO): interno (sócio acumulando) ou serviço externo?** Precisa estar nomeado e publicado com canal `privacidade@` antes do primeiro cliente.
9. **Prazo contratual de notificação de incidente ao controlador: 24 h ou 48 h?** Mais curto vende melhor, mas cria obrigação operacional para time sem plantão. Precisa estar no DPA. (Lembre: o controlador tem 3 dias úteis para a ANPD.)
10. **Posição final e escrita sobre screenshots.** A tabela canônica diz: fora do MVP, decisão de marca, talvez nunca. Prospects vão condicionar compra a isso já nas primeiras demos — você está disposto a perder essas vendas? A resposta vira política comercial escrita, não improviso por negociação.
11. **BYOD: recusa formal em contrato?** Recomendação: sim, equipamento corporativo apenas — risco trabalhista e LGPD de máquina pessoal é alto demais para o MVP.
12. **Canal MSP/revendas: venda 100% direta nos primeiros 12 meses, ou desconto de canal desde o GA?** MSPs são canal natural para PME BR (e quem instala o agente em muitos clientes) — mas canal mal desenhado canibaliza preço cedo.
13. **Meta comercial do ano 1**: venda 100% fundador-led ou contratar SDR/vendedor após o piloto? Define o motor de aquisição e o caixa.

### Pode esperar (mas registre a decisão quando tomar)

14. **RDS/Citrix**: cortado do MVP (não suportado). Reavaliar para v2 se contabilidades com ERP via Terminal Server aparecerem com frequência no funil — afeta arquitetura de coleta por sessão e modelo de cobrança.
15. **SSO corporativo (Entra ID/Google)**: algum cliente âncora exige? Se não, OAuth social na v1.1 e SAML só com enterprise real batendo na porta.
16. **Funcionário ver o próprio histórico (autosserviço)**: diferencial de transparência interessante, mas muda a arquitetura de auth do portal — decidir com calma pós-MVP.
17. **i18n**: o spec mestre já fixou pt-BR hardcoded (corte explícito de i18n). Vale antecipar a externalização de strings na F2 (reabrindo o corte de i18n) por alguma ambição de mercado ex-BR? Default: não.
18. **Curadoria do catálogo global de apps→categoria**: quem mantém e com que processo? Precisa de resposta operacional até a F3.
19. **Retenção estendida / diferenciada por plano**: fixa no MVP; vira alavanca de pricing junto com o gateway de billing (v1.1).
20. **Modo "privacy-first"** (tenant opera só com agregados, sem drill-down individual): vendável a clientes maduros; avaliar pós-MVP.
21. **Tamper-resistance extra** (senha de desinstalação, DACL no serviço): o padrão do MVP (admin para parar + AGENT_TAMPER + gap visível) atende? Só mude com requisito comercial concreto.
22. **Demanda por deploy on-premises** (bancos, saúde): se aparecer, o Docker Compose + banco único já é quase um artefato de produto — mas não persiga isso agora.
23. **Integração futura com ponto homologado**: a resposta de produto para quem pede cálculo de horas é "integração com sistema de ponto homologado", nunca cálculo próprio — se a demanda se repetir, avaliar parceria.

---

## 10. Como começar (semana 1)

Passo a passo, na ordem:

1. **Responda as 6 perguntas "BLOQUEIA INÍCIO"** (seção 9). A mais urgente é o nome — destrave o certificado EV hoje, não na F4: emita para a razão social, processo leva semanas e o code signing é gate da F4.
2. **Contrate (ou pelo menos selecione) o advogado** LGPD/trabalhista. Entregue a ele a seção 5 deste documento e o `docs/design/04-lgpd-seguranca.md` como briefing. O kit revisado precisa existir até o fim da F4 — lead time corre desde já.
3. **Provisione a cloud BR**: conta, VM de staging, Postgres gerenciado, bucket de backup — tudo conferido para residência nacional (incluindo réplicas). Guarde os comprovantes de configuração: viram anexo de DPA.
4. **Crie o monorepo** (agente + backend + portal + infra no mesmo repositório, como o spec define) e configure o CI/CD mínimo: build + testes + deploy automático em staging.
5. **Abra o `PROMPT-DESENVOLVIMENTO.md` na F0 e siga** o critério de pronto: esqueleto multi-tenant com `tenant_id` desde a primeira migration, auth do portal, e — inegociável — **o teste automatizado de isolamento cross-tenant passando no CI antes de qualquer feature**. Esse teste é o seguro contra o incidente existencial do produto (empresa A vendo dados da empresa B).
6. **Registre formalmente a derrogação do REQ-PRIV-07** (Manager-por-equipe adiado para v1.1): um parágrafo com decisão, justificativa e data, num `DECISOES.log` ou ADR no repo. Faça o mesmo com as próximas decisões de produto — este documento é o primeiro registro, não o último.
7. **Comece a lista das 50 empresas-alvo** e identifique candidatas ao piloto (F5) já — a conversa amadurece em paralelo ao código, e no fim da F2 você precisa de gente para ver a primeira demo.
8. **Combine com o time a regra da fonte única**: números operacionais e contratos saem da tabela canônica (seção 3 / spec). Os documentos de design 01–05 contêm contradições já resolvidas — são referência de raciocínio, não de valores. A crítica (06) explica o porquê de cada resolução.

O risco nº 1 do projeto não é errar uma API — é começar a F1 com cada dev implementando uma spec diferente (era exatamente o estado dos documentos antes da reconciliação), ou chegar ao GA com o produto pronto e o jurídico/comercial não. Este documento existe para que nenhuma das duas coisas aconteça.

Boa construção.
