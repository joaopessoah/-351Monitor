import type { ApiProblem } from "./types";

const API_BASE = "/api/v1";

// Access token mantido EM MEMÓRIA (nunca em localStorage). A sessão sobrevive a
// reload via refresh cookie httpOnly (POST /auth/refresh).
let accessToken: string | null = null;

export function setAccessToken(token: string | null): void {
  accessToken = token;
}

export function getAccessToken(): string | null {
  return accessToken;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ApiProblem | null;

  constructor(status: number, problem: ApiProblem | null, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
  }
}

// Uma única tentativa de refresh por vez: requisições que tomarem 401
// simultaneamente aguardam a MESMA promise (fila implícita), evitando
// tempestade de chamadas a /auth/refresh.
let refreshPromise: Promise<boolean> | null = null;

export function refreshAccessToken(): Promise<boolean> {
  if (refreshPromise === null) {
    refreshPromise = doRefresh().finally(() => {
      refreshPromise = null;
    });
  }
  return refreshPromise;
}

async function doRefresh(): Promise<boolean> {
  try {
    const res = await fetch(`${API_BASE}/auth/refresh`, {
      method: "POST",
      credentials: "include",
      headers: { Accept: "application/json" },
    });
    if (!res.ok) {
      accessToken = null;
      return false;
    }
    const data = (await res.json()) as { access_token?: string };
    if (typeof data.access_token !== "string" || data.access_token.length === 0) {
      accessToken = null;
      return false;
    }
    accessToken = data.access_token;
    return true;
  } catch {
    return false;
  }
}

async function parseProblem(res: Response): Promise<ApiProblem | null> {
  try {
    const text = await res.text();
    if (text.length === 0) return null;
    return JSON.parse(text) as ApiProblem;
  } catch {
    return null;
  }
}

export interface ApiOptions {
  method?: string;
  body?: unknown;
  /**
   * true (default): envia Authorization: Bearer e, em 401, tenta UM refresh e
   * repete a requisição uma única vez. false: endpoint público (login, convite...).
   */
  auth?: boolean;
  /**
   * Token Bearer explícito (ex.: mfa_token temporário do login) — enviado no
   * Authorization no lugar do access token em memória, SEM retry de refresh.
   */
  bearerToken?: string;
  signal?: AbortSignal;
}

export async function api<T>(path: string, options: ApiOptions = {}): Promise<T> {
  const { method = "GET", body, auth = true, bearerToken, signal } = options;

  const doFetch = (): Promise<Response> => {
    const headers: Record<string, string> = { Accept: "application/json" };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (bearerToken !== undefined) {
      headers["Authorization"] = `Bearer ${bearerToken}`;
    } else if (auth && accessToken !== null) {
      headers["Authorization"] = `Bearer ${accessToken}`;
    }
    return fetch(`${API_BASE}${path}`, {
      method,
      headers,
      credentials: "include",
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal,
    });
  };

  let res = await doFetch();

  if (res.status === 401 && auth && bearerToken === undefined) {
    const refreshed = await refreshAccessToken();
    if (refreshed) {
      res = await doFetch();
    }
  }

  if (!res.ok) {
    const problem = await parseProblem(res);
    throw new ApiError(res.status, problem, problem?.detail ?? problem?.title ?? `HTTP ${res.status}`);
  }

  if (res.status === 204) {
    return undefined as T;
  }
  return (await res.json()) as T;
}
