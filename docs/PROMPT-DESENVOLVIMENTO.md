# PROMPT DE DESENVOLVIMENTO — **+351 Monitor** (Sistema de Monitoramento de Estações Windows)

> **Como usar este documento.** Este é o prompt mestre de especificação do produto. Ele será colado (inteiro ou por fases) em sessões do Claude Code para construir o sistema. Trate-o como **a fonte única de verdade**: em qualquer conflito com outro documento, comentário de código, README ou memória de sessão, **este documento vence**. Construa fase a fase conforme a Seção 10 — cada fase referencia as Seções 1–9 como contrato. Todo número, nome de evento, endpoint e enum aqui é **canônico e exato**: não invente variações, não "melhore" nomes, não adicione campos não especificados. Quando este documento diz "DEVE", é requisito verificável; quando diz "JAMAIS", é proibição arquitetural inegociável.

---

## 1. Contexto e objetivo do produto

Você deve construir um **SaaS B2B brasileiro de monitoramento transparente de estações Windows** para PMEs (10–200 funcionários, sweet spot 20–80 dispositivos). O produto pertence ao polo "workforce analytics / gestão transparente de produtividade" — **explicitamente NÃO é** spyware, keylogger, ferramenta de screenshots, DLP nem registro de ponto eletrônico.

O sistema responde três perguntas para o gestor/RH/TI da empresa cliente:

1. **"Minha equipe está trabalhando agora?"** — dashboard de presença em tempo quase-real.
2. **"Como foi o dia/a semana de cada pessoa/máquina?"** — timeline visual do dia e dashboards históricos.
3. **"Quais máquinas pararam de reportar?"** — painel de saúde dos agentes.

Composição do produto (3 componentes + 1 contrato):

| Componente | O que é |
|---|---|
| **Agente Windows** | Serviço + helper de sessão que coleta eventos de uso (janela ativa, sessão, ociosidade) e os envia em lote ao backend |
| **Backend** | API de ingestão + pipeline de intervalização + API REST do portal, multi-tenant, PostgreSQL |
| **Portal Web** | SPA React para o cliente (gestor/RH/TI): dashboard, timeline, relatórios, configurações, LGPD |
| **Contrato canônico** | Seção 5 deste documento — envelope de eventos, tipos, config, números. Agente, ingestão, pipeline e portal usam EXATAMENTE a mesma tabela |

Modelo comercial (contexto, não escopo de código além do indicado): cobrança **por dispositivo/mês** (Essencial R$ 19,90 / Pro R$ 34,90, piso 10 devices), billing **manual** no MVP (Pix/boleto), piloto sem prazo fixo limitado a **25 devices** com onboarding assistido, org criada via backoffice (sem signup self-service). Meta de dimensionamento técnico: **~2.500 devices** (10–30 contas de 20–80 devices com folga) — NÃO dimensionar para 10k.

Papéis LGPD: a empresa cliente é **controladora**; nós somos **operadora**. DPA assinado é pré-condição de provisionamento de tenant (processo comercial, não código).

---

## 2. Princípios inegociáveis

Estes princípios valem para TODO o código produzido. Violação de qualquer um deles é bug crítico.

1. **Transparência por arquitetura.** O agente é sempre visível: ícone de bandeja permanente sem flag de ocultação (a opção não existe no binário), nomes de processo claros (`MonitorAgentService.exe`, `MonitorAgentSession.exe`), entrada normal em "Aplicativos instalados", janela "O que está sendo coletado agora" acessível pelo tray. Modo stealth é inexistente por design, não "desabilitado".
2. **Proibições codificadas (hard-coded, não configuráveis).** O agente JAMAIS contém código de: hook de teclado (`WH_KEYBOARD*` ou similar), captura de tela (GDI/DXGI), leitura de clipboard, leitura de conteúdo de documentos/e-mails/mensagens/DOM, injeção de DLL em processos de terceiros, microfone/câmera, geolocalização. Não é configuração: o código não existe. Ver Seção 9.7 para a lista completa de linhas vermelhas.
3. **LGPD by design.** Minimização (lista de coleta fechada e exaustiva — Seções 5.3 e 9.1), mascaramento de títulos aplicado **no agente antes de persistir em disco** (dado sensível nunca toca a fila local nem a rede), retenção com purga automática, auditoria de acesso a dados pessoais, direitos do titular (DSR) no MVP, evento `NOTICE_ACK` como evidência de ciência do funcionário.
4. **Multi-tenant desde a primeira migration.** `tenant_id uuid NOT NULL` em TODAS as tabelas de dados desde a migration inicial + filtro global obrigatório na camada de dados (EF Core global query filter + carimbo em insert) + **teste automatizado de isolamento entre tenants como gate de CI desde a F0**. Retrofit de multi-tenancy é o retrabalho mais caro possível neste produto.
5. **Agente "burro", servidor inteligente.** O agente NÃO calcula durações nem intervalos — apenas emite eventos pontuais idempotentes. O backend deriva intervalos da sequência ordenada de eventos. Isso garante idempotência, reprocessamento e correção de relógio no servidor.
6. **Idempotência por `event_id`.** Todo evento tem `event_id` UUIDv7 gerado no agente. Reenvio de lote após timeout é sempre seguro: o servidor deduplica por `event_id`. Eventos aceitos só recebem ack **após commit** no banco (ack pós-commit; perda de evento ackado = 0).
7. **Gaps visíveis, nunca silenciosos.** Todo descarte de evento (expurgo FIFO do buffer, rate limit) gera `EVENTS_DROPPED`; toda lacuna de `seq` vira flag "dados incompletos" na timeline/relatório. O usuário do portal sempre sabe quando falta dado.
8. **Vocabulário neutro.** Estados de máquina são fisiológicos: Ativo, Ocioso, Bloqueado, Desligada, Sem comunicação. Julgamento de valor ("Relacionado ao trabalho / Neutro / Não relacionado") existe SOMENTE na camada de categorias configurada pelo cliente. Nunca "produtivo/improdutivo" em estado de máquina; nunca ranking de pessoas.

---

## 3. Arquitetura geral

### 3.1 Visão macro (fluxo de dados)

```
┌────────────────────────── Estação Windows ──────────────────────────┐
│  MonitorAgentService.exe (serviço, LocalSystem, Session 0)          │
│   ├── fila SQLite (WAL) + device token (DPAPI)                      │
│   ├── eventos WTS (logon/logoff/lock/unlock) e Power (suspend)      │
│   └── lança 1 helper por sessão interativa (CreateProcessAsUser)    │
│  MonitorAgentSession.exe (helper, token do usuário, por sessão)     │
│   ├── janela ativa (polling 5 s + dedupe), ociosidade, heartbeat    │
│   └── tray icon + janela de transparência + NOTICE_ACK              │
│        ▲ named pipe \\.\pipe\monitoragent.{sessionId} ▼             │
└──────────────────────────────────────────────────────────────────────┘
                 │ HTTPS TLS 1.2+ (gzip), lote a cada 30 s / 500 eventos
                 ▼
        POST /api/v1/ingest/batch          POST /api/v1/agent/enroll
                 │
┌────────────────▼──────────────── 1 VM (Docker Compose) ─────────────┐
│  Caddy (TLS automático) → API ASP.NET Core 8                        │
│   ├── ingestão: INSERT multi-row ON CONFLICT DO NOTHING (Dapper)    │
│   ├── atualiza device_current_state (projeção "agora")              │
│   └── ack: { accepted, duplicates, rejected, config?, commands? }   │
│  Worker (mesmo deploy): pipeline de intervalização (cursores dirty) │
│   ├── raw_events ──► activity_intervals (máquina de estados §7.3)   │
│   ├── activity_intervals ──► daily_device_summaries/daily_app_usage │
│   └── jobs: partições, retenção/purga, housekeeping, exports CSV    │
│  PostgreSQL 16 gerenciado (região BR) — particionado por tempo      │
│  Serilog → Seq · Sentry · /healthz                                  │
└────────────────┬──────────────────────────────────────────────────────┘
                 │ REST /api/v1 (JWT, polling 60 s)
                 ▼
        Portal React + TS (SPA servida pelo ASP.NET Core)
        dashboard "agora" · timeline device/equipe · relatórios CSV
        configurações · privacidade/DSR · transparência
```

### 3.2 Arquitetura do agente (por que dois processos)

Desde o Windows Vista existe **Session 0 Isolation**: serviços rodam na sessão 0, isolada das sessões interativas. `GetForegroundWindow`, `GetWindowTextW` e `GetLastInputInfo` operam sobre a window station/desktop da sessão **chamadora** — chamadas a partir do serviço retornariam dados da sessão 0 (vazia). Portanto:

```
            MonitorAgentService.exe  (LocalSystem, Session 0)
            ─ orquestração, fila SQLite, envio HTTP, config,
              watchdog, auto-update, eventos WTS/Power
                    │
   WTSQueryUserToken(sessionId)  ─►  exige SE_TCB_NAME (só LocalSystem)
   DuplicateTokenEx → CreateEnvironmentBlock → CreateProcessAsUser
                    │
                    ▼  (um helper POR sessão interativa ativa)
            MonitorAgentSession.exe --session {id}
            (token do PRÓPRIO usuário, baixo privilégio)
            ─ janela ativa, título, idle, heartbeat de sessão,
              tray icon, janela de transparência, NOTICE_ACK
                    │
            named pipe \\.\pipe\monitoragent.{sessionId}
            DACL: GENERIC_WRITE só para o SID do usuário da sessão + SYSTEM
            protocolo: JSON delimitado por linha (helper→serviço: eventos;
            serviço→helper: config/comandos)
```

O helper **não** tem acesso à fila SQLite nem ao device token — toda persistência e comunicação externa é do serviço. O serviço **não escuta portas** (apenas cliente HTTPS de saída).

---

## 4. Stack obrigatória

Decisões fechadas. NÃO reabrir (em particular: não reabrir Blazor — a justificativa completa está na seção de design 03; resumo: o produto é visualização de dados densa, pior caso do Blazor e melhor caso do React).

| Camada | Tecnologia | Observações |
|---|---|---|
| Agente — serviço | **.NET 8 LTS** Worker Service (`UseWindowsService()`), `MonitorAgentService.exe`, LocalSystem, Automatic (Delayed Start) | P/Invoke via CsWin32; planejar retarget .NET 10 LTS antes de nov/2026 |
| Agente — helper | **WinForms mínimo** (`NotifyIcon` + janela de status), `MonitorAgentSession.exe`, token do usuário | WinForms, não WPF (footprint) |
| Agente — publicação | **self-contained, single-file, win-x64** (`PublishReadyToRun=true`, sem trimming) | Zero dependência de runtime no parque. SEM build win-arm64 no MVP |
| Agente — fila/HTTP/JSON | `Microsoft.Data.Sqlite` (WAL) · `HttpClient` + Polly · `System.Text.Json` source-generated | |
| Agente — instalador | **MSI (WiX Toolset)**, per-machine, assinado Authenticode | Silencioso: `msiexec /i MonitorAgent.msi /qn ENROLLKEY=... SERVERURL=...` |
| Backend — API | **ASP.NET Core 8** — Controllers para o portal; Minimal APIs para `/agent/enroll` e `/ingest/batch` | |
| Backend — dados | **EF Core 8** (CRUD/migrations) + **Dapper/Npgsql puro** nos hot paths (ingestão, agregações) | |
| Banco | **PostgreSQL 16 gerenciado em região brasileira** — Azure Database for PostgreSQL Flexible Server, **Brazil South** (ou equivalente AWS sa-east-1) | Particionamento nativo por tempo via migrations (SEM pg_partman) |
| Jobs | Worker de background **no mesmo deploy** (container `worker` no mesmo Compose/solution), Quartz.NET + `pg_advisory_lock` | |
| Deploy | **1 VM com Docker Compose** (`caddy`, `api`, `worker`, `seq`) atrás de **Caddy** (TLS automático) | Banco fora do Compose (gerenciado) |
| Observabilidade MVP | **Serilog → Seq**, **Sentry**, `/healthz` + monitor de uptime externo | OTel/Prometheus/Grafana é v1.1 |
| Portal | **React + TypeScript + Vite + Tailwind CSS + shadcn/ui + ECharts + TanStack Query** (+ TanStack Table, React Router, date-fns) | SPA servida como assets estáticos do ASP.NET Core; tipos TS gerados do OpenAPI |
| Testes | xUnit + Testcontainers-Postgres (isolamento multi-tenant, pipeline); Vitest + Testing Library; Playwright (3 fluxos E2E) | |
| CI/CD | GitHub Actions: build+test → docker buildx → GHCR → deploy SSH (`compose pull && up -d`); migrations como bundle pré-deploy | Staging auto (main), prod manual |

**SO suportado: Windows 10 1809+ e Windows 11, x64** (sem build win-arm64 no MVP; SKU Server/RDS = não suportado — Seção 6.9).

Multi-tenancy (reforço de stack): pool model — schema único, `tenant_id` em toda tabela, EF global query filter + interceptor de `SaveChanges` que carimba/valida `TenantId`, lookup sempre por `(tenant_id, id)`, IDs expostos sempre UUIDv7. Recurso de outro tenant retorna **404** (nunca 403).

---

## 5. CONTRATO CANÔNICO agente↔backend

**Esta é a seção mais importante do documento.** Agente, endpoint de ingestão, pipeline de intervalização e portal usam EXATAMENTE estes nomes, campos e números. Qualquer divergência é bug de contrato.

### 5.1 Endpoints do agente: dois de registro/ingestão (+ o manifesto de auto-update, somente leitura — Seção 6.7)

| Endpoint | Auth | Função |
|---|---|---|
| `POST /api/v1/agent/enroll` | enrollment key (no body) | Registro do device; devolve `device_id`, `device_token`, `config` |
| `POST /api/v1/ingest/batch` | `Authorization: Bearer dt_...` (device token) | Ingestão de lote; o **ack** é o único canal de config e comandos |
| `POST /api/v1/agent/diagnostics` | `Authorization: Bearer dt_...` (device token) | F5 — upload MANUAL do ZIP de diagnóstico (`application/zip`, máx. 10 MB), disparado pelo item "Enviar diagnóstico ao suporte" do tray com confirmação do usuário. Só logs já redigidos; gravado em `{Exports:Directory}/diagnostics/` |

NÃO existem no MVP: endpoint separado de policy/config (`GET .../policy`), endpoint de rotação de token iniciado pelo agente, endpoint de ack de comandos. Config e comandos trafegam exclusivamente no ack do batch.

