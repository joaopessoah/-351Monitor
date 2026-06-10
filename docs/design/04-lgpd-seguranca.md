## Privacidade (LGPD) e Segurança de Produto

### 1. Enquadramento LGPD

#### 1.1 Papéis dos agentes de tratamento

| Agente | Papel LGPD | Implicações práticas |
|---|---|---|
| Empresa cliente (empregadora) | **Controladora** (art. 5º, VI) | Decide finalidade e meios: quem monitorar, quais relatórios usar, política interna, comunicação aos empregados, atendimento a direitos dos titulares, comunicação à ANPD/titulares em incidente. |
| Nós (fornecedor SaaS) | **Operadora** (art. 5º, VII) | Tratamos dados **exclusivamente sob instrução documentada da controladora**. As instruções são materializadas no contrato + DPA + configurações do tenant no portal (a configuração do tenant É a instrução documentada — registrar em log de auditoria toda mudança de configuração de coleta). |
| Provedor de nuvem (Azure/AWS) | **Suboperador** | Deve constar no DPA (autorização genérica com lista publicada + direito de objeção da controladora). |

Consequências de sermos operadora:
- **Não podemos usar os dados para finalidades próprias** (ex.: treinar modelos, benchmarks comerciais entre clientes) sem previsão contratual explícita. Telemetria de produto anonimizada/agregada deve estar prevista no DPA.
- **Responsabilidade solidária possível** (art. 42, §1º): respondemos se descumprirmos a LGPD ou as instruções da controladora. Por isso o produto deve **impedir tecnicamente** configurações ilegais, não apenas desaconselhá-las.
- DPA (acordo de tratamento de dados) é **pré-condição de onboarding** de qualquer tenant — sem DPA assinado, tenant não é provisionado.

#### 1.2 Base legal para o monitoramento de empregados

- Base típica: **legítimo interesse da controladora** (art. 7º, IX c/c art. 10) — gestão de produtividade, segurança da informação e gestão de ativos de TI — **condicionado a**: (a) teste de balanceamento documentado (LIA), (b) transparência total ao empregado, (c) proporcionalidade/minimização, (d) expectativa legítima do titular (equipamento corporativo + política comunicada).
- Bases acessórias possíveis: **execução de contrato** (de trabalho) para controle de jornada quando aplicável; **cumprimento de obrigação legal** em casos pontuais (ex.: registro de ponto, se o cliente usar os dados para isso — atenção: usar para ponto puxa a Portaria 671/2021 do MTE, fora do escopo do MVP; documentar que o produto NÃO é registrador de ponto).
- **Consentimento NÃO é base adequada** na relação de emprego: o art. 5º, XII exige manifestação **livre**; a subordinação e a assimetria de poder na relação empregado-empregador viciam a liberdade (o empregado não pode recusar sem temer represália, e a revogação a qualquer tempo — direito inerente ao consentimento — inviabilizaria a operação). O Termo de Ciência que fornecemos é instrumento de **transparência/ciência**, não de consentimento — o texto do termo deve dizer isso explicitamente.
- Quem define e sustenta a base legal é a **controladora**; nós fornecemos o modelo de LIA e a documentação técnica que a suporta.

#### 1.3 Natureza dos dados coletados

