# Estratégia de Produto e Escopo de MVP — Monitoramento Corporativo Transparente de Estações Windows

## 1. Mercado e concorrência

### 1.1 Panorama da categoria
A categoria global é chamada de "employee monitoring / workforce analytics". Há dois polos de posicionamento:
- **Workforce analytics / produtividade** (ActivTrak, DeskTime, Hubstaff, Time Doctor): foco em insights de produtividade, dashboards para gestores, linguagem "amigável" (coaching, burnout, capacity planning). Tendem a evitar a palavra "vigilância".
- **Insider threat / UEBA / compliance** (Teramind, Veriato): foco em segurança, DLP, gravação de sessão, screenshots, keylogging opcional. Vendido para enterprise/segurança, ticket alto, ciclo de venda longo.

O produto proposto pertence claramente ao primeiro polo, e isso deve ser dito explicitamente em todo material: **gestão transparente de produtividade, não vigilância**.

### 1.2 Concorrentes globais (ordens de grandeza, preços públicos historicamente praticados — validar antes do lançamento)

| Concorrente | Posicionamento típico | Faixa de preço (ordem de grandeza, por usuário/mês) | Observações relevantes para um entrante BR |
|---|---|---|---|
| **ActivTrak** | Workforce analytics, "produtividade saudável", forte em PME/mid-market EUA | ~US$ 10–15 (planos pagos); tem free tier limitado | Referência de UX e narrativa; preço em dólar pesa para PME BR (R$ 55–85/usuário/mês ao câmbio) |
| **Hubstaff** | Time tracking para equipes remotas/freelancers, com screenshots e GPS opcionais | ~US$ 5–15 | Forte em agências/remoto; screenshots fazem parte do core — narrativa mais invasiva |
| **Time Doctor** | Time tracking + produtividade, foco em BPO/outsourcing | ~US$ 6–20 | Muito usado por BPOs com clientes no exterior; suporte em inglês |
| **Teramind** | UEBA, insider threat, DLP, gravação de sessão | ~US$ 12–25+ (mínimos de assento, deploy dedicado custa mais) | Não é concorrente direto do MVP; é o "teto" da categoria e o que NÃO queremos parecer |
| **DeskTime** | Time tracking automático simples, PME | ~US$ 6–14 | O mais próximo em simplicidade do que propomos; bom benchmark de feature set mínimo viável |

### 1.3 Concorrentes brasileiros
- **fSense** (Grupo Meta/BR): monitoramento de estações Windows para PME brasileira, cobrado por máquina, preço em real historicamente na faixa de R$ 10–25/máquina/mês. É o concorrente direto mais óbvio; produto funcional porém com UX datada — oportunidade de diferenciação por experiência de produto e narrativa LGPD.
- **Monitora-like / players regionais** (ex.: soluções de revendas de TI, módulos de suítes de service desk, ferramentas tipo "controle de ponto + tela"): fragmentados, frequentemente on-premise ou semi-artesanais, venda via canal de TI local. Competem por preço e relacionamento, não por produto.
- **Adjacências que confundem o comprador**: sistemas de ponto eletrônico (Pontomais, Ahgora/TOTVS), DLP nacional, RMM de provedores de TI (que "meio que" mostram uso de máquina). O material de vendas precisa de uma seção "o que NÃO somos" (não somos ponto, não somos antivírus, não somos spyware).

### 1.4 Onde um entrante brasileiro pequeno ganha
1. **Preço em real, sem surpresa cambial** — concorrente global a US$ 10/usuário vira R$ 55+; cobrar R$ 20–35/dispositivo é metade do custo percebido e elimina objeção de câmbio/cartão internacional.
2. **LGPD-first como feature de venda, não como compliance defensivo** — termo de ciência do funcionário pronto, minimização de dados por design (sem keylog/screenshot), retenção configurável, relatório de "dados coletados sobre mim" (DSR). Nenhum global entrega isso pronto em português com linguagem jurídica brasileira.
3. **Suporte local em português, no WhatsApp/horário comercial BR** — para PME, suporte do fornecedor global é chamado em inglês com 48h de SLA; aqui é diferencial real.
4. **Simplicidade radical para PME** — instalação em 10 minutos, dashboard que o dono da empresa entende sem treinamento, sem 40 telas de configuração. O concorrente global é "gordo" demais para uma empresa de 30 pessoas.
5. **Boleto/Pix e contrato em português** — fricção de compra menor que cartão internacional + contrato em inglês.