### 5.2 Envelope comum de evento

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---|---|
| `event_id` | string UUID **v7** | sim | Gerado no agente; ordenável por tempo; chave de idempotência |
| `seq` | int64 | sim | Sequência monotônica **por device**, persistida no SQLite (`AUTOINCREMENT`); o backend persiste e detecta lacunas |
| `type` | string | sim | Um dos 19 tipos da tabela 5.3 |
| `occurred_at` | string ISO-8601 **UTC** | sim | Relógio de parede no momento do evento; **imutável** após gravado na fila local (dedupe determinístico entre retries) |
| `tz_offset_min` | int | sim | Offset local em minutos (ex.: `-180`) |
| `mono_ms` | int64 | sim | `GetTickCount64` no momento do evento (imune a ajuste de relógio) |
| `boot_id` | string UUID | sim | GUID novo por boot; com `mono_ms` reconstrói a ordem real mesmo com relógio adulterado |
| `session_id` | int ou null | quando aplicável | Sessão Windows (null em eventos de máquina) |
| `windows_sid` | string ou null | quando aplicável | SID do usuário Windows |
| `windows_user` | string ou null | quando aplicável | `DOMINIO\usuario` |
| `data` | objeto | sim (pode ser `{}`) | Campos específicos do tipo |

### 5.3 Tabela canônica de tipos de evento do MVP (única — 19 tipos)

Esta tabela é usada por: agente (emissão), ingestão (validação), pipeline (máquina de estados) e portal (exibição). **`APPS_SNAPSHOT` foi CORTADO do MVP** (sem consumidor + minimização LGPD) — não implementar a coleta nem o tipo.

Os dois acréscimos à tabela original de 17 entraram na F5: o 18º (`AGENT_ERROR`) e o 19º (`UPDATE_FAILED`). Rollout **agente-primeiro** nos dois casos, porque um ingest anterior a eles apenas ignora o tipo desconhecido (regra no fim desta seção) sem rejeitar o lote.

| Tipo | Emissor | `data` (payload) | Papel no pipeline |
|---|---|---|---|
| `AGENT_START` | Serviço | `agent_version, os_version, os_build, hostname, boot_id, uptime_ms, start_reason: boot\|install\|update\|crash_recovery\|service_restart, monitors, is_vm, join_type: ad\|aad\|workgroup` | Atualiza inventário do device; início de cobertura |
| `AGENT_STOP` | Serviço | `reason: shutdown\|service_stop\|update\|uninstall` | Fecha intervalo corrente → `off_clean` |
| `SESSION_START` | Serviço (WTS logon) | `logon_type: console\|rdp` | Abre cobertura da sessão |
| `SESSION_END` | Serviço (WTS logoff) | `{}` | Fecha intervalo corrente → `off_clean` |
| `LOCK` | Serviço (WTS lock) | `{}` | Fecha intervalo → abre `locked` (lock vence idle) |
| `UNLOCK` | Serviço (WTS unlock) | `{}` | Fecha `locked` → abre `active` |
| `ACTIVE_WINDOW_CHANGED` | Helper | `process_name` ("chrome.exe", lowercase), `exe_path`, `app_id` (AUMID UWP, opcional), `window_title` (string ou null se política APP_ONLY/processo ignorado), `title_masked: bool` | Fecha intervalo corrente → abre `active(app)` |
| `IDLE_START` | Helper | **`last_input_at`** (ISO-8601 UTC) — obrigatório | Fecha o intervalo ativo **RETROATIVAMENTE em `last_input_at`** → abre `idle` a partir de `last_input_at` |
| `IDLE_END` | Helper | `idle_duration_ms` | Fecha `idle` → abre `active` com o último app conhecido |
| `HEARTBEAT` | Helper (sessão) e Serviço (máquina sem usuário logado) | `state: active\|idle\|locked\|no_session, foreground_process, idle_ms, queue_depth` + saúde operacional injetada pelo SERVIÇO: `dead_letter_count, last_reject_code, working_set_mb, queue_db_bytes` (`no_session`: máquina ligada sem sessão interativa; campos de sessão null no heartbeat de máquina) | Prova de vida; mantém intervalo aberto; alimenta `last_seen_at` |
| `SYSTEM_SUSPEND` | Serviço (Power) | `{}` | Fecha intervalo → `off_clean` |
| `SYSTEM_RESUME` | Serviço (Power) | `sleep_duration_ms` (estimado por wall-clock) | Reabre cobertura |
| `TIME_CHANGED` | Serviço | `old_utc, new_utc, delta_ms, new_tz_offset_min` | Marca eventos vizinhos como suspeitos de relógio; atualiza tz do device |
| `EVENTS_DROPPED` | Serviço | `count, oldest_dropped_at, reason: retention_cap\|rate_limit\|pipe_overflow` (`pipe_overflow`: buffer volátil do helper cheio — contado pelo helper e reportado ao serviço na reconexão) | Gap explicado na timeline ("dados descartados") |
| `AGENT_TAMPER` | Serviço | `reason: helper_killed\|helper_killed_repeatedly\|pipe_denied` | Sinalização no painel de saúde |
| `NOTICE_ACK` | Helper | `notice_version, shown_at` | Evidência de ciência LGPD (Seção 9.4); persistido e consultável por device/usuário |
| `POLICY_APPLIED` | Serviço | `config_version` | Confirma aplicação de config; auditável |
| `AGENT_ERROR` | Serviço e Helper | `error_type` (nome do tipo da exceção), `stack_hash` (SHA-256 truncado da pilha), `count` (ocorrências desde o último evento do mesmo `error_type`) — **JAMAIS a `message` crua da exceção**, que pode conter caminho, título de janela ou usuário. Limite de taxa: **máx. 1 evento por `error_type` por hora**, e as ocorrências suprimidas viram o `count` | Neutro no pipeline; falha do agente visível no painel de saúde em vez de morrer no log da máquina |
| `UPDATE_FAILED` | Serviço | `from_version`, `to_version`, `reason: download\|hash\|signature\|install` — a ETAPA do auto-update que reprovou, **jamais a mensagem crua da exceção**. Não existe evento de sucesso (o sucesso é o `AGENT_START{start_reason:"update"}` da versão nova) nem motivo `rollback` (o agente não desfaz atualização) | Neutro no pipeline; materializado em `devices.last_update_failure_*` e lido pela distribuição de versões da frota (`GET /devices/version-summary`) |

**Regra de ingestão para tipo desconhecido:** ignorar o evento e incrementar métrica (`ingest_unknown_type_total`). **JAMAIS rejeitar o lote inteiro** por causa de um tipo desconhecido — garante compatibilidade quando agente novo falar com backend velho e vice-versa.

### 5.4 Lote de ingestão — exemplo completo

`POST /api/v1/ingest/batch` · Headers: `Authorization: Bearer dt_...`, `Content-Type: application/json`, `Content-Encoding: gzip` (recomendado; títulos comprimem ~85%).

```json
{
  "batch_id": "01976f2a-0001-7aaa-b111-000000000001",
  "agent_version": "1.0.3",
  "sent_at": "2026-06-09T14:32:07.512Z",
  "config_version": 4,
  "events": [
    {
      "event_id": "01976f2a-3b10-7cc4-a1e2-9d8f6b3c0a11",
      "seq": 48211,
      "type": "UNLOCK",
      "occurred_at": "2026-06-09T14:25:01.003Z",
      "tz_offset_min": -180,
      "mono_ms": 86400123,
      "boot_id": "f0a1b2c3-d4e5-4f60-8a9b-0c1d2e3f4a5b",
      "session_id": 1,
      "windows_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "windows_user": "ACME\\maria.silva",
      "data": {}
    },
    {
      "event_id": "01976f2a-8e55-7d12-b3f4-1a2b3c4d5e6f",
      "seq": 48212,
      "type": "ACTIVE_WINDOW_CHANGED",
      "occurred_at": "2026-06-09T14:25:06.118Z",
      "tz_offset_min": -180,
      "mono_ms": 86405238,
      "boot_id": "f0a1b2c3-d4e5-4f60-8a9b-0c1d2e3f4a5b",
      "session_id": 1,
      "windows_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "windows_user": "ACME\\maria.silva",
      "data": {
        "process_name": "excel.exe",
        "exe_path": "C:\\Program Files\\Microsoft Office\\root\\Office16\\EXCEL.EXE",
        "app_id": null,
        "window_title": "Orcamento_2026.xlsx - Excel",
        "title_masked": false
      }
    },
    {
      "event_id": "01976f2b-1c20-7e33-c5d6-7e8f9a0b1c2d",
      "seq": 48213,
      "type": "IDLE_START",
      "occurred_at": "2026-06-09T14:31:40.000Z",
      "tz_offset_min": -180,
      "mono_ms": 86799120,
      "boot_id": "f0a1b2c3-d4e5-4f60-8a9b-0c1d2e3f4a5b",
      "session_id": 1,
      "windows_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "windows_user": "ACME\\maria.silva",
      "data": { "last_input_at": "2026-06-09T14:26:40.000Z" }
    },
    {
      "event_id": "01976f2b-5a01-7f44-d7e8-9f0a1b2c3d4e",
      "seq": 48214,
      "type": "HEARTBEAT",
      "occurred_at": "2026-06-09T14:32:00.001Z",
      "tz_offset_min": -180,
      "mono_ms": 86819121,
      "boot_id": "f0a1b2c3-d4e5-4f60-8a9b-0c1d2e3f4a5b",
      "session_id": 1,
      "windows_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "windows_user": "ACME\\maria.silva",
      "data": { "state": "idle", "foreground_process": "excel.exe", "idle_ms": 320001, "queue_depth": 4 }
    },
    {
      "event_id": "01976f2b-c890-7a66-8e9f-3c4d5e6f7a8b",
      "seq": 48215,
      "type": "HEARTBEAT",
      "occurred_at": "2026-06-09T15:45:00.000Z",
      "tz_offset_min": -180,
      "mono_ms": 86879121,
      "boot_id": "f0a1b2c3-d4e5-4f60-8a9b-0c1d2e3f4a5b",
      "session_id": 1,
      "windows_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "windows_user": "ACME\\maria.silva",
      "data": { "state": "idle", "foreground_process": "excel.exe", "idle_ms": 380001, "queue_depth": 5 }
    }
  ]
}
```

O 5º evento simula relógio adiantado na máquina: seu `occurred_at` (15:45) está mais de 5 min à frente do `server_time` do ack (`14:32:07.852Z` — Seção 5.5), logo será rejeitado individualmente com reason `timestamp_in_future`. Já o 1º evento (`UNLOCK`) é um reenvio após timeout de um lote anterior — deduplicado por `event_id`, conta em `duplicates` no ack.

Notas do lote: `device_id` NÃO vai no body — o servidor o resolve do device token. O **skew de relógio é calculado no servidor** (`received_at − sent_at`, média móvel dos últimos 5 lotes, persistido em `devices.clock_offset_ms`); o agente NÃO envia skew. **Lote vazio (`events: []`) é válido e funciona como keep-alive** — atualiza `last_seen_at` e entrega config/comandos.

### 5.5 Resposta (ack) — exemplo completo

```json
{
  "accepted": 3,
  "duplicates": 1,
  "rejected": [
    { "event_id": "01976f2b-c890-7a66-8e9f-3c4d5e6f7a8b", "reason": "timestamp_in_future" }
  ],
  "server_time": "2026-06-09T14:32:07.852Z",
  "config_version": 5,
  "config": {
    "heartbeat_sec": 60,
    "active_window_poll_sec": 5,
    "idle_threshold_sec": 300,
    "window_title_policy": "MASKED_PATTERNS",
    "masked_patterns": ["(?i)senha", "(?i)\\bbanco\\b", "\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}"],
    "ignored_processes": ["keepass.exe", "1password.exe", "bitwarden.exe", "logonui.exe", "lockapp.exe", "consent.exe"],
    "collection_window": { "mode": "ALWAYS", "days": null, "start": null, "end": null },
    "transparency_url": "https://app.exemplo.com.br/transparencia/acme",
    "notice_text": null,
    "notice_version": 1,
    "device_transparency_url": "https://app.exemplo.com.br/t/01976f2a-0001-7aaa-b111-000000000001"
  },
  "commands": [
    { "id": "01976f2c-0000-7aaa-b111-00000000c0de", "type": "UNENROLL", "payload": {} }
  ]
}
```

Aritmética do exemplo (fórmula da Seção 5.6): 5 recebidos = 3 aceitos + 1 duplicado (o `UNLOCK` reenviado de um lote anterior) + 1 rejeitado (o 5º evento, HEARTBEAT com relógio adiantado).

Regras do ack:
- `config` só vem quando o `config_version` enviado pelo agente está desatualizado; caso contrário `config: null`. **A config é entregue EXCLUSIVAMENTE por este canal** (sem endpoint de policy, sem assinatura de config no MVP — TLS + device token bastam). Ao aplicar, o agente emite `POLICY_APPLIED { config_version }`.
- Objeto `config` completo (11 campos, sempre todos presentes): `heartbeat_sec`, `active_window_poll_sec`, `idle_threshold_sec`, `window_title_policy` (`FULL` | `MASKED_PATTERNS` | `APP_ONLY`), `masked_patterns[]`, `ignored_processes[]`, `collection_window` (`{mode: ALWAYS | BUSINESS_HOURS, days, start, end}`), `transparency_url`, `notice_text`, `notice_version`, `device_transparency_url`.
- `device_transparency_url` é a página pública DAQUELE dispositivo (`/t/{token}`, de `devices.transparency_token`): a mesma política da organização MAIS o bloco "Este dispositivo". É o **único** caminho pelo qual o token chega ao agente, e é opcional de propósito: `null` para servidor anterior ao campo ou device sem token, e nesse caso o tray abre o `transparency_url` por slug. A url carrega um segredo de baixo valor: nunca vai para log, nem para query string de telemetria.
- `notice_text` (F5) é o CORPO do aviso de ciência definido pelo tenant; `null` = o agente usa o texto padrão embutido nele. O enquadramento jurídico ("isto registra a sua ciência, não é um pedido de consentimento" + como ver a coleta em tempo real) é **fixo no agente e sempre concatenado** — o tenant não consegue publicar um aviso que transforme o `NOTICE_ACK` em consentimento. `notice_version` versiona o aviso: bump reexibe na frota (o helper compara com a versão confirmada localmente) e gera novo `NOTICE_ACK`.
- `commands` no MVP contém **apenas `UNENROLL`** (`ROTATE_TOKEN`, `UPDATE_AGENT`, `PAUSE` são v1.1 — não implementar handlers). Ao receber `UNENROLL`: o agente **para a coleta e DESCARTA a fila local** (revogação definitiva). Sem endpoint de ack de comando: o servidor marca a entrega ao incluir no ack; o comando é idempotente se reentregue.
- Erros HTTP: `401` token inválido/revogado (tratado como transitório: o agente **mantém a fila** e tenta re-enroll a cada **1 h** com a enrollment key persistida); `413` payload grande demais; `422` body sintaticamente malformado (JSON inválido) ou lote com > 500 eventos (reason `batch_too_large`) — únicos casos de rejeição do lote inteiro; `429`/`503` com `Retry-After` (agente respeita).

