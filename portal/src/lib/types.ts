// =============================================================================
// Contratos de auth do portal (+351 Monitor) — alinhados aos endpoints REAIS do
// backend (M351.Api, Seções 7.4/7.5 do PROMPT-DESENVOLVIMENTO.md). JSON em
// snake_case; erros em RFC 9457 (problem+json) com extensão opcional `code`.
// =============================================================================

export type Role = "owner" | "admin" | "viewer";

/** Resposta de `POST /auth/login` e de `POST /auth/invite/accept`. */
export type LoginResponse =
  /** Credenciais ok e MFA não exigida/já cumprida — refresh cookie httpOnly setado pelo servidor. */
  | { status: "ok"; access_token: string; expires_in: number }
  /** MFA habilitada — informar TOTP em `POST /auth/mfa/verify` com o token temporário. */
  | { status: "mfa_required"; mfa_token: string }
  /** Owner/Admin sem TOTP provisionado — obrigatório completar `POST /auth/mfa/setup` + verify. */
  | { status: "mfa_setup_required"; mfa_token: string };

/** Body de `POST /auth/login`. */
export interface LoginRequest {
  email: string;
  password: string;
}

/**
 * Resposta de `POST /auth/mfa/setup` — SEM body; autenticado com
 * `Authorization: Bearer {mfa_token}` (token temporário do login/convite).
 */
export interface MfaSetupResponse {
  /** URI otpauth:// para apps autenticadores (QR). */
  otpauth_uri: string;
  /** Segredo TOTP em Base32, para digitação manual. */
  secret: string;
}

/** Body de `POST /auth/mfa/verify` (autenticado com `Authorization: Bearer {mfa_token}`). */
export interface MfaVerifyRequest {
  code: string;
}

/** Resposta de `POST /auth/mfa/verify` — refresh cookie httpOnly setado pelo servidor. */
export interface MfaVerifyResponse {
  status: "ok";
  access_token: string;
  expires_in: number;
}

/** Resposta de `POST /auth/mfa/recovery-codes` — 10 códigos exibidos UMA única vez. */
export interface RecoveryCodesResponse {
  codes: string[];
}

/** Resposta de `POST /auth/refresh` (autenticado pelo cookie httpOnly). */
export interface RefreshResponse {
  access_token: string;
  expires_in: number;
}

/** Resposta de `GET /me`. */
export interface MeResponse {
  user: {
    id: string;
    email: string;
    display_name: string;
    role: Role;
  };
  organization: {
    id: string;
    name: string;
    slug: string;
    timezone: string;
    /** Horário de trabalho configurado (jsonb cru) - null quando a org não definiu. */
    business_hours: BusinessHours | null;
    /** Plano comercial da organização (ex.: "trial"). */
    plan: string;
    /** Limite de dispositivos do plano - null sem limite definido. */
    device_limit: number | null;
    /**
     * Instante do dismiss do checklist de primeiros passos (POST
     * /organization/onboarding-checklist/dismiss) - null enquanto o card da
     * Visão Geral está visível; o DELETE na mesma rota reabre (volta a null).
     */
    onboarding_checklist_dismissed_at: string | null;
  };
}

/** Resposta de `GET /auth/invite/{token}` (público; 404 inexistente, 410 expirado/usado). */
export interface InvitationInfo {
  email: string;
  role: Role;
  organization_name: string;
  /** true para owner/admin — exige setup de TOTP após definir a senha. */
  mfa_required: boolean;
}

/** Body de `POST /auth/invite/accept`. */
export interface InvitationAcceptRequest {
  token: string;
  display_name: string;
  password: string;
}

/** Body de `POST /auth/forgot-password` (resposta sempre genérica, 202). */
export interface ForgotPasswordRequest {
  email: string;
}

/** Body de `POST /auth/reset-password` (token de 60 min; invalida todas as sessões). */
export interface ResetPasswordRequest {
  token: string;
  password: string;
}

/** Erro RFC 9457 (problem+json) retornado pela API do portal. */
export interface ApiProblem {
  type?: string;
  title?: string;
  detail?: string;
  status?: number;
  /**
   * Código de erro do backend, ex.: `account_locked` (lockout N22 — 401),
   * `invite_expired` (410), `weak_password` (400).
   */
  code?: string;
}

