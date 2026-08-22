import { ApiError } from "./api";

// Mensagens de erro pt-BR — sempre genéricas, sem vazar detalhes técnicos.

export const GENERIC_ERROR = "Não foi possível concluir a operação. Tente novamente.";

/**
 * Disclaimer da Portaria 671/MTE (DoD 11.3), VERBATIM. Obrigatório em toda tela
 * que mostre tempos de uma pessoa por dia: o relatório de Jornada
 * (pages/relatorios/JornadaPage.tsx, onde o texto está inline no banner) e a
 * visão individual da pessoa. O mesmo texto é a última linha de todo CSV de
 * jornada (backend ExportService.JornadaDisclaimer) - os três precisam bater
 * palavra por palavra.
 */
export const JORNADA_DISCLAIMER =
  "Relatório gerencial de uso da estação de trabalho. Não constitui registro eletrônico de " +
  "ponto (Portaria 671/MTE) e não substitui o controle de jornada do art. 74 da CLT.";
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

/**
 * Mensagem do ProblemDetails do backend (detail, senão title) quando ela
 * existe - as regras de negócio respondem em pt-BR pronto para exibição
 * (ex.: 409 "A organização precisa de pelo menos um Owner ativo."). Sem
 * problem legível, cai na mensagem genérica/de rede.
 */
export function problemErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    const text = err.problem?.detail ?? err.problem?.title;
    if (text !== undefined && text.trim().length > 0) return text;
    return GENERIC_ERROR;
  }
  return NETWORK_ERROR;
}