## 2. ICP — Perfil de Cliente Ideal do MVP

### 2.1 Empresa-alvo
- **PME brasileira, 10–200 funcionários**, com trabalho de escritório ou híbrido em estações **Windows** (escritórios de contabilidade, advocacia, BPO/call center pequeno, agências, distribuidoras com backoffice, software houses, corretoras de seguro/imobiliárias com equipe interna).
- Sweet spot inicial: **20–80 dispositivos monitorados** — grande o suficiente para dor real ("não sei o que minha equipe faz no híbrido"), pequena o suficiente para decisão rápida (1–2 decisores, ciclo de venda de 1–4 semanas).
- Já tem alguma TI (interna de 1 pessoa ou terceirizada/MSP) capaz de instalar um MSI — o MSP terceirizado é também um **canal de distribuição** futuro.

### 2.2 Personas compradoras
| Persona | Dor | Gatilho de compra | O que precisa ver na demo |
|---|---|---|---|
| **Dono / diretor de operações** | "Pago salário e não sei se as 8h são trabalhadas, principalmente no híbrido" | Adoção de híbrido; queda de produtividade percebida; desconfiança pontual | Dashboard com horas ativas vs. ociosas por pessoa/equipe, em 1 tela |
| **RH / DP** | Embasar conversas de desempenho e desligamentos com dados; compliance de jornada | Disputa trabalhista; pedido da diretoria | Relatório exportável por período + termo de ciência LGPD pronto |
| **TI (influenciador, não comprador)** | Não quer mais um agente pesado que dá problema | É quem instala | Instalador MSI silencioso, consumo baixo de CPU/RAM, painel de saúde dos agentes |

### 2.3 Quem NÃO perseguir no MVP (anti-ICP)
- **Enterprise (500+)**: exigirá SSO/SAML, questionário de segurança de 200 itens, DPA customizado, SLA contratual, ciclo de 6–12 meses. Recusar educadamente e anotar para v2.
- Empresas que pedem **screenshots/keylogging** como condição: conflita com o posicionamento; dizer não é parte da marca.
- Operações 100% de campo/fábrica sem estação Windows individual.
- Órgãos públicos (licitação) no MVP.

## 3. Corte de MVP

Princípio do corte: **o MVP é o menor produto pelo qual uma PME paga R$ 500–1.500/mês e renova no mês seguinte**. Tudo que não contribui para "gestor olha o dashboard toda semana e acha que vale o preço" sai.

