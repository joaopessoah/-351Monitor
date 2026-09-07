// =============================================================================
// Nível MÊS da Linha do Tempo (F4, Seção 3 do spec de 07/09/2026): mapa de
// calor de UMA linha por colaborador, uma célula por DIA, ordem ALFABÉTICA
// (decisão 2: sem ranking, nunca ordenado por índice aqui).
//
// A célula é o DIA - a granularidade do mockup aprovado. Ela só ficou possível
// com `GET /people/daily`, que devolve a quebra (pessoa, dia) do banco: o mês
// inteiro sai em UMA consulta, contra a ordem de 150 que custaria chamar a
// listagem agregada de /people um dia por vez.
//
// ESCALA DE COR: rampa de UM tom (o verde de atividade da marca, do escuro ao
// claro). Um tom só é a redundância não-cromática do mapa: quem não distingue
// matiz continua lendo a LUMINÂNCIA, e o número exato vive no title/aria-label
// de cada célula e no fallback tabular (os mesmos números, sempre).
//
// AUSÊNCIA DE DADO NUNCA É VALOR BAIXO: dia sem nenhum registro (fim de semana,
// folga, máquina desligada) sai vazio com hachura leve a 45°; dia com dado mas
// SEM tempo classificado (índice null) sai cinza-neutro com contorno pontilhado.
// Nenhum dos dois entra na rampa - pintar "sem dado" de verde escuro leria como
// desempenho ruim, que é afirmação diferente de "não sei".
// =============================================================================

import type { CSSProperties } from "react";
import { BRAND } from "@/lib/brandTheme";
import { ddmm, formatDuration } from "@/lib/format";
import { formatHours, formatPct } from "@/lib/period";
import type { PersonDay, PersonRow } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Skeleton } from "@/components/ui/skeleton";

/** Uma pessoa no mapa: o total do mês + uma célula por DIA (null = sem dado). */
export interface MonthPersonRow {
  sid: string;
  displayName: string;
  /** Agregado do mês inteiro, como o servidor calculou (índice incluído). */
  month: PersonRow;
  /** Mesma ordem de `days`; null quando a pessoa não tem registro naquele dia. */
  cells: (PersonDay | null)[];
}

/**
 * Rampa sequencial de um tom só (escuro -> claro), terminando no verde de
 * atividade da marca. Espelha a rampa do mockup aprovado; alterar aqui exige
 * alterar a legenda "menor -> maior" (ela lê deste array).
 */
export const HEAT_RAMP = ["#1F3A20", "#3A6A26", "#5F9C33", "#83C73F", BRAND.vizProdutivo] as const;

/** Índice 0..1 -> degrau da rampa. Só é chamada com índice não-null. */
function heatColor(index: number): string {
  const step = Math.min(Math.max(Math.floor(index * HEAT_RAMP.length), 0), HEAT_RAMP.length - 1);
  return HEAT_RAMP[step];
}

/** Hachura leve a 45° da célula SEM DADO - nunca pintada como índice baixo. */
const emptyHatch: CSSProperties = {
  backgroundImage: `repeating-linear-gradient(45deg, ${BRAND.line} 0 1.5px, transparent 1.5px 4px)`,
};

// 23×19 é a geometria do mockup: 31 colunas + a coluna de nome couberam sem
// rolagem horizontal no notebook de referência (a rolagem segue disponível).
const CELL_W = 23;
const CELL_H = 19;
const NAME_W = 176;

const dowFormat = new Intl.DateTimeFormat("pt-BR", { weekday: "long", timeZone: "UTC" });

/** "quinta-feira" - dia da semana de um yyyy-MM-dd (aritmética em UTC, sem fuso). */
function weekday(date: string): string {
  return dowFormat.format(new Date(`${date}T00:00:00Z`));
}

/** true no sábado e no domingo: usado só para esmaecer o cabeçalho da coluna. */
function isWeekend(date: string): boolean {
  const dow = new Date(`${date}T00:00:00Z`).getUTCDay();
  return dow === 0 || dow === 6;
}

/** "03" - número do dia, o rótulo curto da coluna (o mês está no título do card). */
function dayNumber(date: string): string {
  return date.slice(8, 10);
}

/**
 * OBSERVAÇÃO da célula: a linha que explica o que a cor NÃO está dizendo.
 * Sem dado e sem classificação são estados distintos e ambos ficam fora da
 * rampa; com dado, a observação lembra que ocioso não é improdutivo (ele não
 * entra no índice, que só divide tempo classificado).
 */