// =============================================================================
// Contratos da F2: presença (GET /dashboard/presence), timeline
// (GET /timeline/device) e devices (GET /devices) — JSON snake_case real da API.
// =============================================================================

/** Estado canônico de intervalo (Seção 7.3). */
export type IntervalState = "active" | "idle" | "locked" | "off_clean" | "no_data";

/** Estado de presença derivado (N6): inclui no_session (máquina ligada sem usuário). */
export type PresenceState = IntervalState | "no_session";

/** Item de `GET /dashboard/presence`. */
export interface PresenceItem {
  device_id: string;
  device_name: string;
  hostname: string;
  state: string;
  presence_state: PresenceState;
  windows_username: string | null;
  foreground_process: string | null;
  foreground_title: string | null;
  state_since: string | null;
  app_since: string | null;
  last_contact_at: string;
}

export interface PresenceResponse {
  items: PresenceItem[];
  server_time: string;
}

/** Intervalo de `GET /timeline/device`. */
export interface TimelineInterval {
  started_at: string;
  ended_at: string;
  state: IntervalState;
  app: { app_id: string; process_name: string; display_name: string; category: string | null } | null;
  window_title: string | null;
  data_incomplete: boolean;
}

export interface TimelineSummary {
  first_event_at: string | null;
  last_event_at: string | null;
  seconds_on: number;
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
}

export interface TimelineResponse {
  device_id: string;
  device_name: string;
  date: string;
  timezone: string;
  device_tz_offset_min: number | null;
  resolution_sec: number;
  data_incomplete: boolean;
  server_time: string;
  intervals: TimelineInterval[];
  summary: TimelineSummary;
}

/**
 * Lane de `GET /timeline/team` (F3.4): um device NÃO-archived do tenant —
 * inclusive devices sem intervalos no dia (lane vazia). Intervalos com o MESMO
 * shape do `GET /timeline/device`.
 */
export interface TeamTimelineLane {
  device_id: string;
  device_name: string;
  device_tz_offset_min: number | null;
  data_incomplete: boolean;
  intervals: TimelineInterval[];
}

/** Resposta de `GET /timeline/team?date=` — lanes ordenadas por nome de exibição asc. */
export interface TeamTimelineResponse {
  date: string;
  resolution_sec: number;
  server_time: string;
  /** true quando o cap de ~3000 intervalos (N21) parou de adicionar lanes INTEIRAS. */
  truncated: boolean;
  lanes: TeamTimelineLane[];
}

/** Item de `GET /devices`. */
export interface DeviceItem {
  id: string;
  hostname: string;
  display_name: string | null;
  os_type: string | null;
  os_version: string | null;
  agent_version: string | null;
  status: "active" | "paused" | "archived" | "revoked";
  /** A API emite null (não []) para device sem tags - a coluna text[] não tem default. */
  tags: string[] | null;
  last_seen_at: string | null;
  tz_offset_min: number | null;
  clock_offset_ms: number;
  /**
   * F4.4 — saúde do agente. notice_acked_at: instante do primeiro NOTICE_ACK
   * do device (granularidade por device; por usuário Windows é follow-up), null
   * enquanto pendente. last_tamper_at/last_tamper_reason: último AGENT_TAMPER
   * materializado na ingestão (raw_events expira em 90 dias; o portal só destaca
   * os últimos 7). reason ∈ helper_killed | helper_killed_repeatedly | pipe_denied.
   * agent_outdated: agent_version < min_version do release vigente do canal
   * 'stable' — comparação SEMVER no BACKEND (o portal apenas exibe o booleano).
   */
  notice_acked_at: string | null;
  last_tamper_at: string | null;
  last_tamper_reason: string | null;
  agent_outdated: boolean;
}

/**
 * Body de `PATCH /devices/{id}` (F3.7, admin/owner) - campos ausentes não
 * mudam. display_name null limpa o apelido (o device volta a exibir o
 * hostname). revoked é terminal: o backend responde 400 para qualquer PATCH
 * em device revogado (só o re-enroll revive). Resposta 200 com o DeviceItem
 * atualizado (mesmo shape do GET).
 */
export interface DevicePatchRequest {
  display_name?: string | null;
  tags?: string[];
  status?: "active" | "paused" | "archived";
}

export interface PagedResponse<T> {
  items: T[];
  total: number;
  page: number;
  page_size: number;
}

