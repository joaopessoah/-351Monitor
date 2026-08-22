# Especificação de Backend — SaaS de Monitoramento de Estações Windows (MVP)

## 1. Stack e Topologia de Deploy

### 1.1 Stack recomendada

| Camada | Escolha (MVP) | Justificativa | Alternativa considerada |
|---|---|---|---|
| Runtime/API | **ASP.NET Core 8 (LTS)**, C# 12 | LTS até nov/2026, time já domina; performance de sobra para o volume estimado | .NET 9 (STS — evitar em produto comercial) |
| Estilo de API | **Controllers para a API do portal; Minimal APIs para os 3 endpoints do agente** (`/agent/enroll`, `/ingest/batch`, ack de comandos) | Portal tem CRUD extenso (controllers + filtros + `[Authorize]` por papel ficam mais organizados); ingest é hot path pequeno e estável | Tudo minimal APIs (aceitável, questão de gosto) |
| ORM | **EF Core 8** para portal/CRUD; **Dapper/Npgsql puro** no caminho de ingestão e nos jobs de agregação (multi-row `INSERT ... ON CONFLICT`, SQL de janela) | EF no hot path de escrita gera overhead e SQL subótimo; agregações são SQL puro por natureza | Dapper para tudo (perde migrations e produtividade no CRUD) |
| Banco | **PostgreSQL 16 — banco único** com particionamento nativo por tempo | Ver 1.2 | TimescaleDB / ClickHouse — ver 1.2 |
| Jobs | **Worker Service .NET separado** (container `worker`) com **Quartz.NET** + `pg_advisory_lock` | Ver seção 5.4 | Hangfire (aceitável; dashboard útil, mas mais uma dependência de storage) |
| Proxy/TLS | **Caddy** (HTTPS automático Let's Encrypt) | Zero manutenção de certificado | Nginx + certbot |
| Deploy | **Docker Compose** em 1 VM (MVP): `caddy`, `api`, `worker`, `postgres`, `seq` (logs), `otel-collector` | Time pequeno; sem orquestrador até precisar | k8s — só quando houver >1 nó por necessidade real |
| Filas | **Nenhuma no MVP.** Postgres é a fila (tabela de cursores + polling do worker) | Evitar RabbitMQ/Redis = menos peças para operar | Redis para cache/rate-limit distribuído (fase 2, quando houver >1 instância de API) |

### 1.2 PostgreSQL único vs TimescaleDB vs ClickHouse

- **PostgreSQL puro no MVP**: o volume estimado (~15 M eventos/dia no teto de 10.000 devices — ver seção 8) é ~175 inserts/s em média, ~1.000/s em pico de rajada. Inserção em lote via `INSERT` multi-row resolve com folga em um único nó NVMe. Particionamento nativo por dia + `DROP PARTITION` dá retenção barata. Um único sistema = um único backup, um único expertise operacional.
- **TimescaleDB é o primeiro degrau de evolução, não o ponto de partida**: é extensão do Postgres (mesmo SQL, mesmas ferramentas, migração quase trivial — `create_hypertable` sobre as mesmas tabelas). Adotar quando: (a) compressão se tornar necessária (storage de raw/intervals > ~1 TB) — compressão columnar do Timescale reduz 90%+; (b) continuous aggregates simplificarem o pipeline. Atenção à licença (Timescale License para compressão — ok para self-host, restrição é para revender DBaaS).
- **ClickHouse só quando** houver >50–100 M eventos/dia ou dashboards analíticos ad-hoc sobre raw que o Postgres não atende. Custo: segundo sistema de armazenamento, semântica de dedup diferente (`ReplacingMergeTree` é eventual), time precisa aprender operação. Não se justifica no MVP.
- **Gatilhos objetivos de migração**: p95 do dashboard > 500 ms com agregados já pré-calculados; lag de processamento > 15 min sustentado; storage > 1,5 TB; ingest > 5.000 eventos/s sustentado.

### 1.3 Hospedagem — latência e LGPD

| Opção | Latência BR | Residência de dados | Custo relativo | Recomendação |
|---|---|---|---|---|
| **Azure Brazil South** (São Paulo) | <20 ms | Brasil | Alto (~R$ 3–5 mil/mês para VM D4as_v5 + Postgres Flexible Server D4ds + storage/backup) | **Recomendada se o time já tem afinidade Azure/.NET**; Postgres Flexible Server tira backup/PITR das suas costas |
| **AWS sa-east-1** (São Paulo) | <20 ms | Brasil | Alto (sa-east-1 é ~30–40% mais cara que us-east-1) | Equivalente; Lightsail é opção barata de entrada |
| **Hetzner / Contabo (EU)** | 110–200 ms | **Fora do Brasil** | Muito baixo (€50–100/mês para máquina equivalente) | **Não recomendada para este produto**: dado de monitoramento de funcionário é sensível comercialmente; clientes B2B (RH/jurídico) vão exigir residência nacional em contrato. Transferência internacional é possível sob LGPD (art. 33, com salvaguardas), mas vira atrito de venda |
| **Magalu Cloud / Oracle Cloud (Vinhedo)** | <25 ms | Brasil | Baixo–médio | Alternativa BR barata se o custo de Azure/AWS pesar; Oracle tem free tier generoso para staging |

**Recomendação MVP**: 1 VM (8 vCPU / 32 GB / NVMe ≥ 1 TB) em região brasileira com Docker Compose, **ou** VM menor (4 vCPU/16 GB) + Postgres gerenciado (Azure Flexible Server). O Postgres gerenciado custa mais, mas elimina o maior risco operacional do time pequeno (backup/PITR/failover). Latência importa pouco para o agente (envio assíncrono em lote), e muito para o portal.

---

## 2. Multi-tenancy

### 2.1 Modelo: schema compartilhado + `tenant_id` em toda tabela + RLS

**Recomendação: pool model** — todas as organizações no mesmo schema, coluna `tenant_id uuid NOT NULL` em toda tabela de dados, com **Row-Level Security do Postgres como segunda camada** obrigatória.

Por que não schema-por-tenant no MVP:
- 50+ schemas × migrations EF = pesadelo operacional para time pequeno (migração falha no tenant 37 e o deploy fica inconsistente).
- Particionamento por tempo + schema por tenant multiplica número de objetos (50 tenants × 90 partições diárias = 4.500 tabelas só de raw).
- Connection pooling degrada (search_path por conexão).
- Métricas/queries internas cross-tenant (billing, saúde de agentes) ficam fáceis no pool model.
- Schema/banco por tenant fica reservado para o futuro **plano enterprise com isolamento dedicado** (e aí é banco separado, não schema).

### 2.2 Defesa em profundidade (3 camadas)

1. **Camada de aplicação — `ITenantContext` obrigatório**: middleware resolve o tenant do JWT (claim `org_id`) ou do device token e injeta `ITenantContext` (scoped). EF Core com **global query filter** `e => e.TenantId == _tenantContext.TenantId` em todas as entidades; `SaveChanges` interceptado para carimbar `TenantId` em inserts e lançar exceção se divergir. Repositórios não expõem método sem filtro; queries Dapper passam por um `TenantScopedConnection` que exige o parâmetro.
2. **Camada de banco — RLS**: aplicação conecta com role `app_user` (sem `BYPASSRLS`, **nunca** o owner das tabelas — owner ignora RLS por padrão). Por requisição/transação: `SET LOCAL app.tenant_id = '<uuid>'`. Política padrão em toda tabela:
   ```sql
   ALTER TABLE devices ENABLE ROW LEVEL SECURITY;
   ALTER TABLE devices FORCE ROW LEVEL SECURITY;
   CREATE POLICY tenant_isolation ON devices
     USING (tenant_id = current_setting('app.tenant_id')::uuid);
   ```
   Usar `SET LOCAL` dentro de transação (não `SET`) para não vazar o GUC entre requisições no pool do Npgsql. Implementar via interceptor de conexão/transação.
3. **Testes de contrato cross-tenant no CI**: suíte que cria 2 tenants, popula dados e verifica que cada endpoint do portal com IDs do tenant B autenticado como tenant A retorna 404 (nunca 403 — não revelar existência).

### 2.3 Identificadores

- Todos os IDs expostos: **UUIDv7** (ordenável por tempo, índice b-tree amigável, não enumerável — mitiga IDOR por chute).
- Lookup sempre por `(tenant_id, id)` — mesmo que `id` seja globalmente único, a cláusula dupla garante isolamento mesmo se uma camada falhar.

---

## 3. Modelo de Dados (DDL conceitual)

Convenções: `id uuid PK default uuidv7`, `tenant_id uuid NOT NULL REFERENCES organizations(id)`, `created_at/updated_at timestamptz`. Todas as tabelas (exceto `organizations` e `app_catalog`) têm `tenant_id` + RLS.

### 3.1 Identidade e tenancy

```sql
CREATE TABLE organizations (
  id uuid PRIMARY KEY,
  name text NOT NULL,
  slug text UNIQUE NOT NULL,
  timezone text NOT NULL DEFAULT 'America/Sao_Paulo',   -- agregação diária usa TZ da org
  business_hours jsonb,            -- ex.: {"mon":["08:00","18:00"],...} p/ relatórios
  collect_window_titles boolean NOT NULL DEFAULT true,  -- toggle LGPD/minimização
  raw_retention_days int NOT NULL DEFAULT 90,
  plan text NOT NULL DEFAULT 'mvp',
  status text NOT NULL DEFAULT 'active',  -- active|suspended|cancelled
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (                -- usuários do PORTAL
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL,
  email citext NOT NULL,
  password_hash text,               -- argon2id; NULL se convite pendente
  display_name text NOT NULL,
  role text NOT NULL CHECK (role IN ('owner','admin','manager','viewer')),
  mfa_secret_enc bytea,             -- TOTP, cifrado em repouso; NULL = MFA off
  status text NOT NULL DEFAULT 'invited',  -- invited|active|disabled
  last_login_at timestamptz,
  UNIQUE (tenant_id, email)
);

CREATE TABLE invitations (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  email citext NOT NULL, role text NOT NULL,
  token_hash bytea NOT NULL,        -- SHA-256 do token enviado por e-mail
  expires_at timestamptz NOT NULL,  -- 7 dias
  accepted_at timestamptz, invited_by uuid REFERENCES users(id)
);

CREATE TABLE refresh_tokens (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  user_id uuid NOT NULL REFERENCES users(id),
  token_hash bytea NOT NULL,        -- SHA-256; rotação a cada uso
  family_id uuid NOT NULL,          -- detecção de reuso (revoga família inteira)
  expires_at timestamptz NOT NULL, revoked_at timestamptz,
  user_agent text, ip inet
);
```

### 3.2 Dispositivos e enrollment

```sql
CREATE TABLE enrollment_keys (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  key_prefix text NOT NULL,         -- ex.: 'ek_7f3a' visível no portal
  key_hash bytea NOT NULL,          -- SHA-256 do segredo completo
  label text,                       -- 'Filial SP', 'Setor Financeiro'
  max_uses int,                     -- NULL = ilimitado
  use_count int NOT NULL DEFAULT 0,
  expires_at timestamptz, revoked_at timestamptz,
  default_tags text[]               -- tags aplicadas aos devices enrolados
);

CREATE TABLE devices (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  hostname text NOT NULL,
  machine_fingerprint text NOT NULL,  -- hash(MachineGuid + BIOS serial) p/ re-enroll idempotente
  os_version text, agent_version text,
  enrollment_key_id uuid REFERENCES enrollment_keys(id),
  token_hash bytea NOT NULL,          -- SHA-256 do device token vigente
  token_rotated_at timestamptz,
  config_version int NOT NULL DEFAULT 1,
  tags text[],
  status text NOT NULL DEFAULT 'active', -- active|paused|revoked
  last_seen_at timestamptz,            -- atualizado a cada batch (heartbeat)
  clock_offset_ms bigint NOT NULL DEFAULT 0, -- skew estimado no último batch
  UNIQUE (tenant_id, machine_fingerprint)
);
CREATE INDEX ix_devices_tenant_lastseen ON devices (tenant_id, last_seen_at);

CREATE TABLE device_users (           -- usuários Windows observados na máquina
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  device_id uuid NOT NULL REFERENCES devices(id),
  windows_sid text NOT NULL,
  windows_username text NOT NULL,     -- 'DOMINIO\jsilva'
  display_name text,                  -- editável no portal
  first_seen_at timestamptz NOT NULL, last_seen_at timestamptz NOT NULL,
  UNIQUE (tenant_id, device_id, windows_sid)
);

CREATE TABLE device_commands (        -- canal de comando pull (via ack do ingest)
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  device_id uuid NOT NULL,
  type text NOT NULL,    -- UPDATE_CONFIG | ROTATE_TOKEN | UPDATE_AGENT | PAUSE | UNENROLL
  payload jsonb NOT NULL DEFAULT '{}',
  created_at timestamptz NOT NULL DEFAULT now(),
  delivered_at timestamptz, acked_at timestamptz, expires_at timestamptz
);
CREATE INDEX ix_commands_pending ON device_commands (device_id) WHERE acked_at IS NULL;
```

### 3.3 Telemetria — raw, intervalos, agregados

```sql
-- RAW: particionada por DIA, retenção 90 dias (DROP PARTITION via job)
CREATE TABLE raw_events (
  tenant_id uuid NOT NULL,
  device_id uuid NOT NULL,
  event_id uuid NOT NULL,            -- UUIDv7 gerado pelo AGENTE (idempotência)
  occurred_at timestamptz NOT NULL,  -- relógio do AGENTE, IMUTÁVEL (dedup determinístico)
  event_type smallint NOT NULL,      -- enum: ver seção 5.1
  windows_sid text,
  session_id int,
  process_name text,                 -- 'chrome.exe' (lowercase, sem path)
  window_title text,                 -- truncado em 256 chars; NULL se org desabilitou
  payload jsonb,                     -- campos extras por tipo (idle_threshold, exit_code...)
  received_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (device_id, event_id, occurred_at)  -- partição exige occurred_at na PK
) PARTITION BY RANGE (occurred_at);
-- dedup: mesmo event_id ⇒ mesmo occurred_at (imutável no agente) ⇒ PK funciona entre retries
CREATE INDEX ix_raw_tenant_dev_time ON raw_events (tenant_id, device_id, occurred_at);

-- INTERVALOS derivados: particionada por MÊS, retenção 13 meses
CREATE TABLE activity_intervals (
  id uuid NOT NULL,
  tenant_id uuid NOT NULL,
  device_id uuid NOT NULL,
  device_user_id uuid NOT NULL,
  started_at timestamptz NOT NULL,   -- já corrigido por clock_offset
  ended_at timestamptz NOT NULL,
  state smallint NOT NULL,           -- 1=active 2=idle 3=locked 4=offline_gap
  app_id uuid,                       -- NULL quando idle/locked/offline
  window_title text,                 -- título dominante do intervalo
  source_day date NOT NULL,          -- dia local (TZ da org) p/ reagregação
  PRIMARY KEY (tenant_id, device_id, started_at, id)
) PARTITION BY RANGE (started_at);
CREATE INDEX ix_intervals_user_time ON activity_intervals (tenant_id, device_user_id, started_at);
-- invariante: intervalos de um (device_id, device_user_id) nunca se sobrepõem

-- AGREGADOS DIÁRIOS: sem partição (volume baixo), retenção 25 meses
CREATE TABLE daily_device_summaries (
  tenant_id uuid NOT NULL, summary_date date NOT NULL,  -- dia local da org
  device_id uuid NOT NULL, device_user_id uuid NOT NULL,
  seconds_active int NOT NULL DEFAULT 0,
  seconds_idle int NOT NULL DEFAULT 0,
  seconds_locked int NOT NULL DEFAULT 0,
  seconds_productive int NOT NULL DEFAULT 0,   -- via categoria do tenant
  seconds_unproductive int NOT NULL DEFAULT 0,
  seconds_neutral int NOT NULL DEFAULT 0,
  first_activity_at timestamptz, last_activity_at timestamptz,
  session_count smallint, computed_at timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, summary_date, device_id, device_user_id)
);

CREATE TABLE daily_app_usage (
  tenant_id uuid NOT NULL, summary_date date NOT NULL,
  device_id uuid NOT NULL, device_user_id uuid NOT NULL,
  app_id uuid NOT NULL,
  seconds_active int NOT NULL, focus_count int NOT NULL,
  PRIMARY KEY (tenant_id, summary_date, device_id, device_user_id, app_id)
);
CREATE INDEX ix_dau_tenant_date_app ON daily_app_usage (tenant_id, summary_date, app_id);
```

### 3.4 Catálogo de apps e categorias

```sql
-- GLOBAL (sem tenant_id, sem RLS, read-only para tenants; curado pelo produto)
CREATE TABLE app_catalog (
  id uuid PRIMARY KEY,
  process_name text UNIQUE NOT NULL,  -- 'chrome.exe' (chave de matching)
  display_name text NOT NULL,         -- 'Google Chrome'
  vendor text,
  default_category text,              -- sugestão global: 'browser','dev','office','chat'...
  curated boolean NOT NULL DEFAULT false  -- false = auto-criado por telemetria, pendente
);
-- processo desconhecido no ingest ⇒ INSERT ON CONFLICT DO NOTHING com display_name=process_name
-- curadoria (F1.1): o dicionário versionado apps-br.csv (embutido na Infrastructure) é aplicado
-- por um seeder idempotente no startup da API, preenchendo display_name, default_category e
-- curated=true. O seeder nunca escreve em tenant_app_categories nem em custom_display_name.

-- POR TENANT: classificação produtivo/improdutivo é decisão de negócio do cliente
CREATE TABLE categories (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  name text NOT NULL,                 -- 'Desenvolvimento', 'Redes Sociais'
  productivity smallint NOT NULL,     -- 1=produtivo 0=neutro -1=improdutivo
  color text,
  UNIQUE (tenant_id, name)
);
-- seed na criação da org: Desenvolvimento(+1), Escritório(+1), Comunicação(0),
-- Navegação(0), Entretenimento(-1), Sem categoria(0)

CREATE TABLE tenant_app_categories (
  tenant_id uuid NOT NULL, app_id uuid NOT NULL REFERENCES app_catalog(id),
  category_id uuid NOT NULL REFERENCES categories(id),
  custom_display_name text,
  PRIMARY KEY (tenant_id, app_id)
);
```

### 3.5 Operação e auditoria

```sql
CREATE TABLE ingest_cursors (        -- controla pipeline de intervalização (seção 5)
  tenant_id uuid NOT NULL, device_id uuid PRIMARY KEY,
  processed_until timestamptz NOT NULL,   -- watermark
  dirty_from timestamptz,                 -- menor occurred_at não processado (NULL = limpo)
  updated_at timestamptz NOT NULL
);

CREATE TABLE dirty_days (            -- dias a reagregar por dados atrasados
  tenant_id uuid NOT NULL, device_id uuid NOT NULL, day date NOT NULL,
  PRIMARY KEY (tenant_id, device_id, day)
);

CREATE TABLE audit_log (             -- append-only; LGPD: quem viu o quê
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  actor_user_id uuid, actor_ip inet,
  action text NOT NULL,       -- 'view_timeline','export_csv','login','update_category',...
  target_type text, target_id uuid,    -- 'device_user', 'device', 'report'
  detail jsonb,               -- período consultado, filtros, n° de linhas exportadas
  occurred_at timestamptz NOT NULL DEFAULT now()
) PARTITION BY RANGE (occurred_at);   -- mensal, retenção 25 meses

CREATE TABLE export_jobs (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, requested_by uuid NOT NULL,
  kind text NOT NULL, params jsonb NOT NULL,
  status text NOT NULL DEFAULT 'queued',  -- queued|running|done|failed
  file_path text, row_count int, expires_at timestamptz  -- arquivo expira em 7 dias
);
```

### 3.6 Particionamento e retenção (resumo)

| Tabela | Partição | Retenção sugerida | Mecanismo |
|---|---|---|---|
| `raw_events` | diária | **90 dias** (configurável por org até 90; reduzir é minimização LGPD) | job noturno `DROP PARTITION`; partições criadas D+3 à frente |
| `activity_intervals` | mensal | **13 meses** (timeline detalhada + comparação YoY) | `DROP PARTITION` |
| `daily_*_summaries` | — | **25 meses** (2 anos + mês corrente) | `DELETE` por data |
| `audit_log` | mensal | **25 meses** | `DROP PARTITION` |

Gerenciar partições com **pg_partman** ou job próprio no worker (preferir job próprio: menos uma extensão, lógica trivial).

---

## 4. API de Ingestão

### 4.1 Enrollment

`POST /api/v1/agent/enroll` — sem autenticação prévia; body: `{ enrollment_key, hostname, machine_fingerprint, os_version, agent_version }`.
- Valida key (hash, expiração, max_uses, revogação). Se `(tenant_id, machine_fingerprint)` já existe ⇒ **re-enroll idempotente**: revoga token antigo, emite novo, mantém histórico do device.
- Resposta `201`: `{ device_id, device_token, config: {...}, config_version }`. `device_token` = `dt_` + 256 bits base64url, **opaco** (não JWT — revogação imediata por lookup de hash, sem estado no token). Armazenado só como SHA-256.
- Rate limit por IP: 10/min (proteção contra brute-force de keys).

### 4.2 `POST /api/v1/ingest/batch`

Headers: `Authorization: Bearer dt_...`, `Content-Encoding: gzip` (aceito e recomendado), `Content-Type: application/json`.

```json
{
  "batch_id": "uuidv7",
  "agent_version": "1.2.0",
  "sent_at": "2026-06-09T14:32:10.120-03:00",
  "config_version": 4,
  "events": [
    { "event_id": "uuidv7", "type": "ACTIVE_WINDOW_CHANGED",
      "occurred_at": "2026-06-09T14:31:55.000-03:00",
      "windows_sid": "S-1-5-21-...", "session_id": 2,
      "process_name": "chrome.exe", "window_title": "Relatório Q2 - Google Docs" },
    { "event_id": "uuidv7", "type": "IDLE_START",
      "occurred_at": "...", "windows_sid": "...", "session_id": 2 }
  ]
}
```

**Limites**: máx. **500 eventos/batch**; body máx. **1 MB** (pós-gzip ~5 MB descompactado, validar tamanho descompactado também — zip bomb); `window_title` truncado a 256 chars no agente e revalidado no servidor.

**Idempotência barata**: `INSERT ... ON CONFLICT (device_id, event_id, occurred_at) DO NOTHING` em statement multi-row único (500 linhas = 1 round-trip). `occurred_at` é o timestamp **original do agente, nunca alterado** — garante que retry do mesmo batch deduplica deterministicamente. Duplicatas = `events.length − rows_inserted` (via `RETURNING 1`). Sem tabela de dedup separada, sem Redis.

**Validação de relógio**: `skew = received_at − sent_at` (desconta-se latência estimada; suficiente no MVP). Persistir `devices.clock_offset_ms` (média móvel dos últimos 5 batches). Regras: evento com `occurred_at > received_at + 2 min` ⇒ **rejeitado** individualmente (listado em `rejected`); `occurred_at < now − 30 dias` ⇒ rejeitado (lixo/replay); o offset é aplicado **na intervalização** (raw fica intacto), corrigindo `started_at/ended_at` para o relógio do servidor.

**Rate limiting por device** (token bucket em memória por token; mover p/ Redis quando houver 2+ instâncias):
- Sustentado: **6 batches/min**, burst 30 — cobre operação normal (1 batch/min) e catch-up pós-offline.
- Cota diária dura: **100.000 eventos/device/dia** ⇒ acima disso `429` + comando `SET_CONFIG` reduzindo verbosidade (proteção contra agente bugado em loop).
- Resposta `429` com `Retry-After`; agente respeita com backoff exponencial + jitter.

**Resposta (ack) — canal de configuração pull (sem conexão persistente no MVP)**:

```json
{
  "accepted": 498, "duplicates": 2,
  "rejected": [ { "event_id": "...", "reason": "timestamp_in_future" } ],
  "server_time": "2026-06-09T14:32:10.500-03:00",
  "config_version": 5,
  "config": { "window_poll_ms": 5000, "idle_threshold_s": 120,
              "batch_interval_s": 60, "collect_window_titles": true,
              "title_redact_patterns": [] },
  "commands": [
    { "id": "uuid", "type": "ROTATE_TOKEN", "payload": { "new_token": "dt_..." } }
  ]
}
```

- `config` só vem quando `config_version` do agente está desatualizado (senão `config: null`).
- Comandos pendentes (`device_commands` com `acked_at IS NULL`, máx. 5 por ack) são marcados `delivered_at`; o agente confirma via `POST /api/v1/agent/commands/{id}/ack`. Comando não-ackado em 24 h é reenviado; `expires_at` cancela.
- Agente envia batch a cada **60 s** (ou imediatamente ao atingir 500 eventos); **batch vazio funciona como heartbeat** quando não há eventos — garante `last_seen_at` e entrega de comandos com frequência mínima de 1/min.
- Respostas de erro: `401` token inválido/revogado (agente entra em modo re-enroll se tiver key persistida), `403` device `paused` (descarta coleta), `413` payload, `422` batch malformado inteiro.

---

## 5. Pipeline de Processamento

### 5.1 Tipos de evento (enum `event_type`)

| # | Tipo | Origem no agente |
|---|---|---|
| 1 | `AGENT_START` / 2 `AGENT_STOP` | serviço Windows inicia/para (proxy de boot/shutdown) |
| 3 | `SESSION_LOGON` / 4 `SESSION_LOGOFF` | SENS/WTS API |
| 5 | `SESSION_LOCK` / 6 `SESSION_UNLOCK` | `WTSSessionChange` |
| 7 | `ACTIVE_WINDOW_CHANGED` | polling 5 s de `GetForegroundWindow`, emite só na mudança (processo ou título) |
| 8 | `IDLE_START` / 9 `IDLE_END` | `GetLastInputInfo`, limiar default **120 s** sem input |
| 10 | `HEARTBEAT` | a cada **300 s** se nenhum outro evento ocorreu |

### 5.2 Intervalização — algoritmo

Máquina de estados por **(device_id, windows_sid)**, executada pelo worker em micro-batches:

1. **Seleção de trabalho**: a cada **60 s**, worker varre `ingest_cursors WHERE dirty_from IS NOT NULL` (o endpoint de ingest faz upsert do cursor com `dirty_from = min(occurred_at do batch)` se menor que o atual). Processa por device, com `pg_advisory_xact_lock(hash(device_id))` para exclusão mútua.
2. **Janela de reprocessamento**: para cada device sujo, define `R = [date_trunc('hour', dirty_from) − 1h, now]`. `DELETE FROM activity_intervals WHERE device_id = X AND ended_at > R.start` e **reconstrói** a partir de `raw_events` ordenados por `occurred_at` desde `R.start`. Reconstrução idempotente resolve eventos atrasados/fora de ordem (máquina offline que despeja 3 dias de backlog ⇒ `dirty_from` antigo ⇒ janela cobre os 3 dias). Custo aceitável: reprocessar 1 dia de 1 device ≈ 1.500 eventos.
3. **Máquina de estados** (corrigindo timestamps com `clock_offset_ms`):
   - Estado corrente: `(state ∈ {active, idle, locked, offline}, app_id, window_title, since)`.
   - `ACTIVE_WINDOW_CHANGED` ⇒ fecha intervalo corrente em `t`, abre `active(app)` — resolve `app_id` por `process_name` no `app_catalog` (auto-insere se desconhecido).
   - `IDLE_START` ⇒ fecha em `t`, abre `idle`. `IDLE_END` ⇒ fecha `idle`, abre `active` com o **último app conhecido** (o agente também emite `ACTIVE_WINDOW_CHANGED` logo após, que corrige se mudou).
   - `SESSION_LOCK` ⇒ fecha, abre `locked` (lock vence idle: se estava idle, idle termina). `SESSION_UNLOCK` ⇒ fecha `locked`, abre `active`.
   - `SESSION_LOGOFF`/`AGENT_STOP` ⇒ fecha intervalo corrente, estado vira `offline` (não gera intervalo até novo logon).
   - **Detecção de gap**: se a diferença entre eventos consecutivos > **2× heartbeat (600 s)**, fecha o intervalo corrente em `last_event + 300 s` (grace) e registra intervalo `offline_gap` até o próximo evento — cobre queda de energia/kill do agente sem `AGENT_STOP`.
   - Intervalos < **1 s** são descartados (ruído de alt-tab rápido); fusão de intervalos adjacentes idênticos (mesmo state+app+title).
   - Invariante verificada: intervalos de um (device, sid) **não se sobrepõem** (constraint lógica testada; opcionalmente `EXCLUDE USING gist` com `tstzrange` — avaliar custo do índice GiST, provavelmente deixar só no teste).
4. Ao final, atualiza `processed_until`, zera `dirty_from`, e insere em `dirty_days` cada dia local (TZ da org) tocado.

### 5.3 Agregação diária

- Job `DailyAggregation` a cada **15 min**: consome `dirty_days`, recomputa `daily_device_summaries` e `daily_app_usage` do dia/device via `INSERT ... ON CONFLICT DO UPDATE` (full recompute do dia — barato e idempotente). `seconds_productive/unproductive/neutral` resolvidos no momento da agregação via `tenant_app_categories` ⇒ **mudança de categoria dispara reagregação** (endpoint de categoria insere `dirty_days` dos últimos 30 dias do tenant — documentar que histórico além disso mantém classificação antiga, ou oferecer botão "reprocessar período").
- Job `PartitionMaintenance` diário 02:00 BRT: cria partições futuras, dropa expiradas.
- Job `TokenAndKeyHousekeeping` diário: expira refresh tokens, invitations, export_jobs.

### 5.4 Onde roda

**Worker Service .NET separado (container `worker` no mesmo Compose, mesmo solution/repo)** — não no processo da API:
- Pico de ingest não disputa CPU com reprocessamento de backlog (o cenário "100 máquinas religam às 8h da segunda" é real).
- Deploy/restart independentes; API permanece stateless e horizontalmente escalável.
- Custo marginal zero no Compose.

Scheduler: **Quartz.NET** (cron, misfire handling) + `pg_advisory_lock` para garantir instância única de cada job. **Hangfire** é alternativa válida (dashboard de jobs pronto ajuda ops de time pequeno; storage no próprio Postgres) — escolher um e não misturar. Evitar `IHostedService` + `PeriodicTimer` artesanal para mais de 2 jobs.

---

## 6. API do Portal (REST, `/api/v1`)

Papéis: `Owner ⊃ Admin ⊃ Manager ⊃ Viewer`. **No MVP, Manager = Viewer** (escopo por time fica pós-MVP; o papel já existe no enum para não migrar dados depois). Todas as rotas exigem JWT; tenant vem do token, **nunca** da URL.

| Método e rota | Papel mínimo | Descrição |
|---|---|---|
| `POST /auth/login` | público | e-mail+senha ⇒ `{access_token, expires_in}` + refresh em cookie httpOnly; se MFA on ⇒ `mfa_required` + token temporário |
| `POST /auth/mfa/verify` | público | TOTP ⇒ tokens |
| `POST /auth/refresh` | cookie | rotação de refresh token (família) |
| `POST /auth/logout` | Viewer | revoga refresh |
| `POST /auth/forgot-password` / `POST /auth/reset-password` | público | fluxo padrão, token 1 h |
| `GET /me` | Viewer | perfil + papel + org |
| `GET /dashboard/summary?from&to&device_id&device_user_id` | Viewer | KPIs: horas ativas/ociosas, % produtivo, devices online, top 5 apps — **lê só de daily_summaries** |
| `GET /dashboard/top-apps?from&to&limit=20` | Viewer | de `daily_app_usage` |
| `GET /dashboard/activity-by-hour?date` | Viewer | distribuição horária (de `activity_intervals`) |
| `GET /devices?status&tag&q&page` | Viewer | lista paginada (padrão: `page`/`page_size` máx. 100) |
| `GET /devices/{id}` | Viewer | detalhe + última atividade + versão do agente |
| `PATCH /devices/{id}` | Admin | renomear, tags, `status=paused` |
| `DELETE /devices/{id}` | Admin | revoga token, `status=revoked` (soft) |
| `POST /devices/{id}/rotate-token` | Admin | enfileira `ROTATE_TOKEN` |
| `GET /devices/{id}/timeline?date` | Viewer | intervalos do dia (de `activity_intervals`) |
| `GET /devices/version-summary` | Viewer | distribuição de versões do agente na frota (F5): total ativo, versão vigente e mínima do canal `stable`, contagem por versão com marcação de desatualizada por SemVer, e as falhas de auto-update dos últimos 7 dias (`UPDATE_FAILED`, teto de 20 linhas) |
| `GET /devices/{id}/transparency-link` | **Admin** | endereço da página pública tokenizada daquele dispositivo (`/public/t/{token}`). Endpoint próprio de propósito: o `DeviceResponse` é lido por Viewer e trafega na listagem inteira. Dispositivo sem token responde `404`, igual ao inexistente |
| `GET /device-users?device_id&q` | Viewer | usuários Windows observados |
| `PATCH /device-users/{id}` | Admin | `display_name` |
| `GET /device-users/{id}/timeline?date` | Viewer | timeline consolidada do usuário (multi-device) |
| `GET /reports/usage?from&to&group_by=app\|category\|device\|device_user[&tag]` | Viewer | relatório tabular paginado; `tag` recorta a equipe (F5) |
| `GET /reports/jornada?from&to[&device_ids][&tag]` | Viewer | uma linha por dispositivo × dia do período |
| `GET /reports/fora-do-horario?from&to[&device_ids][&tag]` | Viewer | atividade fora do horário declarado (indicador de equilíbrio) |
| `POST /exports` | Viewer | `{kind:'usage_csv', params:{...}}` ⇒ `202 {export_id}` (assíncrono no worker; máx. 500 k linhas); `params.tag` aplica o mesmo recorte no CSV |
| `GET /exports/{id}` | Viewer (dono) | status + URL de download assinada (expira 7 dias) |
| `GET/POST /categories`, `PATCH/DELETE /categories/{id}` | Admin | CRUD; produtividade por tenant |
| `GET /app-catalog?uncategorized=true&q` | Viewer | apps vistos pelo tenant; inclui `default_category` (sugestão do dicionário brasileiro, F1.1) |
| `PUT /app-catalog/{appId}/category` | Admin | mapeia app ⇒ categoria do tenant |
| `PUT /app-catalog/categories/batch` | Admin | N mapeamentos numa única transação, com uma única reagregação de 30 dias (F1.1) |
| `GET/POST /enrollment-keys`, `DELETE /enrollment-keys/{id}` | Admin | segredo exibido **uma única vez** no POST |
| `GET /users`, `POST /users/invitations`, `PATCH /users/{id}`, `DELETE /users/{id}` | Admin (papel `owner` só editável por Owner) | gestão do portal |
| `GET/PATCH /organization` | Owner | timezone, business hours, toggles de coleta, retenção |
| `GET/PATCH /organization/agent-config` | Admin / **Owner** | política de coleta operável pela controladora e, na F5, o `notice_text`: o corpo do aviso de ciência exibido no primeiro logon. Limite já descontando o enquadramento fixo do agente, recusa de HTML ou marcação e recusa de texto que imite pedido de consentimento (`notice_text_too_long`, `notice_text_markup`, `notice_text_consent`). Salvar sobe `config_version` **e** `notice_version`, que é o que reexibe o aviso na frota |
| `GET /audit-logs?from&to&actor&action` | Owner | trilha LGPD |
| `POST /webhooks` *(pós-MVP)* | Admin | eventos: `device.offline>24h`, `export.ready`, `device.enrolled` |

Convenções: erros RFC 9457 (`application/problem+json`); paginação por página no MVP (cursor-based pós-MVP para timeline); datas sempre ISO-8601 com offset; recurso de outro tenant ⇒ `404`.

Filtro de equipe (`tag`, F5): mesmo parâmetro em `/dashboard/presence`, `/dashboard/summary`, `/dashboard/top-apps`, `/timeline/team`, os três relatórios e `POST /exports`. É filtro de VISUALIZAÇÃO, não escopo de permissão: qualquer papel continua vendo tudo e só escolhe o recorte exibido. Vazio equivale a sem filtro; etiqueta inexistente devolve recorte vazio, nunca `404` (etiqueta não é recurso com dono); o denominador dos percentuais é recortado junto. O recorte é sempre agregado, o produto não compara equipes lado a lado nem monta ranking entre elas.

---

## 7. AuthN / AuthZ

### 7.1 Portal
- **Senha**: **Argon2id** (lib `Konscious.Security.Cryptography` ou libsodium via `NSec`; parâmetros: 64 MB, 3 iterações, paralelismo 4). Se optarem por ASP.NET Identity, trocar o `PasswordHasher` default (PBKDF2) por Argon2id. Política: mín. 10 chars, checagem contra lista de senhas vazadas (pós-MVP: k-anonymity HIBP).
- **Sessão**: JWT de acesso **15 min** (claims: `sub`, `org_id`, `role`, `jti`), assinado HS256 com chave de 256 bits em secret store (RS256 quando houver múltiplos serviços validando). **Refresh token 30 dias**, opaco, httpOnly+Secure+SameSite=Strict, **rotação a cada uso com detecção de reuso por família** (reuso ⇒ revoga família ⇒ força re-login).
- **MFA TOTP opcional** por usuário (lib `Otp.NET`); org pode tornar obrigatório (`organizations` flag, pós-MVP). Recovery codes (10, hash).
- **Convites**: Admin cria invitation ⇒ e-mail com link token (válido 7 dias) ⇒ usuário define senha. Sem signup aberto no MVP (criação de org via backoffice interno).
- Lockout: 5 falhas/15 min por conta + rate limit por IP em `/auth/*` (10/min).

### 7.2 Dispositivos
- `enrollment_key` (criada no portal, com expiração/limite de usos/revogação) ⇒ troca por **device token opaco** de 256 bits, hash SHA-256 no banco, lookup O(1) por hash. Opaco em vez de JWT: **revogação instantânea** (device roubado/desligado) sem lista de bloqueio.
- **Rotação**: automática a cada **90 dias** (worker enfileira `ROTATE_TOKEN`; novo token entregue no ack; o antigo permanece válido por **24 h de carência** — coluna `previous_token_hash` + `previous_expires_at`) e manual via portal.
- Token de device autoriza **somente** os endpoints `/agent/*` e `/ingest/*` (policy separada, audiences distintas).

### 7.3 Autorização
- Policies ASP.NET Core: `RequireRole("admin")` etc. mapeadas da claim; recurso sempre re-verificado contra `tenant_id` no repositório (papel autoriza a ação, tenancy autoriza o dado).
- Pós-MVP (Manager por time): tabela `teams` + `team_members(device_user_id)` + `team_managers(user_id)`; filtro adicional no `ITenantContext` (`allowed_device_user_ids`). Deixar o hook no contexto desde já para não refatorar repositórios depois.

---

## 8. Requisitos Não-Funcionais (com números)

### 8.1 Dimensionamento — a conta

Por device, em um dia útil (9 h ligado, ~6 h de uso ativo):

| Fonte | Estimativa |
|---|---|
| `ACTIVE_WINDOW_CHANGED` (média 3 trocas/min em uso ativo × 360 min) | ~1.080 |
| `IDLE_START/END` (~20 ciclos) | ~40 |
| `LOCK/UNLOCK` (~10 ciclos) | ~20 |
| `LOGON/LOGOFF/AGENT_START/STOP` | ~6 |
| `HEARTBEAT` (300 s, só sem outros eventos) | ~50 |
| **Total/dia/device** | **~1.200–1.500** (dimensionar por **2.000** pior caso) |

Escala MVP — **50 tenants × 200 devices = 10.000 devices** (teto):
- **15–20 M eventos/dia** ≈ 175–230/s média; concentração em horário comercial ⇒ ~450/s sustentado, **rajadas de ~1.500/s** (segunda 8h, retorno de backlog). Postgres com insert em lote: confortável.
- **Requisições de ingest**: 10.000 devices × 1 batch/min ≈ **167 req/s** — trivial para 1 instância Kestrel.
- **Storage**: raw ~400 B/linha com índices ⇒ 6–8 GB/dia ⇒ **~600–700 GB para 90 dias**; intervals ~150 B × 12 M/dia ⇒ ~1,8 GB/dia ⇒ **~700 GB para 13 meses**; summaries são desprezíveis (<20 GB/2 anos). Total planejado: **NVMe 2 TB** no teto da escala (no início real, <50 GB). Esse número é o argumento para retenção raw=90d e para TimescaleDB+compressão como primeiro degrau de evolução.
- Hardware alvo no teto: Postgres 8 vCPU/32 GB; API 2 vCPU; worker 2–4 vCPU. No lançamento (5 tenants × 50 devices): tudo em 1 VM 4 vCPU/16 GB.

### 8.2 Alvos

| Métrica | Alvo MVP |
|---|---|
| Latência portal — dashboards (de agregados) | **p95 < 500 ms** |
| Latência portal — timeline detalhada (intervals) | p95 < 1,5 s |
| Latência ingest | p95 < 300 ms, p99 < 800 ms |
| **Lag de processamento** (evento recebido ⇒ visível na timeline) | < 5 min p95; alerta se > 15 min |
| Disponibilidade | 99,5% (janela de manutenção declarada); agente bufferiza offline por até 7 dias, então indisponibilidade curta da API **não perde dado** |
| Perda de eventos aceitos (pós-ack) | 0 (ack só após commit) |

### 8.3 Backups e DR
- **PITR**: `pgBackRest` (ou `wal-g`) — full semanal + diferencial diário + arquivamento contínuo de WAL para bucket S3-compatível **em região BR**; RPO ≤ 5 min, RTO ≤ 4 h.
- `pg_dump` lógico semanal (defesa contra corrupção física replicada) + **teste de restore mensal automatizado** em VM efêmera (backup não testado não é backup).
- Se Postgres gerenciado (Azure Flexible Server): PITR nativo 7–35 dias resolve, manter ainda o dump lógico semanal externo.

### 8.4 Observabilidade
- **Logs**: Serilog JSON no stdout, com `tenant_id`, `device_id`, `trace_id` como propriedades; **nunca logar `window_title`** (dado pessoal) — destino: **Seq** (container no Compose, excelente DX para time .NET) ou Loki.
- **Traces/métricas**: OpenTelemetry SDK ⇒ OTel Collector ⇒ Prometheus/Grafana (ou Grafana Cloud free tier). Métricas de negócio obrigatórias: `ingest_events_total`, `ingest_rejected_total{reason}`, `ingest_duplicates_total`, `processing_lag_seconds` (max `now − dirty_from`), `devices_online` (last_seen < 5 min), `portal_request_duration_seconds`.
- **Erros**: Sentry (SDK .NET; plano free cobre MVP) com scrubbing de payload.
- `/healthz` (liveness) e `/readyz` (Postgres + idade do último job) + uptime externo (UptimeRobot/Better Stack).

### 8.5 CI/CD
- GitHub Actions: `dotnet build + test` (inclui suíte cross-tenant com Testcontainers-Postgres) ⇒ `docker buildx` ⇒ push GHCR ⇒ deploy SSH (`docker compose pull && up -d`) em **staging** (auto, branch main) e **prod** (aprovação manual/tag).
- Migrations EF como **migration bundle** executado como step de deploy antes do swap dos containers; migrations sempre retrocompatíveis (expand/contract) para deploy sem downtime.

---

## 9. Segurança e LGPD

- **TLS 1.2+** em tudo (Caddy, HSTS, redirect 80⇒443); agente faz **certificate pinning leve** (pin da CA, não do leaf) opcional pós-MVP.
- **Segredos**: nunca no repositório; `.env` na VM com permissão 600 + sops/age para versionar cifrado, ou Azure Key Vault se Azure. Chave JWT, connection string, SMTP, DSN Sentry. Rotação documentada.
- **Anti-IDOR (tripla camada — seção 2.2)**: `ITenantContext` + EF global query filter + RLS; UUIDv7 não enumeráveis; recurso alheio ⇒ 404; **teste automatizado cross-tenant é gate de merge**.
- **Criptografia em repouso**: disco cifrado (LUKS/managed disk); `mfa_secret_enc` cifrado em nível de aplicação (AES-GCM, chave no secret store). Hash (nunca claro) para: senhas (Argon2id), device tokens, refresh tokens, enrollment keys, invitation tokens.
- **Auditoria de acesso a dados (LGPD art. 37/46)**: middleware registra em `audit_log` toda leitura de dado de monitoramento (ação, alvo, período consultado, IP) e toda exportação (com contagem de linhas). Append-only, sem UPDATE/DELETE pela role da aplicação. Exposta ao Owner via `GET /audit-logs`.
- **Minimização e papéis LGPD**: o cliente é **controlador**, o produto é **operador** ⇒ DPA no contrato. `window_title` pode conter dado pessoal (assunto de e-mail, nome de paciente): toggle por org (`collect_window_titles`), lista de regex de redação aplicada **no agente** (`title_redact_patterns` na config), retenção raw configurável ≤ 90 dias. Transparência: o ack carrega a config que o agente usa para exibir o aviso ao funcionário.
- **Hardening adicional**: rate limit global por IP no Caddy; headers de segurança (CSP do portal é do front, mas API envia `X-Content-Type-Options`, etc.); dependabot/`dotnet list package --vulnerable` no CI; usuário não-root nos containers; Postgres não exposto publicamente (rede interna do Compose); SSH só com chave + fail2ban.
- **Eliminação de dados**: oboarding do tenant cancelado ⇒ job de purge (30 dias de carência) que deleta por `tenant_id` em todas as tabelas + registro do purge. Direitos do titular (acesso/eliminação por funcionário) são atendidos via cliente-controlador: endpoint interno de purge por `device_user_id` (pós-MVP exposto no portal).

---

## Apêndice: Decisões-chave recomendadas

- PostgreSQL 16 único com particionamento nativo por tempo no MVP; TimescaleDB como primeiro degrau de evolução (mesma base, migração trivial) e ClickHouse só acima de ~50M eventos/dia — um único sistema operável por time pequeno cobre os ~15-20M eventos/dia do teto de escala.
- Multi-tenancy pool model: tenant_id em toda tabela + Row-Level Security do Postgres (SET LOCAL app.tenant_id por transação, role sem BYPASSRLS) + ITenantContext/global query filter na aplicação — schema-por-tenant multiplicaria objetos e migrations sem ganho real no MVP.
- Hospedagem em região brasileira (Azure Brazil South ou AWS sa-east-1; Magalu/Oracle Vinhedo como opção barata) — Hetzner/Contabo descartados porque dado de monitoramento de funcionário torna residência nacional exigência comercial dos clientes B2B, mesmo a LGPD permitindo transferência com salvaguardas.
- Idempotência de ingestão por PK (device_id, event_id, occurred_at) com INSERT ON CONFLICT DO NOTHING e occurred_at imutável do agente — dedup determinístico em 1 round-trip, sem Redis nem tabela auxiliar.
- Canal de configuração e comandos por pull no ack do POST /ingest/batch (batch vazio = heartbeat a cada 60s) — elimina necessidade de WebSocket/conexão persistente no MVP.
- Device token opaco de 256 bits com hash no banco (não JWT) — revogação instantânea de máquina comprometida; rotação automática a cada 90 dias com carência de 24h.
- Pipeline de intervalização por reconstrução idempotente: cursor dirty_from por device + delete-and-rebuild da janela afetada — resolve eventos atrasados/fora de ordem de máquinas offline sem lógica incremental frágil.
- Worker Service .NET em container separado desde o dia 1 (mesmo repo/solution), com Quartz.NET + pg_advisory_lock — picos de reprocessamento de backlog não competem com a API de ingest.
- Dashboards leem exclusivamente de daily_summaries pré-calculadas (reagregação dirigida por dirty_days) — é isso que garante p95 < 500ms, não hardware.
- Retenção em camadas: raw_events 90 dias (partição diária, DROP barato), activity_intervals 13 meses (partição mensal), daily_summaries 25 meses — equilibra custo de storage (~1,5TB no teto) com valor analítico.
- Manager = Viewer no MVP (papel existe no enum, escopo por time fica pós-MVP), com hook de filtro já previsto no ITenantContext para não refatorar repositórios depois.
- Argon2id para senhas, JWT de acesso 15 min + refresh token rotativo de 30 dias com detecção de reuso por família, MFA TOTP opcional.

## Apêndice: Riscos

- window_title é o campo mais sensível do produto (pode conter assunto de e-mail, nome de cliente/paciente, dados de saúde) — sem toggle por tenant, redação por regex no agente e exclusão total de logs, um vazamento vira incidente LGPD grave; tratar como dado pessoal desde o primeiro commit.
- RLS tem armadilhas clássicas: o owner das tabelas ignora políticas por padrão (usar FORCE ROW LEVEL SECURITY e role de aplicação separada sem BYPASSRLS), e SET (em vez de SET LOCAL) vaza o GUC app.tenant_id entre requisições no pool de conexões do Npgsql — exige interceptor testado.
- Rajada de catch-up: centenas de máquinas religando segunda 8h despejam dias de backlog simultaneamente; sem burst allowance no rate limit + worker separado + janela de reprocessamento eficiente, o pipeline trava exatamente no horário de maior visibilidade para o cliente.
- Relógio de máquina cliente é não confiável (skew de minutos a horas, fuso errado, BIOS resetada) — sem a correção por clock_offset e rejeição de timestamps futuros, timelines saem visualmente quebradas e minam a confiança no produto.
- Unique constraint em tabela particionada precisa incluir a coluna de partição (occurred_at na PK de raw_events) — se o agente alterar occurred_at entre retries (ex.: por re-cálculo de skew local), a deduplicação quebra silenciosamente; occurred_at deve ser imutável no agente, gravado uma única vez no disco local antes do primeiro envio.
- Mudança de categoria pelo tenant exige reagregação retroativa de daily_summaries — decidir e documentar o horizonte (sugerido 30 dias) antes de codar, senão relatórios históricos ficam inconsistentes com a configuração atual e geram tickets de suporte.
- Crescimento de storage é o primeiro limite físico (~6-8 GB/dia de raw no teto): monitorar desde o início e ter o plano TimescaleDB+compressão pronto antes de atingir ~1TB, pois migrar sob pressão de disco cheio é o pior cenário.
- Múltiplos usuários Windows por máquina e Terminal Server/RDS (vários usuários simultâneos em sessões distintas) quebram o modelo 1 device ≈ 1 pessoa — a máquina de estados precisa ser por (device, windows_sid, session_id) desde o início ou RDS fica inviável.
- Export CSV síncrono ou sem limite de linhas derruba a API por memória — manter assíncrono no worker com teto de 500k linhas e streaming para arquivo.
- Se usar Hangfire, o dashboard não pode ficar exposto publicamente (vazamento de dados de jobs cross-tenant); se usar Quartz, garantir lock distribuído antes de escalar para 2 workers.
- Eventos aceitos com ack devem estar commitados (ack pós-commit) — ack otimista antes do commit cria perda silenciosa de dados que só aparece como 'buraco na timeline' semanas depois, quase impossível de depurar.
- Auto-inserção de processos desconhecidos no app_catalog global a partir de telemetria de tenants pode poluir o catálogo (executáveis internos com nomes reveladores) — manter flag curated=false e nunca propagar window_title para o catálogo.

## Apêndice: Perguntas abertas (dependem do dono do produto)

- A residência de dados no Brasil será compromisso contratual/comercial (afeta escolha definitiva de cloud e de bucket de backup) ou apenas preferência técnica?
- Coleta de window_title vem habilitada por padrão (mais valor no dashboard) ou desabilitada por padrão (postura de minimização LGPD mais defensável)? Qual o posicionamento jurídico/comercial?
- O produto precisa suportar Terminal Server/RDS e máquinas compartilhadas por turnos no MVP, ou o alvo inicial é estritamente 1 estação ≈ 1 funcionário?
- Existe demanda já mapeada de clientes por SSO corporativo (Entra ID/Azure AD) no portal? Isso muda a prioridade do módulo de auth (OIDC desde o MVP vs e-mail+senha).
- Criação de organizações será self-service com trial (exige billing, verificação de e-mail, anti-abuso) ou somente via time comercial/backoffice no MVP?
- O papel Manager com escopo por time fica confirmado como pós-MVP, ou algum cliente âncora exige visibilidade segmentada por equipe já no lançamento?
- Retenção (raw 90d / intervals 13m / agregados 25m) será uniforme ou diferenciada por plano comercial? Isso afeta o modelo de billing e o job de purge.
- Como o cliente-controlador atenderá pedidos de titulares (funcionários) de acesso/eliminação de dados — o portal precisa de uma tela de DSR no MVP ou um processo manual via suporte basta no início?
- Haverá demanda de deploy on-premises/private cloud por clientes grandes (bancos, saúde)? Se sim, isso reforça Docker Compose/banco único como artefato de produto, não só conveniência de MVP.
- Qual o teto de preço aceitável de infraestrutura mensal no lançamento? Define a escolha entre Postgres gerenciado (Azure Flexible Server, mais caro e mais seguro operacionalmente) e self-host na VM.