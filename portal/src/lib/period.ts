// =============================================================================
// Período global das telas de análise (F6): Hoje / Esta semana / Este mês /
// Personalizado, resolvido SEMPRE no fuso da organização e vivendo na URL
// (?periodo=, ?de=, ?ate=) para o link reproduzir o recorte visível.
//
// A régua da comparação é a MESMA do backend (GET /dashboard/overview?compare):
// o período imediatamente anterior de igual duração. O portal não recalcula
// nada disso - só monta o rótulo.
//
// Semana = segunda a domingo (mesma convenção do resumo semanal e das metas).
// Mês = dia 1 até hoje. Nenhum preset olha para o futuro: o "até" nunca passa
// de hoje, porque a API não tem nada a dizer sobre amanhã.
// =============================================================================

import { addDays, isIsoDate, localDateOf, mondayOf } from "./format";
import type { UrlStateCodec } from "./useUrlState";

export type PeriodPreset = "dia" | "semana" | "mes" | "custom";

export interface PeriodState {
  preset: PeriodPreset;
  /** Só usado por "custom"; ignorado nos presets. */
  from: string | null;
  to: string | null;
}

export interface ResolvedPeriod {
  preset: PeriodPreset;
  from: string;
  to: string;
  /** Rótulo humano do recorte, já com o "até hoje" quando o período é parcial. */
  label: string;
}

/** Teto de janela dos endpoints históricos (mesmo do backend). */
export const MAX_PERIOD_DAYS = 92;

export const PERIOD_LABELS: Record<PeriodPreset, string> = {
  dia: "Hoje",
  semana: "Esta semana",
  mes: "Este mês",
  custom: "Personalizado",
};

export const DEFAULT_PERIOD: PeriodState = { preset: "semana", from: null, to: null };

/** ?periodo=dia|semana|mes|custom + ?de=&ate= (só no custom). Default sai da URL. */
export const PERIOD_CODEC: UrlStateCodec<PeriodState> = {
  parse: (params) => {
    const raw = params.get("periodo");
    const preset: PeriodPreset =
      raw === "dia" || raw === "semana" || raw === "mes" || raw === "custom" ? raw : "semana";
    const from = params.get("de");
    const to = params.get("ate");
    // O custom só sobrevive à URL se for um intervalo QUE A API ACEITA: datas
    // válidas, em ordem e dentro do teto de janela. Um link colado com 200 dias
    // viraria 400 no servidor, e um estado que só existe para falhar é pior do
    // que voltar ao default - a mesma escolha que este parser já faz com data
    // inválida.
    if (
      preset === "custom" &&
      from !== null &&
      to !== null &&
      isIsoDate(from) &&
      isIsoDate(to) &&
      from <= to &&
      daysBetweenInclusive(from, to) <= MAX_PERIOD_DAYS
    ) {
      return { preset, from, to };
    }
    return { preset: preset === "custom" ? "semana" : preset, from: null, to: null };
  },
  serialize: (value) => ({
    periodo: value.preset === "semana" ? null : value.preset,
    de: value.preset === "custom" ? value.from : null,
    ate: value.preset === "custom" ? value.to : null,
  }),
};

/** "9 de setembro" / "14/09 a 18/09" - rótulos de dia e de intervalo em pt-BR. */
function dayLabel(dateStr: string): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  return new Intl.DateTimeFormat("pt-BR", { weekday: "long", day: "2-digit", month: "long", timeZone: "UTC" })
    .format(new Date(Date.UTC(y, m - 1, d)));
}

function ddmmLabel(dateStr: string): string {
  const [, m, d] = dateStr.split("-");
  return `${d}/${m}`;
}

/**
 * Dias INCLUSIVOS do intervalo: de 14/09 a 14/09 são 1 dia, não 0. É a régua do
 * MAX_PERIOD_DAYS, então tem de contar igual ao backend (que valida o mesmo
 * teto sobre o intervalo fechado).
 */
export function daysBetweenInclusive(from: string, to: string): number {
  const [fy, fm, fd] = from.split("-").map(Number);
  const [ty, tm, td] = to.split("-").map(Number);
  // Aritmética em UTC: imune ao horário de verão do fuso do navegador.
  const diff = Date.UTC(ty, tm - 1, td) - Date.UTC(fy, fm - 1, fd);
  return Math.floor(diff / 86_400_000) + 1;
}

/** "14/09 - 18/09" (ou só "14/09" num único dia): rótulo curto de botão. */
export function shortRangeLabel(from: string, to: string): string {
  return from === to ? ddmmLabel(from) : `${ddmmLabel(from)} – ${ddmmLabel(to)}`;
}