// =============================================================================
// Contratos da F3.2: dashboard histórico (GET /dashboard/summary,
// GET /dashboard/top-apps) e business_hours da organização em GET /me.
// =============================================================================

/** Horário de trabalho da organização, ex.: {"days":[1,2,3,4,5],"start":"08:00","end":"18:00"}. */
export interface BusinessHours {
  /** Dias da semana ISO (1 = segunda … 7 = domingo). */
  days: number[];
  /** Início "HH:mm" no fuso da organização. */
  start: string;
  /** Fim "HH:mm" no fuso da organização. */
  end: string;
}

/** Um dia agregado de `GET /dashboard/summary` - dias sem linhas NÃO aparecem. */
export interface DashboardSummaryDay {
  date: string;
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
  seconds_on: number;
  seconds_work_related: number;
  seconds_neutral: number;
  seconds_not_work_related: number;
  data_incomplete: boolean;
  device_count: number;
}

/** Totais do período: mesmos campos somados; device_count distinct do período. */
export interface DashboardSummaryTotals {
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
  seconds_on: number;
  seconds_work_related: number;
  seconds_neutral: number;
  seconds_not_work_related: number;
  data_incomplete: boolean;
  device_count: number;
}

export interface DashboardSummaryResponse {
  days: DashboardSummaryDay[];
  totals: DashboardSummaryTotals;
}

/** Categoria do tenant aplicada a um app (classification: 1, 0 ou -1). */
export interface TopAppCategory {
  id: string;
  name: string;
  classification: number;
  /** Cor hex opcional da categoria - null quando não definida (coluna nullable). */
  color: string | null;
}

/** Item de `GET /dashboard/top-apps` (ordenado por seconds_active desc). */
export interface TopAppItem {
  app_id: string;
  process_name: string;
  display_name: string;
  custom_display_name: string | null;
  category: TopAppCategory | null;
  seconds_active: number;
  device_count: number;
}

export interface TopAppsResponse {
  items: TopAppItem[];
  /** Soma de TODOS os apps do período - não apenas os do top. */
  total_seconds_active: number;
}

// =============================================================================
// Contratos da F3.3: categorias do tenant (CRUD /categories), catálogo de apps
// (GET /app-catalog, PUT /app-catalog/{appId}/category, GET
// /app-catalog/{appId}/titles) e relatório de uso (GET /reports/usage).
// Classificação SEMPRE no vocabulário fixo: 1 = Relacionado ao trabalho,
// 0 = Neutro, -1 = Não relacionado ao trabalho, sem mapeamento = Não
// categorizado (ver lib/classification.ts).
// =============================================================================

/** Item de `GET /categories` - ordenado por classification desc, name asc. */
export interface CategoryItem {
  id: string;
  name: string;
  /** 1, 0 ou -1 (vocabulário fixo acima). */
  classification: number;
  color: string | null;
  /** Quantos apps do tenant estão mapeados nesta categoria. */
  app_count: number;
}

export interface CategoriesResponse {
  items: CategoryItem[];
}

/** Body de `POST /categories` (201) - nome duplicado no tenant responde 409. */
export interface CategoryCreateRequest {
  name: string;
  classification: number;
  color?: string;
}

/** Body de `PATCH /categories/{id}` - mudar classification reagrega 30 dias. */
export interface CategoryUpdateRequest {
  name?: string;
  classification?: number;
  color?: string;
}

/**
 * Item de `GET /app-catalog?q=&uncategorized=true` - apps CONHECIDOS DO TENANT
 * (catálogo global, recorte do tenant), janela fixa dos últimos 30 dias no
 * fuso da organização, ordem seconds_active_30d desc, máximo 500 itens.
 */
export interface AppCatalogItem {
  app_id: string;
  process_name: string;
  display_name: string;
  custom_display_name: string | null;
  category: TopAppCategory | null;
  seconds_active_30d: number;
  device_count_30d: number;
}

export interface AppCatalogResponse {
  items: AppCatalogItem[];
  /** Total de apps do tenant sem categoria (independe de q/uncategorized). */
  uncategorized_count: number;
}

/**
 * Body de `PUT /app-catalog/{appId}/category` - category_id null desmapeia
 * (app volta a Não categorizado); reagrega os últimos 30 dias no backend.
 */
