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