- Dados coletados (apps abertos, janela ativa com título, uptime, login/logoff/lock/unlock, ociosidade) são **dados pessoais** — vinculados a usuário identificado (login Windows/AD, hostname, e-mail). **Não são dados sensíveis por natureza** (art. 5º, II).
- **Risco residual crítico: títulos de janela podem vazar dados sensíveis ou de terceiros**. Exemplos: `"Resultado_Exame_HIV.pdf - Adobe Reader"`, `"Sindicato dos Metalúrgicos - Filiação — Google Chrome"`, `"Consulta psicólogo - WhatsApp Web"`, nomes de clientes/pacientes em assuntos de e-mail.
- Mitigações de produto (obrigatórias no MVP):
  1. **Mascaramento de títulos configurável por tenant** com três níveis: `FULL_TITLE` (título completo), `APP_ONLY` (só nome do app/exe, título descartado **no agente**, nunca enviado), `MASKED_PATTERNS` (título enviado, mas trechos que casam com lista de padrões/regex são substituídos por `***` no agente, antes do envio). **Default do produto: `MASKED_PATTERNS` com lista padrão de fábrica** (termos de saúde, sindicais, religiosos, financeiros pessoais; navegação em sites de banco, saúde, e-mail pessoal).
  2. Lista de **apps na blocklist de coleta de título** por padrão: navegadores em janela anônima/privada (detectável pelo título "InPrivate"/"anônima"), apps de saúde, apps bancários — coleta vira `APP_ONLY` automaticamente.
  3. Mascaramento aplicado **no agente** (client-side): título sensível nunca trafega nem persiste no servidor.
  4. Orientação documentada à controladora (no manual de configuração e no LIA modelo) sobre o risco e a responsabilidade de configurar adequadamente.

### 2. Prática trabalhista brasileira → requisitos de produto

Panorama consolidado da jurisprudência (TST e TRTs, visão geral): o monitoramento de **ferramentas corporativas** (equipamento da empresa, e-mail corporativo, software da empresa) é aceito quando há (i) **política clara e prévia**, (ii) **ciência inequívoca do empregado**, (iii) **proporcionalidade** (meio menos invasivo que atende a finalidade). Geram condenações por dano moral: monitoramento **oculto**, acesso a **conteúdo pessoal** (e-mail pessoal, conversas privadas), revista de dispositivos pessoais, e uso de dados para exposição vexatória do empregado. Tradução em requisitos de produto:

| ID | Requisito | Detalhe |
|---|---|---|
| REQ-PRIV-01 | Ícone visível obrigatório | Ícone na bandeja do sistema (systray) sempre visível enquanto o agente coleta. **Não existe flag de ocultação no binário** — não é "desabilitado por padrão", é inexistente por arquitetura. Tooltip: "Monitoramento corporativo ativo — clique para detalhes". |
| REQ-PRIV-02 | Tela "O que é coletado" no agente | Clique no ícone abre janela local listando: dados coletados, dados NÃO coletados, finalidade, nome da empresa controladora, link da política de privacidade do tenant, e estado atual (coletando / pausado / fora de janela de coleta). |
| REQ-PRIV-03 | Aviso no primeiro logon | Na primeira sessão de cada usuário em máquina monitorada, exibir notificação (toast + janela) informando o monitoramento, com botão "Entendi" (registro do evento `AgentNoticeAcknowledged` com timestamp — vira evidência de ciência para a controladora). Não é consentimento; é ciência. |
| REQ-PRIV-04 | Termo de Ciência modelo | Entregamos modelo de Termo de Ciência do Empregado (ver §6) e o onboarding do tenant exige que o admin marque declaração: "Declaro que os empregados monitorados foram formalmente cientificados" (checkbox com log de auditoria). |
| REQ-PRIV-05 | Defaults conservadores | De fábrica: mascaramento `MASKED_PATTERNS` ativo; coleta restrita à **janela de expediente configurada** (default 07:00–20:00, dias úteis — fora dela o agente registra apenas uptime/login, sem janelas/apps); títulos de navegação privada nunca coletados. Ampliar exige ação explícita do admin (logada). |
| REQ-PRIV-06 | Sem captura de conteúdo | Nenhuma versão do agente contém código de keylogging, screenshot, webcam, microfone ou leitura de arquivos. Auditável por terceiros (ver §7). |
| REQ-PRIV-07 | Granularidade de acesso no portal | Perfis: `TenantAdmin`, `Manager` (vê só sua equipe), `Viewer-Agregado` (só dashboards agregados, sem drill-down individual). Permite ao cliente implementar proporcionalidade interna (RH vê agregado; gestor direto vê individual). |
| REQ-PRIV-08 | Sem ranking público de funcionários | O portal não oferece "ranking de produtividade" gamificado/exposto; relatórios individuais são acessíveis apenas a perfis autorizados, com acesso auditado. |