| Capacidade | MVP | v1.1 (60–90 dias pós-GA) | Depois (v2+) |
|---|---|---|---|
| Agente Windows (10/11, x64; Win Server p/ RDS avaliar) | ✅ | — | — |
| Agente macOS / Linux | ❌ | ❌ | ✅ (só com demanda paga comprovada) |
| Coleta: apps abertos, janela ativa (processo + título), sessões (login/logoff/lock/unlock), ociosidade | ✅ | — | — |
| Ícone visível + aviso de monitoramento no endpoint (LGPD/transparência) | ✅ (inegociável) | — | — |
| Dashboard org/equipe/pessoa (horas ativas, ociosas, top apps) | ✅ | melhorias | — |
| Timeline do dia por dispositivo/usuário | ✅ | — | — |
| Relatórios por período + **export CSV** | ✅ | export XLSX/PDF agendado por e-mail | — |
| Categorização de apps/sites (produtivo/neutro/improdutivo) **por tenant**, com catálogo padrão pré-carregado | ✅ | regras por equipe/cargo | auto-categorização assistida (ML) |
| Multi-tenant com isolamento lógico por tenant | ✅ | — | — |
| Gestão de usuários do portal (admin/gestor, escopo por equipe) | ✅ (2 papéis) | papéis granulares | — |
| Instalador MSI + token de enrollment por tenant | ✅ | deploy via GPO documentado | Intune/RMM packs |
| Auto-update do agente | ✅ (canal simples) | canais staged | — |
| Buffer offline no agente (fila local, reenvio) | ✅ | — | — |
| Painel de saúde dos agentes (último heartbeat, versão) | ✅ (mínimo: lista + status) | alertas de agente parado | — |
| **Alertas** (ociosidade alta, app proibido, agente offline) | ❌ | ✅ (e-mail) | webhooks/Slack/Teams |
| **Billing**: cobrança manual (Pix/boleto emitido manualmente, controle em planilha/ERP) | ✅ | gateway (ex.: Stripe/Pagar.me/Asaas) + cobrança recorrente automática | self-service completo, upgrade in-app |
| SSO (Google/Microsoft social login simples) | ❌ (e-mail+senha+2FA TOTP opcional) | login Google/Microsoft (OAuth) | SAML/SCIM (enterprise) |
| API pública / webhooks | ❌ | ❌ | ✅ |
| Screenshots | ❌ | ❌ | Avaliar com muitas salvaguardas (opt-in contratual, blur, retenção curtíssima, log de quem viu) — pode nunca entrar |
| Monitoramento de URLs/sites (além do título da janela do navegador) | ❌ (título da aba já dá sinal) | extensão de navegador opcional | — |
| App mobile do gestor | ❌ | ❌ | avaliar |
| White-label para MSPs | ❌ | ❌ | ✅ (motor de expansão B2B2B) |

**Justificativa do billing manual no MVP**: com meta de 10–30 contas nos primeiros 6 meses, são 10–30 faturas/mês — 2h de trabalho administrativo. Integrar gateway recorrente (Pagar.me/Asaas/Iugu) custa 2–4 semanas de dev + tratamento de inadimplência/retentativa/conciliação, e ainda haverá clientes pedindo boleto faturado com PO. O dinheiro entra igual via Pix/boleto manual; o tempo de dev vai para o produto. Gatilho para automatizar: >30 contas ativas ou >4h/mês de esforço de cobrança (v1.1).

**Justificativa de não ter alertas no MVP**: alerta mal calibrado gera ruído e desliga o cliente; antes é preciso ter dados históricos e categorias maduras para alertar com precisão. O painel de saúde de agentes cobre o caso operacional mais urgente (agente parado) de forma passiva.

## 4. Precificação proposta (hipótese inicial a validar no piloto)

### 4.1 Modelo
- **Cobrança por dispositivo monitorado/mês** (não por usuário): mais simples de auditar (nº de agentes ativos), evita discussão sobre turnos/máquinas compartilhadas, e é o modelo do concorrente direto BR.
- Dispositivo "cobrável" = agente que reportou ao menos 1 vez no mês (com tolerância para máquinas desativadas no painel).
- **Pedido mínimo: 10 dispositivos** (ou piso de R$ 199/mês) — filtra micro-contas que consomem suporte e não renovam.

### 4.2 Planos

| | **Essencial** | **Pro** |
|---|---|---|
| Preço (mensal, por dispositivo) | **R$ 19,90** | **R$ 34,90** |
| Retenção de dados detalhados (timeline) | 90 dias | 12 meses |
| Retenção de agregados diários | 12 meses | 24 meses |
| Dashboard + timeline + relatórios CSV | ✅ | ✅ |
| Categorias customizadas por tenant | ✅ | ✅ |
| Relatórios avançados (comparativo entre equipes, tendência, agendados — v1.1) | ❌ | ✅ |
| Alertas (quando lançarem, v1.1) | ❌ | ✅ |
| Suporte | E-mail/chat, SLA 1 dia útil | WhatsApp prioritário, SLA 4h úteis |

