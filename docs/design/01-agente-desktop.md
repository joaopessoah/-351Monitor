# Especificação do Agente de Monitoramento Windows

## 1. Visão geral e arquitetura do agente

### 1.1 Decisão arquitetural: híbrido (serviço + componente de sessão)

O agente é composto por **dois processos** mais um pacote de instalação:

| Componente | Processo | Contexto de execução | Responsabilidade |
|---|---|---|---|
| **Agent Service** | `MonitorAgentService.exe` | Serviço Windows, `LocalSystem`, Session 0 | Orquestração, fila SQLite, envio ao backend, política/config, auto-update, watchdog, eventos de sessão/energia (WTS/Power), lançamento do helper por sessão |
| **Session Helper** | `MonitorAgentSession.exe` | Processo na sessão interativa do usuário, rodando **com o token do próprio usuário** (baixo privilégio) | Janela ativa, título, ociosidade (`GetLastInputInfo`), snapshot de apps visíveis, ícone de bandeja (transparência) |
| **Instalador** | `MonitorAgent.msi` | per-machine, elevado | Deploy via GPO/Intune/RMM |

**Por que híbrido é obrigatório:** desde o Windows Vista existe *Session 0 Isolation* — serviços rodam na sessão 0, isolada de qualquer sessão interativa. APIs como `GetForegroundWindow`, `GetWindowText` e `GetLastInputInfo` operam sobre a *window station/desktop da sessão chamadora*; chamadas a partir do serviço retornam dados da sessão 0 (vazia), nunca a janela ativa do usuário logado. Portanto a captura de janela ativa e ociosidade **tem** que rodar dentro da sessão do usuário. O serviço, por sua vez, é necessário porque: (a) sobrevive a logoff e captura uptime/boot; (b) roda antes de qualquer logon; (c) só ele pode lançar processos em sessões de usuário (`CreateProcessAsUser`); (d) protege a fila e o token do dispositivo de adulteração pelo usuário; (e) parada exige privilégio de administrador.

**Por que `LocalSystem` (e não conta de serviço dedicada):** o serviço precisa chamar `WTSQueryUserToken` para obter o token do usuário logado e lançar o helper via `CreateProcessAsUser`. `WTSQueryUserToken` exige o privilégio `SE_TCB_NAME`, que apenas `LocalSystem` possui por padrão. Mitigação da superfície de ataque: o serviço **não escuta portas** (apenas cliente HTTPS de saída), não interpreta dados externos além de JSON de política assinada pelo backend, e toda a coleta sensível ocorre no helper com privilégio do usuário. `LocalService`/`NetworkService` ou MSA são inviáveis sem conceder TCB manualmente (frágil em frota).

### 1.2 Lançamento e ciclo de vida do Session Helper

1. Serviço registra `SERVICE_ACCEPT_SESSIONCHANGE` e recebe `SERVICE_CONTROL_SESSIONCHANGE` (`WTS_SESSION_LOGON`, `WTS_SESSION_LOGOFF`, `WTS_SESSION_LOCK`, `WTS_SESSION_UNLOCK`, `WTS_REMOTE_CONNECT`, `WTS_REMOTE_DISCONNECT`, `WTS_CONSOLE_CONNECT/DISCONNECT`) com o `sessionId`.
2. No boot, enumera sessões existentes com `WTSEnumerateSessions` (estado `WTSActive`).
3. Para cada sessão interativa ativa: `WTSQueryUserToken(sessionId)` → `DuplicateTokenEx` → `CreateEnvironmentBlock` → `CreateProcessAsUser` apontando para `MonitorAgentSession.exe --session {id}`.
4. **Um helper por sessão** (suporta multiusuário/Fast User Switching e RDP nativamente — ver §9).
5. IPC: named pipe `\\.\pipe\monitoragent.{sessionId}`, criado pelo serviço com DACL permitindo `GENERIC_WRITE` apenas ao SID do usuário daquela sessão + SYSTEM. Protocolo: mensagens JSON delimitadas por linha (eventos do helper → serviço; política/comandos serviço → helper). O helper **não** tem acesso à fila SQLite nem ao token do dispositivo.
6. **Watchdog:** serviço monitora o handle do processo helper (`WaitForSingleObject`); se morrer, relança após 5 s, máx. 5 relançamentos em janela de 10 min; ao exceder, gera evento `AGENT_TAMPER {reason:"helper_killed_repeatedly"}` e tenta novamente a cada 15 min.

### 1.3 Ícone de bandeja (transparência — requisito de produto)