### 3. Princípios LGPD → requisitos concretos

#### 3.1 Minimização (art. 6º, III)

**Coletamos (lista exaustiva e fechada — qualquer adição passa por revisão de privacidade):**

| Dado | Evento/Entidade | Observação |
|---|---|---|
| Identificação da máquina e usuário | `Device {deviceId, hostname, domainUser, tenantId}` | Vínculo usuário↔pessoa é mantido pela controladora |
| Sessão: login/logoff/lock/unlock | `SessionEvent {type: Logon\|Logoff\|Lock\|Unlock, timestamp}` | Fonte: eventos do Windows |
| Máquina ligada/desligada | `PowerEvent {type: Boot\|Shutdown\|Sleep\|Resume, timestamp}` | |
| Janela/app ativo | `ForegroundEvent {exeName, appDisplayName, windowTitle(mascarável), startTs, endTs}` | Amostragem por mudança de foco + heartbeat 30 s; título sujeito a §1.3 |
| Processos com janela aberta | `ProcessSnapshot {[exeName, appDisplayName]}` a cada 5 min | Sem linha de comando, sem argumentos (argumentos vazam segredos/caminhos) |
| Ociosidade | `IdleEvent {idleStartTs, idleEndTs}` | Threshold default 5 min sem input; coletamos apenas o **fato** da ausência de input, jamais o input |
| Versão/saúde do agente | `AgentHeartbeat {agentVersion, lastSeenTs}` | Telemetria operacional |

**NUNCA coletamos (compromisso público, na política de privacidade e no contrato):** teclas digitadas (keylogging) ou qualquer conteúdo de input; screenshots/gravação de tela (MVP); conteúdo de arquivos, e-mails, mensagens ou área de transferência; áudio/microfone/webcam; geolocalização; URLs completas com query string (pós-MVP, se houver coleta de navegação, será **somente domínio**); senhas ou hashes de credenciais do usuário; dados de máquinas/perfis não provisionados pelo tenant.

#### 3.2 Finalidade (art. 6º, I)

Finalidades declaradas e fechadas: (1) **gestão de produtividade e de uso do tempo de trabalho**; (2) **gestão de ativos e licenças de TI** (inventário de software em uso, máquinas ociosas); (3) **segurança da informação** (uso de software não homologado). Qualquer uso fora disso (ex.: prova em processo disciplinar) é decisão e responsabilidade da controladora — documentar no DPA. O produto não oferece e não oferecerá funcionalidades cuja finalidade primária seja vigilância de conteúdo.

#### 3.3 Transparência (art. 6º, VI; art. 9º)

- **Página pública de privacidade do produto** (`/privacidade` no site): o que o agente coleta e não coleta, papéis (operadora/controladora), subprocessadores, residência dos dados, retenção default, canal de contato do nosso Encarregado (DPO).
- **Kit de transparência para a controladora**: texto pronto para intranet, slide de comunicação interna, FAQ para os empregados.
- **Tela do agente** (REQ-PRIV-02) como transparência em tempo real, no endpoint humano da coleta.

#### 3.4 Retenção e eliminação (art. 15 e 16)

| Categoria | Default | Configurável (faixa) | Mecanismo |
|---|---|---|---|
| Eventos brutos (`ForegroundEvent`, `SessionEvent`, `IdleEvent`, snapshots) | **90 dias** | 30–180 dias por tenant | Job diário `DataRetentionPurgeJob` (hard delete + log da execução) |
| Agregados diários/semanais por usuário (horas ativas, top apps, % ocioso) | **24 meses** | 12–36 meses | Mesmo job |
| Logs de auditoria de acesso (§4) | 24 meses fixo | não reduzível pelo tenant | Obrigação de accountability |
| Backups | ciclo máx. 35 dias | — | Dados purgados saem do ciclo de backup em até 35 dias; documentar no DPA |

