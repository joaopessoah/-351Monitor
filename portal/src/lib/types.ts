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
    /**
     * Plano comercial da organização (ex.: "trial", "pro"). Governa recursos
     * exclusivos do Pro no portal, como o e-mail de alertas de dispositivos. O
     * PREÇO é decisão comercial fora do sistema: o portal nunca exibe valor em
     * reais.
     */
    plan: string;
    /** Limite de dispositivos do plano - null sem limite definido. */
    device_limit: number | null;
    /**
     * Instante do dismiss do checklist de primeiros passos (POST
     * /organization/onboarding-checklist/dismiss) - null enquanto o card da
     * Visão Geral está visível; o DELETE na mesma rota reabre (volta a null).
     */
    onboarding_checklist_dismissed_at: string | null;
    /**
     * Meta semanal de horas ativas da EQUIPE (nunca individual) - null sem meta.
     * Editável em PATCH /organization (1 a 10000).
     */
    goal_weekly_active_hours: number | null;
    /**
     * Meta de percentual do tempo ativo em aplicativos relacionados ao trabalho
     * - null sem meta. Editável em PATCH /organization (1 a 100).
     */
    goal_work_related_pct: number | null;
  };
}

/**
 * Resposta de `GET /me/email-prefs` e body de `PATCH /me/email-prefs`
 * (PATCH parcial: campos ausentes não mudam). São e-mails da PESSOA logada,
 * não da organização.
 *
 * weekly_digest: resumo semanal da equipe.
 * fleet_alerts: avisos de dispositivos com problema (exclusivo do plano Pro).
 * jornada_weekly: relatório semanal de jornada (exclusivo do plano Pro; o
 * PATCH responde 403 ao tentar LIGAR fora dele, desligar é sempre permitido).
 *
 * Cuidado de vocabulário: "alertas" no portal são estes e-mails; as pendências
 * do sino do topo NUNCA se chamam alertas.
 */
export interface EmailPrefs {
  weekly_digest: boolean;
  fleet_alerts: boolean;
  jornada_weekly: boolean;
}

export type EmailPrefsPatchRequest = Partial<EmailPrefs>;

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

/**
 * Resposta de `GET /devices/{id}/transparency-link` (Admin+): a URL da página
 * pública DAQUELE dispositivo, a mesma que o tray do agente abre na máquina do
 * funcionário.
 *
 * Endpoint separado, e não um campo de DeviceItem, de propósito: a URL carrega um
 * token, que é um segredo de baixo valor mas é um segredo. Viewer não a recebe, e
 * ela não trafega na listagem inteira de dispositivos.
 */
export interface DeviceTransparencyLinkResponse {
  device_id: string;
  url: string;
}

/**
 * Resposta de `GET /devices/health-summary` (Viewer+): contadores de saúde da
 * FROTA INTEIRA do tenant, computados no backend sobre todos os devices não
 * arquivados. Nada a ver com a página corrente de `GET /devices` — é justamente
 * o que os contadores derivados no cliente (lib/deviceHealth.ts) não conseguem
 * responder, porque só veem os 50 devices da página.
 *
 * As dimensões são as MESMAS de lib/deviceHealth.ts, que continua derivando a
 * saúde por linha para os badges da tabela. within_business_hours diz se
 * server_time cai dentro do horário de trabalho da organização — o mesmo
 * critério que promove "sem comunicação" a offline_severe.
 *
 * O filtro correspondente da listagem é `GET /devices?health=alert`, que aplica
 * o mesmo predicado de with_alert à frota inteira (com paginação normal).
 */
export interface DeviceHealthSummaryResponse {
  /** Devices não arquivados da organização (base de todos os contadores). */
  active_devices: number;
  /**
   * Dispositivos que ocupam licença: não arquivados e não revogados, a mesma regra
   * do limite de instalação (um "pausado" ocupa licença). Alimenta o medidor de
   * licenças da versão de teste (LicencasDeTeste).
   */
  licensed_devices: number;
  offline: number;
  /** Sem comunicação há mais de 30 min E em horário de trabalho. */
  offline_severe: number;
  clock_skewed: number;
  outdated: number;
  tampered: number;
  notice_pending: number;
  /** Devices com pelo menos uma dimensão acionada (nunca soma das dimensões). */
  with_alert: number;
  within_business_hours: boolean;
  server_time: string;
}

/** Uma versão do agente presente na frota. `version` null = máquina que ainda não reportou versão. */
export interface FleetVersionRow {
  version: string | null;
  count: number;
  /** Abaixo do min_version do release vigente - comparação SEMVER no BACKEND. */
  outdated: boolean;
}

/**
 * Falha RECENTE de auto-update num dispositivo, materializada do evento
 * UPDATE_FAILED. `reason` é a ETAPA que reprovou, sempre da lista canônica
 * (download | hash | signature | install) - nunca texto livre nem mensagem de
 * exceção, que poderia carregar caminho de arquivo ou nome de usuário.
 */
export interface UpdateFailureRow {
  device_id: string;
  hostname: string;
  display_name: string | null;
  reason: "download" | "hash" | "signature" | "install";
  /** Versão que a tentativa mirava (to_version do evento). */
  target_version: string | null;
  occurred_at: string;
}