export interface AppCategoryPutRequest {
  category_id: string | null;
  custom_display_name?: string | null;
}

/** Item de `GET /app-catalog/{appId}/titles?from&to` (top 20 títulos por tempo ativo). */
export interface AppTitleItem {
  window_title: string;
  seconds_active: number;
}

export interface AppTitlesResponse {
  items: AppTitleItem[];
  /** Soma dos intervalos cujo título foi mascarado pela política de privacidade. */
  masked_seconds: number;
  total_seconds: number;
}

/** Envelope de `GET /reports/usage` (paginado; exclui devices arquivados). */
export interface UsageReportResponse<TItem> {
  items: TItem[];
  total: number;
  page: number;
  page_size: number;
  /** Tempo ativo total do período INTEIRO - todos os itens, não só a página. */
  total_seconds_active: number;
}

/** Item de `GET /reports/usage?group_by=app` (fonte daily_app_usage). */
export interface UsageAppItem {
  app_id: string;
  process_name: string;
  display_name: string;
  custom_display_name: string | null;
  category: TopAppCategory | null;
  seconds_active: number;
  device_count: number;
}

/** Item de `group_by=category` - campos null representam o Não categorizado. */
export interface UsageCategoryItem {
  category_id: string | null;
  name: string | null;
  classification: number | null;
  color: string | null;
  seconds_active: number;
  app_count: number;
}

/** Item de `group_by=device` (fonte daily_device_summaries). */
export interface UsageDeviceItem {
  device_id: string;
  device_name: string;
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
  seconds_on: number;
  seconds_work_related: number;
  seconds_neutral: number;
  seconds_not_work_related: number;
}

/** Item de `group_by=device_user` - device_user_id de UUID zero = "Máquina (sem usuário)". */
export interface UsageDeviceUserItem extends UsageDeviceItem {
  device_user_id: string;
  /** Usuário Windows quando resolvível via device_users. */
  windows_user: string | null;
  /**
   * Nome de exibição JÁ resolvido pelo backend (mesma regra das lanes da
   * timeline): display_name amigável de device_users, senão windows_username,
   * "Máquina (sem usuário)" para a lane de UUID zero e "Usuário desconhecido"
   * para titular removido por DSR. Renderize este campo - nunca reimplemente
   * a regra no cliente.
   */
  display_name: string;
}

// =============================================================================
// Contratos da F3.5: relatório de jornada (GET /reports/jornada) e exports CSV
// assíncronos (POST/GET /exports, GET /exports/{id}/download). O CSV é gerado
// pelo worker (UTF-8 com BOM, separador ';'); o disclaimer da Portaria 671/MTE
// vai em tela E como rodapé de todo CSV de jornada.
// =============================================================================

/**
 * Observação da linha de jornada - null quando o dia tem dados normais.
 * dados_incompletos: dia com data_incomplete; sem_comunicacao: seconds_on 0
 * com no_data registrado; sem_dados: seconds_on 0 sem nenhum registro.
 */
export type JornadaNote = "dados_incompletos" | "sem_comunicacao" | "sem_dados";

/**
 * Linha de `GET /reports/jornada` - um device × dia do range INTEIRO (dias sem
 * dados TAMBÉM viram linha, com observação). Colunas de tela SEMPRE "Primeiro
 * evento"/"Último evento" - jamais "Entrada"/"Saída" (não é ponto eletrônico).
 */
export interface JornadaRow {
  date: string;
  device_id: string;
  device_name: string;
  /** Nomes das lanes de usuário com tempo no dia, separados por ", " - null sem usuários. */
  users: string | null;
  first_event_at: string | null;
  last_event_at: string | null;
  seconds_on: number;
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
  note: JornadaNote | null;
}

/** Totais por device do RANGE INTEIRO - independem da página corrente. */
export interface JornadaDeviceTotals {
  device_id: string;
  device_name: string;
  seconds_on: number;
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
  days_with_data: number;
}

/** Resposta de `GET /reports/jornada` - items ordenados por device_name, date. */
export interface JornadaReportResponse {
  items: JornadaRow[];
  total: number;
  page: number;
  page_size: number;
  device_totals: JornadaDeviceTotals[];
}