- Agregação roda em pipeline diário; após agregado, o bruto vive só até o fim da janela de retenção.
- Tela `Admin > Privacidade > Retenção` mostra a política vigente e a data da última purga.

#### 3.5 Direitos do titular (arts. 18–19)

O titular (empregado) exerce direitos **perante a controladora**; nós damos as ferramentas para a controladora cumprir em até 15 dias (prazo do art. 19, II):

- **Tela** `Admin > Privacidade > Dados do Titular`: buscar por usuário/dispositivo → ações **Exportar** e **Excluir**.
- **Endpoints** (autenticados, perfil `TenantAdmin`, sempre auditados):
  - `POST /api/v1/privacy/subjects/{subjectId}/export` → gera pacote assíncrono (JSON + CSV legível) com todos os eventos e agregados do usuário; download com link expirante (72 h).
  - `DELETE /api/v1/privacy/subjects/{subjectId}/data?scope=all|raw` → exclusão irreversível (com confirmação dupla e motivo registrado); emite `DataErasureReceipt {requestId, executedAt, recordCounts}`.
  - `DELETE /api/v1/privacy/devices/{deviceId}/data` → mesmo fluxo por dispositivo.
- Exclusão de um titular **não** apaga os agregados anonimizados de equipe já computados (documentar essa regra no DPA e na política).

#### 3.6 Término de contrato (offboarding de tenant)

- Ao encerrar o contrato: portal entra em modo somente-exportação por **30 dias**; a controladora baixa o acervo completo (`POST /api/v1/privacy/tenants/{tenantId}/full-export`).
- No dia 31: exclusão definitiva de todos os dados do tenant (incl. saída dos backups no ciclo de 35 dias) + emissão de **Certificado de Eliminação de Dados** assinado.
- Tokens de todos os devices do tenant revogados no dia do encerramento (agentes param de coletar e exibem estado "contrato encerrado").

### 4. Segurança técnica (requisitos exigíveis no MVP)

| Área | Requisito |
|---|---|
| Criptografia em trânsito | TLS 1.2 mínimo, TLS 1.3 preferencial, em agente→API e navegador→portal. HSTS no portal. Sem fallback HTTP. Certificate pinning no agente é opcional/avaliar (complica rotação). |
| Criptografia em repouso | Banco com TDE (SQL Server/Azure SQL) ou storage encryption (PostgreSQL em disco gerenciado criptografado); blobs/exports criptografados (SSE); chaves em cofre gerenciado (Azure Key Vault). |
| Identidade do agente | Enrollment por **chave de provisionamento por tenant** (rotacionável) → device recebe token próprio (certificado de device ou JWT longo com refresh). **Revogação individual e em massa** via portal (`Admin > Dispositivos > Revogar`); token revogado = agente para de coletar e descarta buffer local. Buffer offline local criptografado (DPAPI) com teto de 7 dias. |
| Segregação multi-tenant | `tenantId` obrigatório em toda entidade e toda query (global query filter no EF Core); **Row-Level Security** no banco como segunda camada; **testes automatizados de isolamento cross-tenant no CI** (suite que tenta acessar dados do tenant B autenticado no tenant A — gate de release). |
| Auditoria de acesso a dados pessoais | Evento `AuditLog {auditId, tenantId, actorUserId, action, targetSubjectId/deviceId, resource, timestamp, sourceIp}` para: visualização de relatório individual, export, exclusão, mudança de configuração de coleta/retenção, mudança de permissões, revogação de device. Tela `Admin > Auditoria` com filtro e export CSV. Imutável (append-only), retenção 24 meses. Responde "quem do portal viu o relatório de quem, quando". |
| Autenticação do portal | Senhas: mínimo 12 caracteres, verificação contra listas de senhas vazadas, sem expiração forçada arbitrária; **MFA TOTP obrigatório para `TenantAdmin` e nossa equipe interna**, opcional-incentivado para demais; lockout progressivo; sessão com timeout. SSO (Entra ID/SAML) no roadmap pós-MVP como diferencial enterprise. |
| Acesso interno (nós) | Acesso de suporte a dados de tenant só via break-glass justificado e auditado (mesmo `AuditLog`, `actorType=VendorStaff`); cláusula no DPA. |
| Gestão de vulnerabilidades | SCA/dependabot no CI; SAST básico; imagem do agente assinada (code signing — obrigatório também para reputação antivírus); pentest externo anual + antes do GA; canal `security@` divulgado e política de disclosure. |
| Backups | Automatizados, criptografados, região Brasil, teste de restore trimestral documentado. |
| Resposta a incidentes | Plano escrito com papéis; **notificação à controladora em prazo contratual (propor 24 h após confirmação, máx. 48 h)** com informações mínimas (natureza, titulares afetados, medidas); a **controladora** comunica ANPD e titulares — o regulamento da ANPD (Resolução CD/ANPD nº 15/2024) exige comunicação de incidentes relevantes em **3 dias úteis**, logo nosso prazo de avisar a controladora deve deixar folga. Registro interno de todos os incidentes, mesmo não relevantes. |
| Hardening do agente | Serviço Windows com privilégio mínimo necessário; config assinada vinda do servidor (anti-tampering básico); sem endpoints de escuta locais abertos na rede. |

