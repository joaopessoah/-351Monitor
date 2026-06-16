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

/** Tipos de export do MVP - dsr_* são F4 (o backend rejeita com 400). */
export type ExportKind = "usage_csv" | "jornada_csv";

export type ExportStatus = "queued" | "running" | "done" | "failed";

/** Params do job - validados com os MESMOS validadores dos endpoints de leitura. */
export interface ExportParams {
  from: string;
  to: string;
  device_ids?: string[];
  /** Apenas usage_csv. */
  group_by?: "app" | "category" | "device" | "device_user";
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