/**
 * Tipos de export. usage_csv/jornada_csv (F3.5) são criados pelo POST /exports
 * genérico; os pacotes DSR (F4.5) são criados pelos endpoints /privacy/* e NUNCA
 * pelo POST /exports - mas a listagem e o download os servem (pacote ZIP, prazo
 * de 72h em vez dos 7 dias do CSV de relatório).
 */
export type ExportKind = "usage_csv" | "jornada_csv" | "dsr_subject" | "dsr_device" | "tenant_full";

/** Os pacotes DSR/offboarding (F4.5) saem como ZIP; o CSV de relatório como text/csv. */
export const DSR_EXPORT_KINDS: ExportKind[] = ["dsr_subject", "dsr_device", "tenant_full"];

export function isDsrExportKind(kind: ExportKind): boolean {
  return DSR_EXPORT_KINDS.includes(kind);
}

export type ExportStatus = "queued" | "running" | "done" | "failed";

/**
 * Params do job. Para usage_csv/jornada_csv: from/to (validados com os MESMOS
 * validadores dos endpoints de leitura) e, no usage_csv, group_by. Para os
 * pacotes DSR (F4.5) o backend grava o alvo: device_user_id (dsr_subject) ou
 * device_id (dsr_device); tenant_full não tem alvo. Todos os campos são
 * opcionais aqui porque um mesmo ExportJobItem pode ser de qualquer kind.
 */
export interface ExportParams {
  from?: string;
  to?: string;
  device_ids?: string[];
  /** Apenas usage_csv. */
  group_by?: "app" | "category" | "device" | "device_user";
  /** dsr_subject: titular alvo do pacote. */
  device_user_id?: string;
  /** dsr_device: dispositivo alvo do pacote. */
  device_id?: string;
}

/** Body de `POST /exports` (202 - o job entra na fila do worker). */
export interface ExportCreateRequest {
  kind: ExportKind;
  params: ExportParams;
}

/** Resposta 202 de `POST /exports`. */
export interface ExportCreateResponse {
  id: string;
  kind: ExportKind;
  status: "queued";
  created_at: string;
}

/** Item de `GET /exports` (últimos 30 dias do tenant, desc, máx. 100 - trilha de auditoria). */
export interface ExportJobItem {
  id: string;
  kind: ExportKind;
  status: ExportStatus;
  created_at: string;
  requested_by_name: string;
  params: ExportParams;
  /** Linhas de dados do CSV - null enquanto não concluído; máx. 500.000. */
  row_count: number | null;
  /** Teto de 500.000 linhas atingido: o CSV é PARCIAL - a tela avisa o usuário. */
  truncated: boolean;
  /** Conclusão + 7 dias - null enquanto não concluído. */
  expires_at: string | null;
  /** Job done com prazo vencido ou arquivo removido - o download responde 410. */
  expired: boolean;
}

export interface ExportsResponse {
  items: ExportJobItem[];
}

// =============================================================================
// Contratos da F4.5: DSR completo (direitos do titular - LGPD art. 18/19).
// O TITULAR é um device_user (NÃO um usuário do portal): as rotas usam
// {deviceUserId}/{deviceId}. Endpoints (snake_case):
//  - POST   /privacy/subjects/{deviceUserId}/export  (admin+owner) -> 202
//  - DELETE /privacy/subjects/{deviceUserId}/data     (owner)       -> 200 recibo
//  - POST   /privacy/devices/{deviceId}/export        (admin+owner) -> 202
//  - DELETE /privacy/devices/{deviceId}/data          (owner)       -> 200 recibo
//  - POST   /privacy/tenant/full-export               (owner)       -> 202
// O export devolve um ExportCreateResponse (mesmo shape do POST /exports) com
// kind dsr_subject/dsr_device/tenant_full; o download do pacote é ZIP, link
// válido por 72h, acompanhado em /relatorios/exportacoes (F3.5).
// =============================================================================

/**
 * Titular candidato a DSR, derivado de `GET /device-users` (listagem dedicada).
 * UUID zero = lane-máquina (sem usuário Windows): NÃO é um titular e o backend
 * já a mantém fora da listagem.
 */
export interface DsrSubject {
  device_user_id: string;
  device_id: string;
  device_name: string;
  /** Usuário Windows quando resolvível; null para titular já anonimizado por DSR. */
  windows_user: string | null;
  /** Nome de exibição JÁ resolvido pelo backend (nunca reimplementar no cliente). */
  display_name: string;
}