### 5. Residência de dados

- A LGPD **não obriga** hospedagem no Brasil; transferência internacional é lícita com salvaguardas (arts. 33–36 + Resolução CD/ANPD nº 19/2024, cláusulas-padrão contratuais). Porém hospedar fora adiciona atrito jurídico em toda venda B2B (anexo de transferência internacional, questionários de procurement).
- **Decisão recomendada para o MVP: hospedar 100% no Brasil** — Azure **Brazil South (São Paulo)**, alinhado ao stack .NET da equipe (App Service/AKS + Azure SQL + Key Vault + Blob); alternativa equivalente AWS `sa-east-1`. Backups e réplicas também em região brasileira (atenção: algumas redundâncias geográficas default da nuvem replicam para fora — configurar LRS/ZRS dentro do país ou par brasileiro).
- Transformar em argumento comercial explícito: selo "Dados hospedados no Brasil" no site e no material de vendas; declarar região e subprocessadores na página de privacidade.

### 6. Artefatos não-código (entregáveis junto com o MVP)

| # | Entregável | Descrição | Quando |
|---|---|---|---|
| 1 | Política de Privacidade do produto | Página pública: coleta, papéis, subprocessadores, retenção, residência, contato do Encarregado | Antes do primeiro cliente |
| 2 | Modelo de DPA (Acordo de Tratamento Operador↔Controlador) | Anexo contratual padrão: instruções, suboperadores, segurança, incidentes (prazo de notificação), auditoria, término/devolução em 30 dias, responsabilidades | Antes do primeiro contrato |
| 3 | Modelo de Termo de Ciência do Empregado | Documento que o cliente adapta e colhe assinatura/aceite; texto deixa claro que é ciência, não consentimento | Kit de onboarding do tenant |
| 4 | Modelo de Política de Uso Aceitável (AUP) | Política interna de uso de recursos de TI para o cliente adaptar — sustenta a expectativa de privacidade reduzida em equipamento corporativo | Kit de onboarding |
| 5 | Registro de Operações de Tratamento (RoT) — nosso | Art. 37: inventário das nossas operações como operadora (e como controladora dos dados de conta/billing dos usuários do portal) | Antes do lançamento |
| 6 | Modelo de LIA (avaliação de legítimo interesse) | Template preenchível pelo cliente: finalidade, necessidade, balanceamento, salvaguardas (cita as features: mascaramento, janela de expediente, perfis de acesso) | Kit de onboarding |
| 7 | Modelo de RIPD (Relatório de Impacto) simplificado | Para clientes cujo DPO exigir; reaproveita a LIA + descrição técnica | Pós-MVP imediato |
| 8 | FAQ de privacidade para o site | Perguntas dos dois públicos: empresa compradora e funcionário monitorado ("isso lê o que eu digito?" → não, e por quê é verificável) | Antes do lançamento |
| 9 | Kit de comunicação interna para o cliente | Texto de intranet/e-mail + slide explicando o monitoramento aos empregados | Kit de onboarding |
| 10 | Plano de Resposta a Incidentes + runbook de notificação | Interno, com modelo de comunicação à controladora | Antes do lançamento |
| 11 | Nomeação do Encarregado (DPO) + canal | Encarregado nosso (pode ser acumulado por sócio/advogado externo), e-mail `privacidade@` publicado | Antes do lançamento |