- `NotifyIcon` sempre visível no Session Helper, com tooltip "Monitoramento corporativo ativo — {NomeDaEmpresa}".
- Menu de contexto: **"O que está sendo coletado agora"** (janela mostrando em tempo real: app ativo, título capturado — ou mascarado —, estado idle/ativo, último envio ao servidor), **"Política de monitoramento"** (texto/URL configurado pelo tenant), **"Status da conexão"**, **"Sobre"** (versão do agente, ID do dispositivo). 
- **Sem opção "Sair"** no menu. Fechar/matar o helper via Task Manager é possível (roda como o usuário), mas o watchdog o relança e o evento de tamper é registrado — transparência sem truques de ocultação: nomes de processo claros, ícone visível, entrada normal em "Aplicativos instalados".

## 2. Coleta: o quê e como (APIs concretas)

| Dado | API / Mecanismo | Onde roda | Frequência |
|---|---|---|---|
| Janela ativa | `GetForegroundWindow` → `GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `QueryFullProcessImageNameW`; título via `GetWindowTextW` (seguro: para janelas de outro processo lê o cache do USER, não envia `WM_GETTEXT` cross-process, logo não trava com app congelado) | Helper | Polling **5 s** com dedupe (§3) |
| Apps UWP (janela pertence a `ApplicationFrameHost.exe`) | `EnumChildWindows` na janela frame, achar child com PID ≠ frame; AUMID via `GetApplicationUserModelId` no processo real | Helper | No mesmo polling |
| Apps com janela visível (contexto, não foco) | `EnumWindows` + filtro: `IsWindowVisible`, sem owner (`GetWindow(GW_OWNER)==NULL`), título não vazio, exclui *tool windows* (`WS_EX_TOOLWINDOW`) e janelas *cloaked* (`DwmGetWindowAttribute(DWMWA_CLOAKED)`) | Helper | Snapshot a cada **300 s** |
| Ociosidade | `GetLastInputInfo` (por sessão) comparado a `GetTickCount64` | Helper | Verificação a cada **5 s**, limiar **300 s** (§3) |
| Logon/Logoff/Lock/Unlock/conexão RDP | `SERVICE_CONTROL_SESSIONCHANGE` (WTS) no serviço — **não** usar EventLog de segurança (4800/4801 dependem de auditoria habilitada) nem SENS (legado, frágil) | Serviço | Orientado a evento |
| Suspensão/retomada | `SERVICE_CONTROL_POWEREVENT`: `PBT_APMSUSPEND`, `PBT_APMRESUMEAUTOMATIC`/`PBT_APMRESUMESUSPEND` | Serviço | Orientado a evento |
| Boot/uptime | `GetTickCount64` no start do serviço; shutdown via `SERVICE_CONTROL_SHUTDOWN`/`PRESHUTDOWN` | Serviço | No start/stop |
| Usuário ativo por sessão | `WTSQuerySessionInformation` (`WTSUserName`, `WTSDomainName`) + SID via token | Serviço | No logon |
| Inventário: hostname, SO, domínio, versão do agente | `GetComputerNameExW(ComputerNameDnsHostname)`, `RtlGetVersion`/`OSVERSIONINFOEX`, `NetGetJoinInformation`, versão embutida no build; nº de monitores via `GetSystemMetrics(SM_CMONITORS)` | Serviço | No enrollment e a cada `AGENT_START` |
| Mudança de relógio | Comparação contínua wall-clock (`GetSystemTimePreciseAsFileTime`) vs monotônico (`QueryPerformanceCounter`); desvio > 30 s → evento | Serviço | Contínuo |

**Evolução pós-MVP:** trocar polling por `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_NAMECHANGE)` (event-driven, latência menor e CPU menor), mantendo polling como fallback. No MVP, polling de 5 s é mais simples, robusto e suficiente para relatórios gerenciais.

**Proibições codificadas (não configuráveis):** nenhum hook de teclado (`WH_KEYBOARD*`), nenhuma captura de tela (GDI/DXGI), nenhuma leitura de clipboard, nenhuma leitura de conteúdo de documento/DOM/mensagem, nenhuma injeção de DLL em processos de terceiros.

## 3. Modelo de eventos e amostragem

### 3.1 Envelope comum (todo evento)

| Campo | Tipo | Descrição |
|---|---|---|
| `event_id` | UUID **v7** (string) | Gerado no cliente; ordenável por tempo; chave de idempotência |
| `seq` | int64 | Sequência monotônica por dispositivo (persistida no SQLite); detecta lacunas |
| `type` | string | Tipo do evento (tabela abaixo) |
| `occurred_at` | string ISO-8601 UTC | Relógio de parede no momento do evento |
| `tz_offset_min` | int | Offset local em minutos (ex.: -180) |
| `mono_ms` | int64 | `GetTickCount64` no momento do evento (imune a ajuste de relógio) |
| `session_id` | int? | Sessão Windows (null para eventos de máquina) |
| `user_sid` / `user_name` | string? | SID e `DOMINIO\usuario` (null para eventos de máquina) |
| `data` | objeto | Campos específicos do tipo |

### 3.2 Tipos de evento

| Tipo | `data` | Emissor |
|---|---|---|
| `AGENT_START` | `agent_version, os_version, os_build, hostname, boot_id (GUID novo por boot), uptime_ms, start_reason: boot\|install\|update\|crash_recovery\|service_restart, monitors, is_vm, join_type: ad\|aad\|workgroup` | Serviço |
| `AGENT_STOP` | `reason: shutdown\|service_stop\|update` | Serviço |
| `SESSION_START` | `logon_type: console\|rdp` | Serviço (WTS_SESSION_LOGON) |
| `SESSION_END` | — | Serviço (WTS_SESSION_LOGOFF) |
| `LOCK` / `UNLOCK` | — | Serviço (WTS lock/unlock) |
| `SESSION_ATTACH` / `SESSION_DETACH` | `via: console\|rdp` | Serviço (WTS remote/console connect/disconnect) |
| `ACTIVE_WINDOW_CHANGED` | `process_name ("chrome.exe"), exe_path, app_id (AUMID UWP, opcional), window_title (ou null se mascarado), title_masked: bool` | Helper |
| `IDLE_START` | `last_input_at` (UTC) | Helper |
| `IDLE_END` | `idle_duration_ms` | Helper |
| `HEARTBEAT` | `state: active\|idle\|locked, foreground_process, idle_ms, queue_depth` | Helper (sessão) e Serviço (máquina, sem usuário logado) |
| `APPS_SNAPSHOT` | `apps: [{process_name, window_count}]` (sem títulos para conter volume) | Helper |
| `SYSTEM_SUSPEND` / `SYSTEM_RESUME` | `RESUME: sleep_duration_ms` (estimado por wall-clock) | Serviço |
| `TIME_CHANGED` | `old_utc, new_utc, delta_ms, new_tz_offset_min` | Serviço |
| `EVENTS_DROPPED` | `count, oldest_dropped_at, reason: retention_cap\|rate_limit` | Serviço |
| `AGENT_TAMPER` | `reason: helper_killed\|helper_killed_repeatedly\|pipe_denied` | Serviço |
| `POLICY_APPLIED` | `policy_version` | Serviço |

### 3.3 Estratégia de amostragem — orientada a evento com heartbeat

| Parâmetro | Valor | Justificativa |
|---|---|---|
| Polling janela ativa | **5 s** | Granularidade suficiente para relatório gerencial; abaixo de 5 s o ganho é nulo e o volume explode; acima de 10 s perde trocas rápidas |
| Dedupe de janela ativa | Emitir só se `(process_name, window_title_normalizado)` mudou vs amostra anterior | Reduz ~95% do volume vs amostrar sempre |
| Coalescência anti-flapping | Mudança só de título (mesmo processo) em < **10 s** desde o último evento do mesmo processo → atualizar último evento local em vez de emitir novo (ex.: título do player de música/aba com contador) | Apps que mudam título por segundo gerariam 17k eventos/dia |
| Rate limit | Máx. **1 `ACTIVE_WINDOW_CHANGED`/s** e **600/h** por sessão; excedente coalescido + `EVENTS_DROPPED{reason:rate_limit}` | Proteção contra apps patológicos |
| Limiar de ociosidade | **300 s (5 min)** sem input, configurável por política (60–1800 s) | Padrão de mercado; menor que isso pune leitura/reunião; `IDLE_START.last_input_at` retroage ao último input real, então não há perda de precisão |
| Heartbeat | **60 s** | Prova de vida; permite ao backend fechar intervalos quando a máquina morre sem `AGENT_STOP` (queda de energia); tolerância servidor: marcar offline após 3 heartbeats perdidos (180 s) |
| Snapshot de apps visíveis | **300 s** | Contexto sem inflar volume |

**Semântica de duração:** o agente **não calcula duração de janela ativa**; o backend deriva intervalos da sequência ordenada (`ACTIVE_WINDOW_CHANGED` → próximo evento de mudança/`IDLE_START`/`LOCK`/`SESSION_END`/gap de heartbeat). Mantém o agente burro e idempotente.

**Volume estimado:** 1.500–3.500 eventos/dia/dispositivo ≈ 0,8–1,5 MB JSON bruto ≈ **100–200 KB/dia gzip**. 1.000 dispositivos ≈ 200 MB/dia ingestão.

## 4. Buffer local e resiliência offline

- **SQLite** em `C:\ProgramData\{Vendor}\MonitorAgent\queue.db`, `journal_mode=WAL`, `synchronous=NORMAL` (sobrevive a kill do processo; perda máxima de ~1 checkpoint em queda de energia — aceitável). ACL do diretório: SYSTEM + Administrators (Full), sem acesso a usuários.
- Tabela `events(seq INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT UNIQUE, type TEXT, payload TEXT, created_at_utc TEXT, sent INTEGER DEFAULT 0)`; tabela `kv` para device_id, política em cache, marca d'água de envio.
- **Envio em lote:** flush quando `>= 200 eventos pendentes` **ou** a cada **30 s** (o que vier primeiro); lote máx. **500 eventos** ou **512 KB** descomprimido.
- **Confirmação:** eventos marcados `sent=1` apenas após HTTP 200 com `accepted_count`; deleção física dos enviados a cada 10 min.
- **Retry:** backoff exponencial `5s → 10s → 30s → 1m → 5m → 10m (teto)` com **jitter ±20%**; em HTTP 429/503 respeitar `Retry-After`. Erros 4xx de validação (exceto 401/403): mover lote para tabela `dead_letter` (cap 5 MB) e prosseguir — um lote ruim não pode travar a fila. 401: reexecutar fluxo de renovação de token; se revogado, parar coleta e manter heartbeat de enrollment a cada 1 h.
- **Retenção offline:** cap de **30 dias OU 200.000 eventos OU 100 MB** (o que estourar primeiro); descarte FIFO dos mais antigos com emissão de `EVENTS_DROPPED{reason:retention_cap}` para o gap ser visível no portal.
- **Ordenação e idempotência:** envio sempre em ordem de `seq`; servidor deduplica por `event_id` (UPSERT/ignore) — reenvio de lote após timeout de resposta é seguro; `seq` permite ao backend detectar lacunas e sinalizar "dados incompletos" no relatório.

## 5. Comunicação com o backend

### 5.1 Transporte e autenticação

- **HTTPS obrigatório, TLS 1.2+** (`SslProtocols.Tls12 | Tls13`), validação de cadeia padrão; *certificate pinning* opcional por política (desligado por padrão — quebra com proxies corporativos de inspeção TLS; detectar e reportar proxy MITM no diagnóstico).
- **Enrollment:** `POST /api/v1/devices/enroll` com `{enrollment_key (chave da organização, distribuída via parâmetro MSI), hostname, machine_fingerprint, os_version, agent_version}` → resposta `{device_id (UUID), device_token, policy}`. `machine_fingerprint` = SHA-256 de (`MachineGuid` do registro `HKLM\SOFTWARE\Microsoft\Cryptography` + SID da máquina) — permite ao backend reconciliar reinstalações com o mesmo dispositivo.
- `device_token`: opaco, escopo único de ingestão, rotacionado a cada **30 dias** via `POST /api/v1/devices/{id}/token:rotate`; armazenado criptografado com **DPAPI escopo máquina** (`CRYPTPROTECT_LOCAL_MACHINE`) em `%ProgramData%`, ACL SYSTEM-only. Enviado em `Authorization: Bearer`.
- **Política/config:** `GET /api/v1/devices/{id}/policy` com `If-None-Match` (ETag) a cada **15 min**; cache local para operação offline; campos: limiar idle, máscara de títulos, lista de processos ignorados, URL da política de privacidade, canal de update, intervalos.
- **Compressão:** corpo do lote com `Content-Encoding: gzip` (títulos de janela comprimem ~85%).
- **Relógio:** cada evento carrega `occurred_at` (UTC local) + `tz_offset_min` + `mono_ms`; o request carrega `sent_at`; toda resposta do servidor inclui `server_time`; o agente calcula e persiste `clock_skew_ms` e o envia no envelope do lote — o backend corrige timestamps quando `|skew| > 120 s` e marca os eventos como `time_suspect`. Pares `mono_ms`/`boot_id` permitem reconstruir ordem real mesmo com relógio adulterado.

### 5.2 Payload de exemplo — `POST /api/v1/ingest/events` (gzip)

```json
{
  "schema_version": 1,
  "device_id": "8c9f2b1e-77aa-4f3e-9c1d-2b5e8a01f4c2",
  "agent_version": "1.4.2",
  "boot_id": "f0a1b2c3-d4e5-4f60-8a9b-0c1d2e3f4a5b",
  "sent_at": "2026-06-09T14:32:07.512Z",
  "clock_skew_ms": -340,
  "events": [
    {
      "event_id": "01976f2a-3b10-7cc4-a1e2-9d8f6b3c0a11",
      "seq": 48211,
      "type": "UNLOCK",
      "occurred_at": "2026-06-09T14:25:01.003Z",
      "tz_offset_min": -180,
      "mono_ms": 86400123,
      "session_id": 1,
      "user_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "user_name": "ACME\\maria.silva",
      "data": {}
    },
    {
      "event_id": "01976f2a-8e55-7d12-b3f4-1a2b3c4d5e6f",
      "seq": 48212,
      "type": "ACTIVE_WINDOW_CHANGED",
      "occurred_at": "2026-06-09T14:25:06.118Z",
      "tz_offset_min": -180,
      "mono_ms": 86405238,
      "session_id": 1,
      "user_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "user_name": "ACME\\maria.silva",
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
      "session_id": 1,
      "user_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "user_name": "ACME\\maria.silva",
      "data": { "last_input_at": "2026-06-09T14:26:40.000Z" }
    },
    {
      "event_id": "01976f2b-5a01-7f44-d7e8-9f0a1b2c3d4e",
      "seq": 48214,
      "type": "HEARTBEAT",
      "occurred_at": "2026-06-09T14:32:00.001Z",
      "tz_offset_min": -180,
      "mono_ms": 86819121,
      "session_id": 1,
      "user_sid": "S-1-5-21-3623811015-3361044348-30300820-1013",
      "user_name": "ACME\\maria.silva",
      "data": { "state": "idle", "foreground_process": "excel.exe", "idle_ms": 320001, "queue_depth": 4 }
    }
  ]
}
```

Resposta: `200 {"accepted_count": 4, "duplicate_count": 0, "server_time": "2026-06-09T14:32:07.852Z"}`.

## 6. Instalação, atualização e operação em frota

### 6.1 Instalador MSI

- **WiX Toolset v5**, MSI per-machine, assinado com **Authenticode (certificado OV/EV)** — crítico para reputação no SmartScreen/Defender e exigência de EDRs corporativos.
- Instalação silenciosa: `msiexec /i MonitorAgent.msi /qn ENROLLMENT_KEY=ORG-7F3A-... SERVER_URL=https://api.produto.com.br [PROXY_URL=...] [NOENROLL=1]` — compatível com GPO (Software Installation), Intune (Win32/LOB), e RMMs (N-able, Datto, etc.).
- `NOENROLL=1` para preparação de imagem dourada/sysprep: instala sem efetuar enrollment; o serviço se registra no primeiro boot real (evita clonagem de identidade — ver §9).
- Diretórios: binários em `%ProgramFiles%\{Vendor}\MonitorAgent\`; dados/fila/logs/config em `%ProgramData%\{Vendor}\MonitorAgent\`.
- Serviço: start **Automatic (Delayed Start)**; recovery nativo do SCM (`sc failure`): restart em 10 s / 60 s / 300 s, reset após 1 dia.
- Desinstalação: exige admin (MSI per-machine); opcional por política: `UNINSTALL_PASSWORD` validado por custom action (camada extra, não segurança forte). Desinstalar gera tentativa de flush final da fila + `AGENT_STOP{reason:"uninstall"}`.

### 6.2 Auto-update

- Verificação a cada **6 h** (com jitter de até 30 min) em `GET /api/v1/agent/update-manifest?channel={stable|beta}&current=1.4.2` → `{version, url, sha256, signature, min_version_forced}`.
- Download em background; verificação de **SHA-256 do manifesto + assinatura Authenticode do MSI** antes de executar; instalação via `msiexec /i /qn` (major upgrade do MSI preserva `%ProgramData%`, incluindo fila e identidade).
- **Anéis de implantação controlados pelo backend:** canary (frota interna) → 5% por tenant → 100%; rollback = publicar versão anterior no manifesto (MSI suporta downgrade via `MSIINSTALLPERUSER`... usar major-upgrade com `AllowDowngrades` controlado).
- Janela de update: por política do tenant (padrão: qualquer horário; update troca o serviço em < 10 s e o helper reconecta).

### 6.3 Proteção contra parada acidental (sem ocultação)

- Parar/desinstalar o serviço exige privilégio de administrador (comportamento padrão do SCM — suficiente; **não** alterar DACL do serviço para bloquear admins no MVP).
- Usuário comum pode matar o Session Helper (roda como ele) → watchdog relança + `AGENT_TAMPER`. Gap de coleta fica visível no portal ("sem dados entre X e Y").
- Nenhuma técnica de rootkit/ocultação de processo: nomes claros, descrição do serviço explica a finalidade e aponta a URL da política de privacidade do empregador.

### 6.4 Logs de diagnóstico

- Serilog para arquivo: `%ProgramData%\{Vendor}\MonitorAgent\logs\service-.log` e `session-{sid}-.log`, rotação diária, **5 MB/arquivo, máx. 10 arquivos** (50 MB teto), nível `Information` (configurável `Debug` via política para suporte).
- **Logs nunca contêm títulos de janela nem nomes de usuário em nível Information** (só em Debug, com aviso na política).
- `MonitorAgentSession.exe --diag` gera pacote ZIP de suporte (logs + config sanitizada + contadores) para anexo em chamado.

## 7. Stack recomendada

- **.NET 8 LTS** (planejar retarget para **.NET 10 LTS** antes de nov/2026, fim do suporte do 8 — mudança trivial): casa com a equipe, P/Invoke maduro via `CsWin32` (source generators para Win32), `Microsoft.Extensions.Hosting.WindowsServices` para o Worker Service.
- **Serviço:** Worker Service (`UseWindowsService()`), + P/Invoke para `HandlerEx`/sessão (ou `ServiceBase.OnSessionChange` com `CanHandleSessionChangeEvent=true`).
- **Helper/bandeja:** processo WinForms mínimo (apenas `NotifyIcon` + uma janela de status) — WinForms, não WPF (footprint menor, sem necessidade de UI rica).
- **Publicação: self-contained, single-file, win-x64 (+ win-arm64)** — frota corporativa não pode depender de runtime .NET pré-instalado nem de updates de runtime fora do controle do agente. Sem trimming (incompatível com WinForms); `PublishReadyToRun=true` para startup. Tamanho esperado: ~40 MB (serviço) + ~65 MB (helper) em disco — aceitável; se tamanho virar problema, reescrever o helper com `NotifyIcon` Win32 puro + NativeAOT (~8 MB).
- **Metas de consumo (gate de release, medido em VM 2 vCPU/4 GB):** CPU média **< 0,5%** e pico < 5% por 1 s no polling; RAM working set: serviço **< 60 MB**, helper **< 50 MB**; disco total (binários+fila+logs) **< 400 MB**; rede **< 5 MB/dia/dispositivo**.
- SQLite via `Microsoft.Data.Sqlite`; HTTP via `HttpClient` + Polly (retry/backoff); JSON via `System.Text.Json` (source-generated).
- Alternativas: **Rust** — footprint mínimo e sem runtime, mas custo de rampa alto para equipe .NET e ecossistema Windows-service menos pronto; **Go** — deploy simples, mas interop Win32/sessões é desajeitada e GC/threads não trazem ganho aqui; **C++/Win32** — controle e footprint máximos, porém produtividade baixa e risco de memory-safety num produto que precisa iterar rápido.

## 8. Privacidade no cliente (LGPD by design)

- **Lista de processos ignorados** (política do tenant, com default de fábrica): `keepass.exe`, `1password.exe`, `bitwarden.exe`, `logonui.exe`, `lockapp.exe`, `consent.exe` + processos do próprio agente. Para processo ignorado: registra `ACTIVE_WINDOW_CHANGED` com `process_name="(privado)"`, sem título — o tempo conta, o conteúdo não.
- **Mascaramento de títulos por política do tenant:** `title_policy = full | masked | denylist_regex`. `masked` envia só `process_name` (`title_masked:true`); `denylist_regex` aplica regexes locais (ex.: `.*[Bb]ank.*`, números com formato de CPF/cartão) substituindo o título por `"(mascarado)"` **antes** de persistir na fila — dado sensível nunca toca o disco nem a rede.
- **Garantias absolutas (hard-coded, não configuráveis):** sem keylogging, sem screenshot, sem clipboard, sem conteúdo de arquivos/mensagens/URLs além do título da janela, sem áudio/vídeo/webcam, sem geolocalização.
- **Transparência ativa:** ícone permanente; clique → painel "O que está sendo coletado agora" exibindo exatamente os campos que sairão no próximo lote + horário do último envio + versão da política aplicada; primeiro logon após instalação exibe notificação (toast) "Este computador é monitorado pela {Empresa}. Clique para detalhes." (texto customizável pelo tenant — base para o aviso exigido pela LGPD/legitimação trabalhista).
- Fila local com ACL restrita (SYSTEM/Administrators); avaliação pós-MVP: cifrar payloads na fila com AES-GCM + chave DPAPI-máquina.

## 9. Casos extremos

| Caso | Comportamento especificado |
|---|---|
| **Fast User Switching / múltiplos usuários** | Um helper por sessão (§1.2); todo evento carrega `session_id` + `user_sid`; sessão desconectada (mas não encerrada) gera `SESSION_DETACH` e o helper pausa coleta de janela (a sessão não tem desktop visível) mantendo heartbeat `state:"detached"` |
| **RDP / Terminal Server (RDS)** | A arquitetura suporta N sessões nativamente. **MVP: suporte oficial a SKUs workstation (Windows 10 1809+/11) com até 2 sessões simultâneas**; em SKU Server/RDS o agente roda best-effort, marca o device `os_type:"server"` e o portal exibe "suporte experimental" — promover a suportado pós-MVP com teste de carga (30+ sessões, custo de N helpers) |
| **Máquinas virtuais / VDI** | Detecção via CPUID hypervisor-bit + `Win32_ComputerSystem.Model` → `is_vm:true` no `AGENT_START`. VDI **não-persistente** (clones do mesmo golden image) duplicaria identidade → mitigação: `NOENROLL=1` na imagem + fingerprint inclui `MachineGuid` (regenerado por sysprep); documentar runbook de imagem dourada |
| **Hibernação/suspensão** | `SYSTEM_SUSPEND` fecha intervalos abertos no backend; `SYSTEM_RESUME{sleep_duration_ms}` reabre; tampa fechada sem suspend (raro) coberto pelo gap de heartbeat (>180 s ⇒ backend fecha intervalo no último heartbeat) |
| **Mudança de horário/fuso/DST** | Tudo em UTC + `tz_offset_min` por evento (não por dispositivo — usuário pode viajar); salto wall-clock vs `QueryPerformanceCounter` > 30 s ⇒ `TIME_CHANGED`; `mono_ms`+`boot_id` preservam ordenação real; DST é só mudança de `tz_offset_min` |
| **Múltiplos monitores** | Foreground é único no Windows independentemente de monitores — sem mudança na coleta; `monitors` reportado no `AGENT_START` como inventário; "app visível no 2º monitor sem foco" aparece via `APPS_SNAPSHOT`, não como tempo ativo |
| **Sem usuário logado** | Serviço emite `HEARTBEAT` de máquina (sem `session_id`) — viabiliza relatório "máquina ligada vs em uso" |
| **Queda de energia / kill -9** | WAL preserva fila; próximo `AGENT_START{start_reason:"crash_recovery"}` (flag de shutdown limpo em `kv` não setada); backend fecha intervalos órfãos no último heartbeat |
| **Proxy corporativo** | Suporte a proxy de sistema (WinHTTP) + `PROXY_URL` explícito no MSI; falha de TLS por inspeção MITM gera diagnóstico claro no log e estado "erro de certificado" no tray |

## 10. Telas/superfícies do agente (resumo)

1. **Tray icon** (sempre visível) — tooltip + menu.
2. **Janela "Transparência"** — coleta em tempo real, política aplicada, último envio, device_id.
3. **Toast de primeiro logon** — aviso de monitoramento.
4. **`--diag`** — pacote de suporte (CLI, sem UI).

Não existe UI de configuração local: toda configuração vem do backend por política do tenant (fonte única de verdade, auditável).

---

## Apêndice: Decisões-chave recomendadas

- Arquitetura híbrida obrigatória: serviço Windows (LocalSystem) para fila/envio/watchdog + um processo helper por sessão de usuário para janela ativa e ociosidade — Session 0 Isolation impede serviço de enxergar a janela ativa, e WTSQueryUserToken (que exige SE_TCB_NAME, só LocalSystem) é o caminho para lançar o helper com o token do usuário
- Coleta orientada a eventos com dedupe (polling de janela ativa a 5 s emitindo só mudanças, anti-flapping de 10 s, rate limit 600/h) + heartbeat de 60 s + limiar de idle de 5 min — equilibra precisão gerencial, volume (~150 KB/dia gzip por máquina) e consumo
- Agente burro: não calcula durações; backend deriva intervalos da sequência de eventos ordenada por seq/mono_ms — simplifica idempotência, reprocessamento e correção de relógio
- Fila local SQLite WAL com event_id UUIDv7 + seq monotônico, lotes de até 500 eventos/30 s, backoff exponencial com jitter, retenção offline de 30 dias/100 MB com descarte FIFO sinalizado por EVENTS_DROPPED — gaps sempre visíveis, nunca silenciosos
- Identidade: enrollment com chave da organização via parâmetro do MSI → device token rotacionado (30 dias) protegido por DPAPI-máquina; machine_fingerprint para reconciliar reinstalações; NOENROLL=1 para golden image de VDI
- Stack .NET 8 LTS (retarget para .NET 10 antes de nov/2026), Worker Service + helper WinForms mínimo, publicação self-contained single-file sem dependência de runtime na frota; MSI WiX assinado Authenticode para GPO/Intune/RMM
- Transparência como recurso de produto, não obstáculo: ícone permanente, painel 'o que está sendo coletado agora', toast de primeiro logon, nomes de processo claros, zero técnicas de ocultação; proteção contra parada = privilégio admin padrão do SCM + watchdog + evento AGENT_TAMPER
- Privacidade hard-coded no binário (sem keylog/screenshot/clipboard/conteúdo) + política por tenant para mascaramento de títulos e lista de processos ignorados, aplicada ANTES de persistir em disco
- MVP suporta oficialmente SKUs workstation (Win 10 1809+/11); a arquitetura por-sessão já é multi-sessão, então RDS roda em modo 'experimental' detectado e sinalizado, com promoção a suportado pós-MVP

## Apêndice: Riscos

- LGPD/trabalhista: títulos de janela podem conter dados pessoais sensíveis do funcionário (saúde, banco, conversas) mesmo sem keylogging — o produto precisa de mascaramento por regex/política funcionando ANTES da persistência local e de orientação jurídica no onboarding do tenant; é o maior risco de produto, não técnico
- Falsos positivos de antivírus/EDR: software que enumera janelas, lança processos em sessões de usuário e roda como LocalSystem tem assinatura comportamental de spyware — exige Authenticode (idealmente EV), submissão prévia ao Microsoft Defender e programa de whitelisting junto aos principais EDRs antes do go-to-market
- Usuário comum pode matar o Session Helper (roda com o token dele) — mitigado por watchdog + AGENT_TAMPER + gap visível no portal, mas o produto deve comunicar que tamper-proofing absoluto contra admin local é impossível e indesejável (transparência)
- Atribuição de apps UWP/ApplicationFrameHost e apps Electron com títulos dinâmicos pode gerar dados errados ou volume explosivo — anti-flapping e rate limit precisam de testes com Spotify, Teams, navegadores com contadores de notificação no título
- Auto-update é vetor de supply chain: comprometer o canal de update = execução como SYSTEM em toda a frota; verificação de SHA-256 + Authenticode no cliente e anéis de implantação são obrigatórios desde a v1, não 'depois'
- VDI não-persistente e clonagem de imagem sem NOENROLL=1/sysprep gera identidades duplicadas e dados misturados entre máquinas — precisa de runbook documentado e detecção server-side de fingerprint duplicado
- Clock skew e adulteração de relógio pelo usuário corrompem timelines; o esquema mono_ms+boot_id+server_time mitiga, mas o backend precisa implementar a correção desde o MVP ou relatórios sairão errados em máquinas com relógio ruim
- Proxies corporativos com inspeção TLS quebram a conexão do agente silenciosamente — sem diagnóstico claro no tray/log, isso vira o principal motivo de chamados de 'agente não reporta'
- Janela de polling de 5 s perde interações menores que o intervalo e GetForegroundWindow pode retornar NULL durante trocas — código deve tratar NULL/janelas zumbis sem crashar o loop de coleta
- .NET 8 sai de suporte em nov/2026 — se o MVP lançar nele, o retarget para .NET 10 precisa estar no roadmap dos primeiros 6 meses junto com o mecanismo de auto-update já validado

## Apêndice: Perguntas abertas (dependem do dono do produto)

- Qual o default de fábrica da política de títulos de janela: coleta completa (full) ou mascarada (masked)? Isso define o posicionamento de privacidade do produto e o risco LGPD assumido por padrão
- Suporte a Windows Server/RDS (terminal services) entra no escopo comercial do MVP ou fica como 'experimental'? Há clientes-alvo relevantes no Brasil usando RDS/Citrix que mudariam essa prioridade?
- Qual o SO mínimo suportado? A especificação assume Windows 10 1809+; existe demanda real da base-alvo por Windows 7/8.1 (comum em PMEs brasileiras) que justifique o custo de um build legado?
- O funcionário deve ver apenas 'o que está sendo coletado agora' no tray, ou também seu próprio histórico (autosserviço)? Impacta arquitetura de autenticação do portal e o discurso de transparência
- Tenants com funcionários que são admins locais das próprias máquinas: aceitamos que eles possam parar o serviço (com evento de tamper visível) ou há requisito comercial de tamper-resistance mais forte (DACL no serviço, senha de desinstalação obrigatória)?
- Coleta fora do horário contratual/em BYOD: o agente deve suportar janelas de coleta por política (ex.: pausar fora de 8h-18h) já no MVP? Tem implicação jurídica e de escopo relevante
- A rotação do device token a cada 30 dias e o cap de retenção offline de 30 dias/100 MB são aceitáveis para o perfil de cliente (ex.: máquinas de campo que ficam semanas offline)?
- Quem controla o anel/canal de update: somente o fornecedor, ou o tenant pode travar versão/janela de manutenção (requisito comum de TI corporativa)?