/**
 * Resposta de `GET /devices/version-summary` (Viewer+): a vigilância de rollout
 * da frota, computada no servidor no mesmo padrão do health-summary.
 *
 * current_version/min_version dizem para onde a frota deveria estar indo (release
 * vigente do canal estável), `versions` diz onde ela está de fato, e
 * `recent_failures` diz em que etapa quem não chegou lá emperrou. O contador
 * "desatualizados" do health-summary responde só a primeira metade disso.
 */
export interface DeviceVersionSummaryResponse {
  active_devices: number;
  current_version: string | null;
  min_version: string | null;
  /** Da versão mais nova para a mais antiga; a desconhecida vem por último. */
  versions: FleetVersionRow[];
  /** Total de dispositivos com falha na janela (pode ser maior que recent_failures). */
  update_failures: number;
  /** Amostra detalhada das falhas mais recentes (teto no servidor). */
  recent_failures: UpdateFailureRow[];
  update_failure_window_days: number;
  server_time: string;
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
  /** F6 - tempo ativo em app SEM categoria no tenant (antes vinha somado no neutro). */
  seconds_unclassified: number;
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
  /** F6 - tempo ativo em app SEM categoria no tenant (antes vinha somado no neutro). */
  seconds_unclassified: number;
  data_incomplete: boolean;
  device_count: number;
  /**
   * F9 - do SERVIDOR, pela fórmula única (decisão 4) e sobre EXATAMENTE estes baldes.
   * Calculados no mesmo recorte da resposta, inclusive quando ela é de uma lane só: é
   * isso que impede o índice e a composição exibidos lado a lado de falarem de escopos
   * diferentes. `null` sem denominador, nunca 0.
   */
  productivity_index: number | null;
  classification_coverage: number | null;
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
  /**
   * SUGESTÃO do dicionário brasileiro (F1.1): nome canônico de categoria, igual ao
   * vocabulário semeado na criação da organização, ou null para app sem curadoria.
   * É só sugestão: quem decide é `category`. O portal traduz esse nome no id da
   * categoria da organização para oferecer o lote.
   */
  default_category: string | null;
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

/**
 * Item do lote `PUT /app-catalog/categories/batch` - mesma semântica do PUT individual,
 * category_id null desmapeia. custom_display_name fica de fora do lote: renomear é ato
 * individual, e o nome custom já definido SOBREVIVE ao lote.
 */
export interface AppCategoryBatchItem {
  app_id: string;
  category_id: string | null;
}

/**
 * Body do lote. No máximo 500 itens por chamada e sem app_id repetido (o backend
 * responde 400 nos dois casos); app ou categoria desconhecidos respondem 404 e NADA
 * é aplicado. Tudo numa transação, com UMA única reagregação de 30 dias.
 */
export interface AppCategoryBatchRequest {
  items: AppCategoryBatchItem[];
}

/** Resposta do lote: `applied` mapeamentos escritos e o estado final de cada um. */
export interface AppCategoryBatchResponse {
  applied: number;
  items: {
    app_id: string;
    process_name: string;
    display_name: string;
    custom_display_name: string | null;
    category: TopAppCategory | null;
  }[];
  /** Linhas enfileiradas em dirty_days pela única reagregação do lote. */
  reaggregation_days: number;
}

/** Teto de itens por chamada do lote (espelha AppCatalogController.MaxBatchItems). */
export const APP_CATEGORY_BATCH_MAX = 500;

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
  /** F6 - tempo ativo em app SEM categoria no tenant. */
  seconds_unclassified: number;
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

// ----- GET /reports/fora-do-horario (atividade fora do horário de trabalho) -----

/**
 * Situação do painel. Fora de "ok" a resposta vem SEM número de propósito: sem
 * horário de trabalho declarado, ou com a coleta restrita ao próprio horário,
 * qualquer valor seria enganoso - a tela explica o motivo em vez de exibir zero.
 */
export type ForaDoHorarioStatus =
  | "ok"
  | "horario_nao_configurado"
  | "coleta_restrita_ao_horario";

/** Totais do período INTEIRO - outside = before + after + non_business_day. */
export interface ForaDoHorarioTotals {
  /** Tempo ativo total do recorte, da MESMA fonte (activity_intervals). */
  seconds_active: number;
  seconds_outside: number;
  /** Antes do início do horário de trabalho. */
  seconds_before: number;
  /** Depois do fim do horário de trabalho. */
  seconds_after: number;
  /** Em dias que não estão na escala declarada. */
  seconds_non_business_day: number;
  devices_with_activity_outside: number;
}

/** Uma linha por dispositivo COM atividade fora do horário no período. */
export interface ForaDoHorarioItem {
  device_id: string;
  device_name: string;
  seconds_active: number;
  seconds_outside: number;
  seconds_before: number;
  seconds_after: number;
  seconds_non_business_day: number;
  days_with_activity_outside: number;
}

/**
 * Resposta de `GET /reports/fora-do-horario`. Indicador de EQUILÍBRIO da equipe:
 * o vocabulário da tela é sempre "atividade fora do horário de trabalho", jamais
 * hora extra, jornada extraordinária ou banco de horas.
 *
 * `items` só vem preenchido com include_devices=true (ou device_ids); `total` é o
 * número de dispositivos com atividade fora no período inteiro.
 */
export interface ForaDoHorarioResponse {
  status: ForaDoHorarioStatus;
  timezone: string;
  business_hours: BusinessHours | null;
  collection_window_mode: string;
  totals: ForaDoHorarioTotals | null;
  items: ForaDoHorarioItem[];
  total: number;
  page: number;
  page_size: number;
}

/**
 * Tipos de export. usage_csv/jornada_csv/fora_horario_csv (F3.5) são criados pelo
 * POST /exports genérico; os pacotes DSR (F4.5) são criados pelos endpoints
 * /privacy/* e NUNCA pelo POST /exports - mas a listagem e o download os servem
 * (pacote ZIP, prazo de 72h em vez dos 7 dias do CSV de relatório).
 */
export type ExportKind =
  | "usage_csv"
  | "jornada_csv"
  | "fora_horario_csv"
  /** Resumo em PDF (F6); com params.windows_sid vira a variante PESSOAL (F9). */
  | "resumo_pdf"
  | "dsr_subject"
  | "dsr_device"
  | "tenant_full";

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
  /** Etiqueta de equipe do recorte (F5) - ausente quando o CSV é da org inteira. */
  tag?: string;
  /** Apenas usage_csv. */
  group_by?: "app" | "category" | "device" | "device_user";
  /** dsr_subject: titular alvo do pacote. */
  device_user_id?: string;
  /** dsr_device: dispositivo alvo do pacote. */
  device_id?: string;
  /**
   * Apenas resumo_pdf: transforma o resumo agregado na variante PESSOAL, restrita
   * a esta pessoa. É o caminho de PAPEL da decisão 10 - quem imprime é o gestor,
   * porque o colaborador não tem acesso ao painel.
   */
  windows_sid?: string;
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
 * id/nome/e-mail para o filtro por ator e o sino de pendências lê o status
 * para achar convite parado.
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
 * Bloco "Este dispositivo", presente APENAS na resposta da rota por token
 * (`GET /public/t/{token}` - o link que o tray da máquina abre). Só estado da
 * INSTALAÇÃO: nenhuma hora ativa/ociosa e nenhum aplicativo, porque a URL não
 * tem autenticação e o link circula.
 */
export interface TransparenciaDeviceBlock {
  hostname: string;
  /** Instante em que o agente confirmou a exibição do aviso - null se pendente. */
  notice_acked_at: string | null;
  last_seen_at: string | null;
  status: "active" | "paused" | "archived" | "revoked";
}

/**
 * Resposta de `GET /api/v1/public/transparencia/{slug}` e de
 * `GET /api/v1/public/t/{token}` (AllowAnonymous, rate-limited). SEM auth, SEM
 * cookies, Cache-Control curto. Slug/token inexistente -> 404.
 * `coletado`/`nunca_coletado` chegam prontos em pt-BR (derivados da política e
 * da lista fixa da 9.7); o portal apenas renderiza. `device` só vem preenchido
 * na rota por token.
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
  /** Estado da instalação - só na rota por token; null na página por slug. */
  device: TransparenciaDeviceBlock | null;
}