### 5.6 Validação na ingestão

- Limites: máx. **500 eventos/lote**; body comprimido máx. 1 MB; descomprimido máx. 5 MB (validar — proteção contra zip bomb); `window_title` truncado a 256 chars no agente e revalidado no servidor.
- Lote com > 500 eventos → `422` com reason `batch_too_large` (lote inteiro rejeitado; o agente divide e reenvia — pelo contrato ele nunca envia > 500).
- Janela temporal: rejeitar individualmente (listar em `rejected`) evento com `occurred_at < now − 14 dias` (reason `timestamp_too_old`) ou `occurred_at > now + 5 min` (reason `timestamp_in_future`). A janela de 14 dias dá folga de 2× sobre o buffer offline de 7 dias.
- Idempotência: `INSERT ... ON CONFLICT (device_id, event_id, occurred_at) DO NOTHING` multi-row (1 round-trip); `duplicates = recebidos − inseridos − rejeitados − ignorados`.
- Rate limit por device: sustentado 6 lotes/min, burst 30 (token bucket em memória — 1 instância de API no MVP); cota diária dura 100.000 eventos/device/dia → `429`.
- Tipo desconhecido: ignorar + métrica (regra da Seção 5.3).
- Ack somente **após commit** da transação de insert.

### 5.7 Enrollment

`POST /api/v1/agent/enroll` — sem autenticação prévia; rate limit 10/min por IP.

```json
{
  "enrollment_key": "ek_4Qz8kT2mWx9P",
  "hostname": "NB-JOAO",
  "machine_fingerprint": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "os_version": "Windows 11 Pro 23H2 (22631)",
  "agent_version": "1.0.3"
}
```

Resposta `201`:

```json
{
  "device_id": "01976f00-aaaa-7bbb-8ccc-dddddddddddd",
  "device_token": "dt_Jh3K...256-bits-base64url...",
  "config_version": 5,
  "config": { "...": "objeto config completo, mesmos 11 campos da Seção 5.5" }
}
```

- **Enrollment key**: formato `ek_` + **12 chars** aleatórios (base62), por tenant, revogável no portal, com label/expiração/limite de usos opcionais. Armazenada como SHA-256 + prefixo visível (ex.: `ek_4Qz8`).
- **Device token**: opaco (`dt_` + 256 bits base64url), armazenado como SHA-256, escopo exclusivo dos endpoints `/api/v1/agent/*` e `/api/v1/ingest/*`. Revogação **manual** via portal; **SEM rotação automática no MVP**.
- **`machine_fingerprint`** = SHA-256 de (`MachineGuid` de `HKLM\SOFTWARE\Microsoft\Cryptography` + serial do BIOS). Se `(tenant_id, machine_fingerprint)` já existe → **re-enroll idempotente**: revoga o token antigo, emite novo, preserva o device e seu histórico.
- No agente, o token é cifrado com **DPAPI escopo máquina** (`CRYPTPROTECT_LOCAL_MACHINE`) em `%ProgramData%\{Vendor}\MonitorAgent\`, ACL SYSTEM/Administrators.

### 5.8 TABELA DE NÚMEROS CANÔNICOS

Estes números aparecem em TODO o documento sempre com o mesmo valor. Se algum código/tela/teste usar outro valor, é bug.

| # | Parâmetro | Valor canônico |
|---|---|---|
| N1 | Polling de janela ativa | **5 s**, com dedupe (emite só mudança de `(process_name, título normalizado)`) |
| N2 | Heartbeat | **60 s** |
| N3 | Envio de lote | a cada **30 s** OU **500 eventos** (o que vier primeiro); lote vazio = keep-alive |
| N4 | Limiar de ociosidade (default) | **300 s** (5 min); faixa do protocolo **60–1800 s**; faixa da UI **3–15 min** |
| N5 | Fechamento retroativo do idle | `IDLE_START` fecha o intervalo ativo em **`last_input_at`** (NUNCA no timestamp do evento) |
| N6 | "Online agora" (dashboard de presença) | último contato ≤ **180 s** (3 heartbeats) |
| N7 | Gap que fecha intervalo (pipeline) | ≥ **600 s** sem evento → fecha no último evento e abre `no_data` |
| N8 | Buffer offline local | **7 dias** OU **50.000 eventos** OU **100 MB** (o que vier primeiro); expurgo FIFO + `EVENTS_DROPPED{reason:retention_cap}` |
| N9 | Janela de aceitação da ingestão | rejeita `occurred_at < now − 14 dias` ou `> now + 5 min` |
| N10 | Retenção `raw_events` | **90 dias** (fixa no MVP; configurável v1.1) |
| N11 | Retenção `activity_intervals` | **12 meses** |
| N12 | Retenção `daily_device_summaries` | **24 meses** |
| N13 | Retenção `audit_log` | **24 meses** |
| N14 | Retry de envio do agente | backoff exponencial 5s → 10s → 30s → 1m → 5m → 10m (teto), jitter ±20%; respeita `Retry-After` |
| N15 | Re-enroll após 401 | a cada **1 h**, mantendo a fila |
| N16 | Anti-flapping de título | mudança só de título (mesmo processo) em < 10 s → atualiza o último evento local em vez de emitir novo |
| N17 | Rate limit de `ACTIVE_WINDOW_CHANGED` no agente | máx. 1/s e 600/h por sessão; excedente coalescido + `EVENTS_DROPPED{reason:rate_limit}` |
| N18 | Polling do portal (dashboard/timeline de hoje) | **60 s** (sem websocket) |
| N19 | Watchdog do helper | relança após 5 s; máx. 5 relançamentos/10 min; ao exceder → `AGENT_TAMPER` + retry a cada 15 min |
| N20 | Intervalo mínimo persistido | descartar intervalos < 1 s; fundir adjacentes idênticos (mesmo estado+app+título) |
| N21 | Resolução da timeline (server-side) | fixa **1 min**; cap ~**3.000 intervalos** por resposta |
| N22 | Lockout de login do portal | 10 tentativas → 15 min |
| N23 | JWT de acesso / senha | 15 min / mínimo 12 chars (Argon2id) |
| N24 | Limite de trial | 25 devices |
| N25 | Dimensionamento alvo | ~2.500 devices (≈ 3–5 M eventos/dia; rajada de catch-up ~400 eventos/s) |

---

## 6. Especificação do AGENTE Windows

Você deve construir o agente como dois processos (.NET 8, Seção 4) + MSI. Tudo nesta seção obedece ao contrato da Seção 5.

### 6.1 Ciclo de vida serviço ↔ helper

1. O serviço registra `SERVICE_ACCEPT_SESSIONCHANGE` e trata `SERVICE_CONTROL_SESSIONCHANGE` (`WTS_SESSION_LOGON`, `WTS_SESSION_LOGOFF`, `WTS_SESSION_LOCK`, `WTS_SESSION_UNLOCK`, `WTS_CONSOLE_CONNECT/DISCONNECT`, `WTS_REMOTE_CONNECT/DISCONNECT`) com o `sessionId`. Também trata `SERVICE_CONTROL_POWEREVENT` (`PBT_APMSUSPEND`, `PBT_APMRESUMEAUTOMATIC`/`PBT_APMRESUMESUSPEND`) e `SERVICE_CONTROL_SHUTDOWN`/`PRESHUTDOWN`.
2. No start, enumera sessões existentes com `WTSEnumerateSessions` (estado `WTSActive`) e, para cada sessão interativa: `WTSQueryUserToken(sessionId)` → `DuplicateTokenEx` → `CreateEnvironmentBlock` → `CreateProcessAsUser` lançando `MonitorAgentSession.exe --session {id}`. **Um helper por sessão** (Fast User Switching suportado nativamente).
3. IPC: named pipe `\\.\pipe\monitoragent.{sessionId}` criado pelo serviço, DACL com `GENERIC_WRITE` apenas para o SID do usuário daquela sessão + SYSTEM. Mensagens JSON delimitadas por linha: helper→serviço (eventos), serviço→helper (config aplicável: `idle_threshold_sec`, `active_window_poll_sec`, `heartbeat_sec`, política de títulos, processos ignorados, `collection_window`, `transparency_url`).
4. **Watchdog** (N19): o serviço monitora o handle do helper (`WaitForSingleObject`); se morrer, relança após 5 s, máx. 5 relançamentos em 10 min; ao exceder → `AGENT_TAMPER{reason:"helper_killed_repeatedly"}` e novas tentativas a cada 15 min. Morte única do helper → `AGENT_TAMPER{reason:"helper_killed"}`. Falha de DACL/acesso ao pipe → `AGENT_TAMPER{reason:"pipe_denied"}`.
5. Sessão desconectada mas não encerrada (RDP/FUS): o helper pausa a coleta de janela (a sessão não tem desktop visível) e mantém heartbeat. Logoff → o serviço emite `SESSION_END`.

### 6.2 Coleta — APIs Win32 concretas

| Dado | API / Mecanismo | Onde roda | Frequência |
|---|---|---|---|
| Janela ativa | `GetForegroundWindow` → `GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `QueryFullProcessImageNameW`; título via `GetWindowTextW` (lê o cache do USER — não envia `WM_GETTEXT` cross-process, logo não trava com app congelado) | Helper | Polling **5 s** com dedupe (N1) |
| Apps UWP (janela do `ApplicationFrameHost.exe`) | `EnumChildWindows` na janela frame, localizar child com PID ≠ frame; AUMID via `GetApplicationUserModelId` no processo real | Helper | No mesmo polling |
| Ociosidade | `GetLastInputInfo` (por sessão) comparado a `GetTickCount64`; verificação a cada 5 s; limiar = `idle_threshold_sec` (default 300 s, N4). Ao cruzar o limiar → `IDLE_START{last_input_at}` com o instante real do último input | Helper | 5 s |
| Logon/logoff/lock/unlock/RDP | `SERVICE_CONTROL_SESSIONCHANGE` (WTS) — **não** usar EventLog de segurança (4800/4801 dependem de auditoria) nem SENS (legado) | Serviço | Evento |
| Suspensão/retomada | `SERVICE_CONTROL_POWEREVENT` | Serviço | Evento |
| Boot/uptime/shutdown | `GetTickCount64` no start; `SERVICE_CONTROL_SHUTDOWN`/`PRESHUTDOWN` com flush final da fila | Serviço | Start/stop |
| Usuário da sessão | `WTSQuerySessionInformation` (`WTSUserName`, `WTSDomainName`) + SID via token | Serviço | No logon |
| Inventário (hostname, SO, domínio, monitores, VM) | `GetComputerNameExW`, `RtlGetVersion`, `NetGetJoinInformation`, `GetSystemMetrics(SM_CMONITORS)`, CPUID hypervisor-bit + `Win32_ComputerSystem.Model` | Serviço | Enrollment e a cada `AGENT_START` |
| Mudança de relógio | Comparação contínua wall-clock (`GetSystemTimePreciseAsFileTime`) vs monotônico (`QueryPerformanceCounter`); desvio > 30 s → `TIME_CHANGED` | Serviço | Contínuo |

Regras de robustez do loop de coleta: `GetForegroundWindow` pode retornar NULL durante trocas — tratar sem emitir evento e sem crashar; janelas zumbis/processos finalizados entre chamadas → ignorar amostra. Dedupe (N1), anti-flapping (N16) e rate limit (N17) obrigatórios — testar com Spotify, Teams e navegadores com contadores no título.

**Semântica de duração:** o agente NÃO calcula duração de nada (Princípio 5). Evolução pós-MVP (não fazer agora): `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` event-driven com polling como fallback.

### 6.3 Privacidade no cliente (enforcement local)

Aplicado **antes de persistir na fila SQLite** — dado mascarado nunca toca o disco nem a rede:

- `window_title_policy` (da config, Seção 5.5):
  - `FULL`: título completo (truncado a 256 chars).
  - `MASKED_PATTERNS` (**default de fábrica**): aplica as regex de `masked_patterns[]` ao título; trecho que casa é substituído por `***`; `title_masked: true` quando houve substituição.
  - `APP_ONLY`: `window_title: null`, só `process_name`.
- **Rebaixamento automático para `APP_ONLY` em navegação anônima/privada** (heurística best-effort por sufixo de título, qualquer que seja a política vigente): `"(navegação anônima)"` / `"(navegação anónima)"` / `"(Incognito)"` (Chrome pt-BR / pt-PT / en-US), `"InPrivate"` (Edge, mesmo sufixo em qualquer idioma), `"(navegação privativa)"` / `"(navegação privada)"` / `"(Private Browsing)"` (Firefox pt-BR / pt-PT / en-US). Case-insensitive, comparação no fim do título.
- `ignored_processes[]` (lista do tenant + defaults de fábrica: `keepass.exe`, `1password.exe`, `bitwarden.exe`, `logonui.exe`, `lockapp.exe`, `consent.exe` + processos do próprio agente): emite `ACTIVE_WINDOW_CHANGED` com `process_name: "(privado)"` e `window_title: null` — **o tempo conta, o conteúdo não**.
- `collection_window` (da config): em `mode: BUSINESS_HOURS`, fora de `days/start/end` o helper NÃO coleta janela ativa nem idle; o serviço continua emitindo eventos de sessão/energia e heartbeat de máquina (uptime/login apenas). Em `mode: ALWAYS`, coleta contínua. A escolha é do tenant no onboarding (Seção 8.3) — quem decide é a controladora.
- Logs de diagnóstico **nunca** contêm títulos de janela nem nomes de usuário em nível Information (apenas em Debug, ativado por config com aviso).

### 6.4 Buffer local e resiliência offline