function cellNote(day: PersonDay | null): string {
  if (day === null) return "Sem dados neste dia (fim de semana, folga ou máquina desligada)";
  if (day.productivity_index === null) return "Sem tempo classificado - índice não calculado";
  return `Ocioso ${formatDuration(day.seconds_idle)} - tempo ocioso não é improdutivo`;
}

/** Texto único do tooltip/aria: ligada, ativa, índice e a observação. */
function cellTitle(name: string, date: string, day: PersonDay | null): string {
  const head = `${name} · ${ddmm(date)} (${weekday(date)})`;
  if (day === null) return `${head}\n${cellNote(day)}`;
  return (
    `${head}\nLigada ${formatDuration(day.seconds_on)} · Ativa ${formatDuration(day.seconds_active)}` +
    `\nÍndice ${formatPct(day.productivity_index)}\n${cellNote(day)}`
  );
}

export interface MonthHeatmapProps {
  /** Dias do mês já recortados por hoje (yyyy-MM-dd, em ordem). */
  days: string[];
  rows: MonthPersonRow[];
  /** Clique na célula: abre o nível DIA naquele dia exato. */
  onSelectDay: (date: string) => void;
}

/**
 * Mapa de calor pessoa × DIA. Cada célula é um botão: clique (ou Enter no foco)
 * abre o nível DIA na data da própria célula - não há mais aproximação, porque
 * a célula e o dia aberto são a mesma coisa.
 */
export function MonthHeatmap({ days, rows, onSelectDay }: MonthHeatmapProps) {
  return (
    <div className="overflow-x-auto">
      <table className="border-separate text-xs" style={{ borderSpacing: 3 }}>
        <caption className="sr-only">
          Índice de produtividade por colaborador e dia. Os números exatos estão no fallback
          tabular.
        </caption>
        <thead>
          <tr>
            <th scope="col" className="pb-1 pr-3 text-left font-medium text-muted-foreground" style={{ width: NAME_W }}>
              Colaborador
            </th>
            {days.map((date) => (
              <th
                key={date}
                scope="col"
                title={`${ddmm(date)} · ${weekday(date)}`}
                className={cn(
                  "pb-1 text-center text-[10.5px] font-medium tabular-nums text-muted-foreground",
                  // fim de semana esmaecido: dá a forma da semana sem inventar
                  // rótulo nem cor nova numa linha de 31 colunas
                  isWeekend(date) && "opacity-50",
                )}
                style={{ width: CELL_W }}
              >
                {dayNumber(date)}
              </th>
            ))}
            <th scope="col" className="pb-1 pl-3 text-right font-medium text-muted-foreground">
              Índice do mês
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.sid}>
              <th
                scope="row"
                className="max-w-0 truncate pr-3 text-left font-normal"
                style={{ width: NAME_W }}
                title={row.displayName}
              >
                {row.displayName}
              </th>
              {row.cells.map((cell, i) => {
                const date = days[i];
                const title = cellTitle(row.displayName, date, cell);
                const hasIndex = cell !== null && cell.productivity_index !== null;
                return (
                  <td key={date} className="p-0" style={{ width: CELL_W, height: CELL_H }}>
                    <button
                      type="button"
                      title={title}
                      aria-label={title}
                      onClick={() => onSelectDay(date)}
                      className={cn(
                        "block h-full w-full rounded-[4px] transition-shadow",
                        "hover:ring-2 hover:ring-ring focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                        // Sem dado: só hachura. Com dado e sem classificação:
                        // cinza neutro com contorno pontilhado (nunca a rampa).
                        cell === null && "border border-dashed border-border",
                        cell !== null && !hasIndex && "border border-dotted border-brand-slate bg-muted",
                      )}
                      style={{
                        width: CELL_W,
                        height: CELL_H,
                        ...(cell === null ? emptyHatch : {}),
                        ...(hasIndex
                          ? { backgroundColor: heatColor(cell.productivity_index as number) }
                          : {}),
                      }}
                    />
                  </td>
                );
              })}
              <td className="pl-3 text-right font-display tabular-nums">
                {formatPct(row.month.productivity_index)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Legenda da rampa: "menor -> maior" + os dois estados que ficam FORA dela. */
export function MonthHeatmapLegend() {
  return (
    <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-muted-foreground">
      <span className="flex items-center gap-1.5">
        menor
        {HEAT_RAMP.map((color) => (
          <span
            key={color}
            aria-hidden
            className="h-2.5 w-4 shrink-0 rounded-sm"
            style={{ backgroundColor: color }}
          />
        ))}
        maior
      </span>
      <span className="flex items-center gap-1.5">
        <span aria-hidden className="h-2.5 w-4 shrink-0 rounded-sm border border-dashed border-border" style={emptyHatch} />
        Sem dados no dia
      </span>
      <span className="flex items-center gap-1.5">
        <span aria-hidden className="h-2.5 w-4 shrink-0 rounded-sm border border-dotted border-brand-slate bg-muted" />
        Sem tempo classificado
      </span>
    </div>
  );
}

export interface MonthTableProps {
  days: string[];
  rows: MonthPersonRow[];
}

/**
 * Fallback tabular OBRIGATÓRIO do nível mês (Seção 8.5): os MESMOS números do
 * mapa - uma linha por pessoa × dia COM DADO, mais a linha do total do mês.
 * Também é o fallback de screen reader (a página o renderiza em sr-only sob o
 * mapa).
 *
 * Só os dias COM dado entram: um mês × 200 pessoas daria mais de 6 mil linhas,
 * a maioria repetindo "sem dados". A ausência não perde informação aqui - ela
 * está no mapa (célula hachurada) e na contagem de dias com dado da linha do
 * total.
 */
export function MonthTable({ days, rows }: MonthTableProps) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">Colaborador</th>
            <th scope="col" className="px-3 py-2">Dia</th>
            <th scope="col" className="px-3 py-2 text-right">Ligada</th>
            <th scope="col" className="px-3 py-2 text-right">Ativa</th>
            <th scope="col" className="px-3 py-2 text-right">Ociosa</th>
            <th scope="col" className="px-3 py-2 text-right">Índice</th>
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={6} className="px-3 py-8 text-center text-muted-foreground">
                Nenhum colaborador com dados neste mês.
              </td>
            </tr>
          ) : (
            rows.map((row) => <MonthTableGroup key={row.sid} days={days} row={row} />)
          )}
        </tbody>
      </table>
    </div>
  );
}