// =============================================================================
// Contrato de GET /api/v1/compliance/summary (Admin/Owner, read-only): as
// evidências de conformidade da organização. Nenhum dado pessoal - contagens e
// carimbos de tempo. maintenance_runs é tabela GLOBAL: o backend expõe só
// job_name/finished_at/status, nunca o detail jsonb (que soma todos os tenants).
// =============================================================================

/** Última execução de um job de manutenção; status "never_run" quando nunca rodou. */
export interface MaintenanceRunSummary {
  job_name: string;
  finished_at: string | null;
  status: string;
}

/** Cobertura de ciência do aviso na frota ATIVA (status active). */
export interface NoticeCoverageSummary {
  active_devices: number;
  acknowledged: number;
  pending: number;
}

/** Contagens da trilha no mês corrente (fuso da organização), month = "yyyy-MM". */
export interface AuditActivitySummary {
  month: string;
  view_timeline: number;
  view_report: number;
  export_csv: number;
  dsr_export: number;
  dsr_delete: number;
}

/** Pacotes DSR de titular/dispositivo por status. */
export interface DsrExportStatusSummary {
  status: string;
  count: number;
}

export interface ComplianceSummaryResponse {
  organization_name: string;
  /** Carimbo do servidor - a data impressa no dossiê. */
  generated_at: string;
  maintenance_runs: MaintenanceRunSummary[];
  notice_coverage: NoticeCoverageSummary;
  audit_activity: AuditActivitySummary;
  dsr_exports: DsrExportStatusSummary[];
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
  /** Meta semanal de horas ativas da EQUIPE - null sem meta (ver MeResponse). */
  goal_weekly_active_hours: number | null;
  /** Meta de % do tempo em apps relacionados ao trabalho - null sem meta. */
  goal_work_related_pct: number | null;
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
  /** Meta semanal de horas ativas da EQUIPE: 1 a 10000; null remove a meta. */
  goal_weekly_active_hours?: number | null;
  /** Meta de % do tempo em apps relacionados ao trabalho: 1 a 100; null remove. */
  goal_work_related_pct?: number | null;
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
  /** Corpo do aviso de ciência escrito pela controladora; null = corpo padrão do agente. */
  notice_text: string | null;
  /** Sobe a cada mudança do aviso: é o que reexibe o aviso em toda a frota. */
  notice_version: number;
  /** Read-only: corpo padrão exibido quando notice_text é null. */
  notice_default_body: string;
  /** Read-only: enquadramento fixo concatenado PELO AGENTE, não editável pelo tenant. */
  notice_fixed_framing: string;
  /** Read-only: limite do corpo do tenant, já descontado o enquadramento fixo. */
  notice_max_length: number;
  updated_at: string;
}