// =============================================================================
// Contratos dos TITULARES (device_users): GET /device-users?device_id&q&page&
// page_size e GET /device-users/{id} (Viewer+), PATCH /device-users/{id}
// (Admin/Owner, trilha update_device_user com de→para).
//
// O titular é o par (dispositivo, usuário do Windows) - NÃO um usuário do
// portal. O modelo é POR DISPOSITIVO: a mesma pessoa em duas máquinas tem dois
// registros, com ids diferentes. Nenhuma tela pode prometer que um registro
// atravessa dispositivos.
// =============================================================================

/** Item de `GET /device-users` e resposta de `GET/PATCH /device-users/{id}`. */
export interface DeviceUserItem {
  id: string;
  device_id: string;
  /** COALESCE(display_name, hostname) do dispositivo, resolvido pelo backend. */
  device_name: string;
  windows_username: string;
  /** Nome amigável definido no portal - null enquanto ninguém renomeou. */
  display_name: string | null;
  first_seen_at: string;
  last_seen_at: string;
}

/**
 * Body de `PATCH /device-users/{id}` (Admin/Owner). display_name null (ou vazio)
 * limpa o apelido: as telas voltam a exibir o windows_username. O backend audita
 * como update_device_user com o de→para.
 */
export interface DeviceUserPatchRequest {
  display_name: string | null;
}

/** Nome exibido de um titular: apelido quando houver, senão o usuário do Windows. */
export function deviceUserLabel(item: DeviceUserItem): string {
  return item.display_name !== null && item.display_name.length > 0
    ? item.display_name
    : item.windows_username;
}

/**
 * Body de `DELETE /privacy/subjects/{deviceUserId}/data` e
 * `DELETE /privacy/devices/{deviceId}/data`. confirmation deve bater com o valor
 * de segurança exigido pelo backend (o windows_username/display_name do titular,
 * ou o hostname do device); reason é obrigatório. confirmation/reason inválidos
 * -> 400; titular/device de outro tenant -> 404. A operação é HARD DELETE
 * IRREVERSÍVEL dos dados pessoais identificáveis.
 */
export interface DsrDeleteRequest {
  confirmation: string;
  reason: string;
}

/**
 * Recibo de exclusão (LGPD art. 19): contagens do que foi apagado/anonimizado.
 * raw_events_deleted/intervals_deleted: dados pessoais identificáveis apagados
 * (hard delete). device_users_anonymized: linhas de titular cujo nome virou
 * marcador neutro, preservando o device_user_id como chave. daily_rows_kept:
 * agregados de equipe já computados, MANTIDOS sem identificar a pessoa (a
 * exclusão do titular NÃO apaga agregados de equipe - decisão documentada no
 * DPA, spec linha 995). O backend pode incluir campos extras (Record aberto).
 */
export interface DsrReceipt {
  raw_events_deleted: number;
  intervals_deleted: number;
  device_users_anonymized: number;
  daily_rows_kept: number;
  [key: string]: number | string | boolean | null;
}

/** Resposta 200 de `DELETE /privacy/subjects|devices/.../data`. */
export interface DsrDeleteResponse {
  receipt: DsrReceipt;
}

// =============================================================================
// Contratos das chaves de instalação (enrollment keys - Seção 8.3), endpoints
// /enrollment-keys (PolicyAdminPlus). O segredo completo (`key`) aparece UMA
// única vez, na resposta do POST - depois disso só o key_prefix é exibido.
// =============================================================================

/** Item de `GET /enrollment-keys`. */
export interface EnrollmentKeyItem {
  id: string;
  /** Prefixo visível da chave (ex.: "ek_ab12") - o segredo completo nunca volta. */
  key_prefix: string;
  label: string | null;
  /** Limite de usos - null sem limite. */
  max_uses: number | null;
  use_count: number;
  /** Expiração - null sem expiração. */
  expires_at: string | null;
  /** Instante da revogação - null enquanto a chave está válida. */
  revoked_at: string | null;
}

export interface EnrollmentKeysResponse {
  items: EnrollmentKeyItem[];
}

/** Body de `POST /enrollment-keys` (201) - todos os campos opcionais. */
export interface EnrollmentKeyCreateRequest {
  label?: string;
  max_uses?: number;
  expires_at?: string;
}

