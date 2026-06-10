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

/** Item de `GET /devices`. */
export interface DeviceItem {
  id: string;
  hostname: string;
  display_name: string | null;
  os_type: string | null;
  os_version: string | null;
  agent_version: string | null;
  status: "active" | "paused" | "archived" | "revoked";
  tags: string[];
  last_seen_at: string | null;
  tz_offset_min: number | null;
  clock_offset_ms: number;
}

export interface PagedResponse<T> {
  items: T[];
  total: number;
  page: number;
  page_size: number;
}