/** Body de `PATCH /api/v1/organization/agent-config` (OwnerOnly; campos ausentes não mudam). */
export interface AgentConfigPatchRequest {
  idle_threshold_sec?: number;
  window_title_policy?: "MASKED_PATTERNS" | "APP_ONLY";
  masked_patterns?: string[];
  ignored_processes?: string[];
  collection_window?: CollectionWindow;
  /** null volta ao corpo padrão do agente; o enquadramento fixo nunca é afetado. */
  notice_text?: string | null;
}

// =============================================================================
// Contratos da cobrança: GET /billing/billable-devices?month=YYYY-MM (papel
// OWNER). Extrato mensal dos dispositivos que contam para o mês, insumo do
// billing manual. O portal exibe CONTAGEM e EVIDÊNCIA e jamais valor em reais:
// preço é decisão comercial fora do sistema.
// =============================================================================

/**
 * Primeira regra de cobrança que casou (events > enrolled > keep_alive):
 * eventos recebidos no mês, registro (enroll) no mês, ou último contato no mês
 * (lote vazio de keep-alive, que só atualiza o last_seen_at).
 */
export type BillableEvidence = "events" | "enrolled" | "keep_alive";

export interface BillableDeviceItem {
  device_id: string;
  display_name: string | null;
  hostname: string;
  /** Status ATUAL do device - archived nunca é cobrável; revoked que usou, conta. */
  status: string;
  /** Instante do registro (enroll) do device. */
  enrolled_at: string;
  last_seen_at: string | null;
  /** Um BillableEvidence; tipado como texto para tolerar regra nova no backend. */
  evidence: string;
}

/**
 * Resposta de `GET /billing/billable-devices?month=`. frozen=true significa mês
 * fechado e CONGELADO (o snapshot não muda mais, seguro para anexar à fatura);
 * frozen=false é o mês corrente ao vivo, cujo número ainda pode mudar até o
 * fechamento. criteria é a regra aplicada, em texto legível montado pelo
 * backend.
 */
export interface BillableDevicesResponse {
  /** Mês pedido, no formato YYYY-MM. */
  month: string;
  device_count: number;
  criteria: string;
  items: BillableDeviceItem[];
  frozen: boolean;
  /** Instante do congelamento - null enquanto o mês não fechou. */
  frozen_at: string | null;
}

// =============================================================================
// Contratos da F6: visão geral macro (GET /dashboard/overview), atividade por
// hora (GET /dashboard/activity-by-hour) e colaboradores (GET/PATCH /people).
// O ÍNDICE e a COBERTURA vêm calculados do servidor (fonte única da fórmula,
// decisão 4 do spec de 07/09/2026): o portal NUNCA recalcula, só formata.
// =============================================================================

/** Período resolvido pelo servidor, inclusivo, no fuso da organização. */
export interface OverviewPeriod {
  from: string;
  to: string;
  /** Quantidade de dias do intervalo (base das médias por dia). */
  days: number;
}

/**
 * Totais do período com os seis baldes da composição e os dois indicadores.
 * productivity_index e classification_coverage são frações de 0 a 1 e vêm
 * `null` quando o denominador é zero - null é "sem dado", nunca "zero".
 * person_count/person_days ignoram a lane-máquina (máquina não é pessoa).
 */
export interface OverviewTotals {
  seconds_on: number;
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
  seconds_work_related: number;
  seconds_neutral: number;
  seconds_not_work_related: number;
  seconds_unclassified: number;
  productivity_index: number | null;
  classification_coverage: number | null;
  device_count: number;
  person_count: number;
  person_days: number;
  data_incomplete: boolean;
}

/** Metas semanais da organização, repetidas na resposta para a tela não precisar do /me. */
export interface OverviewGoals {
  weekly_active_hours: number | null;
  work_related_pct: number | null;
}

/** Resposta de `GET /dashboard/overview` - previous só vem com compare=true. */
export interface OverviewResponse {
  period: OverviewPeriod;
  totals: OverviewTotals;
  previous: OverviewTotals | null;
  days: DashboardSummaryDay[];
  goals: OverviewGoals;
  /**
   * A janela contra a qual `previous` foi apurado (null sem compare). Existe para a
   * comparação ser conferível - no grão mensal ela é a MESMA janela deslocada N meses,
   * não "a mesma quantidade de dias".
   */
  previous_period: OverviewPeriod | null;
}

/**
 * Um aplicativo da pessoa no período, na visão do colaborador.
 *
 * `classification` é +1/0/-1 quando a empresa classificou e NULL quando ninguém
 * classificou. Null não é zero: zero significa "a empresa decidiu que isto é
 * neutro", e dizer neutro onde não houve decisão inventaria uma posição da
 * empresa sobre o trabalho da pessoa.
 */
export interface PersonSelfViewApp {
  process_name: string;
  display_name: string;
  classification: number | null;
  seconds_active: number;
}

/**
 * Resposta de `GET /people/{sid}/self-view` - os mesmos números que a pessoa
 * veria sobre si, servidos DENTRO do painel para quem já tem acesso.
 *
 * Índice e cobertura vêm CALCULADOS DO SERVIDOR: é o endpoint que encerra a
 * duplicação da fórmula no cliente que a página da pessoa carregava.
 */