/**
 * Resposta 201 de `POST /enrollment-keys` - `key` é o segredo COMPLETO,
 * retornado uma única vez (o portal exibe com aviso de guardar agora).
 */
export interface EnrollmentKeyCreateResponse {
  id: string;
  key: string;
  key_prefix: string;
  label: string | null;
  max_uses: number | null;
  expires_at: string | null;
}

// =============================================================================
// Contratos da F4.7: auditoria de acesso (GET /audit-logs) e a listagem de
// usuários (GET /users) usada para o filtro por ator. PolicyAdminPlus
// (Owner+Admin) — o Viewer NÃO acessa nenhum dos dois. JSON snake_case.
// =============================================================================

/** Status do usuário do portal: convidado (sem senha ainda), ativo ou desativado. */
export type UserStatus = "invited" | "active" | "disabled";

/**
 * Item de `GET /users` (PolicyAdminPlus). Shape completo do backend
 * (UserContracts.cs): a tela de Usuários consome tudo; a auditoria usa só
 * id/nome/e-mail para o filtro por ator.
 */
export interface UserListItem {
  id: string;
  email: string;
  display_name: string;
  role: Role;
  status: UserStatus;
  mfa_enabled: boolean;
  /** Último login - null para quem nunca entrou (ex.: convite pendente). */
  last_login_at: string | null;
}

export interface UsersResponse {
  items: UserListItem[];
}

/** Body de `POST /users/invitations` (201). Convidar Owner exige ator Owner. */
export interface InviteUserRequest {
  email: string;
  role: Role;
  display_name?: string;
}

/** Resposta 201 de `POST /users/invitations` - o convite vale por 7 dias. */
export interface InviteUserResponse {
  user_id: string;
  invitation_id: string;
  expires_at: string;
}

/**
 * Body de `PATCH /users/{id}` - troca de papel. Mexer em Owner (origem ou
 * destino) exige ator Owner; o backend garante sempre >= 1 Owner ativo (409).
 */
export interface UserRolePatchRequest {
  role: Role;
}

/**
 * Linha de `GET /audit-logs` (PolicyAdminPlus, Owner+Admin). A própria leitura
 * de /audit-logs NÃO é auditada (evita recursão). actor_name é o
 * display_name/e-mail resolvido pelo backend via join em users; null para ações
 * de SISTEMA (ex.: tokens de serviço, jobs). actor_ip é texto (inet) ou null
 * (ações sem IP, como gravações em transação). detail é o jsonb cru da ação
 * (período/filtros/alvo) — o portal resume os campos conhecidos.
 */
export interface AuditLogItem {
  id: string;
  occurred_at: string;
  actor_user_id: string | null;
  actor_name: string | null;
  actor_ip: string | null;
  /** Verbo da ação no vocabulário do backend (ex.: "view_timeline", "export_csv"). */
  action: string;
  /** Tipo do alvo (ex.: "device", "team", "report", "user") — null para ações sem alvo. */
  target_type: string | null;
  /** Id do alvo (UUID, slug, etc.) — null para ações sem alvo. */
  target_id: string | null;
  /** jsonb cru da ação: período (from/to), filtros aplicados, etc. — pode ser null. */
  detail: Record<string, unknown> | null;
}

export interface AuditLogsResponse {
  items: AuditLogItem[];
  total: number;
  page: number;
  page_size: number;
}

// =============================================================================
// Contratos da F4.8: transparência pública (GET /public/transparencia/{slug},
// AllowAnonymous) e a configuração editável da organização (GET/PATCH
// /organization). A página pública JAMAIS expõe dado pessoal, window_title ou
// os masked_patterns crus: só a POLÍTICA vigente, derivada pelo backend em
// vocabulário pt-BR amigável. JSON snake_case.
// =============================================================================

/** Política de títulos de janela (Seção 9). O backend já entrega a descrição. */
export interface WindowTitlePolicyPublic {
  /** FULL = títulos completos; MASKED_PATTERNS = com mascaramento; APP_ONLY = só o app. */
  mode: "FULL" | "MASKED_PATTERNS" | "APP_ONLY";
  /** Frase pt-BR amigável montada pelo backend (nunca os regex crus). */
  descricao: string;
}