- **Desconto anual: 2 meses grátis (~16,7%)** no pagamento à vista anual (Pix/boleto) — melhora caixa e trava churn.
- Um terceiro plano "Business/Enterprise" fica como linha "fale conosco" no site desde o dia 1 (ancoragem de preço + captura de leads maiores para v2), sem compromisso de entrega.
- Ancoragem competitiva: Essencial a R$ 19,90 fica na faixa do player BR direto e a ~1/3 do custo em reais de um ActivTrak; Pro a R$ 34,90 ainda é ~50–60% do global.

### 4.3 Trial e piloto
- **Piloto sem prazo fixo e sem cartão (billing é manual mesmo)**, limitado a 25 dispositivos, **com onboarding assistido obrigatório**: call de 30 min para instalar os 5 primeiros agentes junto com a TI do cliente. Isso resolve ativação E qualifica o lead ao mesmo tempo.
- Programa de piloto fundador (F5): 2–3 empresas amigas, 3 meses com 50–70% de desconto em troca de feedback quinzenal estruturado + depoimento/logo no site + permissão para case.

### 4.4 Unit economics alvo (hipóteses para o modelo, não promessas)
- Ticket médio alvo: 40 dispositivos × R$ 25 (mix) ≈ **R$ 1.000/mês por conta**.
- Custo de infra por dispositivo: centavos a poucos reais/mês (eventos de metadados são leves) — margem bruta alvo >85%.
- CAC alvo no início: venda fundador-led + inbound de conteúdo (LGPD + produtividade híbrida), CAC < 3 meses de receita da conta.

## 5. Métricas do produto (instrumentar desde F2)

### 5.1 Ativação
- **TTFD (time to first device)**: tempo entre criação do tenant e primeiro evento recebido. Alvo: **< 24h** (idealmente na própria call de onboarding).
- **% de orgs com ≥5 dispositivos reportando na semana 1**. Alvo: **≥ 60%** dos trials. É o melhor preditor de conversão: menos de 5 devices = piloto de brincadeira.
- % de trials que completam a call de onboarding assistido. Alvo: ≥ 80%.

### 5.2 Engajamento/retenção (o dashboard precisa virar hábito)
- **WAU de gestores por tenant**: % de semanas em que ao menos 1 gestor do tenant fez login e viu dashboard/relatório. Alvo: **≥ 75% das semanas**. Tenant com 2 semanas sem login = risco; acionar CS (e-mail "resumo da semana" é a feature de retenção mais barata — colocar no v1.1).
- Nº de relatórios exportados/mês por tenant (proxy de uso em decisões reais de RH).
- % de tenants que customizaram ≥1 categoria (proxy de apropriação do produto).

### 5.3 Expansão
- **Dispositivos ativos por conta, mês a mês** (net device expansion). Crescimento de devices = expansão de receita sem venda nova. Alvo: NRR > 100% só com devices.
- % de contas Essencial→Pro após lançamento dos relatórios avançados.

### 5.4 Churn e sinais precoces (ordenados por antecedência)
1. **Agentes silenciosos sem reinstalação**: % de devices do tenant sem heartbeat há >7 dias e que não voltam. Cliente desinstalando aos poucos é churn em câmera lenta — alertar CS automaticamente.
2. Queda de logins do gestor (de semanal para zero).
3. Queda do nº de devices cobráveis 2 meses seguidos.
4. Atraso de pagamento (com billing manual, o financeiro É um sensor de churn).
- Métrica de saída: **churn lógico mensal de contas** alvo < 3%/mês após estabilização; medir também churn de devices.

## 6. Riscos de negócio (honestos)