- **SQLite** em `C:\ProgramData\{Vendor}\MonitorAgent\queue.db`, `journal_mode=WAL`, `synchronous=NORMAL`. ACL do diretório: SYSTEM + Administrators (Full), sem acesso a usuários comuns.
- Tabelas: `events(seq INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT UNIQUE, type TEXT, payload TEXT, created_at_utc TEXT, sent INTEGER DEFAULT 0)` — o `seq` do envelope É este autoincrement; `kv` (device_id, device_token cifrado DPAPI, enrollment key (cifrada DPAPI), config em cache, config_version, boot_id, flag de shutdown limpo, marca d'água de envio); `dead_letter` (cap 5 MB).
- **Envio**: em ordem de `seq`, lote de até 500 eventos a cada 30 s (N3). Eventos marcados `sent=1` somente após HTTP 200 (considerando `accepted+duplicates+rejected` como processados); deleção física dos enviados a cada 10 min.
- **Retry**: N14. Em 4xx de validação que rejeite o lote inteiro (422): mover o lote para `dead_letter` e prosseguir — um lote ruim não pode travar a fila. 401: N15 (mantém fila, re-enroll 1 h). `UNENROLL` no ack: para coleta e **descarta a fila** (única situação de descarte deliberado além do FIFO).
- **Retenção offline**: N8 (7 dias OU 50.000 eventos OU 100 MB; FIFO + `EVENTS_DROPPED`).
- **Queda de energia / kill**: WAL preserva a fila; próximo start sem flag de shutdown limpo → `AGENT_START{start_reason:"crash_recovery"}`.
- **Proxy corporativo**: suporte a proxy de sistema (WinHTTP) + `PROXYURL` opcional no MSI; falha de TLS por inspeção MITM gera diagnóstico claro no log e estado "erro de certificado" no tray.

### 6.5 Tray, transparência e NOTICE_ACK

- `NotifyIcon` **sempre visível**, tooltip "Monitoramento corporativo ativo — {NomeDaEmpresa}". **Sem opção "Sair"** no menu; sem flag de ocultação (a opção não existe no código).
- Menu: **"O que está sendo coletado agora"** (janela em tempo real: app ativo, título capturado — ou mascarado/null —, estado ativo/idle, último envio, `config_version` aplicada, device_id) · **"Política de monitoramento"** (abre o `device_transparency_url` da config, a página deste dispositivo, caindo no `transparency_url` por slug quando ele não vem) · **"Status da conexão"** · **"Sobre"** (versão, device_id).
- **NOTICE_ACK (gate LGPD):** no primeiro logon de cada usuário Windows após a instalação, o helper exibe aviso (toast + janela): *"Esta máquina é monitorada por {Empresa} — clique para ver o que é coletado"*, com link para a janela de transparência e botão **"Entendi"**. O clique emite `NOTICE_ACK{notice_version, shown_at}` — evidência de ciência para a controladora (NÃO é consentimento; é ciência). Persistir localmente que o usuário já confirmou (não reexibir a cada logon); reexibir se `notice_version` mudar. O CORPO do aviso pode ser gerenciado pelo tenant (`notice_text` da config — Seção 5.5); o enquadramento jurídico é **fixo no agente e sempre concatenado**, e `notice_version`/`notice_text` chegam ao helper pelo mesmo caminho do `transparency_url` (config entregue pelo ack e repassada no pipe).
- `MonitorAgentSession.exe --diag` gera ZIP de suporte (logs + config sanitizada + contadores), sem UI.
- Item **"Enviar diagnóstico ao suporte"** no menu do tray (F5): pede confirmação declarando o que vai e o que NÃO vai no pacote (só logs redigidos; sem título de janela, usuário ou conteúdo), e o **serviço** — não o helper — empacota o MESMO ZIP do `--diag` e faz o `POST /api/v1/agent/diagnostics` com o device token. Resultado (sucesso/falha) volta ao tray como balão.

### 6.6 Instalador MSI e operação em frota

- **WiX**, MSI per-machine, assinado **Authenticode** (OV/EV — reputação SmartScreen/Defender; submeter ao Microsoft Defender antes de instalar em cliente).
- Instalação silenciosa: `msiexec /i MonitorAgent.msi /qn ENROLLKEY=ek_4Qz8kT2mWx9P SERVERURL=https://api.produto.com.br [PROXYURL=...] [NOENROLL=1]` — compatível com GPO/Intune/RMM. `NOENROLL=1` para golden image (enrolls no primeiro boot real; evita identidade clonada).
- Binários em `%ProgramFiles%\{Vendor}\MonitorAgent\`; dados/fila/logs em `%ProgramData%\{Vendor}\MonitorAgent\`.
- Serviço: Automatic (Delayed Start); recovery do SCM (`sc failure`): restart 10 s / 60 s / 300 s, reset 1 dia.
- Desinstalação exige admin; gera flush final + `AGENT_STOP{reason:"uninstall"}`. Parar o serviço exige admin (comportamento padrão do SCM — NÃO alterar DACL do serviço no MVP). Usuário comum pode matar o helper → watchdog + `AGENT_TAMPER` + gap visível no portal. Nenhuma técnica de ocultação.
- Logs: Serilog em `%ProgramData%\{Vendor}\MonitorAgent\logs\` (`service-.log`, `session-{sid}-.log`), rotação diária, 5 MB/arquivo, máx. 10 arquivos.

### 6.7 Auto-update (simples — canal único)

- Verificação a cada 6 h (jitter até 30 min): `GET {SERVERURL}/api/v1/agent/update-manifest?current=1.0.3` → `{version, url, sha256, min_version}`.
- Download em background; verificação de **SHA-256 do manifesto + assinatura Authenticode do MSI** antes de executar; instalação via `msiexec /i /qn` (major upgrade preserva `%ProgramData%` — fila e identidade). `min_version`: abaixo dela o update é forçado imediatamente.
- A verificação Authenticode é real (`WinVerifyTrust`, `WINTRUST_ACTION_GENERIC_VERIFY_V2`, mais checagem de que o `Subject` do signatário contém o CN esperado) e fica atrás da flag `verify_authenticode` do `install.json` — **default `false`** enquanto o certificado de code signing não foi comprado (`docs/runbooks/comprar-certificado-codesigning.md`); o release empacotado com o certificado liga a flag e o `expected_signer_cn`. Com a flag ligada, MSI sem assinatura confiável (ou de outro titular) é descartado sem instalar.
- **Rollback = publicar a versão anterior no manifesto** (major-upgrade com downgrade controlado). **SEM anéis canary/percentuais, SEM canal beta no MVP** (v1.1).

### 6.8 Metas de consumo (gate de release, medido em VM 2 vCPU/4 GB)

| Métrica | Alvo |
|---|---|
| CPU média (serviço + helper) | **< 1%** (pico < 5% por 1 s no polling) |
| RAM working set somada | **< 100 MB** |
| Disco total (binários + fila + logs) | < 400 MB |
| Rede | < 5 MB/dia/dispositivo (lotes gzip) |

### 6.9 Casos extremos (comportamento especificado)

| Caso | Comportamento |
|---|---|
| Fast User Switching / múltiplos usuários | Um helper por sessão; todo evento carrega `session_id` + `windows_sid`; sessão desconectada pausa coleta de janela, mantém heartbeat |
| RDS/Citrix/Terminal Server | **Não suportado no MVP**: detectar SKU Server/host multi-sessão e marcar o device como `os_type:"server"` / "não suportado" no portal; sem trabalho de detecção de VDI nem runbook de golden image além do `NOENROLL=1` |
| Hibernação/suspensão | `SYSTEM_SUSPEND` fecha intervalos no backend (`off_clean`); `SYSTEM_RESUME{sleep_duration_ms}` reabre; tampa fechada sem suspend coberta pelo gap de 600 s (N7) |
| Mudança de horário/fuso/DST | Tudo em UTC + `tz_offset_min` por evento; salto wall-clock vs monotônico > 30 s → `TIME_CHANGED`; `mono_ms`+`boot_id` preservam ordem real |
| Múltiplos monitores | Foreground é único no Windows — sem mudança; `monitors` é inventário no `AGENT_START` |
| Sem usuário logado | Serviço emite `HEARTBEAT` de máquina (sem `session_id`) — viabiliza "ligada vs em uso" |
| Queda de energia / kill -9 | WAL preserva fila; `AGENT_START{start_reason:"crash_recovery"}`; backend fecha intervalo órfão pelo gap (N7) |
| Relógio adulterado pelo usuário | `TIME_CHANGED` + correção por `clock_offset_ms` no servidor + ordem por `mono_ms`/`boot_id` |

---

## 7. Especificação do BACKEND

### 7.1 Modelo de dados (todas as tabelas)

Convenções: `id uuid PK` (UUIDv7), `tenant_id uuid NOT NULL` em toda tabela de dados (exceto `app_catalog`, global), `created_at/updated_at timestamptz`. EF Core global query filter por `tenant_id` em todas as entidades + interceptor de `SaveChanges` que carimba `TenantId` e lança exceção em divergência. Queries Dapper recebem `tenant_id` obrigatório por convenção de repositório.

#### Identidade e tenancy

```sql
CREATE TABLE organizations (
  id uuid PRIMARY KEY,
  name text NOT NULL,
  slug text UNIQUE NOT NULL,
  timezone text NOT NULL DEFAULT 'America/Sao_Paulo',  -- IANA; corte do "dia" dos agregados
  business_hours jsonb,            -- {"days":[1..5],"start":"08:00","end":"18:00"} p/ referência visual e collection_window
  plan text NOT NULL DEFAULT 'trial',      -- trial|essencial|pro
  device_limit int,                        -- 25 no trial (N24); enforcement no enroll
  status text NOT NULL DEFAULT 'active',   -- active|suspended|closing|closed
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (                 -- usuários do PORTAL
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  email citext NOT NULL,
  password_hash text,                -- Argon2id (64 MB, 3 iterações, paralelismo 4); NULL = convite pendente
  display_name text NOT NULL,
  role text NOT NULL CHECK (role IN ('owner','admin','viewer')),  -- enum extensível; SÓ 3 papéis no MVP
  mfa_secret_enc bytea,              -- TOTP cifrado (AES-GCM, chave no secret store); MFA OBRIGATÓRIA p/ owner/admin
  mfa_enabled boolean NOT NULL DEFAULT false,
  failed_login_count int NOT NULL DEFAULT 0,
  locked_until timestamptz,          -- lockout: 10 falhas → 15 min (N22)
  status text NOT NULL DEFAULT 'invited',  -- invited|active|disabled
  last_login_at timestamptz,
  UNIQUE (tenant_id, email)
);

CREATE TABLE invitations (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  email citext NOT NULL, role text NOT NULL,
  token_hash bytea NOT NULL,         -- SHA-256 do token do link
  expires_at timestamptz NOT NULL,   -- 7 dias
  accepted_at timestamptz, invited_by uuid REFERENCES users(id)
);

CREATE TABLE refresh_tokens (        -- refresh SIMPLES no MVP (sem famílias de rotação)
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  user_id uuid NOT NULL REFERENCES users(id),
  token_hash bytea NOT NULL,
  expires_at timestamptz NOT NULL,   -- 30 dias
  revoked_at timestamptz, user_agent text, ip inet
);
```

#### Dispositivos e enrollment

```sql
CREATE TABLE enrollment_keys (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  key_prefix text NOT NULL,          -- 'ek_4Qz8' (visível no portal)
  key_hash bytea NOT NULL,           -- SHA-256 da chave completa (ek_ + 12 chars)
  label text, max_uses int, use_count int NOT NULL DEFAULT 0,
  expires_at timestamptz, revoked_at timestamptz
);

CREATE TABLE devices (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  hostname text NOT NULL,
  display_name text,                  -- editável no portal; default hostname
  machine_fingerprint text NOT NULL,  -- SHA-256(MachineGuid + serial do BIOS); re-enroll idempotente
  os_version text, os_type text NOT NULL DEFAULT 'workstation',  -- workstation|server (server = não suportado)
  agent_version text,
  enrollment_key_id uuid REFERENCES enrollment_keys(id),
  token_hash bytea NOT NULL,          -- SHA-256 do device token vigente
  config_version int NOT NULL DEFAULT 1,
  tags text[],
  status text NOT NULL DEFAULT 'active',   -- active|paused|archived|revoked
  last_seen_at timestamptz,                -- atualizado a CADA batch (incl. vazio)
  clock_offset_ms bigint NOT NULL DEFAULT 0,  -- média móvel dos últimos 5 lotes
  tz_offset_min int,                  -- último offset reportado; badge de fuso no portal
  tz_iana text,                       -- quando disponível
  seq_max bigint NOT NULL DEFAULT 0,  -- maior seq visto; base da detecção de lacunas
  notice_acked_at timestamptz,        -- último NOTICE_ACK visto (atalho p/ painel LGPD)
  UNIQUE (tenant_id, machine_fingerprint)
);
CREATE INDEX ix_devices_tenant_lastseen ON devices (tenant_id, last_seen_at);

CREATE TABLE device_users (           -- usuários Windows observados
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  device_id uuid NOT NULL REFERENCES devices(id),
  windows_sid text NOT NULL, windows_username text NOT NULL,
  display_name text,                  -- editável no portal
  first_seen_at timestamptz NOT NULL, last_seen_at timestamptz NOT NULL,
  UNIQUE (tenant_id, device_id, windows_sid)
);

CREATE TABLE device_commands (        -- canal pull via ack; MVP: apenas UNENROLL
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, device_id uuid NOT NULL,
  type text NOT NULL CHECK (type = 'UNENROLL'),   -- ampliar enum na v1.1
  payload jsonb NOT NULL DEFAULT '{}',
  created_at timestamptz NOT NULL DEFAULT now(), delivered_at timestamptz
);

CREATE TABLE device_current_state (   -- projeção "agora", atualizada NO CAMINHO DA INGESTÃO
  tenant_id uuid NOT NULL, device_id uuid PRIMARY KEY,
  state text NOT NULL,                -- active|idle|locked|off_clean|no_data
  windows_sid text, windows_username text,
  foreground_process text, foreground_title text,   -- respeita mascaramento (vem mascarado do agente)
  state_since timestamptz,            -- "neste app/estado há X min"
  app_since timestamptz,
  last_contact_at timestamptz NOT NULL,
  updated_at timestamptz NOT NULL
);
```

Atualização de `device_current_state`: ao processar um batch, a API aplica os eventos relevantes (ordem por `seq`) — `ACTIVE_WINDOW_CHANGED` (estado `active` + app), `IDLE_START`/`IDLE_END`, `LOCK`/`UNLOCK`, `SESSION_END`/`AGENT_STOP`/`SYSTEM_SUSPEND` (→ `off_clean`), `HEARTBEAT` (refresh). `last_contact_at` = recepção do batch. A leitura de presença deriva: estado exibido = `state` se `last_contact_at ≤ 180 s` (N6); senão **"Sem comunicação"** — a menos que o último evento tenha sido um desligamento limpo (`off_clean`), que continua "Desligada".

#### Telemetria

```sql
-- RAW: particionada por DIA (partições criadas por migration/job próprio — SEM pg_partman), retenção 90 dias (N10)
CREATE TABLE raw_events (
  tenant_id uuid NOT NULL,
  device_id uuid NOT NULL,
  event_id uuid NOT NULL,
  seq bigint NOT NULL,                -- PERSISTIDO: detecção de lacunas
  occurred_at timestamptz NOT NULL,   -- relógio do agente, IMUTÁVEL
  event_type text NOT NULL,           -- string do contrato 5.3
  tz_offset_min int,
  mono_ms bigint, boot_id uuid,
  session_id int, windows_sid text, windows_username text,
  process_name text,                  -- extraído de data p/ índice (lowercase)
  window_title text,                  -- truncado 256; null se APP_ONLY/ignorado
  payload jsonb,                      -- data{} completo
  received_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (device_id, event_id, occurred_at)   -- partição exige occurred_at na PK; dedup determinístico
) PARTITION BY RANGE (occurred_at);
CREATE INDEX ix_raw_tenant_dev_time ON raw_events (tenant_id, device_id, occurred_at);

-- INTERVALOS: particionada por MÊS, retenção 12 meses (N11)
CREATE TABLE activity_intervals (
  id uuid NOT NULL,
  tenant_id uuid NOT NULL, device_id uuid NOT NULL,
  device_user_id uuid,                -- null p/ intervalos de máquina (off_clean/no_data sem sessão)
  started_at timestamptz NOT NULL,    -- já corrigido por clock_offset_ms
  ended_at timestamptz NOT NULL,
  state text NOT NULL CHECK (state IN ('active','idle','locked','off_clean','no_data')),
  app_id uuid,                        -- null quando não-active
  window_title text,                  -- título dominante do intervalo
  data_incomplete boolean NOT NULL DEFAULT false,  -- lacuna de seq na janela
  source_day date NOT NULL,           -- dia local (TZ da org)
  PRIMARY KEY (tenant_id, device_id, started_at, id)
) PARTITION BY RANGE (started_at);
CREATE INDEX ix_intervals_user_time ON activity_intervals (tenant_id, device_user_id, started_at);
-- invariante (testada): intervalos de um (device_id, windows_sid) nunca se sobrepõem

-- AGREGADOS DIÁRIOS: sem partição, retenção 24 meses (N12)
CREATE TABLE daily_device_summaries (
  tenant_id uuid NOT NULL, summary_date date NOT NULL,
  device_id uuid NOT NULL,
  device_user_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',  -- UUID zero = agregado da máquina (sem usuário)
  seconds_active int NOT NULL DEFAULT 0, seconds_idle int NOT NULL DEFAULT 0,
  seconds_locked int NOT NULL DEFAULT 0, seconds_on int NOT NULL DEFAULT 0,
  seconds_work_related int NOT NULL DEFAULT 0,    -- via categorias do tenant
  seconds_neutral int NOT NULL DEFAULT 0, seconds_not_work_related int NOT NULL DEFAULT 0,
  first_event_at timestamptz, last_event_at timestamptz,
  data_incomplete boolean NOT NULL DEFAULT false,
  computed_at timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, summary_date, device_id, device_user_id)
);

CREATE TABLE daily_app_usage (
  tenant_id uuid NOT NULL, summary_date date NOT NULL,
  device_id uuid NOT NULL,
  device_user_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',  -- UUID zero = agregado da máquina (sem usuário)
  app_id uuid NOT NULL,
  seconds_active int NOT NULL, focus_count int NOT NULL,
  PRIMARY KEY (tenant_id, summary_date, device_id, device_user_id, app_id)
);
CREATE INDEX ix_dau_tenant_date_app ON daily_app_usage (tenant_id, summary_date, app_id);
```

#### Catálogo de apps e categorias

```sql
CREATE TABLE app_catalog (             -- GLOBAL (sem tenant_id), curado pelo produto
  id uuid PRIMARY KEY,
  process_name text UNIQUE NOT NULL,   -- 'chrome.exe'
  display_name text NOT NULL, vendor text,
  default_category text,               -- sugestão global
  curated boolean NOT NULL DEFAULT false
);
-- processo desconhecido na intervalização ⇒ INSERT ON CONFLICT DO NOTHING (display_name = process_name)
-- JAMAIS propagar window_title para o catálogo

CREATE TABLE categories (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  name text NOT NULL,
  classification smallint NOT NULL,    -- 1=relacionado ao trabalho, 0=neutro, -1=não relacionado
  color text,
  UNIQUE (tenant_id, name)
);
-- seed na criação da org: Desenvolvimento(+1), Escritório/Documentos(+1), Comunicação(+1), Reuniões(+1),
-- Navegação(+1), Design(+1), ERP/Sistemas internos(+1), Sistema/Utilitários(+1),
-- Música/Streaming de áudio(0), Não categorizado(0), Jogos(-1), Redes sociais(-1), Vídeo/Streaming(-1)

CREATE TABLE tenant_app_categories (
  tenant_id uuid NOT NULL, app_id uuid NOT NULL REFERENCES app_catalog(id),
  category_id uuid NOT NULL REFERENCES categories(id),
  custom_display_name text,
  PRIMARY KEY (tenant_id, app_id)
);
```

#### Operação, auditoria e LGPD

```sql
CREATE TABLE ingest_cursors (
  tenant_id uuid NOT NULL, device_id uuid PRIMARY KEY,
  processed_until timestamptz NOT NULL,
  dirty_from timestamptz,              -- menor occurred_at não processado (NULL = limpo)
  updated_at timestamptz NOT NULL
);

CREATE TABLE dirty_days (
  tenant_id uuid NOT NULL, device_id uuid NOT NULL, day date NOT NULL,
  PRIMARY KEY (tenant_id, device_id, day)
);

CREATE TABLE audit_log (               -- append-only (sem UPDATE/DELETE pela role da app); retenção 24 meses (N13)
  id uuid NOT NULL, tenant_id uuid NOT NULL,
  actor_user_id uuid, actor_ip inet,
  action text NOT NULL,    -- 'login','view_timeline','view_report','export_csv','dsr_export','dsr_delete',
                           -- 'update_privacy_config','update_category','revoke_device','revoke_key',
                           -- 'collection_window_choice','update_user_role',...
  target_type text, target_id uuid,
  detail jsonb,            -- período consultado, filtros, nº de linhas exportadas, de→para de config
  occurred_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (id, occurred_at)        -- partição exige occurred_at na PK
) PARTITION BY RANGE (occurred_at);    -- mensal

CREATE TABLE export_jobs (             -- CSV assíncrono + exports DSR
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, requested_by uuid NOT NULL,
  kind text NOT NULL,                  -- 'usage_csv'|'jornada_csv'|'fora_horario_csv'|'dsr_subject'|'dsr_device'|'tenant_full'
  params jsonb NOT NULL,
  status text NOT NULL DEFAULT 'queued',   -- queued|running|done|failed
  file_path text, row_count int, expires_at timestamptz   -- expira em 7 dias para CSV de relatórios; 72 h para pacotes DSR
);

CREATE TABLE jornada_report_deliveries (  -- F5: costura assinatura semanal -> export -> e-mail com link
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  user_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  export_job_id uuid NOT NULL REFERENCES export_jobs (id) ON DELETE CASCADE,
  week_start date NOT NULL, week_end date NOT NULL,
  queued_at timestamptz NOT NULL, emailed_at timestamptz, gave_up_at timestamptz
);
-- UNIQUE (user_id, week_start) é a idempotência do job no BANCO: o trigger roda de 5 em 5 min
-- dentro da janela das 07h e duas instâncias do worker não duplicam a entrega.
```

### 7.2 Retenção e particionamento (resumo executável)

| Tabela | Partição | Retenção (fixa no MVP) | Mecanismo |
|---|---|---|---|
| `raw_events` | diária | **90 dias** (N10) | job noturno `DROP PARTITION`; partições D+3 criadas pelo mesmo job |
| `activity_intervals` | mensal | **12 meses** (N11) | `DROP PARTITION` |
| `daily_device_summaries` / `daily_app_usage` | — | **24 meses** (N12) | `DELETE` por data |
| `audit_log` | mensal | **24 meses** (N13) | `DROP PARTITION` |

### 7.3 Pipeline de intervalização — máquina de estados canônica

Enum único de estado (pipeline E timeline E presença): **`active` · `idle` · `locked` · `off_clean` · `no_data`**.

- `off_clean` = desligada/suspensa de forma limpa (houve `AGENT_STOP`, `SYSTEM_SUSPEND` ou `SESSION_END`). Visual: "Desligada/suspensa" (cinza, contorno). Estado esperado, sem alerta.
- `no_data` = gap ≥ 600 s (N7) sem evento e sem desligamento limpo. Visual: "Sem comunicação" (vermelho hachurado + ⚠). Problema de TI. **A timeline DISTINGUE visualmente os dois — é a distinção nº 1 de chamados de suporte do domínio.**

Execução (worker, micro-batches):

1. **Seleção**: a cada 60 s, varre `ingest_cursors WHERE dirty_from IS NOT NULL` (a ingestão faz upsert do cursor com `dirty_from = min(occurred_at do lote)` se menor que o atual). Processa por device com `pg_advisory_xact_lock(hash(device_id))`.
2. **Janela de reprocessamento**: `R = [date_trunc('hour', dirty_from) − 1 h, now]`. `DELETE FROM activity_intervals WHERE device_id = X AND ended_at > R.start` e **reconstrói** a partir de `raw_events` ordenados por `(occurred_at, seq)` desde `R.start`. Reconstrução idempotente resolve eventos atrasados/fora de ordem (máquina que despeja dias de backlog ⇒ `dirty_from` antigo ⇒ janela cobre o backlog inteiro).
3. **Máquina de estados** por `(device_id, windows_sid)`, com timestamps corrigidos por `clock_offset_ms`:
   - `ACTIVE_WINDOW_CHANGED` → fecha intervalo corrente em `t`, abre `active(app)`; resolve `app_id` por `process_name` no `app_catalog` (auto-insere não-curado).
   - **`IDLE_START` → fecha o intervalo `active` corrente RETROATIVAMENTE em `data.last_input_at`** e abre `idle` a partir de `last_input_at`. REGRA CRÍTICA: usar o timestamp do evento aqui está ERRADO — sem o fechamento retroativo, todo ciclo de ociosidade ganha ~5 min (o limiar) de "ativo" falso. Se `last_input_at` for anterior ao início do intervalo corrente, fechar no início do intervalo (nunca gerar duração negativa).
   - `IDLE_END` → fecha `idle` em `t`, abre `active` com o último app conhecido (o `ACTIVE_WINDOW_CHANGED` seguinte corrige se mudou).
   - `LOCK` → fecha intervalo corrente (inclusive `idle`: **lock vence idle**), abre `locked`. `UNLOCK` → fecha `locked`, abre `active`.
   - `SESSION_END` / `AGENT_STOP` / `SYSTEM_SUSPEND` → fecha intervalo corrente em `t`, abre `off_clean` (intervalo de máquina, `device_user_id` null) até o próximo evento de retomada (`AGENT_START`/`SYSTEM_RESUME`/`SESSION_START`/`UNLOCK`).
   - **Gap**: diferença entre eventos consecutivos ≥ 600 s (N7) sem desligamento limpo → fecha o intervalo corrente no último evento e registra `no_data` até o próximo evento. `HEARTBEAT` conta como evento (mantém o intervalo aberto).
   - `EVENTS_DROPPED` → trecho coberto vira `no_data` com `data_incomplete = true`.
   - `TIME_CHANGED` → não muda estado; eventos da vizinhança mantêm ordem por `mono_ms`/`boot_id`.
   - `NOTICE_ACK`, `POLICY_APPLIED`, `AGENT_TAMPER`, `HEARTBEAT` → não geram transição de app (heartbeat só sustenta o estado).
   - `HEARTBEAT` com `state: no_session` não gera intervalo de usuário; na lane da máquina conta como ligada sem sessão.
   - Pós-processamento: N20 (descartar < 1 s; fundir adjacentes idênticos).
   - **Lacuna de `seq`** dentro da janela (`seq` não contíguo por device) → marcar `data_incomplete = true` nos intervalos afetados; a resposta de timeline/relatório propaga a flag (tooltip "dados incompletos" no portal).
4. Ao final: atualiza `processed_until`, zera `dirty_from`, insere em `dirty_days` cada dia local (TZ da org) tocado.

**Agregação diária**: job a cada 15 min consome `dirty_days` e recomputa `daily_device_summaries` + `daily_app_usage` do dia/device (full recompute, `INSERT ... ON CONFLICT DO UPDATE`). Classificação (`seconds_work_related/...`) resolvida na agregação via `tenant_app_categories` — mudança de categoria insere `dirty_days` dos últimos 30 dias do tenant (documentado: histórico anterior mantém a classificação antiga).

### 7.4 API do Portal (REST, `/api/v1`, JWT)

RBAC MVP: **Owner ⊃ Admin ⊃ Viewer** (3 papéis; enum extensível — Manager-por-equipe e Viewer-agregado são v1.1; derrogação consciente do REQ-PRIV-07 da seção de design 04, registrada aqui). Tenant SEMPRE do token, nunca da URL. Erros RFC 9457; paginação `page`/`page_size` (máx. 100); recurso de outro tenant → **404**.

| Método e rota | Papel mín. | Descrição |
|---|---|---|
| `POST /auth/login` | público | e-mail+senha → `{access_token, expires_in}` + refresh cookie httpOnly/Secure/SameSite=Strict; se MFA habilitada → `mfa_required` + token temporário |
| `POST /auth/mfa/verify` | público | TOTP → tokens |
| `POST /auth/mfa/setup` | Viewer | provisiona TOTP (QR); **obrigatório completar para Owner/Admin antes de qualquer outra rota** |
| `POST /auth/refresh` | cookie | renova access token (refresh simples, sem famílias) |
| `POST /auth/logout` | Viewer | revoga refresh |
| `POST /auth/forgot-password` / `POST /auth/reset-password` | público | token 1 h; resposta sempre genérica; o reset revoga TODAS as sessões (F5) |
| `POST /auth/mfa/recovery-codes` | Viewer | (re)gera os 10 recovery codes; exibidos UMA vez, aceitos no `/auth/mfa/verify` (F5) |
| `POST /users/{id}/mfa/reset` | Admin (Owner p/ Owner) | recuperação assistida: zera MFA e sessões, próximo login exige novo setup (F5) |
| `POST /users/{id}/invitations/resend` | Admin | novo token de 7 dias, invalida os anteriores (F5) |
| `GET /me` | Viewer | perfil + papel + org (inclui `plan`, `device_limit`, metas e estado do checklist) |
| `GET/PATCH /me/email-prefs` | Viewer | preferências de e-mail do próprio usuário: resumo semanal, alertas de frota, jornada semanal (F5) |
| `GET /dashboard/presence` | Viewer | cards "agora" + tabela "Equipe agora" — lê `device_current_state` (estado, app em foco, "neste app há X min", último contato); regra N6 |
| `GET /dashboard/summary?from&to&device_id&device_user_id` | Viewer | KPIs de `daily_device_summaries` |
| `GET /dashboard/top-apps?from&to&limit=10` | Viewer | de `daily_app_usage` |
| `GET /timeline/device?device_id&date` | Viewer | intervalos do dia (resolução fixa 1 min, cap ~3.000 — N21); inclui `data_incomplete` e fuso do device |
| `GET /timeline/team?date` | Viewer | **uma lane por device, visão do dia** — mesma agregação; FICA no MVP (F3, demo vendável) |
| `GET /devices?status&tag&q&page&health` | Viewer | lista paginada + saúde (último contato, versão, `os_type`); `health=alert` filtra a FROTA inteira (F5) |
| `GET /devices/health-summary` | Viewer | contagens de saúde da frota inteira por dimensão, para o card de atenção e os chips totais (F5) |
| `GET /devices/{id}` | Viewer | detalhe |
| `PATCH /devices/{id}` | Admin | renomear, tags, `status` (`active`/`paused`/`archived`) |
| `POST /devices/{id}/revoke` | Admin | revoga token (`status=revoked`) + enfileira `UNENROLL` |
| `GET /device-users?device_id&q` · `PATCH /device-users/{id}` | Viewer · Admin | usuários Windows; editar `display_name` |
| `GET /reports/jornada?from&to&device_ids` | Viewer | linha por device×dia: primeiro/último evento, ligada/ativa/ociosa/bloqueada + disclaimer fixo (Seção 8.6) |
| `GET /reports/usage?from&to&group_by=app\|category\|device\|device_user` | Viewer | tabular paginado |
| `GET /reports/fora-do-horario?from&to&device_ids&include_devices` | Viewer | atividade fora do horário de trabalho: tempo ATIVO somado fora da `business_hours` da org, no fuso do tenant, sobre `activity_intervals`. Indicador de EQUILÍBRIO, jamais hora extra ou banco de horas. `status` explica os dois casos sem número (`horario_nao_configurado`, `coleta_restrita_ao_horario`) em vez de devolver zero. Sem `include_devices` a resposta é agregado de equipe e não audita |
| `POST /exports` · `GET /exports/{id}` | Viewer | CSV assíncrono (worker; máx. 500 k linhas; UTF-8 com BOM, separador `;`); auditado. Kinds: `usage_csv`, `jornada_csv`, `fora_horario_csv` (este exige horário declarado e coleta contínua, senão 409) |
| `GET/POST /categories` · `PATCH/DELETE /categories/{id}` | Admin | CRUD; dispara reagregação 30 dias |
| `GET /app-catalog?uncategorized=true&q` · `PUT /app-catalog/{appId}/category` | Viewer · Admin | apps vistos pelo tenant; mapeamento. A listagem devolve `default_category`, a SUGESTÃO do dicionário brasileiro semeado em `app_catalog` (F1.1); quem decide continua sendo o tenant |
| `PUT /app-catalog/categories/batch` | Admin | aplica N mapeamentos app ⇒ categoria numa ÚNICA transação, com UMA única reagregação de 30 dias e auditoria `update_category` por app (F1.1). Nunca toca `custom_display_name` |
| `GET/POST /enrollment-keys` · `DELETE /enrollment-keys/{id}` | Admin | segredo exibido UMA única vez no POST |
| `GET /users` · `POST /users/invitations` · `PATCH /users/{id}` · `DELETE /users/{id}` | Admin (owner só por Owner) | sempre ≥ 1 Owner ativo |
| `GET/PATCH /organization` | Viewer / Admin | leitura para qualquer papel; edição (Admin+) de transparência, business_hours e metas semanais agregadas (F5) |
| `GET/PATCH /organization/agent-config` | Admin / **Owner** | config de coleta operável pela controladora: política de títulos (`MASKED_PATTERNS`/`APP_ONLY`; `FULL` só via operadora com registro em DPA), padrões de mascaramento (regex validada), processos ignorados, idle, janela de coleta e `notice_text` (corpo do aviso de ciência: limite que já desconta o enquadramento fixo, sem HTML nem marcação, e recusa de texto que imite pedido de consentimento; mudar sobe também `notice_version`, que reexibe o aviso na frota). PATCH bumpa `config_version` na mesma transação e registra `update_privacy_config` + `collection_window_choice` (F5) |
| `POST/DELETE /organization/onboarding-checklist/dismiss` | Admin | dispensa e reabre o card de primeiros passos (Seção 8.3 passo 4) |
| `GET /audit-logs?from&to&actor&action` | Owner/Admin | trilha LGPD |
| `POST /privacy/subjects/{deviceUserId}/export` | Admin | DSR: pacote JSON+CSV de todos os dados do titular (assíncrono, link expira 72 h); auditado |
| `DELETE /privacy/subjects/{deviceUserId}/data` | Owner | DSR: exclusão definitiva (confirmação dupla + motivo); recibo com contagens; auditado |
| `POST /privacy/devices/{deviceId}/export` · `DELETE /privacy/devices/{deviceId}/data` | Admin · Owner | mesmo fluxo por device |
| `POST /privacy/tenant/full-export` | Owner | acervo completo do tenant (offboarding; processo de purge manual documentado) |
| `GET /agent/update-manifest?current=` | device token | manifesto de auto-update (Seção 6.7) |
| `GET /billing/billable-devices?month=` | Owner | **relatório interno mensal de devices cobráveis** (device com ≥ 1 batch no mês, excluindo `archived`) — insumo do billing manual. F5: mês fechado vem CONGELADO de `device_billing_months` (`frozen: true`), então arquivar device não reescreve mais meses passados; o mês corrente segue ao vivo |

Auditoria automática (middleware): toda chamada a timeline/relatórios/exports/DSR grava `audit_log` com ação, alvo, período e filtros — responde "quem viu os dados de quem, quando" (exposta a Owner/Admin).

### 7.5 Auth — regras

- Senha mínima **12 chars**, hash **Argon2id** (64 MB / 3 / 4); checagem contra senhas vazadas é v1.1.
- **MFA TOTP OBRIGATÓRIA para Owner e Admin** (disponível para todos); 10 recovery codes hasheados.
- JWT de acesso **15 min** (claims `sub`, `org_id`, `role`, `jti`), HS256 com chave 256 bits em secret store; refresh token opaco 30 dias, **simples** (revogável; SEM famílias de rotação no MVP).
- Lockout: **10 tentativas → 15 min** (N22) + rate limit por IP em `/auth/*` (10/min).
- Convite por e-mail (7 dias, single-use). **SEM signup self-service**: org criada via backoffice (trial assistido pelo comercial). Não construir tela "Criar conta".
- Device token: policy/audience separada — autoriza somente `/api/v1/agent/*` e `/api/v1/ingest/*`.

### 7.6 Jobs do worker (Quartz.NET + pg_advisory_lock)

| Job | Frequência | Função |
|---|---|---|
| `Intervalization` | 60 s | Seção 7.3 |
| `DailyAggregation` | 15 min | consome `dirty_days` |
| `PartitionMaintenance` | diário 02:00 BRT | cria partições D+3, dropa expiradas (N10–N13) |
| `RetentionPurge` | diário | `DELETE` de summaries > 24 meses; execução logada em `audit_log` |
| `ExportWorker` | contínuo | gera CSVs (streaming p/ arquivo, nunca em memória) e pacotes DSR |
| `Housekeeping` | diário | expira invitations, refresh tokens, export_jobs |
| `WeeklyDigest` (F5) | horário | envia o resumo semanal às orgs cuja hora local é segunda 08h (multi-fuso sem um trigger por org); idempotência por `organizations.last_weekly_digest_at` |
| `FleetAlert` (F5) | 15 min | alertas de saúde de frota por e-mail, só plano `pro`: 1 e-mail por org por ciclo, cooldown de 24 h por device+tipo (`device_alert_state`), silencioso fora do horário de trabalho da org |
| `JornadaWeekly` (F5) | 5 min | enfileira o export de jornada da semana anterior nas orgs `pro` cuja hora local é segunda 07h (multi-fuso sem um trigger por org) e, no mesmo ciclo, envia o LINK do download autenticado quando o export fica pronto (NUNCA anexo); idempotência por `UNIQUE (user_id, week_start)` em `jornada_report_deliveries` |
| `BillingSnapshot` (F5) | diário 04:00 BRT | congela os meses fechados em `device_billing_months` (idempotente, no fuso de cada tenant) |
| `AccountHealth` (F5) | segunda 09h BRT | score interno de contas em risco por e-mail ao CS; só registrado com `Cs:AlertEmail` configurado |
| `DemoKeepAlive` / `DemoReseed` (F5) | 60 s / domingo 04:30 BRT | mantêm a demo pública viva e re-semeada; só registrados com `Demo:Slug` configurado |
| `DeadManSwitch` (F5) | 5 min | ping externo (healthchecks.io) que denuncia worker morto; só registrado com `DeadMan:WorkerUrl` |

### 7.7 NFRs (dimensionados para ~2.500 devices — N25)

| Métrica | Alvo |
|---|---|
| Ingestão | ~2.500 devices × 2 lotes/min = **~85 req/s** pico; p95 < 300 ms |
| Eventos/dia | 3–5 M (~1.500–2.000/device); rajada de catch-up ~400/s — INSERT multi-row dá conta |
| Dashboard (agregados) | p95 < 500 ms |
| Timeline (intervals) | p95 < 1,5 s |
| Lag evento→timeline | < 5 min p95 |
| Storage | raw 90 d ≈ 150–200 GB; intervals 12 m ≈ 150 GB; planejar disco 500 GB–1 TB |
| Disponibilidade | 99,5% com janela declarada; buffer de 7 dias do agente ⇒ indisponibilidade curta não perde dado |
| Backup | PITR do Postgres gerenciado + `pg_dump` lógico semanal externo (região BR); teste de restore documentado |
| Perda pós-ack | **0** (ack pós-commit) |

Métricas mínimas (Serilog/Seq + contadores): `ingest_events_total`, `ingest_rejected_total{reason}`, `ingest_duplicates_total`, `ingest_unknown_type_total`, `processing_lag_seconds`, `devices_online`. Logs JSON com `tenant_id`/`device_id`/`trace_id`; **JAMAIS logar `window_title`**.

---

## 8. Especificação do PORTAL

SPA React (stack da Seção 4). Idioma: pt-BR hardcoded. Formatos: datas `dd/mm/aaaa`, horários `HH:mm` (24h), durações `6h 40min` (nunca decimal na UI; CSVs trazem coluna extra `horas_decimais`). Tipografia Inter com `tabular-nums` em colunas numéricas. Light mode apenas (dark é v1.1). Acessibilidade MVP: contraste ≥ 4.5:1, labels reais em formulários, fallback tabular da timeline — WCAG AA integral e mobile completo são v1.1 (mobile: leitura em coluna única funciona, administração não é alvo).

### 8.1 Mapa de rotas (MVP)

```
/login                      /convite/:token         /recuperar-senha    /redefinir-senha/:token
/onboarding                 (wizard; org sem devices redireciona / → /onboarding)
/                           Visão Geral (dashboard)
/linha-do-tempo             Timeline (modo equipe e modo device)
/apps                       Detalhe de aplicativos
/relatorios                 Hub (Jornada · Uso de apps · Exportações)
/relatorios/jornada         /relatorios/uso (consome GET /reports/usage)         /relatorios/exportacoes
/relatorios/uso?aba=fora-do-horario   Aba de atividade fora do horário de trabalho (GET /reports/fora-do-horario)
/configuracoes/organizacao  /configuracoes/dispositivos   /configuracoes/categorias
/configuracoes/usuarios     /configuracoes/chaves         /configuracoes/privacidade
/configuracoes/privacidade/titulares    (DSR — Dados do Titular)
/transparencia/:slug        Página de transparência — rota PÚBLICA, sem login (só a política de coleta vigente do tenant, sem dados pessoais — Seção 8.8)
```

CORTADO do MVP (não construir): signup self-service ("Criar conta"), rota pública `/t/:token`, exports PDF, telas de alertas, equipes como entidade, feriados (tenant marca dias manualmente no relatório é v1.1 — sem CRUD de feriados no MVP).

Layout persistente: sidebar colapsável (Visão Geral, Linha do Tempo, Aplicativos, Relatórios, Configurações, Transparência), topbar com badge de fuso ("Horários em GMT-3 · São Paulo"), menu do usuário. Banner global quando ≥ 1 device "Sem comunicação" há > 30 min em horário de trabalho.

### 8.2 Autenticação

- **Login**: e-mail + senha; erros sempre genéricos ("e-mail ou senha inválidos"); cooldown visível após lockout (N22). Se MFA pendente de setup (Owner/Admin) → forçar `/auth/mfa/setup` antes de qualquer navegação.
- **Convite** (`/convite/:token`): "Você foi convidado(a) para **{org}** como **{papel}**"; nome + senha (mínimo 12, medidor de força). Token 7 dias, single-use.
- **Reset**: resposta sempre "se este e-mail existir, enviamos instruções"; token 60 min; redefinir invalida todas as sessões.

### 8.3 Onboarding (`/onboarding`) — wizard de 4 passos

Objetivo: primeira máquina reportando em < 15 min. Org sem device SEMPRE cai aqui (retomável).

| Passo | Conteúdo | Ações/Dados |
|---|---|---|
| 1. Organização | Confirmar nome, fuso (default `America/Sao_Paulo`), horário de trabalho (seg–sex 08:00–18:00, editável) e **escolha explícita da `collection_window`**: "Coletar sempre" (sugerido, com explicação) vs "Coletar apenas no horário de trabalho" — a UI explica que fora da janela só uptime/sessão são registrados. **A escolha é registrada em `audit_log` (`collection_window_choice`) — quem decide é a controladora.** | `PATCH /organization` |
| 2. Chave de instalação | Enrollment key gerada (`ek_...`), botão copiar, link do MSI, bloco pronto: `msiexec /i MonitorAgent.msi /qn ENROLLKEY=ek_... SERVERURL=https://...` + variante GPO (link doc) | `POST /enrollment-keys` |
| 3. Aguardando a 1ª máquina | Spinner + "Instale o agente em uma máquina. Ela aparecerá aqui em até 2 minutos." Polling 10 s em `GET /devices`. Primeiro device → card verde (hostname, usuário, status) + "Ver no dashboard". Link "Está demorando? Checklist de firewall/proxy (443 de saída + domínio da API)" | `GET /devices` |
| 4. Próximos passos | Checklist dispensável (persiste como card na Visão Geral): convidar colegas, revisar categorias, revisar privacidade, baixar o Kit de Transparência (PDF) | — |

### 8.4 Visão Geral (`/`) — dashboard

Atualização: **polling 60 s** (N18) via TanStack Query (`refetchInterval`, pausa em aba oculta, `keepPreviousData` — polling em background nunca re-mostra skeleton). Badge "Atualizado há Xs".

**Linha 1 — cards de presença** (de `GET /dashboard/presence`, regra N6):

| Card | Definição |
|---|---|
| Ativos (verde) | estado `active` com último contato ≤ 180 s |
| Ociosos (âmbar) | estado `idle` — tooltip pedagógico FIXO: "Ocioso significa sem uso de teclado/mouse. Reuniões, chamadas e leitura podem aparecer como ociosidade." |
| Bloqueados/sem usuário (azul-acinzentado) | `locked` + `no_session` (máquina ligada sem usuário logado) |
| Desligadas (cinza) | `off_clean` — estado esperado, sem alerta |
| Sem comunicação (vermelho) | sem contato > 180 s SEM desligamento limpo — clicável → lista filtrada |

**Linha 2 — "Equipe agora"**: tabela ao vivo — status (dot + label), Device/Usuário, App em foco (respeita mascaramento: só nome do app se títulos mascarados), "neste app há X min", último contato. Ordenação default: Sem comunicação primeiro, depois Ativos. Clique → timeline do device hoje.

**Linha 3 — dois gráficos (ECharts)**: Horas ativas por dia (barras empilhadas ativo/ocioso, semana atual/anterior, linha de referência = jornada do tenant) · Top 10 apps da semana (barras horizontais, cor pela classificação; clique → `/apps`).

Sem filtros complexos. Densidade > flexibilidade.

### 8.5 Linha do Tempo (`/linha-do-tempo`) — a tela do produto

Objetivo: reconstruir o dia de uma pessoa/máquina em 5 segundos de olhar.

- **Modo device (F2)**: um device, um dia. Faixa de estados (48 px) + sub-faixa de apps (32 px, cor por categoria). Date picker (Hoje/Ontem/◀ ▶, teclas ← →), seletor de device, toggle "Horário de trabalho / 24h" (default: janela do tenant ± 1 h).
- **Modo equipe (F3)**: `GET /timeline/team?date` — **uma lane de 28 px por device, visão do dia**. Clique na lane → modo device.
- **Renderização: Canvas 2D** (um `<canvas>` por viewport), DPI-aware; hover com hit-testing por busca binária no array de intervalos. Tooltip: `09:14 – 09:41 · 27min`, estado, app + título (se não mascarado), categoria, e indicação "dados incompletos" quando `data_incomplete`.
- **MVP NÃO inclui** (v1.1 — não construir): zoom multi-resolução com re-fetch, drag-select com painel agregado, virtualização para > 30 lanes, navegação por teclado desenhando cursor no canvas. A resposta vem do servidor em resolução fixa de 1 min com cap ~3.000 intervalos (N21).
- **Estados e cores (sempre com redundância não-cromática — daltonismo):**

| Estado (enum 7.3) | Rótulo UI | Visual |
|---|---|---|
| `active` | Ativo | verde, sólido |
| `idle` | Ocioso | âmbar, sólido |
| `locked` | Bloqueado | azul-acinzentado, sólido |
| `off_clean` | Desligada/suspensa | cinza claro, contorno sem preenchimento |
| `no_data` | Sem comunicação | vermelho, hachura diagonal + ícone ⚠ |
| (ausência de intervalos) | Sem dados | hachura cinza-claríssima + label (pré-instalação/dia futuro — nunca pintar como ocioso) |

- **Rodapé do modo device**: Primeiro evento `08:02` · Último evento `17:48` · Ligada `9h 12min` · Ativa `6h 40min` · Ociosa `1h 05min` · Bloqueada `1h 27min` — EXATAMENTE os mesmos números do relatório de jornada (consistência absoluta entre telas).
- **Fuso**: horários sempre no fuso do tenant; device divergente ganha badge "Máquina em GMT-4" (tooltip: "convertido para o fuso da organização").
- **Fallback tabular obrigatório** ("Ver como tabela"): mesmos intervalos, mesma fonte — é também o fallback mobile e de screen reader.
- Linha vertical "agora" quando o dia é hoje; dias passados são imutáveis → `Cache-Control` + ETag; só "hoje" é reconsultado por polling (N18).

### 8.6 Aplicativos (`/apps`) e Relatórios (`/relatorios`)

**/apps**: filtros (período com presets até 92 dias, devices multi-select, categoria, classificação); donut por classificação (Relacionado ao trabalho / Neutro / Não relacionado / Não categorizado) + barras por categoria; tabela (App com ícone+nome, Categoria editável inline — vale para o tenant inteiro e reagrega 30 dias, Tempo ativo, %, nº devices); drill-down do app → top títulos com tempo; se títulos mascarados → linha única "Títulos mascarados pela política de privacidade" com link para `/configuracoes/privacidade` (admin). Badge "N apps sem categoria — revisar".

**/relatorios/jornada**: tabela uma linha por device × dia — colunas: Data, Dia, Dispositivo/Usuário, **Primeiro evento**, **Último evento** (JAMAIS "Entrada"/"Saída"), Tempo ligada, Tempo ativo, Tempo ocioso, Tempo bloqueado, Observação ("Sem dados — máquina desligada" vs "⚠ Agente sem comunicação" vs "dados incompletos"). Totais por device. **Banner fixo não-dispensável na tela E rodapé de todo CSV exportado:** *"Relatório gerencial de uso da estação de trabalho. Não constitui registro eletrônico de ponto (Portaria 671/MTE) e não substitui o controle de jornada do art. 74 da CLT."* JAMAIS calcular/exibir: horas extras, adicional noturno, banco de horas, atrasos.

**Exports**: somente **CSV no MVP** (UTF-8 **com BOM**, separador `;` — Excel pt-BR; coluna extra `horas_decimais`). Assíncronos (`POST /exports` → toast → `/relatorios/exportacoes` com histórico 30 dias: quem gerou, quando, filtros — trilha de auditoria). SEM PDF server-side no MVP.

### 8.7 Configurações

- **Organização**: nome, fuso IANA, semana de trabalho (referência visual + `collection_window`).
- **Dispositivos**: tabela (display_name editável, hostname, usuário mais frequente, tag livre, versão do agente, fuso do device, último contato, status `active|paused|archived|revoked`, `os_type` server = badge "não suportado"). Ações: Renomear · Tags · **Arquivar** (sai do billing e dos dashboards; histórico preservado, acessível com toggle "incluir arquivados") · Revogar (modal de confirmação; enfileira `UNENROLL` — explica que o agente para e descarta a fila). Linha vermelha para "Sem comunicação". Badge "relógio dessincronizado" quando `|clock_offset_ms| > 120 s`.
- **Categorias**: duas abas (Categorias com classificação; Mapeamento de apps com busca, contador de não categorizados, recategorização em lote). Vocabulário fixo: `Relacionado ao trabalho` / `Neutro` / `Não relacionado ao trabalho` — JAMAIS "produtivo/improdutivo".
- **Usuários**: lista (nome, e-mail, papel Owner/Admin/Viewer, último acesso, status), Convidar, Reenviar, Alterar papel, Desativar. Regra: sempre ≥ 1 Owner ativo. Badge "MFA pendente" para Owner/Admin sem TOTP.
- **Chaves**: lista (prefixo `ek_4Qz8····`, label, criada em, devices registrados, status), Gerar (segredo exibido UMA vez) · Revogar (modal: "devices já registrados continuam; novas instalações serão recusadas"). Bloco `msiexec` pronto por chave.
- **Privacidade** (a tela que materializa "transparente, não spyware"): `window_title_policy` (FULL / MASKED_PATTERNS / APP_ONLY) com explicação "o enforcement é no agente — títulos mascarados nunca chegam ao servidor"; editor de `masked_patterns`; lista de `ignored_processes` (com defaults); limiar de ociosidade (slider 3–15 min, default 5 — N4); `collection_window`. **Toda alteração logada em `audit_log` (quem, quando, de→para) e exibida no rodapé da tela.** Alterar gera bump de `config_version` → propaga no próximo ack de cada device.
- **Privacidade → Dados do Titular** (`/configuracoes/privacidade/titulares`, DSR — Seção 9.3): buscar usuário/device → **Exportar** (pacote JSON+CSV, link 72 h) e **Excluir definitivamente** (confirmação dupla + motivo; exibe recibo com contagens). Visível para Admin (exclusão só Owner).

### 8.8 Transparência (`/transparencia/:slug` — rota PÚBLICA)

**Página PÚBLICA por slug do tenant (sem login) no MVP**, renderizada do estado REAL das configs do tenant — exibe apenas a política de coleta vigente, **nenhum dado pessoal**: "O que é coletado" (apps em foco com título conforme a política de títulos configurada, sessões, ociosidade, horários), "O que NUNCA é coletado" (teclas digitadas, capturas de tela, conteúdo de arquivos/e-mails/mensagens, webcam/microfone, localização) e as retenções vigentes. Campos editáveis (pelo admin, em Configurações): finalidade declarada, contato do DPO da controladora, data de vigência. Ações: Visualizar · Imprimir. Complementada pelo **Kit de Transparência em PDF** (artefato estático entregue no onboarding — modelo de comunicado interno + Termo de Ciência). O `transparency_url` da config do agente aponta para esta página (link divulgável, sem segredo). A F5 antecipou a versão TOKENIZADA por dispositivo (`GET /public/t/{token}`, de `devices.transparency_token`): mesma página mais o bloco "Este dispositivo" (estado da instalação, jamais dado do dia). O token chega ao agente pelo `device_transparency_url` da config e é o que o tray passa a abrir; no portal, o endereço fica em Dispositivos, no menu de ações da linha (`GET /devices/{id}/transparency-link`, **Admin+**, nunca no `DeviceResponse` que o Viewer lê). O preview "ver como funcionário" segue em v1.1.

### 8.9 Estados vazios e loading (desenhados, não acidentais)

| Contexto | Conteúdo |
|---|---|
| Org sem devices | Wizard de onboarding embutido — nunca dashboard zerado |
| Device sem dados hoje (com histórico) | "NB-JOAO não ligou hoje. Último dado: ontem às 18:32." + botão "Ver ontem" |
| Device sem comunicação | "Sem dados desde 09:14. A máquina pode estar ligada com o agente parado." + link "Como diagnosticar" |
| Período sem dados em relatório | "Nenhum dado no período" + sugestão do último período com dados |
| Apps não categorizados (1ª semana) | Card CTA de curadoria em `/apps` |
| Filtros sem resultado | "Nenhum resultado" + "Limpar filtros" |

Skeletons com a geometria final por componente (nunca spinner de página inteira); timeline desenha eixo+lanes imediatamente e preenche ao chegar; erro de fetch = estado inline por widget com "Tentar novamente".

---

## 9. Requisitos LGPD que viram código

Contexto jurídico mínimo para quem implementa: o cliente (controladora) monitora com base em **legítimo interesse** condicionado a transparência, proporcionalidade e minimização; **consentimento não é base válida** na relação de emprego (assimetria de poder) — por isso o `NOTICE_ACK` é **ciência, não consentimento**, e o texto exibido deve dizer isso. Nós (operadora) respondemos solidariamente se descumprirmos a lei ou as instruções — por isso o produto **impede tecnicamente** configurações ilegais em vez de só desaconselhá-las.

### 9.1 Minimização — lista de coleta FECHADA

O sistema coleta SOMENTE: identificação de máquina/usuário Windows, eventos de sessão (logon/logoff/lock/unlock), eventos de energia (boot/shutdown/suspend/resume), app+título em foco (sujeito a `window_title_policy`), o **fato** da ociosidade (jamais o input), saúde do agente. Qualquer adição passa por revisão de privacidade. Sem linha de comando/argumentos de processo, sem URLs (só título da aba), sem `APPS_SNAPSHOT` (cortado).

### 9.2 Mascaramento de títulos (enforcement no agente — Seção 6.3)

`window_title_policy` com 3 níveis (`FULL` / `MASKED_PATTERNS` / `APP_ONLY`); **default de fábrica: `MASKED_PATTERNS`** com lista padrão (termos de saúde, sindicais, religiosos, financeiros pessoais, padrões de CPF/cartão); **rebaixamento automático para `APP_ONLY` em navegação anônima/privada** (heurística por sufixo de título: "(navegação anônima)" / "(navegação anónima)" / "(Incognito)" Chrome pt-BR / pt-PT / en-US, "InPrivate" Edge em qualquer idioma, "(navegação privativa)" / "(navegação privada)" / "(Private Browsing)" Firefox pt-BR / pt-PT / en-US). Aplicado ANTES de persistir na fila local. `ignored_processes` com defaults (gerenciadores de senha, telas de logon). Servidor JAMAIS loga `window_title`.

### 9.3 Direitos do titular (DSR) — GATE DE LANÇAMENTO (F4)

- Tela `Configurações → Privacidade → Dados do Titular` + endpoints `POST /privacy/subjects/{id}/export`, `DELETE /privacy/subjects/{id}/data`, `POST /privacy/devices/{id}/export`, `DELETE /privacy/devices/{id}/data` (Seção 7.4). Export = pacote JSON + CSV com todos os eventos/intervalos/agregados do titular, link expirante 72 h. Exclusão = hard delete irreversível com confirmação dupla, motivo registrado e recibo com contagens. Tudo auditado. Suporta a controladora a responder o titular em 15 dias (art. 19).
- Exclusão de titular não apaga agregados de equipe já computados (regra documentada no DPA).
- **Encerramento de tenant**: `POST /privacy/tenant/full-export` (acervo completo) + purge com carência de **30 dias** — o processo de purge pode ser **manual documentado** no MVP (runbook), mas o full-export DEVE existir como código.

### 9.4 NOTICE_ACK (evidência de ciência)

Seção 6.5: aviso no primeiro logon, botão "Entendi" → evento `NOTICE_ACK{notice_version, shown_at}` persistido em `raw_events` + atalho `devices.notice_acked_at`. O portal exibe o status por device/usuário (painel de saúde) — é a evidência que a controladora apresenta em disputa trabalhista.

### 9.5 Auditoria de acesso a dados pessoais

Middleware do portal grava em `audit_log` TODA visualização de dado pessoal (timeline, relatório individual, drill-down de apps, export — com período e filtros), além de: login, mudanças de config de privacidade/coleta (de→para), mudanças de papel, revogações, DSR. Append-only (role da aplicação sem UPDATE/DELETE na tabela), retenção 24 meses (N13) não reduzível, exposta a Owner/Admin em `GET /audit-logs`. Responde "quem viu o relatório de quem, quando".

### 9.6 Retenção e purga

Números N10–N13, **fixos no MVP** (configurável por tenant é v1.1): raw 90 d · intervals 12 m · summaries 24 m · audit 24 m. Jobs `PartitionMaintenance` + `RetentionPurge` com execução logada. A tela de Privacidade e a página de Transparência exibem a política vigente e a data da última purga. Backups: dados purgados saem do ciclo em até 35 dias (documentar no DPA).

### 9.7 O que JAMAIS implementar (mesmo sob pedido de cliente)

Keylogging ou captura de qualquer input · screenshots/gravação de tela · clipboard · leitura de conteúdo de arquivos/e-mails/mensagens/DOM · webcam/microfone · geolocalização · modo oculto/stealth (ícone invisível, processo disfarçado) · burla de janela anônima · injeção de DLL · venda/uso secundário de dados (benchmarks identificáveis entre clientes, treinamento de modelos) sem previsão em DPA. A recusa é política comercial registrada e ativo de marca. Pentest externo: agendar **antes da primeira conta grande não-amiga** (não é gate de GA).

---

## 10. Plano de execução F0 → F5

Equipe: 2 devs .NET experientes; estimativas em semanas-calendário; total ~20–24 semanas até GA. Caminho crítico: F0→F1→F2→F3→F4→F5 (nada de F3 antes de F2 validada com dados reais). **Cada fase abaixo pode ser colada como prompt independente junto com as Seções 1–9.**

### F0 — Fundação (2 sem)

**Entregas:** monorepo (agente + backend + portal + infra), CI/CD GitHub Actions (build/test/docker/deploy SSH em staging), Docker Compose (caddy, api, worker, seq) + Postgres gerenciado, migration inicial com TODAS as tabelas da Seção 7.1 (`tenant_id` em tudo desde a 1ª migration), auth do portal completa (login, Argon2id, JWT 15 min + refresh, MFA TOTP obrigatória Owner/Admin, lockout N22, convite por e-mail), RBAC Owner/Admin/Viewer, criação de org via backoffice (CLI/endpoint interno), esqueleto do portal React (layout, rotas, login).
**Pronto quando (verificável):** `git push` → deploy automático em staging; criar tenant via backoffice + logar com MFA + convidar usuário funciona de ponta a ponta; **teste automatizado de isolamento prova que usuário do tenant A recebe 404 para qualquer recurso do tenant B** (gate de CI a partir daqui).

### F1 — Ingestão fim-a-fim (3 sem)

**Entregas:** agente mínimo (serviço + helper, lançamento por sessão, named pipe, coleta de janela ativa/sessão/idle/heartbeat conforme Seções 5–6, fila SQLite WAL com `seq`, envio em lote N3, retry N14), `POST /api/v1/agent/enroll` (key `ek_`, fingerprint, re-enroll idempotente, limite de devices do plano), `POST /api/v1/ingest/batch` completo (validação 5.6, idempotência, skew servidor, ack com config/comandos, tipo desconhecido = ignorar+métrica), entrega de config pelo ack + `POLICY_APPLIED`, `device_current_state` atualizado na ingestão, comando `UNENROLL`.
**Pronto quando:** instalar o agente numa VM limpa com a key → em < 2 min eventos crus aparecem em `raw_events` do tenant certo com `seq`/`tz_offset_min`/`boot_id` persistidos; derrubar a rede 10 min → eventos chegam depois sem perda nem duplicata (verificar `duplicates` no ack do retry); mudar `idle_threshold_sec` no banco → agente aplica no próximo ack e emite `POLICY_APPLIED`; `UNENROLL` para a coleta e zera a fila local.

### F2 — Pipeline de intervalos + Timeline (PRIMEIRA DEMO VENDÁVEL) (4 sem)

**Entregas:** worker de intervalização completo (Seção 7.3: cursores dirty, delete-and-rebuild, máquina de estados com os 5 estados canônicos, **fechamento retroativo em `last_input_at`**, `off_clean` vs `no_data`, lock vence idle, gap N7, lacuna de seq → `data_incomplete`, correção por `clock_offset_ms`), `GET /timeline/device` (resolução 1 min, cap N21), tela `/linha-do-tempo` modo device (canvas + tooltip + fallback tabular + rodapé de resumo), lista de devices com saúde, `GET /dashboard/presence` + cards/tabela "Equipe agora".
**Pronto quando:** para um dia real de uso de 1 máquina (8 h observadas), a timeline bate com a realidade — incluindo os **cenários nomeados da Seção 11.2 passando como testes de integração**; desligar a máquina limpa → timeline mostra "Desligada/suspensa"; matar o serviço na marra → após 600 s vira "Sem comunicação"; demo de 10 min possível para um estranho.

### F3 — Dashboard + relatórios + categorias + demo (4 sem)

**Entregas:** `DailyAggregation` + `daily_*`, Visão Geral completa (gráficos de semana, top apps), `/apps` com categorização (seed de categorias, catálogo global, edição inline, reagregação 30 dias), relatório de jornada com disclaimer fixo, exports CSV assíncronos (UTF-8 BOM, `;`), `GET /timeline/team` + modo equipe da timeline, **seed de demo: gerador de dados sintéticos (tenant demo, ~30 devices, 60 dias) que injeta eventos pelo pipeline REAL de intervalização** (requisito de vendas), **relatório interno de devices cobráveis** (`GET /billing/billable-devices`), status `archived` de device.
**Pronto quando:** gestor responde "quem ficou mais tempo ocioso esta semana?" em < 3 cliques; CSV abre no Excel pt-BR com acentuação e colunas corretas; tenant demo navegável de ponta a ponta (dashboard → timeline equipe → device → relatório → export); relatório de cobráveis bate com a contagem manual do mês.

### F4 — Hardening + LGPD (GATES DE GA) (5 sem)

**Entregas:** MSI WiX com `/qn ENROLLKEY= SERVERURL=` (GPO-friendly, `NOENROLL=1`), code signing Authenticode + submissão ao Defender, auto-update canal único com `min_version` e rollback por manifesto (Seção 6.7), robustez offline final (caps N8 + FIFO + `EVENTS_DROPPED`), tray completo + janela de transparência + **NOTICE_ACK**, **DSR completo (Seção 9.3 — telas + endpoints + full-export de tenant)**, jobs de retenção/purga N10–N13 rodando e logados, auditoria de acesso completa (Seção 9.5), página pública `/transparencia/:slug`, painel de saúde de agentes (tamper, versão, relógio dessincronizado, notice), backup/restore testado, metas de consumo do agente verificadas (Seção 6.8).
**Pronto quando:** atualizar 10 agentes remotamente sem tocar nas máquinas (e reverter 1 por manifesto); MSI instala via GPO em máquina de domínio; expurgo de retenção comprovado com dado antigo plantado; export DSR de um titular abre legível e a exclusão zera os dados dele (com recibo); restore de backup executado em staging; `NOTICE_ACK` visível no portal após primeiro logon de um usuário novo.

### F5 — Piloto (6 sem, parcialmente paralela à F4 final)

**Entregas:** 2–3 empresas amigas (≥ 10 devices cada) por 30–60 dias; correções de campo; kit de instalação para TI (pré-requisitos, troubleshooting dos 5 erros comuns); kit LGPD revisado por advogado; processo de cobrança manual operando com o relatório de cobráveis.
**Pronto quando:** 2 pilotos rodando 30 dias com < 5% de devices silenciosos; ≥ 1 piloto converte em contrato pago; NPS verbal positivo do gestor.

---

## 11. Definition of Done global

Vale para TODA entrega de qualquer fase:

### 11.1 Gates de CI (sempre verdes)

1. **Isolamento multi-tenant**: suíte (Testcontainers-Postgres) que cria 2 tenants, popula e verifica que TODO endpoint do portal com IDs do tenant B autenticado no tenant A retorna **404**; e que a ingestão com device token do tenant B jamais grava em A. Gate de merge desde F0.
2. **Contrato canônico**: testes de contrato validam que agente e backend serializam/aceitam exatamente o envelope 5.2, os 19 tipos 5.3 e o ack 5.5; tipo desconhecido não rejeita lote; lote vazio atualiza `last_seen_at`.
3. Build + testes unitários/integração de backend e portal; `dotnet list package --vulnerable`/dependabot sem críticos.

### 11.2 Cenários nomeados do pipeline (testes de integração obrigatórios desde F2)

| Cenário | Entrada | Resultado esperado |
|---|---|---|
| **lock-vence-idle** | `IDLE_START` … `LOCK` … `UNLOCK` | `idle` termina no `LOCK`; intervalo `locked`; `active` após `UNLOCK` |
| **idle-retroativo** | `active` desde 14:00; `IDLE_START` às 14:31:40 com `last_input_at` 14:26:40 | `active` fecha em **14:26:40**; `idle` começa em 14:26:40 (NUNCA 5 min de ativo falso) |
| **fora-de-ordem** | lote atrasado com eventos de ontem chega hoje | `dirty_from` retrocede; janela reconstruída; intervalos idênticos aos de uma entrega em ordem (idempotência do rebuild) |
| **gap-no-data** | último evento 10:00, próximo 10:20, sem desligamento limpo | intervalo fecha às 10:00; `no_data` 10:00→10:20 |
| **desligamento-limpo** | `SYSTEM_SUSPEND` 12:00, `SYSTEM_RESUME` 13:00 | `off_clean` 12:00→13:00 (JAMAIS `no_data`) |
| **duplicata** | mesmo lote enviado 2× | `duplicates` = n no 2º ack; zero linha duplicada; intervalos inalterados |
| **lacuna-de-seq** | seq 100, 101, **105** | intervalos do trecho com `data_incomplete = true`; flag presente na resposta da timeline |
| **relogio-adulterado** | eventos com `occurred_at` futuro > 5 min / passado > 14 d | rejeitados individualmente com reason correto; restante do lote aceito |
| **timezone** | device em GMT-4, tenant em GMT-3 | timeline/relatório exibem no fuso do tenant; badge de divergência presente; corte do dia à meia-noite do tenant |

### 11.3 Qualidade de produto

- CSV sempre UTF-8 **com BOM** e separador `;` (teste automatizado do arquivo gerado); disclaimer da Portaria 671/MTE presente em tela e em todo CSV de jornada.
- Números do rodapé da timeline = números do relatório de jornada para o mesmo device/dia (teste de consistência).
- Nenhum log (agente ou servidor) contém `window_title` ou nome de usuário em nível Information (teste de scrubbing).
- Toda visualização de dado pessoal gera linha em `audit_log` (teste de middleware).
- Vocabulário: nenhuma string de UI usa "produtivo/improdutivo" para estado de máquina, nem "Entrada/Saída" no relatório de jornada (lint de strings).
- Agente: metas de consumo da Seção 6.8 medidas em VM antes de cada release; `EVENTS_DROPPED` emitido em teste de estouro de buffer.
- Ack só após commit (teste que mata a transação e verifica que o agente reenvia sem perda).

---

## 12. Fora do escopo do MVP (lista explícita — NÃO construir)

Cortes confirmados. Se algo daqui aparecer em código de MVP, é scope creep:

1. `APPS_SNAPSHOT` (coleta e tipo de evento) — sem consumidor + minimização LGPD.
2. Build **win-arm64** do agente.
3. Anéis canary/percentuais e canal beta de auto-update (MVP = canal único + `min_version` + rollback por manifesto, pacote assinado Authenticode).
4. Suporte a RDS/Citrix/Terminal Server — detectar e marcar "não suportado".
5. Engenharia para 10k devices — dimensionar ~2.500 (N25).
6. `pg_partman` — partições mensais/diárias por migration + job próprio.
7. Stack completa de observabilidade (OTel/Prometheus/Grafana) — MVP = Serilog→Seq + Sentry + `/healthz` + uptime externo.
8. Rotação automática de device token (e comandos `ROTATE_TOKEN`/`UPDATE_AGENT`/`PAUSE`) — revogação manual + `UNENROLL` bastam.
9. Famílias de refresh token com detecção de reuso — refresh simples revogável.
10. 4º papel (Manager-por-equipe / Viewer-agregado) e escopo por equipe — Owner/Admin/Viewer.
11. Timeline avançada: zoom multi-resolução com re-fetch, drag-select, virtualização > 30 lanes, cursor de teclado no canvas — MVP = device/dia + equipe/dia com tooltip e fallback tabular, resolução fixa 1 min server-side (N21).
12. PDF server-side (QuestPDF) — MVP = CSV UTF-8 com BOM; XLSX/PDF é v1.1.
13. Página de transparência com link público tokenizado (`/t/:token`, preview "ver como funcionário") — MVP = página pública por slug (`/transparencia/:slug`, só a política de coleta) + kit PDF.
14. Mobile completo e WCAG AA integral — manter básicos (contraste, labels, fallback tabular).
15. Pentest como gate de GA — agendar antes da 1ª conta grande não-amiga.
16. Retenção configurável por tenant/plano — fixa N10–N13 no MVP.
17. macOS / Linux.
18. Screenshots em qualquer forma (mesmo opt-in) — reavaliação só em v2+, como projeto jurídico próprio; keylog/clipboard/webcam/mic JAMAIS (Seção 9.7).
19. Alertas configuráveis, webhooks, API pública, SSO/SAML, billing automatizado (gateway), websocket/SSE, dark mode, PWA, i18n — tudo v1.1+.
20. Signup self-service e tela "Criar conta" — org via backoffice.
21. CRUD de feriados e jornada esperada por pessoa/escala — v1.1.
22. Assinatura criptográfica da config do agente — TLS + device token bastam no MVP.

---

*Fim do prompt mestre. Em dúvida de implementação: Seção 5 (contrato) > Seção 7 (backend) > Seção 6 (agente) > Seção 8 (portal). Números: sempre a tabela N1–N25. Estados: sempre `active | idle | locked | off_clean | no_data`.*