/** Janela de coleta vigente da organização. start/end/days null fora do modo. */
export interface CollectionWindowPublic {
  mode: "ALWAYS" | "BUSINESS_HOURS";
  /** Dias da semana ISO (1 = segunda … 7 = domingo) - null quando ALWAYS. */
  days: number[] | null;
  /** "HH:mm" no fuso da organização - null quando ALWAYS. */
  start: string | null;
  end: string | null;
  /** Frase pt-BR amigável montada pelo backend. */
  descricao: string;
}

/** Retenções FIXAS do produto (N10-N13) - em dias/meses conforme o eixo. */
export interface RetencoesPublic {
  eventos_dias: number;
  intervalos_meses: number;
  agregados_meses: number;
  auditoria_meses: number;
}

/**
 * Resposta de `GET /api/v1/public/transparencia/{slug}` (AllowAnonymous,
 * rate-limited). SEM auth, SEM cookies, Cache-Control público curto. Slug
 * inexistente -> 404. `coletado`/`nunca_coletado` chegam prontos em pt-BR
 * (derivados da política e da lista fixa da 9.7); o portal apenas renderiza.
 */
export interface TransparenciaPublicResponse {
  organization_name: string;
  window_title_policy: WindowTitlePolicyPublic;
  collection_window: CollectionWindowPublic;
  retencoes: RetencoesPublic;
  finalidade_declarada: string | null;
  contato_dpo: string | null;
  /** Data de vigência da política (yyyy-MM-dd) - null quando não definida. */
  vigencia: string | null;
  /** Instante (timestamptz) da última purga concluída - null se nunca houve. */
  ultima_purga: string | null;
  /** Itens coletados em pt-BR, conforme a window_title_policy. */
  coletado: string[];
  /** Lista FIXA do que nunca é coletado (Seção 9.7), em pt-BR. */
  nunca_coletado: string[];
}

/**
 * Resposta de `GET /api/v1/organization` (PolicyAccess - qualquer papel
 * autenticado). business_hours é o jsonb cru da org (null quando não definido).
 */
export interface OrganizationResponse {
  name: string;
  slug: string;
  timezone: string;
  business_hours: BusinessHours | null;
  finalidade_declarada: string | null;
  contato_dpo: string | null;
  /** Data de vigência da política (yyyy-MM-dd) - null quando não definida. */
  data_vigencia: string | null;
}

/**
 * Body de `PATCH /api/v1/organization` (PolicyAdminPlus - Owner/Admin). Campos
 * ausentes não mudam; enviar null limpa o campo (string vazia -> null no
 * cliente). O backend audita a operação como update_privacy_config. Resposta
 * 200 com o OrganizationResponse atualizado.
 */
export interface OrganizationPatchRequest {
  finalidade_declarada?: string | null;
  contato_dpo?: string | null;
  data_vigencia?: string | null;
  /**
   * Horário de trabalho ({days,start,end}, dias ISO 1-7) ou null para limpar;
   * campo ausente não muda. Alimenta a linha de referência dos gráficos e do
   * relatório de jornada (Seção 8.5).
   */
  business_hours?: BusinessHours | null;
}

/** Janela de coleta do agente (jsonb canônico da Seção 5.5). */
export interface CollectionWindow {
  mode: "ALWAYS" | "BUSINESS_HOURS";
  days?: number[] | null;
  start?: string | null;
  end?: string | null;
}

/**
 * Resposta de `GET/PATCH /api/v1/organization/agent-config` (F5, §8.7).
 * heartbeat_sec e active_window_poll_sec são constantes do protocolo (read-only);
 * FULL nunca é aceito pelo PATCH (exige registro em DPA, aplicado pela operadora).
 */
export interface AgentConfigResponse {
  config_version: number;
  heartbeat_sec: number;
  active_window_poll_sec: number;
  idle_threshold_sec: number;
  window_title_policy: "FULL" | "MASKED_PATTERNS" | "APP_ONLY";
  masked_patterns: string[];
  ignored_processes: string[];
  collection_window: CollectionWindow;
  updated_at: string;
}

/** Body de `PATCH /api/v1/organization/agent-config` (OwnerOnly; campos ausentes não mudam). */
export interface AgentConfigPatchRequest {
  idle_threshold_sec?: number;
  window_title_policy?: "MASKED_PATTERNS" | "APP_ONLY";
  masked_patterns?: string[];
  ignored_processes?: string[];
  collection_window?: CollectionWindow;
}