1. **Risco de imagem — "software de espionar funcionário"**: o maior risco da categoria no Brasil. Mitigação: posicionamento "gestão transparente de produtividade" é simultaneamente defensivo (LGPD, clima organizacional) e comercial (diferencia dos globais com screenshot). Concretamente: ícone visível inegociável, página pública "o que coletamos e o que NUNCA coletamos", kit de comunicação interna pronto para o cliente avisar os funcionários (e-mail modelo + termo de ciência), recusa explícita de pedidos de keylog/screenshot. Um único caso público de mau uso por um cliente pode queimar a marca — o contrato deve obrigar o cliente a informar os funcionários (transferência de responsabilidade documentada).
2. **Risco jurídico/LGPD**: monitoramento de empregado é lícito (poder diretivo do empregador, legítimo interesse) MAS exige transparência, proporcionalidade e minimização. Títulos de janela podem conter dados pessoais/sensíveis acidentais (ex.: "Prontuário - Maria Silva.pdf"). Mitigação: parecer jurídico antes do GA, DPA padrão (operador de dados), retenção limitada, e avaliar máscara configurável de títulos por app no v1.1.
3. **Concentração de receita**: com 5–10 contas, perder 1 conta de 100 devices = 20–30% da receita. Mitigação: não deixar nenhuma conta passar de ~25% da MRR sem plano de diversificação; preferir 10 contas de 40 devices a 2 de 200.
4. **Vendas B2B exigem material, não só produto**: sem one-pager, demo com dados realistas e proposta padrão, cada venda vira projeto. **O SEED de dados demo (tenant "Empresa Demo" com 30 devices, 60 dias de histórico sintético plausível, com padrões de equipe distintos) é item de backlog TÉCNICO da F3, não "nice to have"** — a demo vendável depende dele, pois nenhum prospect verá dados reais de outro cliente.
5. **Suporte vira gargalo**: parque Windows de PME é heterogêneo (Win 10 antigo, antivírus agressivo, proxy, máquina de domínio vs. workgroup, usuário sem admin). Cada instalação falha consome horas. Mitigação: instalador MSI robusto com log local, página de troubleshooting pública, pré-requisitos claros, e telemetria de erro de instalação no agente. Falso positivo de antivírus é quase certo: **assinar o agente com certificado de code signing (EV se possível) desde a F4** e submeter aos fornecedores de AV (Microsoft Defender, principalmente).
6. **Dependência de plataforma**: mudanças do Windows (APIs de sessão, políticas de privacidade do Defender/SmartScreen) podem quebrar coleta ou reputação do binário. Mitigação: auto-update funcionando ANTES do GA (é por isso que está na F4 e não no "depois").
7. **Mercado de PME tem churn estrutural alto** (a própria PME fecha/encolhe); o modelo precisa de motor de aquisição contínuo (conteúdo/SEO "monitoramento de funcionários LGPD", parcerias com MSPs e contabilidades) e não só indicação.

## 7. Sequência de construção (1–2 devs experientes .NET; estimativas em semanas-calendário com 2 devs)