/** Grupo de linhas de UMA pessoa: os dias com dado + o total do mês. */
function MonthTableGroup({ days, row }: { days: string[]; row: MonthPersonRow }) {
  const comDado = row.cells
    .map((cell, i) => ({ date: days[i], cell }))
    .filter((c): c is { date: string; cell: PersonDay } => c.cell !== null);

  return (
    <>
      {comDado.length === 0 ? (
        <tr className="border-b">
          <th scope="row" className="max-w-[12rem] truncate px-3 py-1.5 text-left font-medium" title={row.displayName}>
            {row.displayName}
          </th>
          <td colSpan={5} className="px-3 py-1.5 text-muted-foreground">
            Sem dados neste mês
          </td>
        </tr>
      ) : (
        <>
          {comDado.map(({ date, cell }, i) => (
            <tr key={date} className="border-b">
              {i === 0 && (
                <th
                  scope="rowgroup"
                  rowSpan={comDado.length + 1}
                  className="max-w-[12rem] truncate px-3 py-1.5 text-left align-top font-medium"
                  title={row.displayName}
                >
                  {row.displayName}
                </th>
              )}
              <td className="whitespace-nowrap px-3 py-1.5 tabular-nums">
                {ddmm(date)} · {weekday(date)}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {formatDuration(cell.seconds_on)}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {formatDuration(cell.seconds_active)}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {formatDuration(cell.seconds_idle)}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {formatPct(cell.productivity_index)}
              </td>
            </tr>
          ))}
          <tr className="border-b bg-muted/30 font-medium">
            <td className="whitespace-nowrap px-3 py-1.5 tabular-nums">
              Total do mês · {row.month.days_with_data}{" "}
              {row.month.days_with_data === 1 ? "dia com dado" : "dias com dado"}
            </td>
            <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
              {formatHours(row.month.seconds_on)} h
            </td>
            <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
              {formatHours(row.month.seconds_active)} h
            </td>
            <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
              {formatHours(row.month.seconds_idle)} h
            </td>
            <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
              {formatPct(row.month.productivity_index)}
            </td>
          </tr>
        </>
      )}
    </>
  );
}

/**
 * Skeleton com a GEOMETRIA FINAL do mapa (mesma largura de nome, mesmas células
 * de 23×19): a tela não pula quando a resposta chega.
 */
export function MonthHeatmapSkeleton({ days, rows = 8 }: { days: number; rows?: number }) {
  return (
    <div className="space-y-[3px] overflow-hidden">
      {Array.from({ length: rows }, (_, r) => (
        <div key={r} className="flex items-center gap-[3px]">
          <Skeleton className="h-3 shrink-0" style={{ width: NAME_W - 12 }} />
          <span className="w-3 shrink-0" />
          {Array.from({ length: Math.max(days, 1) }, (_, c) => (
            <Skeleton key={c} className="shrink-0 rounded-[4px]" style={{ width: CELL_W, height: CELL_H }} />
          ))}
        </div>
      ))}
    </div>
  );
}