/**
 * Todas as datas do intervalo, INCLUSIVE as sem dado. Os endpoints agregados
 * agrupam por dia e só devolvem os dias que têm linha, então o calendário do
 * gráfico por dia tem de ser construído aqui: sem isso, um dia de máquina
 * desligada desapareceria do eixo em vez de aparecer como lacuna - e "não
 * apareceu" leria como "não existiu".
 *
 * O laço para no teto de janela: nenhum recorte legítimo passa dele, e um `to`
 * corrompido não vira laço infinito.
 */
export function eachDayInclusive(from: string, to: string): string[] {
  const out: string[] = [];
  let cursor = from;
  for (let i = 0; cursor <= to && i < MAX_PERIOD_DAYS; i += 1) {
    out.push(cursor);
    cursor = addDays(cursor, 1);
  }
  return out;
}

/**
 * Resolve o período no fuso da organização. `timezone` null (o /me ainda não
 * respondeu) devolve null: nenhuma consulta deve sair sem fuso resolvido, senão
 * o dia do navegador vaza para o recorte.
 */
export function resolvePeriod(state: PeriodState, timezone: string | null): ResolvedPeriod | null {
  if (timezone === null) return null;
  const today = localDateOf(new Date(), timezone);

  if (state.preset === "custom" && state.from !== null && state.to !== null) {
    const to = state.to > today ? today : state.to;
    const from = state.from > to ? to : state.from;
    return { preset: "custom", from, to, label: `${ddmmLabel(from)} a ${ddmmLabel(to)}` };
  }

  if (state.preset === "dia") {
    return { preset: "dia", from: today, to: today, label: dayLabel(today) };
  }

  if (state.preset === "mes") {
    const first = `${today.slice(0, 7)}-01`;
    const mesLabel = new Intl.DateTimeFormat("pt-BR", { month: "long", year: "numeric", timeZone: "UTC" })
      .format(new Date(`${first}T00:00:00Z`));
    return {
      preset: "mes",
      from: first,
      to: today,
      label: first === today ? `${mesLabel}, primeiro dia` : `${mesLabel}, até ${ddmmLabel(today)}`,
    };
  }

  const monday = mondayOf(today);
  const sunday = addDays(monday, 6);
  const to = sunday > today ? today : sunday;
  return {
    preset: "semana",
    from: monday,
    to,
    label:
      to === sunday
        ? `semana de ${ddmmLabel(monday)} a ${ddmmLabel(sunday)}`
        : `semana de ${ddmmLabel(monday)} a ${ddmmLabel(sunday)}, até ${ddmmLabel(to)}`,
  };
}

/** Rótulo da base de comparação, no vocabulário fixo das telas. */
export function comparisonLabel(preset: PeriodPreset): string {
  if (preset === "dia") return "vs ontem";
  if (preset === "semana") return "vs semana anterior";
  if (preset === "mes") return "vs período anterior";
  return "vs período anterior";
}

/** Query string comum das telas de análise: período + recorte de equipe. */
export function periodQuery(period: ResolvedPeriod, tag: string | null, compare = false): string {
  const parts = [`from=${period.from}`, `to=${period.to}`];
  if (tag !== null && tag !== "") parts.push(`tag=${encodeURIComponent(tag)}`);
  if (compare) parts.push("compare=true");
  return parts.join("&");
}

// ----------------------------------------------------------------- formatação

/** Fração 0..1 -> "78%"; null -> "–" (sem dado NUNCA vira 0%). */
export function formatPct(value: number | null, fractionDigits = 0): string {
  if (value === null) return "–";
  return `${(value * 100).toFixed(fractionDigits)}%`;
}

/** Segundos -> "1.189 h" para números grandes de KPI (horas inteiras). */
export function formatHours(seconds: number): string {
  return Math.round(seconds / 3600).toLocaleString("pt-BR");
}

/**
 * Variação percentual entre dois totais. Devolve null quando não há base -
 * "sem base" é uma leitura honesta, "+100%" não seria.
 */
export function deltaPct(current: number, previous: number | null | undefined): number | null {
  if (previous === null || previous === undefined || previous === 0) return null;
  return (current - previous) / previous;
}

/** Variação em PONTOS de um indicador que já é percentual (índice, cobertura). */
export function deltaPoints(current: number | null, previous: number | null | undefined): number | null {
  if (current === null || previous === null || previous === undefined) return null;
  return Math.round((current - previous) * 100);
}
