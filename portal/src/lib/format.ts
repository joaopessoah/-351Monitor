import type { Role } from "./types";

export const roleLabels: Record<Role, string> = {
  owner: "Proprietário",
  admin: "Administrador",
  viewer: "Visualizador",
};

/** Badge de fuso da topbar, ex.: "Horários em GMT-3 · São Paulo". */
export function timezoneBadge(timezone: string): string {
  let offset = "";
  try {
    const parts = new Intl.DateTimeFormat("pt-BR", {
      timeZone: timezone,
      timeZoneName: "shortOffset",
    }).formatToParts(new Date());
    offset = parts.find((p) => p.type === "timeZoneName")?.value ?? "";
  } catch {
    offset = "";
  }
  const city = (timezone.split("/").pop() ?? timezone).replace(/_/g, " ");
  return offset.length > 0 ? `Horários em ${offset} · ${city}` : `Horários em ${city}`;
}

/** Duração no padrão da spec (Seção 8): "6h 40min", "12min", "45s" — nunca decimal. */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  if (s < 60) return `${s}s`;
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (h === 0) return `${m}min`;
  return m === 0 ? `${h}h` : `${h}h ${m.toString().padStart(2, "0")}min`;
}

/** Hora local do tenant em HH:mm (24h). */
export function formatHm(iso: string, timezone: string): string {
  return new Intl.DateTimeFormat("pt-BR", {
    timeZone: timezone,
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).format(new Date(iso));
}

/** "há 32s" / "há 5min" / "há 2h" — relativo a um relógio de referência (server_time). */
export function formatRelative(iso: string, referenceIso: string): string {
  const diff = Math.max(0, (new Date(referenceIso).getTime() - new Date(iso).getTime()) / 1000);
  if (diff < 60) return `há ${Math.round(diff)}s`;
  if (diff < 3600) return `há ${Math.floor(diff / 60)}min`;
  if (diff < 86400) return `há ${Math.floor(diff / 3600)}h`;
  return `há ${Math.floor(diff / 86400)}d`;
}

/** Rótulos e cores canônicos dos estados (Seção 8.5) — vocabulário NEUTRO obrigatório. */
export const stateLabels: Record<string, string> = {
  active: "Ativo",
  idle: "Ocioso",
  locked: "Bloqueado",
  no_session: "Sem usuário",
  off_clean: "Desligada/suspensa",
  no_data: "Sem comunicação",
};

/** "GMT-4", "GMT+5:30" a partir do offset em minutos (badge de fuso divergente). */
export function gmtLabel(offsetMin: number): string {
  const sign = offsetMin < 0 ? "-" : "+";
  const abs = Math.abs(offsetMin);
  const h = Math.floor(abs / 60);
  const m = abs % 60;
  return m === 0 ? `GMT${sign}${h}` : `GMT${sign}${h}:${String(m).padStart(2, "0")}`;
}

/** "08:00" → minutos desde 00:00 (480); null para valores malformados. */
export function parseHmToMinutes(hm: string): number | null {
  const match = /^(\d{1,2}):(\d{2})$/.exec(hm);
  if (match === null) return null;
  const h = Number(match[1]);
  const m = Number(match[2]);
  if (h > 23 || m > 59) return null;
  return h * 60 + m;
}

/** Data local (yyyy-MM-dd) de um instante no fuso do tenant. */
export function localDateOf(date: Date, timezone: string): string {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: timezone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(date);
  return parts;
}

/** Soma `days` a uma data local yyyy-MM-dd (aritmética em UTC, imune a DST local). */
export function addDays(dateStr: string, days: number): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d + days));
  const mm = String(dt.getUTCMonth() + 1).padStart(2, "0");
  const dd = String(dt.getUTCDate()).padStart(2, "0");
  return `${dt.getUTCFullYear()}-${mm}-${dd}`;
}

/**
 * Segunda-feira (yyyy-MM-dd) da semana que contém `dateStr` — semana segunda a
 * domingo, a MESMA convenção dos gráficos da Visão Geral. Combine com addDays
 * para o fim da semana (addDays(mondayOf(hoje), 6)) ou para semanas anteriores
 * (addDays(mondayOf(hoje), -7)). A data de entrada precisa ser calculada no
 * fuso da ORGANIZAÇÃO (localDateOf), nunca no fuso do navegador.
 */
export function mondayOf(dateStr: string): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  const dow = new Date(Date.UTC(y, m - 1, d)).getUTCDay(); // 0 = domingo
  return addDays(dateStr, -((dow + 6) % 7));
}

/** "09/06" a partir de yyyy-MM-dd. */
export function ddmm(dateStr: string): string {
  const [, m, d] = dateStr.split("-");
  return `${d}/${m}`;
}

export function isIsoDate(s: string): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(s);
}

/** "seg", "ter", "sáb"... dia da semana abreviado pt-BR de uma data yyyy-MM-dd. */
export function weekdayShort(dateStr: string): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  const label = new Intl.DateTimeFormat("pt-BR", { weekday: "short", timeZone: "UTC" }).format(
    new Date(Date.UTC(y, m - 1, d)),
  );
  return label.replace(/\.$/, "");
}

/** "10/06 14:32" de um instante ISO no fuso do tenant. */
export function formatDayMonthTime(iso: string, timezone: string): string {
  const parts = new Intl.DateTimeFormat("pt-BR", {
    timeZone: timezone,
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).formatToParts(new Date(iso));
  const get = (type: string): string => parts.find((p) => p.type === type)?.value ?? "";
  return `${get("day")}/${get("month")} ${get("hour")}:${get("minute")}`;
}

/** "10/06/2026 14:32" (dd/mm/aaaa HH:mm) de um instante ISO no fuso do tenant. */
export function formatDateTime(iso: string, timezone: string): string {
  const parts = new Intl.DateTimeFormat("pt-BR", {
    timeZone: timezone,
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).formatToParts(new Date(iso));
  const get = (type: string): string => parts.find((p) => p.type === type)?.value ?? "";
  return `${get("day")}/${get("month")}/${get("year")} ${get("hour")}:${get("minute")}`;
}