export interface PersonSelfViewResponse {
  windows_sid: string;
  display_name: string | null;
  team_name: string | null;
  period: OverviewPeriod;
  seconds_on: number;
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
  seconds_work_related: number;
  seconds_neutral: number;
  seconds_not_work_related: number;
  seconds_unclassified: number;
  productivity_index: number | null;
  classification_coverage: number | null;
  days_with_data: number;
  device_count: number;
  top_apps: PersonSelfViewApp[];
  open_notes: number;
  open_disputes: number;
}

/**
 * Uma linha de "por que o índice mudou": a contribuição de UM membro (aplicativo,
 * equipe ou dia) para a variação, em pontos e com sinal.
 *
 * `points` NÃO é o índice do membro. Um aplicativo improdutivo que cresceu derruba
 * o índice sem ter um único segundo produtivo, porque engorda o denominador - e é
 * exatamente esse caso que a tela precisa saber nomear.
 */
export interface IndexContributionRow {
  key: string;
  label: string;
  points: number;
  seconds_work_related: number;
  seconds_work_related_previous: number;
  seconds_classified: number;
  seconds_classified_previous: number;
}

/**
 * Resposta de `GET /dashboard/index-explained`. As parcelas das três listas somam
 * `delta_points` - é a promessa do painel, e o rodapé dele exibe a soma.
 *
 * `unavailable` preenchido significa que não há variação a explicar (falta
 * denominador num dos períodos): indicadores em null e as três listas vazias.
 */
export interface IndexExplainedDimension {
  /**
   * A variação que ESTA dimensão explica - é ela que as parcelas somam, sempre.
   * Por equipe e por dia é a mesma do cabeçalho; por aplicativo pode diferir,
   * porque a leitura reaplica a classificação vigente enquanto os baldes
   * agregados guardam a que valia no dia.
   */
  delta_points: number | null;
  /** Motivo, quando o delta desta dimensão diverge do cabeçalho. null = convergem. */
  divergence: string | null;
  items: IndexContributionRow[];
}

export interface IndexExplainedResponse {
  period: OverviewPeriod;
  previous_period: OverviewPeriod;
  index: number | null;
  previous_index: number | null;
  delta_points: number | null;
  unavailable: string | null;
  by_app: IndexExplainedDimension;
  by_team: IndexExplainedDimension;
  by_day: IndexExplainedDimension;
}

/**
 * Uma hora local do tenant. As 24 vêm SEMPRE, inclusive vazias (o gráfico
 * desenha o dia inteiro). avg_people_active é a média de pessoas ativas
 * simultâneas naquela hora no período; null quando não há dia com dado.
 */
export interface ActivityByHourItem {
  hour: number;
  seconds_active: number;
  seconds_idle: number;
  avg_people_active: number | null;
}

export interface ActivityByHourResponse {
  hours: ActivityByHourItem[];
  days_with_data: number;
}

/**
 * Uma PESSOA do tenant no período. Identidade = windows_sid resolvido pela
 * mesclagem; display_name já vem resolvido pelo backend (apelido > nome da
 * lane > usuário do Windows > SID) - renderize este campo, não reimplemente a
 * regra. teams são as etiquetas dos dispositivos em que ela apareceu.
 */
export interface PersonRow {
  windows_sid: string;
  display_name: string;
  seconds_on: number;
  seconds_active: number;
  seconds_idle: number;
  seconds_locked: number;
  seconds_work_related: number;
  seconds_neutral: number;
  seconds_not_work_related: number;
  seconds_unclassified: number;
  productivity_index: number | null;
  classification_coverage: number | null;
  device_count: number;
  days_with_data: number;
  teams: string[];
}

export interface PeopleReportResponse {
  items: PersonRow[];
  total: number;
  page: number;
  page_size: number;
}

/** PATCH /people/{sid}: apelido e mesclagem (Admin+). */
export interface PersonPatchRequest {
  display_name?: string | null;
  merged_into_sid?: string | null;
}

/** POST /reaggregation: janela em dias (1 a 366); resposta é 202. */
export interface ReaggregationRequest {
  days: number;
}

export interface ReaggregationResponse {
  enqueued: number;
  days: number;
}

// =============================================================================
// F6 — Classificação 2.0 (Configurações → Classificação): cobertura da
// classificação em GET /app-catalog e vocabulário da organização em PATCH
// /organization. Tipos À PARTE porque as fatias anteriores já fecharam
// AppCatalogResponse e OrganizationPatchRequest acima e esta fatia não pode
// editá-los — aqui entram só os campos NOVOS que o backend já devolve/aceita.
// (O botão "Recalcular histórico" usa ReaggregationRequest/Response acima,
// sem precisar de tipo novo.)
// =============================================================================

/**
 * GET /app-catalog nesta fase (aceita também `sort=impacto`, sem mudar o
 * formato dos itens): mesmo formato de AppCatalogResponse, mais a COBERTURA da
 * classificação em TEMPO (decisão 4 do spec de 07/09/2026) -
 * uncategorized_seconds_active e total_seconds_active, mesma janela de 30 dias
 * dos itens. cobertura = (total_seconds_active − uncategorized_seconds_active)
 * ÷ total_seconds_active (ver components/apps/classificationCoverage.ts).
 */
export interface AppCatalogResponseF6 extends AppCatalogResponse {
  uncategorized_seconds_active: number;
  total_seconds_active: number;
}