| Fase | Conteúdo | Critério de "pronto" (verificável) | Estimativa | Caminho crítico |
|---|---|---|---|---|
| **F0 — Fundação** | Monorepo, CI/CD (build agente + portal + API), esqueleto multi-tenant (tenant_id em tudo desde a 1ª migration), auth do portal (e-mail+senha, convite, 2 papéis), ambientes dev/prod | `git push` → deploy automático em staging; criar tenant + logar + convidar usuário funciona; teste automatizado prova que usuário do tenant A não lê dado do tenant B | **2 sem** | ✅ |
| **F1 — Ingestão fim-a-fim** | Agente mínimo (serviço Windows + coletor de janela ativa/sessão/idle), enrollment por token do tenant, heartbeat 60s, batch de eventos a cada 30–60s, endpoint de ingestão, persistência crua, buffer offline básico | Instalar agente numa VM limpa com o token → em <2 min eventos crus aparecem no banco do tenant certo; derrubar a rede 10 min → eventos chegam depois sem perda | **3 sem** | ✅ |
| **F2 — Pipeline de intervalos + Timeline (PRIMEIRA DEMO VENDÁVEL)** | Job que transforma eventos crus em intervalos (app X ativo de 09:02 a 09:31; ocioso 12:10–12:40; sessão bloqueada etc.), tela de timeline por device/dia no portal, lista de devices com último heartbeat | Para um dia de uso real de 1 máquina, a timeline no portal bate com a realidade observada (validação manual de 8h de uso); demo de 10 min possível para um estranho | **4 sem** | ✅ |
| **F3 — Dashboard + relatórios + categorias** | Dashboard org/equipe/pessoa (horas ativas/ociosas, top 10 apps, comparativo de período), catálogo de categorias padrão + override por tenant, relatório por período com export CSV, **seed de tenant demo com 60 dias de dados sintéticos**, agregados diários pré-computados | Gestor responde "quem da equipe X ficou mais tempo ocioso esta semana?" em <3 cliques; CSV abre no Excel com acentuação correta (UTF-8 BOM); tenant demo navegável de ponta a ponta | **4 sem** | ✅ |
| **F4 — Hardening** | Instalador MSI (instalação silenciosa `/qn`, GPO-friendly), auto-update do agente, robustez offline (fila com limite e expurgo), code signing, ícone/aviso de monitoramento no endpoint, features LGPD (retenção automática por plano, export "dados de um titular", exclusão de device/titular), log de auditoria do portal (quem viu o quê), painel de saúde de agentes, backup/restore testado | Atualizar 10 agentes remotamente sem tocar nas máquinas; MSI instala via GPO em máquina de domínio; expurgo de retenção comprovado com dado antigo; restore de backup executado com sucesso em staging | **5 sem** | ✅ (auto-update e MSI são bloqueadores de GA) |
| **F5 — Piloto** | 2–3 empresas amigas, 30–60 dias de uso real; correções de campo; materiais do item 8; precificação validada em proposta real | 2 pilotos com ≥10 devices cada rodando 30 dias com <5% de devices silenciosos; ao menos 1 piloto converte em contrato pago; NPS verbal positivo do gestor ("eu pagaria R$ X") | **6 sem** (calendário; dev em paralelo corrige bugs e adianta v1.1) | parcialmente paralelo à F4 final |
| **GA** | Site no ar, contrato padrão, processo de cobrança manual, canal de suporte | Primeira conta paga não-amiga fechada | — | — |

- **Total: ~20–24 semanas (5–6 meses) até GA com 2 devs**; com 1 dev, estimar 8–9 meses (F1/F2 não paralelizam bem com 1 pessoa).
- **Caminho crítico**: F0→F1→F2→F3→F4(MSI+auto-update)→F5. Nada de F3 antes de F2 validada com dados reais — dashboard bonito sobre pipeline de intervalos errado é retrabalho garantido. O pipeline de intervalos (F2) é o coração técnico do produto e o maior risco de estimativa (regras de borda: lock vs. idle, troca rápida de janela, múltiplas sessões/RDP, relógio da máquina errado — reservar buffer).
- Marcos de venda: **fim da F2 = primeira demo a prospects** (com dados da própria equipe); **fim da F3 = demo com tenant seed para qualquer prospect**; **fim da F4 = pode instalar na máquina de cliente de verdade**.

## 8. Preparação além do código (dono do produto/comercial, em paralelo às fases)

