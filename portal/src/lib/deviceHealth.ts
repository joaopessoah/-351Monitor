// =============================================================================
// F4.4 — Saúde dos agentes (painel em /dispositivos). Deriva, por device, as
// dimensões de saúde que respondem "quais máquinas pararam de reportar?"
// (objetivo do produto): sem comunicação (N6 = 180 s; destaque se > 30 min em
// horário de trabalho, igual ao banner global da Seção 8.1), relógio
// dessincronizado (|offset| > 120 s, Seção 8.7), versão desatualizada
// (agent_outdated computado no BACKEND via SEMVER), adulteração (AGENT_TAMPER
// nos últimos 7 dias) e ciência pendente (notice_acked_at null).
//
// O SEMVER do agent_outdated é resolvido no backend (campo booleano do
// DeviceResponse) — o portal NUNCA compara versões.
// =============================================================================

import type { BusinessHours, DeviceItem } from "./types";
import { parseHmToMinutes } from "./format";

/** N6: contato > 180 s sem desligamento limpo = "Sem comunicação". */
export const OFFLINE_LIMIT_MS = 180_000;
/** Seção 8.7: |offset| > 2 min = relógio dessincronizado. */
export const CLOCK_SKEW_LIMIT_MS = 120_000;
/** Banner global (Seção 8.1): sem comunicação há > 30 min em horário de trabalho. */
export const OFFLINE_SEVERE_MS = 30 * 60_000;
/** raw_events expira em 90 dias (N10); só destacamos adulteração recente. */
export const TAMPER_WINDOW_MS = 7 * 24 * 60 * 60_000;

/** Razões de AGENT_TAMPER (N19) traduzidas para o vocabulário neutro do portal. */
export const tamperReasonLabels: Record<string, string> = {
  helper_killed: "Helper encerrado",
  helper_killed_repeatedly: "Helper encerrado repetidamente",
  pipe_denied: "Acesso ao canal negado",
};

/** Rótulo do reason de tamper; reason desconhecido cai num texto neutro. */
export function tamperReasonLabel(reason: string | null): string {
  if (reason === null) return "Adulteração detectada";
  return tamperReasonLabels[reason] ?? "Adulteração detectada";
}

/**
 * Etapas do UPDATE_FAILED em texto de gestor: cada uma leva a uma ação
 * diferente, e é justamente essa diferença que o contador "desatualizados"
 * não conseguia mostrar.
 */
export const updateFailureReasonLabels: Record<string, string> = {
  download: "Falha no download do instalador",
  hash: "Instalador com conteúdo divergente do publicado",
  signature: "Assinatura do instalador recusada",
  install: "Instalação não pôde ser iniciada",
};

/** Rótulo da etapa de falha de atualização; etapa desconhecida cai num texto neutro. */
export function updateFailureReasonLabel(reason: string | null): string {
  if (reason === null) return "Falha ao atualizar";
  return updateFailureReasonLabels[reason] ?? "Falha ao atualizar";
}

/**
 * "Está em horário de trabalho AGORA?" no fuso da organização. Usado só para
 * decidir o REALCE do "sem comunicação" (> 30 min) — fora do expediente uma
 * máquina silenciosa é esperada. Sem business_hours configurado, considera
 * sempre dentro (não suprime o alerta).
 */
export function isWithinBusinessHours(
  businessHours: BusinessHours | null,
  timezone: string | undefined,
  now: Date = new Date(),
): boolean {
  if (businessHours === null || timezone === undefined) return true;
  const start = parseHmToMinutes(businessHours.start);
  const end = parseHmToMinutes(businessHours.end);
  if (start === null || end === null || end <= start) return true;

  // Dia da semana ISO (1 = segunda … 7 = domingo) e minutos do dia, no fuso da org.
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: timezone,
    weekday: "short",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).formatToParts(now);
  const get = (type: string): string => parts.find((p) => p.type === type)?.value ?? "";
  const isoByWeekday: Record<string, number> = {
    Mon: 1,
    Tue: 2,
    Wed: 3,
    Thu: 4,
    Fri: 5,
    Sat: 6,
    Sun: 7,
  };
  const isoDay = isoByWeekday[get("weekday")];
  if (isoDay === undefined || !businessHours.days.includes(isoDay)) return false;

  const hour = Number(get("hour"));
  const minute = Number(get("minute"));
  if (Number.isNaN(hour) || Number.isNaN(minute)) return true;
  const minutesNow = (hour % 24) * 60 + minute;
  return minutesNow >= start && minutesNow < end;
}

/** Resultado da derivação de saúde de UM device. */
export interface DeviceHealth {
  /** Sem comunicação (N6: > 180 s ou nunca contatou). */
  offline: boolean;
  /** Sem comunicação há > 30 min E em horário de trabalho agora (realce vermelho). */
  offlineSevere: boolean;
  /** |clock_offset_ms| > 120 s. */
  clockSkewed: boolean;
  /** agent_outdated do backend (SEMVER < min_version do release vigente). */
  outdated: boolean;
  /** AGENT_TAMPER nos últimos 7 dias. */
  tampered: boolean;
  /** notice_acked_at null (ciência do aviso de coleta pendente). */
  noticePending: boolean;
  /** Qualquer dimensão de saúde acima acionada. */
  hasAlert: boolean;
}

/**
 * Deriva a saúde de um device contra um relógio de referência (server_time da
 * presença, nunca o relógio local) e o horário de trabalho da organização.
 */
export function deriveDeviceHealth(
  device: DeviceItem,
  referenceIso: string,
  businessHours: BusinessHours | null,
  timezone: string | undefined,
): DeviceHealth {
  const refMs = new Date(referenceIso).getTime();
  const lastSeenMs = device.last_seen_at !== null ? new Date(device.last_seen_at).getTime() : null;
  const sinceLastSeen = lastSeenMs !== null ? Math.max(0, refMs - lastSeenMs) : null;

  // Nunca contatou também conta como sem comunicação.
  const offline = sinceLastSeen === null || sinceLastSeen > OFFLINE_LIMIT_MS;
  const offlineSevere =
    offline &&
    (sinceLastSeen === null || sinceLastSeen > OFFLINE_SEVERE_MS) &&
    isWithinBusinessHours(businessHours, timezone, new Date(refMs));

  const clockSkewed = Math.abs(device.clock_offset_ms) > CLOCK_SKEW_LIMIT_MS;
  const outdated = device.agent_outdated;

  const tampered =
    device.last_tamper_at !== null &&
    refMs - new Date(device.last_tamper_at).getTime() <= TAMPER_WINDOW_MS;

  const noticePending = device.notice_acked_at === null;

  return {
    offline,
    offlineSevere,
    clockSkewed,
    outdated,
    tampered,
    noticePending,
    hasAlert: offline || clockSkewed || outdated || tampered || noticePending,
  };
}