/**
 * Body de `PATCH /api/v1/organization` usado só pelo seletor de vocabulário
 * (F6, decisão 1) — o backend aceita este campo junto dos demais de
 * OrganizationPatchRequest (acima), mas esta tela só precisa enviar este.
 * Troca de vocabulário é só ROTULAGEM (nenhum balde muda de valor): o backend
 * não reagrega nada quando este campo muda.
 */
export interface OrganizationVocabularyPatchRequest {
  classification_vocabulary: "produtividade" | "trabalho";
}

// =============================================================================
// F6 - ALERTAS DE GESTAO (secao 4 do spec de 07/09/2026)
// =============================================================================

/**
 * Um alerta de gestao VIVO, ja traduzido pelo servidor. `title` e `context`
 * chegam prontos em portugues: o VOCABULARIO ("variacao e contexto, nunca
 * julgamento", jamais ranking) tem fonte unica no backend, e a tela nao
 * reescreve a frase nem recalcula numero nenhum.
 *
 * `detail` sao os numeros crus da regra, para exibir valor exato sem reparsear
 * a frase. `severity` e so a faixa de cor do cartao - nao e nota nem juizo.
 */
export interface AlertItem {
  kind: string;
  scope_type: "organization" | "team" | "person" | "device";
  scope_key: string;
  scope_label: string | null;
  severity: "atencao" | "informativo";
  title: string;
  context: string;
  action: string;
  /** Rota do portal (sempre relativa) que resolve o alerta. */
  link: string;
  first_seen_at: string;
  last_seen_at: string;
  detail: Record<string, unknown>;
}

/**
 * Resposta de `GET /alerts`.
 *
 * `person_alerts_enabled` e o opt-in da decisao 5: com `false` as duas regras
 * de escopo pessoa nao sao avaliadas, e a tela precisa DIZER isso - omitir
 * faria o gestor concluir que ninguem trabalha fora do horario quando na
 * verdade ninguem esta medindo.
 *
 * `plan_includes_alerts` separa "nada a relatar" de "o plano nao inclui a
 * feature": a diferenca entre um elogio e um upgrade.
 */
export interface AlertsResponse {
  items: AlertItem[];
  person_alerts_enabled: boolean;
  plan_includes_alerts: boolean;
  /** Ultima execucao OK do motor no worker; null se nunca rodou. */
  evaluated_at: string | null;
}

/**
 * Uma linha do comparativo de equipes (decisao 3), DERIVADA no portal de um
 * `GET /dashboard/overview?tag=` por etiqueta (um Promise.all dentro de um
 * useQuery so). O indice e a cobertura vem prontos do servidor por etiqueta -
 * nada de recalculo aqui; o que a tela deriva sao divisoes dos totais do
 * servidor (horas por pessoa-dia, ociosidade, capacidade).
 *
 * Metricas de TOTAL sempre existem; as COMPARATIVAS (medias por pessoa,
 * ociosidade, capacidade) so com 3 pessoas ou mais - abaixo disso a linha
 * mostra totais e imprime traco no resto, porque media de equipe com 2 pessoas
 * e afirmacao sobre individuo com outro nome.
 */
export interface TeamComparisonRow {
  tag: string;
  people: number;
  /** Pares (pessoa, dia) com dado: denominador das medias por pessoa por dia. */
  person_days: number;
  seconds_on: number;
  seconds_active: number;
  seconds_idle: number;
  seconds_unclassified: number;
  /** Do servidor, nunca recalculado no portal. */
  productivity_index: number | null;
  productivity_index_previous: number | null;
  classification_coverage: number | null;
  /** Media de horas ativas por pessoa por dia; null sem mínimo de grupo. */
  active_seconds_per_person_day: number | null;
  /** ocioso / ligada; null sem mínimo de grupo ou sem tempo ligado. */
  idle_share: number | null;
  /** ativo / (jornada declarada x dias uteis x pessoas); null sem base. */
  capacity_used: number | null;
  /** false quando a equipe tem menos de 3 pessoas (decisao 3). */
  meets_group_minimum: boolean;
}

/**
 * Uma linha de `GET /people/daily`: o DIA de uma pessoa. Existe porque o mapa
 * do mes desenha pessoa x DIA e a listagem de /people so agrega o periodo
 * inteiro - com ela, pintar 31 colunas custaria uma requisicao por dia (ordem
 * de 150 chamadas numa tela). Aqui o mes inteiro vem em UMA consulta.
 *
 * Identidade e nome seguem a MESMA regua da listagem: windows_sid resolvido
 * pela mesclagem e display_name JA resolvido pelo servidor - renderize este
 * campo, nao reimplemente a regra.
 *
 * productivity_index e do servidor (produtivo / classificado) e e null quando o
 * dia nao teve NENHUM tempo classificado: null imprime "-", jamais 0%. Sao os
 * quatro baldes que o mapa precisa; quem quer os seis continua no /people.
 */
export interface PersonDay {
  windows_sid: string;
  display_name: string;
  /** Dia no fuso do tenant, yyyy-MM-dd. */
  date: string;
  seconds_on: number;
  seconds_active: number;
  seconds_idle: number;
  seconds_unclassified: number;
  productivity_index: number | null;
}

/** Resposta de `GET /people/daily?from&to[&tag]` - sem paginacao (ver o contrato). */
export interface PeopleDailyResponse {
  items: PersonDay[];
}

