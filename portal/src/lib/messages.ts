import { ApiError } from "./api";

// Mensagens de erro pt-BR — sempre genéricas, sem vazar detalhes técnicos.

export const GENERIC_ERROR = "Não foi possível concluir a operação. Tente novamente.";
export const NETWORK_ERROR =
  "Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.";
export const LOCKOUT_ERROR =
  "Acesso temporariamente bloqueado por excesso de tentativas. Aguarde 15 minutos e tente novamente.";

export function loginErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    // Lockout N22: o backend responde 401 com extensão code=account_locked
    // (mensagem genérica preservada — não revela se a conta existe).
    if (err.problem?.code === "account_locked" || err.status === 423 || err.status === 429) {
      return LOCKOUT_ERROR;
    }
    if (err.status === 401) return "E-mail ou senha inválidos.";
    return GENERIC_ERROR;
  }
  return NETWORK_ERROR;
}

export function mfaErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 400 || err.status === 401 || err.status === 422) {
      return "Código inválido ou expirado. Confira o código no seu aplicativo autenticador.";
    }
    if (err.status === 423 || err.status === 429) return LOCKOUT_ERROR;
    return GENERIC_ERROR;
  }
  return NETWORK_ERROR;
}

export function genericErrorMessage(err: unknown): string {
  if (err instanceof ApiError) return GENERIC_ERROR;
  return NETWORK_ERROR;
}