| Entregável | Conteúdo mínimo | Pronto até |
|---|---|---|
| **Landing page** | Proposta de valor em 1 frase, 3 telas do produto (do tenant demo), seção "Transparência e LGPD" com o que coletamos/não coletamos, preços públicos, CTA "agendar demonstração" + formulário; domínio + e-mail profissional | fim da F3 |
| **One-pager comercial (PDF)** | Dor → solução → 3 prints → preço → diferencial LGPD/preço em real/suporte BR; versão para encaminhar no WhatsApp | fim da F3 |
| **Script de demo (10 min)** | Roteiro fixo sobre o tenant seed: dashboard → drill-down em 1 pessoa → timeline de 1 dia → relatório CSV → painel de agentes → encerrar na página de transparência LGPD; lista das 5 objeções e respostas (é legal? funcionário sabe? pega o que digito? quanto pesa na máquina? e home office?) | fim da F3 |
| **Kit de instalação para a TI do cliente** | PDF/página: pré-requisitos (SO, .NET runtime se aplicável, portas/domínios de saída para liberar no firewall/proxy), passo a passo MSI manual e via GPO, troubleshooting dos 5 erros mais comuns, como validar que o agente reporta | fim da F4 |
| **Kit LGPD para o cliente** | Modelo de comunicado interno aos funcionários, modelo de termo de ciência, FAQ jurídico básico, descrição técnica dos dados coletados (para o DPO do cliente) | fim da F4 (com revisão de advogado) |
| **Contrato/termos** | Termos de uso + contrato de assinatura B2B + DPA (operador/controlador), SLA simples, revisados por advogado com prática em LGPD/trabalhista | antes do 1º piloto pago (F5) |
| **Processo de suporte** | Canal único (e-mail + WhatsApp Business), SLA público simples (1º atendimento em 4h úteis Pro / 1 dia útil Essencial), planilha/board de tickets (ferramenta formal só quando doer), página de status básica | início da F5 |
| **Processo de cobrança manual** | Rotina mensal: contagem de devices cobráveis por tenant (relatório interno do próprio sistema — incluir no backlog F3), emissão de NF (contratar contabilidade que emita NFS-e de SaaS), Pix/boleto via banco/Asaas modo manual, régua de cobrança de inadimplente (D+3 e-mail, D+10 contato, D+20 suspensão) | início da F5 |
| **Pipeline de vendas fundador-led** | Lista de 50 empresas-alvo da rede dos sócios, CRM leve (planilha/Pipedrive), meta: 10 demos no 1º mês pós-GA | GA |

---

## Apêndice: Decisões-chave recomendadas

- Posicionar como 'gestão transparente de produtividade' e nunca como vigilância — recusar keylog/screenshot é decisão de marca e de defesa LGPD ao mesmo tempo, e diferencia dos globais (Hubstaff/Teramind) que dependem de screenshot
- ICP único no MVP: PME brasileira de 10–200 funcionários com estações Windows e TI mínima; recusar enterprise (SSO/SAML, questionários de segurança e ciclo de 6–12 meses matariam um time de 2 devs)
- Cobrar por dispositivo monitorado/mês (não por usuário), com piso de 10 devices/R$ 199, planos Essencial R$ 19,90 e Pro R$ 34,90 diferenciados por retenção e relatórios avançados — ancorado abaixo do player BR direto e a ~1/3 do custo em reais dos globais
- Billing manual (Pix/boleto + NFS-e) até ~30 contas ativas: com <30 faturas/mês, automatizar gateway custa mais em dev do que economiza; o tempo vai para o produto
- Agente Windows-only no MVP; macOS/Linux só com demanda paga comprovada — cada plataforma de agente é quase um produto inteiro de manutenção
- Pipeline de intervalos (F2) é o coração do produto e a primeira demo vendável; nenhum trabalho de dashboard (F3) antes de validar a timeline contra um dia real de uso
- MSI + auto-update + code signing são bloqueadores de GA (F4), não polimento: sem auto-update, cada bug de agente vira visita técnica em parque de cliente
- Seed de dados demo (tenant fictício com 60 dias de histórico sintético) entra no backlog técnico da F3 como requisito de vendas — nenhum prospect pode ver dados reais de outro cliente
- Piloto sem prazo fixo e sem cartão com onboarding assistido obrigatório (call de 30 min instalando os 5 primeiros agentes), resolve ativação e qualificação ao mesmo tempo
- Multi-tenancy lógico com tenant_id em toda tabela desde a primeira migration e teste automatizado de isolamento desde F0 — retrofit de multi-tenancy é o retrabalho mais caro possível neste produto

## Apêndice: Riscos