### 7. Riscos de produto e reputação — linhas vermelhas

**JAMAIS implementar, mesmo sob pedido de cliente (recusa registrada em política comercial):**
- Keylogger / captura de teclas ou de área de transferência.
- Qualquer modo oculto/stealth (ícone invisível, processo disfarçado, instalação silenciosa sem aviso ao usuário).
- Webcam ou microfone.
- Leitura de conteúdo de e-mails, mensagens, documentos.
- Captura de tela contínua/oculta.
- Burla de janela anônima do navegador ou de apps pessoais.
- Venda/uso secundário de dados de monitoramento (benchmarks identificáveis entre clientes).

Racional: além do passivo LGPD/trabalhista solidário, um único caso público de "software brasileiro usado como spyware contra funcionários" destrói a marca; a postura "não construímos isso nem por dinheiro" é ativo de vendas.

**Implementar apenas pós-MVP e com salvaguardas fortes:**
- **Screenshots**: opt-in por tenant (assinatura de aditivo contratual), banner/indicador visível no momento da captura, blur configurável (default on), exclusão de apps/sites na blocklist, retenção curta (ex.: 30 dias), acesso restrito e auditado.
- **Coleta de navegação**: somente domínio (nunca URL completa), com categorização — não conteúdo.
- **BYOD/máquina pessoal**: não suportar no MVP; se um dia, exigir partição clara trabalho/pessoal — risco trabalhista e LGPD alto.

**Outros riscos de produto a tratar em design:**
- Cliente configurando o produto de forma abusiva (retenção máxima + títulos completos + sem comunicação aos empregados): mitigar com defaults conservadores, fricção/avisos nas configurações ampliadas, checkbox de declaração de ciência (REQ-PRIV-04) e cláusula de uso conforme no contrato.
- Relatórios individuais usados para assédio/ranking vexatório: mitigar com perfis de acesso (REQ-PRIV-07/08) e orientação no material.
- Falso positivo de "ociosidade" punindo trabalho legítimo sem input (leitura, reunião presencial, chamada): documentar a limitação da métrica nos relatórios (disclaimer fixo nos dashboards) — risco de litígio trabalhista do cliente baseado em métrica mal interpretada respinga em nós.

### 8. Checklist de conformidade pré-lançamento

**Jurídico/documental**
- [ ] DPA modelo revisado por advogado e integrado ao fluxo de contratação (sem DPA → sem tenant)
- [ ] Política de Privacidade do produto publicada (`/privacidade`)
- [ ] Termo de Ciência do Empregado (modelo) e AUP (modelo) prontos no kit de onboarding
- [ ] Modelo de LIA entregável ao cliente
- [ ] RoT nosso (art. 37) preenchido e versionado
- [ ] Encarregado (DPO) nomeado e canal `privacidade@` publicado
- [ ] Lista de subprocessadores publicada
- [ ] Plano de Resposta a Incidentes escrito, com prazo de notificação à controladora definido em contrato
- [ ] Cláusula de offboarding: export + exclusão em 30 dias + certificado de eliminação

**Produto/agente**
- [ ] Ícone visível sem flag de ocultação no binário; tela "o que é coletado" funcional
- [ ] Aviso de primeiro logon com registro `AgentNoticeAcknowledged`
- [ ] Mascaramento de títulos `MASKED_PATTERNS` ativo por default, aplicado client-side
- [ ] Navegação privada → `APP_ONLY` automático
- [ ] Janela de expediente default ativa; coleta fora dela limitada a sessão/uptime
- [ ] Confirmado por revisão de código: zero código de keylog/screenshot/webcam/mic/conteúdo
- [ ] Binário do agente assinado (code signing)

