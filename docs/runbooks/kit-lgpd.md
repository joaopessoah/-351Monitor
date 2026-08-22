# Kit LGPD do +351 Monitor

> Material de apoio para o **advogado/DPO do cliente** (controladora) e para o jurídico da
> operadora revisarem **antes do primeiro cliente real**. Descreve, de forma fiel ao que está
> implementado em código (F0 a F4), como o produto trata dados pessoais sob a LGPD.
>
> **Anexe este documento ao DPA.** Ele documenta o que o sistema faz na prática; o DPA é que
> firma as obrigações jurídicas entre controladora e operadora.
>
> Para a regra específica de **exclusão de titular** (o que o sistema apaga, anonimiza e mantém),
> veja o documento dedicado [dsr-exclusao-revisao-juridica.md](dsr-exclusao-revisao-juridica.md).
> Este kit referencia aquele runbook no item 5 e não duplica o conteúdo.

---

## 1. Papéis LGPD

- **Empresa-cliente = controladora.** Decide a finalidade e os meios do tratamento, acorda no DPA
  a política de coleta (no MVP fixa em `MASKED_PATTERNS` - item 3), preenche os campos de
  transparência e dispara os direitos do titular. É a empregadora dos titulares monitorados.
- **+351 Monitor = operadora.** Trata os dados em nome da controladora, segundo as instruções dela
  e os limites do produto. Responde solidariamente se descumprir a lei ou as instruções, por isso
  o produto **impede tecnicamente** configurações ilegais em vez de só desaconselhá-las.
- **DPA é pré-condição.** Não há onboarding de cliente real sem Contrato de Tratamento de Dados
  (DPA) assinado. Os pontos que o produto deixa em aberto para a decisão jurídica (item 9) devem
  estar resolvidos e refletidos no DPA antes do primeiro titular.

**Base legal e ciência (não consentimento).** O monitoramento da controladora apoia-se em
**legítimo interesse** condicionado a transparência, proporcionalidade e minimização. Na relação de
emprego o **consentimento não é base válida** (assimetria de poder). Por isso o aceite do
funcionário no agente é **ciência (NOTICE_ACK)**, e não consentimento, e o texto exibido diz isso
(ver item 6).

---

## 2. O que é coletado vs. o que JAMAIS é coletado

### Lista de coleta FECHADA (minimização - Seção 9.1)

O sistema coleta **somente** os itens abaixo. Qualquer adição passa por revisão de privacidade.

- Identificação da máquina e do usuário do Windows.
- Eventos de sessão: logon, logoff, bloqueio e desbloqueio.
- Eventos de energia: ligar, desligar, suspender e retomar.
- Aplicativo em foco e o título da janela em foco, sujeitos à `window_title_policy` (ver item 3).
- O **fato** da ociosidade (jamais o que foi digitado ou clicado).
- Saúde do agente (versão, último contato, integridade).

Não há coleta de linha de comando ou argumentos de processo, não há coleta de URLs (apenas o título
da aba), e não existe captura de aplicativos instalados (`APPS_SNAPSHOT` foi cortado do escopo).

### Linhas vermelhas - o que JAMAIS é coletado (Seção 9.7)

Estas são proibições inegociáveis do produto, recusadas mesmo sob pedido de cliente. A recusa é
política comercial registrada e ativo de marca.

- Teclas digitadas ou qualquer captura de entrada (teclado, mouse).
- Capturas ou gravação de tela.
- Conteúdo da área de transferência (clipboard).
- Conteúdo de arquivos, e-mails, mensagens ou páginas (DOM).
- Webcam ou microfone.
- Localização geográfica.

Também não existem (e não serão implementados): modo oculto/stealth, burla de janela anônima,
injeção de DLL, venda ou uso secundário de dados (benchmarks identificáveis entre clientes,
treinamento de modelos) sem previsão expressa em DPA.

---

## 3. Minimização e mascaramento de títulos (`window_title_policy`)

O título de janela é o dado de maior sensibilidade coletado. O mascaramento é aplicado **no agente,
antes de persistir** o dado na fila local - o conteúdo cru nunca sai da máquina quando a política
exige mascaramento, e o servidor **jamais loga** títulos de janela.

Três níveis estão previstos no protocolo (Seção 9.2):

| Política | O que coleta no título | Status |
|---|---|---|
| `FULL` | Título de janela completo | Só com decisão consciente da controladora **registrada no DPA**; não selecionável no portal, aplicada pela operadora (backoffice) |
| `MASKED_PATTERNS` | Título com mascaramento de termos sensíveis | **Default de fábrica**; editável pela controladora (Owner) no portal |
| `APP_ONLY` | Apenas o nome do aplicativo, sem nenhum título | Coleta mínima; selecionável pela controladora (Owner) no portal |

