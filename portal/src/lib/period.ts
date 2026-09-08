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

export type PeriodPreset = "dia" | "semana" | "mes" | "trimestre" | "ano" | "custom";

/**
 * Grão da série. "month" (F9) lê de monthly_summaries e admite 24 meses onde o
 * diário admite 92 dias — é o que torna trimestre e ano possíveis. Nem todo
 * bloco da tela sabe responder no grão mensal: quem depende de hora ou de
 * aplicativo continua sendo diário, e a tela ESCONDE esses blocos em vez de
 * pedir ao servidor uma janela que ele vai recusar.
 */
export type PeriodGrain = "day" | "month";

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
  grain: PeriodGrain;
}

/** Teto de janela dos endpoints históricos no grão diário (mesmo do backend). */
export const MAX_PERIOD_DAYS = 92;

/** Teto no grão MENSAL: 24 meses, que é a retenção dos agregados (mesmo do backend). */
export const MAX_PERIOD_MONTHS = 24;

export const PERIOD_LABELS: Record<PeriodPreset, string> = {
  dia: "Hoje",
  semana: "Esta semana",
  mes: "Este mês",
  trimestre: "3 meses",
  ano: "12 meses",
  custom: "Personalizado",
};

/** Os presets do grão diário: os únicos que toda tela sabe atender. */
export const SHORT_PRESETS: PeriodPreset[] = ["dia", "semana", "mes"];

/** Presets de janela longa — só a Visão Geral os oferece (ver PERIOD_CODEC_LONG). */
export const LONG_PRESETS: PeriodPreset[] = ["trimestre", "ano"];

/** Grão de um preset. Só os longos são mensais. */
export function grainOf(preset: PeriodPreset): PeriodGrain {
  return preset === "trimestre" || preset === "ano" ? "month" : "day";
}

export const DEFAULT_PERIOD: PeriodState = { preset: "semana", from: null, to: null };

/**
 * ?periodo=dia|semana|mes|custom + ?de=&ate= (só no custom). Default sai da URL.
 *
 * Este codec NÃO aceita os presets longos, de propósito: um link com
 * ?periodo=ano colado numa tela que só fala grão diário pediria 12 meses a um
 * endpoint com teto de 92 dias e receberia 400. Cair no default é melhor do que
 * abrir quebrado. Quem sabe atender janela longa usa PERIOD_CODEC_LONG.
 */
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

/**
 * Igual ao PERIOD_CODEC, mais os presets de janela longa (grão mensal, F9). Só
 * a Visão Geral usa este: é a única tela que sabe o que fazer quando o grão
 * muda — ela esconde os blocos que só existem no grão diário em vez de pedir ao
 * servidor uma janela que ele vai recusar.
 */
export const PERIOD_CODEC_LONG: UrlStateCodec<PeriodState> = {
  parse: (params) => {
    const raw = params.get("periodo");
    if (raw === "trimestre" || raw === "ano") {
      // presets longos não têm de/até: a janela sai do calendário, como os demais
      return { preset: raw, from: null, to: null };
    }
    return PERIOD_CODEC.parse(params);
  },
  serialize: PERIOD_CODEC.serialize,
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
    return { preset: "custom", from, to, label: `${ddmmLabel(from)} a ${ddmmLabel(to)}`, grain: "day" };
  }

  if (state.preset === "dia") {
    return { preset: "dia", from: today, to: today, label: dayLabel(today), grain: "day" };
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
      grain: "day",
    };
  }

  // JANELAS LONGAS (F9): começam no primeiro dia do mês de N−1 meses atrás e
  // terminam hoje, então o mês corrente entra PARCIAL — dizer isso no rótulo é
  // obrigatório, porque um mês pela metade ao lado de meses inteiros pareceria
  // uma queda de produção se ninguém avisasse.
  if (state.preset === "trimestre" || state.preset === "ano") {
    const meses = state.preset === "trimestre" ? 3 : 12;
    const from = firstDayOfMonthsAgo(today, meses - 1);
    return {
      preset: state.preset,
      from,
      to: today,
      label: `últimos ${meses} meses, até ${ddmmLabel(today)} (mês corrente parcial)`,
      grain: "month",
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
    grain: "day",
  };
}

/**
 * Primeiro dia do mês N meses antes da data dada. Aritmética em meses ABSOLUTOS
 * (ano × 12 + mês) em vez de Date: `new Date(y, m - n)` com fuso do navegador
 * escorrega de dia na virada do horário de verão, e aqui a data é um recorte do
 * fuso da ORGANIZAÇÃO, que o navegador não conhece.
 */
function firstDayOfMonthsAgo(dateStr: string, monthsBack: number): string {
  const [year, month] = dateStr.split("-").map(Number);
  const absolute = year * 12 + (month - 1) - monthsBack;
  const y = Math.floor(absolute / 12);
  const m = absolute - y * 12 + 1;
  return `${String(y).padStart(4, "0")}-${String(m).padStart(2, "0")}-01`;
}

/** Todos os primeiros-de-mês do intervalo, inclusive os sem dado (eixo completo). */
export function eachMonthInclusive(from: string, to: string): string[] {
  const out: string[] = [];
  let cursor = `${from.slice(0, 7)}-01`;
  const last = `${to.slice(0, 7)}-01`;
  for (let i = 0; cursor <= last && i < MAX_PERIOD_MONTHS; i += 1) {
    out.push(cursor);
    cursor = firstDayOfMonthsAgo(cursor, -1);
  }
  return out;
}

/** "set/26" — rótulo curto de mês para o eixo do gráfico. */
export function monthLabel(monthStart: string): string {
  return new Intl.DateTimeFormat("pt-BR", { month: "short", year: "2-digit", timeZone: "UTC" })
    .format(new Date(`${monthStart.slice(0, 7)}-01T00:00:00Z`))
    .replace(".", "");
}

/** Rótulo da base de comparação, no vocabulário fixo das telas. */
export function comparisonLabel(preset: PeriodPreset): string {
  if (preset === "dia") return "vs ontem";
  if (preset === "semana") return "vs semana anterior";
  if (preset === "mes") return "vs período anterior";
  return "vs período anterior";
}

/**
 * Query string comum das telas de análise: período + recorte de equipe.
 * O grão só viaja quando é mensal — assim toda chamada existente continua byte a
 * byte a mesma, e a queryKey do cache não muda para quem não pediu janela longa.
 */
export function periodQuery(period: ResolvedPeriod, tag: string | null, compare = false): string {
  const parts = [`from=${period.from}`, `to=${period.to}`];
  if (tag !== null && tag !== "") parts.push(`tag=${encodeURIComponent(tag)}`);
  if (compare) parts.push("compare=true");
  if (period.grain === "month") parts.push("grain=month");
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