**Portal/backend**
- [ ] Retenção default 90 d (brutos) / 24 m (agregados) com `DataRetentionPurgeJob` rodando e logado
- [ ] Export do titular (`/privacy/subjects/{id}/export`) e exclusão (`DELETE .../data`) funcionais e auditados
- [ ] Full export e exclusão de tenant funcionais
- [ ] Revogação de token de device individual e em massa funcional
- [ ] `AuditLog` cobrindo visualização individual, exports, exclusões, mudanças de config e permissões
- [ ] Perfis `TenantAdmin`/`Manager`/`Viewer-Agregado` implementados
- [ ] MFA obrigatório para admins; política de senhas implementada

**Segurança/infra**
- [ ] TLS 1.2+ ponta a ponta; HSTS; sem endpoints HTTP
- [ ] Criptografia em repouso (banco + blobs + backups) verificada
- [ ] Hospedagem e backups confinados a região Brasil (replicação geográfica conferida)
- [ ] Suite de testes de isolamento cross-tenant passando no CI (gate de release)
- [ ] SCA/dependabot ativo; pentest externo executado antes do GA e achados críticos corrigidos
- [ ] Teste de restore de backup executado e documentado
- [ ] Acesso interno de suporte a dados de tenants restrito e auditado

---

## Apêndice: Decisões-chave recomendadas

- Empresa cliente é controladora e nós operadora: DPA assinado é pré-condição técnica de provisionamento de tenant, e toda configuração de coleta do tenant é tratada como instrução documentada (logada em auditoria)
- Base legal recomendada é legítimo interesse com LIA + transparência; consentimento descartado por ser viciado na relação de emprego — o Termo de Ciência é instrumento de transparência, não de consentimento
- Mascaramento de títulos de janela aplicado no agente (client-side), com default MASKED_PATTERNS e APP_ONLY automático para navegação privada — título sensível nunca chega ao servidor
- Ícone visível e tela 'o que é coletado' sem possibilidade de ocultação no binário: modo stealth inexistente por arquitetura, não apenas desabilitado
- Retenção default 90 dias (brutos) / 24 meses (agregados), configurável por faixa limitada por tenant, com purga automática diária e logs de auditoria não reduzíveis (24 meses)
- Hospedagem 100% no Brasil (Azure Brazil South, alinhado ao stack .NET; alternativa AWS sa-east-1), incluindo backups — vira argumento comercial explícito
- Auditoria de acesso a dados pessoais (quem viu relatório de quem, quando) entra no MVP, não no roadmap — é exigência recorrente de DPOs de clientes e barata de construir cedo
- Segregação multi-tenant com tenantId obrigatório + RLS no banco + suite de testes de isolamento cross-tenant como gate de release no CI
- Direitos do titular operacionalizados via controladora: endpoints e tela de export/exclusão por usuário/dispositivo no MVP, com SLA compatível com os 15 dias do art. 19
- Linhas vermelhas publicadas em política comercial: keylogger, modo oculto, webcam/mic e leitura de conteúdo jamais serão implementados; screenshots só pós-MVP com opt-in contratual, banner, blur e retenção curta
- Coleta default restrita à janela de expediente configurada (fora dela só sessão/uptime) — reduz risco trabalhista de vigilância fora da jornada, especialmente em home office

## Apêndice: Riscos

