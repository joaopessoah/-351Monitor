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
