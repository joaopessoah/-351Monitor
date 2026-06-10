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