- Risco de imagem é o maior do negócio: um único caso público de cliente usando o produto para assediar funcionários queima a marca; o contrato deve obrigar o cliente a informar os funcionários (termo de ciência) e o produto deve manter ícone visível inegociável
- Títulos de janela podem capturar dados pessoais/sensíveis acidentais (ex.: nome de paciente em arquivo aberto) — exige parecer jurídico LGPD antes do GA, papel de operador bem definido em DPA, e possivelmente mascaramento configurável de títulos no v1.1
- Pipeline de intervalos tem complexidade subestimada (lock vs. idle, RDP/múltiplas sessões, relógio errado na máquina do cliente, troca rápida de janelas) — é o maior risco de estouro de prazo da F2; reservar buffer e validar contra uso real cedo
- Falsos positivos de antivírus/SmartScreen contra o agente são quase certos sem code signing — assinar binários (idealmente certificado EV) e submeter ao Microsoft Defender antes de qualquer instalação em cliente
- Suporte de instalação em parque Windows heterogêneo de PME (AV agressivo, proxy, usuário sem admin, Win 10 antigo) vira gargalo de um time de 2 pessoas — instalador robusto com logs, kit de instalação para TI e telemetria de erro são mitigação, não luxo
- Concentração de receita: com poucas contas, perder 1 cliente de 100 devices pode significar 20–30% da MRR — preferir muitas contas médias a poucas grandes no primeiro ano
- PME brasileira tem churn estrutural (a empresa cliente encolhe ou fecha) — o modelo exige motor de aquisição contínuo (conteúdo LGPD/produtividade, parcerias com MSPs e contabilidades), não dá para viver só de indicação
- Vender para enterprise cedo demais (tentação de um logo grande) trava o roadmap com SSO/SAML, questionários de segurança e SLA contratual que o time de 2 devs não sustenta
- Agentes silenciosos sem reinstalação são churn em câmera lenta e passam despercebidos sem painel de saúde + rotina de CS olhando essa métrica semanalmente
- Dependência da plataforma Windows: mudanças de API/políticas da Microsoft podem quebrar coleta ou reputação do binário de um dia para o outro — auto-update funcionando é o seguro contra isso

## Apêndice: Perguntas abertas (dependem do dono do produto)

- Nome do produto e da marca (impacta domínio, certificado de code signing, contrato e registro de marca — o certificado EV é emitido para a razão social, definir cedo)
- Quem são as 2–3 empresas amigas do piloto F5 e qual contrapartida será aceita (desconto de 50–70% por 3 meses vs. gratuidade) — depende da rede de relacionamento dos sócios
- Apetite de investimento em jurídico antes do GA: parecer LGPD + contrato + DPA revisados por advogado especializado custam dinheiro real — qual o orçamento?
- Meta de receita/prazo que define o tamanho do motor comercial: venda 100% fundador-led no ano 1 ou contratar 1 SDR/vendedor após o piloto?
- Posição definitiva sobre screenshots: alguns prospects vão condicionar a compra a isso — a empresa está disposta a perder essas vendas para proteger o posicionamento (recomendado), ou quer reavaliar com salvaguardas no v2?
- Monitorar também servidores de terminal (RDS/Citrix, comum em contabilidades brasileiras que usam ERP via TS) entra no escopo do agente MVP ou fica explicitamente fora? Afeta arquitetura de coleta por sessão e o modelo de cobrança por dispositivo
- Política de preço para MSPs/revendas de TI (canal natural para PME BR): haverá desconto de canal/white-label no roadmap ou venda 100% direta nos primeiros 12 meses?
- Hospedagem dos dados: compromisso público de datacenter no Brasil (argumento de venda LGPD, custo de cloud BR um pouco maior) ou região internacional mais barata? Decisão de produto/marketing, não só técnica
- Dois devs em tempo integral estão de fato disponíveis pelos ~6 meses até o GA, ou dividem tempo com outros produtos da empresa? Muda as estimativas de fase quase linearmente