> **A política de coleta é editável pela CONTROLADORA (F5).** O portal (Configurações,
> Privacidade) e o endpoint `PATCH /organization/agent-config` permitem ao **Owner** alternar
> entre `MASKED_PATTERNS` e `APP_ONLY`, editar a lista de mascaramento, os processos ignorados,
> o limiar de ociosidade, a janela de coleta e o texto do aviso de ciência (item 6). Toda mudança dá bump de `config_version`
> (propaga à frota no próximo ack), grava trilha de auditoria com o de→para e, no caso da
> janela de coleta, também a ação própria `collection_window_choice`. **`FULL` continua fora do
> portal**: exige decisão consciente registrada no DPA e é aplicada pela operadora. O
> rebaixamento automático para `APP_ONLY` em aba anônima/privada segue aplicado pelo próprio
> agente (abaixo), independentemente da política escolhida.

Pontos que o jurídico deve conhecer:

- **Default seguro.** Um tenant recém-criado opera em `MASKED_PATTERNS`; mudanças são decisão
  da controladora (Owner), auditadas com de→para.
- **Lista padrão de mascaramento.** Inclui termos de saúde, sindicais, religiosos, financeiros
  pessoais e padrões de CPF/cartão.
- **Rebaixamento automático em navegação anônima/privada.** Ao detectar aba anônima/privada
  (heurística por sufixo do título no Chrome, Edge e Firefox), o agente cai automaticamente para
  `APP_ONLY` naquele título.
- **Processos ignorados.** Há defaults de `ignored_processes` (gerenciadores de senha, telas de
  logon) que não têm título coletado.

A página pública de transparência (item 7) descreve a política vigente em linguagem amigável e
**nunca** expõe o conteúdo dos padrões de mascaramento (os regex internos).

---

## 4. Retenções vigentes e purga