- Títulos de janela vazando dados sensíveis (saúde, sindicato, religião) ou dados de terceiros — se o mascaramento for server-side ou opcional sem default seguro, o passivo é nosso enquanto operadora (responsabilidade solidária do art. 42 §1º)
- Cliente usando o produto de forma abusiva (sem comunicar empregados, retenção máxima, exposição vexatória de relatórios individuais) — o passivo trabalhista é dele, mas o dano reputacional e a solidariedade LGPD respingam em nós; defaults conservadores e declaração de ciência no onboarding são mitigação, não eliminação
- Vazamento cross-tenant é o incidente existencial do produto: um único caso de empresa A vendo dados da empresa B encerra a credibilidade do SaaS — por isso testes de isolamento devem ser gate de release, não item de backlog
- Métrica de ociosidade interpretada como 'não trabalhou' gera litígio trabalhista no cliente baseado em dado nosso mal compreendido (leitura, reuniões e chamadas não geram input) — exigir disclaimer fixo nos dashboards e no material
- Pressão comercial por features invasivas (keylog, screenshot oculto, 'modo discreto') virá cedo de prospects; sem política de recusa escrita e treinamento do time de vendas, a linha vermelha será corroída negócio a negócio
- Replicação geográfica default da nuvem (GRS, réplicas de leitura) pode mandar dados para fora do Brasil silenciosamente, quebrando a promessa comercial de residência nacional — conferir na configuração de cada serviço
- Antivírus/EDR classificando o agente como spyware (categoria de software historicamente abusada) — sem code signing, transparência de comportamento e processo de whitelisting com fabricantes, o deploy nos clientes trava
- Incidente de segurança sem plano: a controladora tem 3 dias úteis (Resolução CD/ANPD 15/2024) para comunicar a ANPD; se demorarmos a avisá-la, transferimos a infração para ela e quebramos o contrato — prazo interno de notificação precisa estar definido antes do primeiro cliente
- Buffer offline do agente na máquina do funcionário é dado pessoal em repouso fora do nosso perímetro — sem criptografia local e teto de armazenamento, vira ponto de vazamento
- Uso secundário de dados de tenants (benchmarks, telemetria, treinamento de modelos) sem previsão no DPA configura tratamento fora de instrução — qualquer feature analítica cross-tenant precisa de revisão jurídica antes do design

## Apêndice: Perguntas abertas (dependem do dono do produto)

- O agente coleta sempre que a máquina está ligada ou apenas dentro da janela de expediente configurada pelo tenant? A recomendação é janela default 07:00–20:00 em dias úteis com coleta reduzida fora dela, mas o dono do produto precisa validar o impacto comercial (clientes com turnos, plantões e home office flexível)
- Suportaremos máquinas pessoais (BYOD) ou o contrato restringirá explicitamente a equipamentos corporativos? Recomendação é recusar BYOD no MVP, mas isso pode excluir prospects pequenos
- Qual o nível default de coleta de título de janela que o negócio aceita: MASKED_PATTERNS (recomendado) ou APP_ONLY (mais conservador, menos valor analítico)? Isso define o equilíbrio risco × proposta de valor do produto
- Prazo contratual de notificação de incidente à controladora: 24h ou 48h após confirmação? Mais curto é argumento de venda, mas cria obrigação operacional para um time pequeno sem plantão
- O Encarregado (DPO) será interno (sócio/líder técnico acumulando) ou contratado como serviço externo? Precisa estar definido e publicado antes do primeiro cliente
- Haverá plano comercial com retenção estendida (>180 dias de brutos) mediante aditivo? Recomendação é não oferecer no MVP, mas clientes de compliance podem pedir
- Nuvem definitiva: Azure Brazil South (alinhada ao stack .NET e à recomendação) ou AWS sa-east-1 (se houver custo/credito melhor)? Decisão de infra com impacto em DPA e lista de subprocessadores
- Perfis de visualização: o produto permitirá que o tenant desligue relatórios individuais e opere só com agregados por equipe (modo 'privacy-first' vendável a clientes europeus/maduros)? Isso muda o desenho de dashboards desde o MVP
- Quem assina juridicamente os modelos (DPA, Termo de Ciência, AUP, LIA): advogado interno da empresa ou escritório externo especializado em LGPD/trabalhista? Os modelos não devem ir a mercado sem revisão jurídica formal
- O produto será posicionado explicitamente como NÃO sendo registrador de ponto (evitando a Portaria 671/2021 do MTE) ou há intenção futura de integrar controle de jornada? A resposta muda finalidade declarada, base legal e requisitos regulatórios