// ---------------------------------------------------------------------------
// Anotacao de periodo e contestacao de classificacao (F6, decisao 6 do spec de
// 07/09/2026) - `GET/POST /people/{sid}/notes` e `PATCH .../notes/{id}`.
//
// A medicao sabe QUANTO, nunca POR QUE: reuniao presencial, treinamento, visita
// a cliente ou maquina em manutencao aparecem como ausencia de atividade, e o
// numero sozinho mente por omissao. A anotacao e o contexto que gente escreve
// sobre um periodo; a contestacao e a discordancia registrada sobre COMO um
// aplicativo foi classificado.
//
// ACEITAR UMA CONTESTACAO NAO ALTERA NENHUM AGREGADO. E um ato de registro e
// insumo da curadoria - o numero so muda quando o gestor remapeia a categoria
// em Configuracoes > Classificacao e o historico e recalculado. O servidor
// devolve essa frase pronta em `effect` (ver PersonNoteReviewResponse) para que
// a tela DIGA isso ao gestor no clique, em vez de deixa-lo esperando.
//
// O segmento {sid} da rota aceita o windows_sid OU o device_user_id: a pagina
// /pessoas/:id navega por device_user_id (o contrato de GET /device-users nao
// devolve o SID) e a resolucao acontece no servidor, pelo par (tenant, id).
// ---------------------------------------------------------------------------

/** "anotacao" = contexto de um periodo; "contestacao" = discordancia da classificacao. */
export type PersonNoteKind = "anotacao" | "contestacao";

/** Nasce "aberta"; so o gestor (Admin+) move para "aceita" ou "recusada". */
export type PersonNoteStatus = "aberta" | "aceita" | "recusada";

/**
 * Uma anotacao ou contestacao. Os nomes de quem criou e de quem revisou vem JA
 * resolvidos do servidor (`created_by_name`/`reviewed_by_name`; null quando o
 * usuario nao esta mais no tenant) - renderize estes campos, nao cruze por id.
 *
 * `app_id`/`app_process_name`/`app_display_name` so existem na contestacao com
 * aplicativo: e o app cuja classificacao se contesta.
 */
export interface PersonNote {
  id: string;
  windows_sid: string;
  kind: PersonNoteKind;
  /** Inicio e fim do periodo anotado, ISO 8601 com fuso. */
  started_at: string;
  ended_at: string;
  app_id: string | null;
  app_process_name: string | null;
  app_display_name: string | null;
  body: string;
  status: PersonNoteStatus;
  created_by_user_id: string;
  created_by_name: string | null;
  created_at: string;
  reviewed_by_user_id: string | null;
  reviewed_by_name: string | null;
  reviewed_at: string | null;
  /** A resposta do gestor - obrigatoria na recusa, opcional na aceitacao. */
  review_note: string | null;
}

/** Resposta de `GET /people/{sid}/notes?from&to` - sem paginacao (uma pessoa, ate 92 dias). */
export interface PersonNotesResponse {
  items: PersonNote[];
}

/**
 * Corpo do `POST`. O status nao vai aqui: nasce sempre "aberta". `app_id` so e
 * aceito em kind "contestacao" (numa anotacao de periodo o servidor responde 400).
 */
export interface PersonNoteCreateRequest {
  kind: PersonNoteKind;
  started_at: string;
  ended_at: string;
  app_id?: string | null;
  body: string;
}

/**
 * Corpo do `PATCH` (Admin+): a revisao do gestor. So "aceita" ou "recusada" -
 * voltar para "aberta" nao existe, a revisao e um fato datado e assinado.
 * `review_note` e obrigatoria na recusa.
 */
export interface PersonNoteReviewRequest {
  status: Exclude<PersonNoteStatus, "aberta">;
  review_note?: string | null;
}

/**
 * Resposta do `PATCH`: a anotacao ja no estado novo mais `effect` - a frase do
 * SERVIDOR dizendo o que a decisao fez (e, no caso da contestacao aceita, o que
 * ela NAO fez: nenhum agregado muda). Exiba `effect` verbatim.
 */
export interface PersonNoteReviewResponse {
  note: PersonNote;
  effect: string;
}

// =============================================================================
// F5 + F7 - CLASSIFICACAO POR EQUIPE, EQUIPES, JORNADA E FERIADOS
// (spec secoes 2.3 e 2.4; fase F7 da secao 6)
//
// Duas nocoes de "equipe" coexistem de proposito no produto:
//  - a ETIQUETA livre de devices.tags, que continua sendo o filtro ?tag de
//    todos os dashboards, relatorios, timeline e exports - nada foi removido;
//  - a ENTIDADE `teams`, vinculada a PESSOA (windows_sid), que tem nome
//    canonico, jornada declarada e regra de classificacao propria.
// Onde as duas respondem, a equipe da PESSOA vence; a etiqueta e o atalho de
// migracao, e a equipe declara em `tag` qual etiqueta legada ela representa.
//
// Tipos A PARTE, no fim do arquivo: as fatias anteriores ja fecharam
// AppCatalogResponse/AppCategoryPutRequest acima e esta fatia nao os edita.
// =============================================================================

/** Escopo de uma regra de classificacao, como o backend o devolve. */
export type ClassificationScope = "team" | "organization";

/**
 * Item de `GET /app-catalog?team_id=` (F5). Mesmo formato de AppCatalogItem
 * mais `category_scope`, que diz de ONDE veio a categoria exibida:
 *   "team"         - a equipe consultada tem regra propria;
 *   "organization" - a regra e a geral, HERDADA (a equipe nao declarou nada);
 *   null           - nenhuma das duas: o app esta sem classificacao.
 * Sem `?team_id` so existem "organization" e null. A distincao importa na
 * tela: "herdado da organizacao" nao e a mesma coisa que "a equipe decidiu".
 */