Os prazos são **fixos no MVP** (configuração por tenant é item de v1.1 - ver "O que o produto NÃO
faz hoje", item 10). São os números N10 a N13 da especificação (Seção 9.6):

| Dado | Retenção | Mecanismo | Job |
|---|---|---|---|
| `raw_events` (eventos brutos) | **90 dias** | `DROP` da partição diária expirada | PartitionMaintenance |
| `activity_intervals` (intervalos) | **12 meses** | `DROP` da partição mensal expirada | PartitionMaintenance |
| `daily_*` (agregados diários) | **24 meses** | `DELETE` por `summary_date` | RetentionPurge |
| `audit_log` (auditoria) | **24 meses** | `DROP` da partição mensal expirada | PartitionMaintenance |

Como funciona a purga (fiel ao código):

- **PartitionMaintenance** roda 1x/dia (02:00 America/Sao_Paulo). Cria partições futuras e dropa as
  expiradas de `raw_events`, `activity_intervals` e `audit_log`. Só dropa a partição cujo limite
  superior já passou do corte de retenção; nunca a partição corrente nem futura. A purga de
  auditoria por `DROP` de partição **não** viola o append-only (item 8), porque é remoção de
  relação inteira (DDL), não exclusão de linha.
- **RetentionPurge** roda 1x/dia (02:30 America/Sao_Paulo). Faz `DELETE` dos agregados diários
  (`daily_device_summaries`, `daily_app_usage`) com `summary_date` além de 24 meses.
- **Toda execução é registrada** na tabela `maintenance_runs`, com contagens, inclusive em caso de
  falha (status `error` com a causa) - uma falha silenciosa enganaria a tela de transparência.

**"Data da última purga".** A tela de Privacidade do portal e a página pública de transparência
exibem a data da última execução bem-sucedida do `RetentionPurge` (último `maintenance_runs` com
`status = ok`). É a evidência operacional de que o ciclo de retenção está rodando.

**Backups.** Dados purgados ainda podem residir em backup por até **35 dias** até saírem do ciclo.
Isso deve estar declarado no DPA. (O runbook de exclusão de titular cita 35 dias; alinhar o número
final no DPA.)

---

## 5. Direitos do titular (DSR)

O **titular** é um usuário de máquina (`device_user`), não um usuário do portal. Os direitos são
exercidos pela controladora a partir do portal e apoiam a resposta ao titular em até 15 dias
(art. 19). Endpoints implementados (Seção 9.3, F4.5):

### Exportação (acesso / portabilidade - art. 18)

- `POST /api/v1/privacy/subjects/{deviceUserId}/export` - todos os dados de um titular.
- `POST /api/v1/privacy/devices/{deviceId}/export` - todos os titulares de um dispositivo.
- `POST /api/v1/privacy/tenant/full-export` - acervo completo do tenant (offboarding).
- Gera um **pacote ZIP** com os dados (eventos, intervalos e agregados) em **CSV** mais um
  `manifest.json` com os metadados do pacote (escopo, titulares, contagens, disclaimer de
  finalidade), disponibilizado por um **link com expiração de 72 horas**.
- Papel exigido: **Admin ou Owner** para subject/device; **Owner** para o full-export do tenant.

### Exclusão (eliminação - art. 18, V)

- `DELETE /api/v1/privacy/subjects/{deviceUserId}/data` e
  `DELETE /api/v1/privacy/devices/{deviceId}/data`.
- **Irreversível**, com **confirmação dupla** (repetir exatamente o `windows_username` do titular ou
  o `hostname` do dispositivo), **motivo obrigatório** (mínimo de 8 caracteres) e **recibo com
  contagens** do que foi apagado, anonimizado e mantido.
- Papel exigido: **Owner**. Um Admin que tente excluir recebe 403.
- A regra exata do que o sistema apaga (eventos e intervalos com conteúdo), anonimiza (cadastro do
  titular) e mantém (agregados de equipe já computados) está em
  **[dsr-exclusao-revisao-juridica.md](dsr-exclusao-revisao-juridica.md)** - documento que o jurídico
  deve revisar e validar antes do primeiro cliente.

Tudo (export e exclusão) é registrado na auditoria na mesma transação da operação.

---

## 6. NOTICE_ACK - evidência de ciência

- No primeiro logon, o agente exibe o aviso de monitoramento e um botão "Entendi".
- Ao clicar, é gravado o evento `NOTICE_ACK` com a versão do aviso e o momento, persistido em
  `raw_events` e materializado como atalho em `devices.notice_acked_at`.
- O portal mostra o status de ciência por dispositivo/usuário no painel de saúde.
- É a evidência que a **controladora** apresenta em eventual disputa trabalhista de que o
  funcionário teve ciência do monitoramento. **Ciência, não consentimento** (ver item 1) - o texto
  do aviso deve deixar isso explícito e é responsabilidade da controladora aprovar esse texto.
- **O texto é editável pela controladora (F5).** Em Configurações, Coleta, o **Owner** escreve o
  corpo do aviso na linguagem da empresa (`notice_text` no `PATCH /organization/agent-config`), com
  preview do texto final na própria tela. Salvar sobe `notice_version`, o que **reexibe o aviso em
  toda a frota** no próximo contato de cada agente e gera um `NOTICE_ACK` novo.
- **O fecho do aviso é fixo e não editável.** O enquadramento "este aviso registra a sua ciência,
  não é um pedido de consentimento" mais o caminho para ver a coleta em tempo real são
  concatenados **pelo agente**, e nenhuma configuração do tenant os remove ou trunca. Por isso o
  servidor recusa, ao salvar: texto que não caiba na janela do aviso já contando esse fecho, HTML
  ou qualquer marcação (a janela exibe texto simples), e texto que imite pedido de consentimento,
  autorização ou aceite.

---

## 7. Página pública de transparência (por slug)

- Endpoint `GET /api/v1/public/transparencia/{slug}` - **público, sem login e sem cookie**. É o link
  que o ícone do agente (tray) abre para o funcionário.
- Renderiza o **estado real** das configurações do tenant: a política de título de janela vigente e
  a janela de coleta, ambas descritas em linguagem amigável em pt-BR.
- Mostra as retenções fixas (90 dias / 12 meses / 24 meses / 24 meses) e a **data da última purga**.
- Mostra os campos editáveis que a controladora preenche no portal: **finalidade declarada**,
  **contato do DPO** e **vigência** da política.
- Lista o que é coletado e o que **nunca** é coletado (itens 2 e 3).
- **Privacidade da própria página:** expõe apenas a política de coleta vigente. **Jamais** dado
  pessoal, título de janela cru ou o conteúdo dos padrões de mascaramento. A descrição da política
  de títulos é sempre amigável, nunca o regex interno.

Cabe à controladora preencher finalidade, contato do DPO e vigência antes de expor o link aos
funcionários, para que a página cumpra o papel de transparência.

---

## 8. Auditoria de acesso append-only

- O portal grava em `audit_log` toda visualização de dado pessoal (timeline, relatório individual,
  drill-down de apps, export - com período e filtros), além de: login, aceite de convite, mudanças
  de papel, revogações de chave/dispositivo, a edição dos campos de transparência da org
  (finalidade declarada, contato do DPO, vigência e horário comercial - registrando o de→para por
  campo via `update_privacy_config`), e as operações de DSR (export e exclusão). A
  `window_title_policy` e a janela de coleta não são editáveis no MVP (item 3), portanto não há
  mudança delas para auditar.
- Responde "quem viu o relatório de quem, e quando".
- **Append-only de verdade** (F4.7): um gatilho de banco a nível de linha barra qualquer `UPDATE`
  ou `DELETE` em `audit_log`, para qualquer perfil de conexão, levantando exceção. O gatilho é
  propagado automaticamente a todas as partições (atuais e futuras). Há ainda `REVOKE UPDATE,
  DELETE` como defesa em profundidade.
- A purga de auditoria respeita a retenção de 24 meses por `DROP` de partição inteira, o que não
  conflita com o append-only (é DDL, não exclusão de linha - ver item 4).
- A leitura da trilha (`GET /api/v1/audit-logs`) é exposta a **Owner e Admin** (Viewer não vê) e,
  por decisão documentada, **não se audita a si mesma** (evitar recursão sem ganho de prestação de
  contas).

---

## 9. Checklist para o advogado / DPO revisar antes do 1º cliente

- [ ] **DPA assinado** com a divisão de papéis (controladora/operadora) e as obrigações da operadora.
- [ ] **Base legal** documentada como legítimo interesse, com transparência, proporcionalidade e
      minimização; e registro de que o aceite do funcionário é **ciência, não consentimento**.
- [ ] **Texto do aviso (NOTICE_ACK)** aprovado pela controladora, deixando claro que é ciência.
- [ ] **Política de título de janela** registrada no DPA como decisão operacional da
      controladora. O Owner alterna entre `MASKED_PATTERNS` e `APP_ONLY` no portal (auditado
      com de→para); `FULL` só com registro no DPA e aplicação pela operadora (item 3).
      Documente a política acordada e a justificativa.
- [ ] **Lista de mascaramento** revisada (saúde, sindical, religioso, financeiro, CPF/cartão) - basta
      para o contexto do cliente?
- [ ] **Retenções** (90d / 12m / 24m / 24m) e a janela de **35 dias de backup** aceitas e
      declaradas no DPA.
- [ ] **Regra de exclusão de titular** revisada e validada - ver
      [dsr-exclusao-revisao-juridica.md](dsr-exclusao-revisao-juridica.md), incluindo a questão
      anonimização vs. pseudonimização dos agregados mantidos.
- [ ] **Página pública de transparência** com **finalidade**, **contato do DPO** e **vigência**
      preenchidos antes de expor o link aos funcionários.
- [ ] **Processo de resposta ao titular em 15 dias** definido na controladora (quem opera o
      export/exclusão no portal, quem entrega o recibo ao titular).
- [ ] **Procedimento de encerramento de tenant (offboarding)** acordado: full-export por código +
      purga manual documentada (ver item 10).
- [ ] **Pentest externo** agendado antes da primeira conta grande não-amiga (não é gate de GA, mas
      deve estar no plano).

---

## 10. O que o produto NÃO faz hoje (para não prometer ao cliente)

Seja transparente com o cliente sobre os limites atuais do MVP:

- **Não há purga automática de tenant.** O `full-export` do tenant é código, mas a **purga
  (exclusão) do tenant no offboarding é um processo manual documentado** (runbook), por decisão
  deliberada de segurança - exclusão de tenant jamais é automatizada.
- **Não há retenção configurável por tenant.** Os prazos (90d / 12m / 24m / 24m) são **fixos no
  MVP**; configuração por tenant está planejada para v1.1.
- **`FULL` não é selecionável no portal.** A controladora edita no portal a política de títulos
  (`MASKED_PATTERNS`/`APP_ONLY`), a lista de mascaramento, os processos ignorados, o limiar de
  ociosidade e a janela de coleta (`collection_window`), tudo auditado com de→para e com bump de
  `config_version`. `FULL` (títulos sem mascaramento) exige decisão registrada no DPA e é
  aplicada pela operadora, nunca por autosserviço.
- **A eficácia plena do `REVOKE`** de auditoria depende de a aplicação conectar com um perfil de
  banco sem ser dono do schema (item de infraestrutura/runbook). O gatilho append-only, porém, já
  garante a imutabilidade de forma independente do perfil.
- **Sem keylogging, screenshots, clipboard, conteúdo, webcam/microfone, geolocalização, modo
  stealth** - e nunca haverá (item 2). Se o cliente pedir, a resposta é não.