export interface AppCatalogItemScoped {
  app_id: string;
  process_name: string;
  display_name: string;
  custom_display_name: string | null;
  category: { id: string; name: string; classification: number; color: string | null } | null;
  default_category: string | null;
  seconds_active_30d: number;
  device_count_30d: number;
  category_scope: ClassificationScope | null;
}

/** Resposta de `GET /app-catalog?team_id=` - cobertura ja pela regua EFETIVA. */
export interface AppCatalogScopedResponse {
  items: AppCatalogItemScoped[];
  uncategorized_count: number;
  uncategorized_seconds_active: number;
  total_seconds_active: number;
}

/**
 * Body de `PUT /app-catalog/{appId}/category` com ESCOPO (F5).
 * `team_id` ausente ou null = regra da ORGANIZACAO (o de sempre).
 * `team_id` preenchido = regra daquela EQUIPE, que vence a geral para as
 * pessoas dela; nesse escopo, `category_id: null` REMOVE so a regra da equipe
 * e o app volta a HERDAR a regra geral - nao vira "sem classificacao".
 * `custom_display_name` e da organizacao e e ignorado no escopo de equipe.
 */
export interface AppCategoryScopedPutRequest {
  category_id: string | null;
  custom_display_name?: string | null;
  team_id?: string | null;
}

/** Body de `PUT /app-catalog/categories/batch` com escopo de equipe (F5). */
export interface AppCategoryScopedBatchRequest {
  items: AppCategoryBatchItem[];
  team_id?: string | null;
}

/**
 * Uma equipe (`GET /api/v1/teams`). `work_hours` e a jornada DECLARADA pela
 * equipe (null = nao declarou); `effective_work_hours` e a que VALE - a da
 * equipe, ou a da organizacao quando `work_hours_inherited` e true. Ausencia
 * de jornada e HERANCA, nunca "sem jornada".
 */
export interface Team {
  id: string;
  name: string;
  /** Etiqueta legada de devices.tags que esta equipe representa; null = so por pessoa. */
  tag: string | null;
  work_hours: BusinessHours | null;
  effective_work_hours: BusinessHours | null;
  work_hours_inherited: boolean;
  member_count: number;
  /** Quantos apps tem regra de classificacao PROPRIA desta equipe. */
  team_rule_count: number;
}

export interface TeamListResponse {
  items: Team[];
  organization_work_hours: BusinessHours | null;
}

/** Body de POST/PATCH de equipe - no PATCH, campo ausente nao muda, null limpa. */
export interface TeamWriteRequest {
  name?: string;
  tag?: string | null;
  work_hours?: BusinessHours | null;
}

/** Uma pessoa vinculada a equipe; o nome ja vem resolvido pelo servidor. */
export interface TeamMember {
  windows_sid: string;
  display_name: string;
}

export interface TeamMembersResponse {
  team_id: string;
  name: string;
  items: TeamMember[];
}

/**
 * Resposta do `PUT /teams/{id}/members` (composicao DECLARATIVA: a lista
 * enviada passa a ser a composicao exata). Uma pessoa esta em no maximo UMA
 * equipe, entao enviar alguem que estava em outra equipe a MOVE.
 * `reaggregation_enqueued` sao os pares (dispositivo, dia) reenfileirados: a
 * equipe decide a regra de classificacao aplicada ao tempo daquelas pessoas.
 */
export interface SetTeamMembersResponse {
  team_id: string;
  items: TeamMember[];
  added: number;
  removed: number;
  reaggregation_enqueued: number;
}

/** Um feriado da organizacao. `date` e yyyy-MM-dd, como todo dia da API. */
export interface Holiday {
  date: string;
  name: string;
}

/**
 * `GET /organization/holidays?year=`. `suggestions` sao os pontos facultativos
 * federais do ano (Carnaval e Corpus Christi) que a semeadura NAO cria de
 * proposito: quem trabalha nesses dias nao pode ter o denominador da
 * capacidade furado por padrao; quem os observa adiciona com um clique.
 */
export interface HolidayListResponse {
  year: number;
  items: Holiday[];
  suggestions: Holiday[];
}

export interface SeedHolidaysResponse {
  years: number[];
  inserted: number;
}

/**
 * Base do denominador da CAPACIDADE UTILIZADA de um escopo no periodo:
 * ativo / (jornada declarada x dias uteis x pessoas).
 *
 * `business_days` JA vem sem os feriados - e essa a correcao da F7 (ate aqui o
 * cartao de equipes da Visao Geral contava feriado como dia util e mostrava
 * capacidade subestimada). `capacity_seconds_per_person` e o produto pronto:
 * multiplique pelo numero de pessoas e divida o tempo ativo.
 */
export interface CapacityScope {
  scope_type: "organization" | "team";
  team_id: string | null;
  scope_label: string;
  /** Etiqueta legada equivalente - permite casar com o `?tag` do overview. */
  tag: string | null;
  daily_hours: number;
  business_days: number;
  holidays_excluded: number;
  capacity_seconds_per_person: number;
  member_count: number;
}

/** `GET /teams/capacity?from&to` - a linha da organizacao mais uma por equipe. */
export interface CapacityResponse {
  from: string;
  to: string;
  scopes: CapacityScope[];
}
