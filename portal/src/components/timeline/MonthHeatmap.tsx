// =============================================================================
// Nível MÊS da Linha do Tempo (F4, Seção 3 do spec de 07/09/2026): mapa de
// calor de UMA linha por colaborador, ordem ALFABÉTICA (decisão 2: sem ranking,
// nunca ordenado por índice aqui).
//
// POR QUE A CÉLULA É UMA SEMANA, E NÃO UM DIA
// O mockup aprovado desenha pessoa × DIA. Com o contrato de hoje isso custaria
// uma consulta por dia (`GET /people?from=D&to=D` não tem quebra por dia na
// resposta): ~30 dias × ~5 semanas de gente = ordem de 150 requisições para
// pintar uma tela. A granularidade DIÁRIA depende de um endpoint por pessoa ×
// dia (`usage por pessoa × dia`, fase F3/F4 do plano) e entra na fase seguinte;
// até lá a célula é a SEMANA (segunda a domingo, a mesma régua de lib/period.ts
// e das metas), o que entrega a MESMA leitura de padrão - quem oscila, quem
// cai numa semana, quem some - com 5 a 7 consultas em vez de 150.
//
// ESCALA DE COR: rampa de UM tom (o verde de atividade da marca, do escuro ao
// claro). Um tom só é a redundância não-cromática do mapa: quem não distingue
// matiz continua lendo a LUMINÂNCIA, e o número exato vive no title/aria-label
// de cada célula e no fallback tabular (os mesmos números, sempre).
//
// AUSÊNCIA DE DADO NUNCA É VALOR BAIXO: semana sem nenhum dia registrado sai
// vazia com hachura leve a 45°; semana com dado mas SEM tempo classificado
// (índice null) sai cinza-neutra com contorno pontilhado. Nenhuma das duas
// entra na rampa.
// =============================================================================

import type { CSSProperties } from "react";
import { BRAND } from "@/lib/brandTheme";
import { ddmm } from "@/lib/format";
import { formatHours, formatPct } from "@/lib/period";
import type { PersonRow } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Skeleton } from "@/components/ui/skeleton";

/** Uma semana do mês, já recortada pelo primeiro dia do mês e por hoje. */
export interface MonthWeek {
  /** Primeiro dia consultado (yyyy-MM-dd). */
  from: string;
  /** Último dia consultado (yyyy-MM-dd) - nunca passa de hoje. */
  to: string;
}

/** Uma pessoa no mapa: o total do mês + uma célula por semana (null = sem dado). */
export interface MonthPersonRow {
  sid: string;
  displayName: string;
  /** Agregado do mês inteiro, como o servidor calculou (índice incluído). */
  month: PersonRow;
  /** Mesma ordem de `weeks`; null quando a pessoa não tem nenhum dia naquela semana. */
  cells: (PersonRow | null)[];
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

const CELL_W = 34;
const CELL_H = 20;
const NAME_W = 176;

/** "01/09 a 07/09" - rótulo da coluna e do tooltip. */
function weekLabel(week: MonthWeek): string {
  return week.from === week.to ? ddmm(week.from) : `${ddmm(week.from)} a ${ddmm(week.to)}`;
}

/** Texto único do tooltip/aria de uma célula: horas, índice e dias com dado. */
function cellTitle(name: string, week: MonthWeek, row: PersonRow | null): string {
  const head = `${name} · ${weekLabel(week)}`;
  if (row === null) return `${head}\nSem dados nesta semana`;
  const dias = `${row.days_with_data} ${row.days_with_data === 1 ? "dia" : "dias"} com dado`;
  return (
    `${head}\nLigada ${formatHours(row.seconds_on)} h · Ativa ${formatHours(row.seconds_active)} h` +
    `\nÍndice ${formatPct(row.productivity_index)} · ${dias}`
  );
}

export interface MonthHeatmapProps {
  weeks: MonthWeek[];
  rows: MonthPersonRow[];
  /** Clique na célula: abre o nível DIA. Ver `onSelectDay` abaixo para a data escolhida. */
  onSelectDay: (date: string) => void;
}

/**
 * Mapa de calor pessoa × semana. Cada célula é um botão: clique (ou Enter no
 * foco) abre o nível DIA. Como a célula é uma SEMANA, a data aberta é o ÚLTIMO
 * dia consultado dela (o `to`, que já vem recortado por hoje) - é o dia mais
 * recente com chance de ter dado naquele recorte.
 */
export function MonthHeatmap({ weeks, rows, onSelectDay }: MonthHeatmapProps) {
  return (
    <div className="overflow-x-auto">
      <table className="border-separate text-xs" style={{ borderSpacing: 3 }}>
        <caption className="sr-only">
          Índice de produtividade por colaborador e semana. Os números exatos estão no fallback
          tabular.
        </caption>
        <thead>
          <tr>
            <th scope="col" className="pb-1 pr-3 text-left font-medium text-muted-foreground" style={{ width: NAME_W }}>
              Colaborador
            </th>
            {weeks.map((week) => (
              <th
                key={week.from}
                scope="col"
                className="pb-1 text-center text-[10.5px] font-medium tabular-nums text-muted-foreground"
                style={{ width: CELL_W }}
              >
                {ddmm(week.from)}
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
                const week = weeks[i];
                const title = cellTitle(row.displayName, week, cell);
                const hasIndex = cell !== null && cell.productivity_index !== null;
                return (
                  <td key={week.from} className="p-0" style={{ width: CELL_W, height: CELL_H }}>
                    <button
                      type="button"
                      title={title}
                      aria-label={title}
                      onClick={() => onSelectDay(week.to)}
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
        Sem dados na semana
      </span>
      <span className="flex items-center gap-1.5">
        <span aria-hidden className="h-2.5 w-4 shrink-0 rounded-sm border border-dotted border-brand-slate bg-muted" />
        Sem tempo classificado
      </span>
    </div>
  );
}

export interface MonthTableProps {
  weeks: MonthWeek[];
  rows: MonthPersonRow[];
}

/**
 * Fallback tabular OBRIGATÓRIO do nível mês (Seção 8.5): os MESMOS números do
 * mapa - uma linha por pessoa × semana, mais a linha do total do mês. Também é
 * o fallback de screen reader (a página o renderiza em sr-only sob o mapa).
 */
export function MonthTable({ weeks, rows }: MonthTableProps) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">Colaborador</th>
            <th scope="col" className="px-3 py-2">Semana</th>
            <th scope="col" className="px-3 py-2 text-right">Ligada</th>
            <th scope="col" className="px-3 py-2 text-right">Ativa</th>
            <th scope="col" className="px-3 py-2 text-right">Índice</th>
            <th scope="col" className="px-3 py-2 text-right">Dias com dado</th>
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
            rows.map((row) => (
              <MonthTableGroup key={row.sid} weeks={weeks} row={row} />
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

/** Grupo de linhas de UMA pessoa: as semanas + o total do mês (rowSpan no nome). */
function MonthTableGroup({ weeks, row }: { weeks: MonthWeek[]; row: MonthPersonRow }) {
  return (
    <>
      {weeks.map((week, i) => (
        <tr key={week.from} className="border-b">
          {i === 0 && (
            <th
              scope="rowgroup"
              rowSpan={weeks.length + 1}
              className="max-w-[12rem] truncate px-3 py-1.5 text-left align-top font-medium"
              title={row.displayName}
            >
              {row.displayName}
            </th>
          )}
          <MonthTableCells label={weekLabel(week)} data={row.cells[i]} />
        </tr>
      ))}
      <tr className="border-b bg-muted/30 font-medium">
        <MonthTableCells label="Total do mês" data={row.month} />
      </tr>
    </>
  );
}

function MonthTableCells({ label, data }: { label: string; data: PersonRow | null }) {
  return (
    <>
      <td className="whitespace-nowrap px-3 py-1.5 tabular-nums">{label}</td>
      {data === null ? (
        <td colSpan={4} className="px-3 py-1.5 text-muted-foreground">
          Sem dados nesta semana
        </td>
      ) : (
        <>
          <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
            {formatHours(data.seconds_on)} h
          </td>
          <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
            {formatHours(data.seconds_active)} h
          </td>
          <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
            {formatPct(data.productivity_index)}
          </td>
          <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
            {data.days_with_data}
          </td>
        </>
      )}
    </>
  );
}

/**
 * Skeleton com a GEOMETRIA FINAL do mapa (mesma largura de nome, mesmas células
 * de 34×20): a tela não pula quando a resposta chega.
 */
export function MonthHeatmapSkeleton({ weeks, rows = 8 }: { weeks: number; rows?: number }) {
  return (
    <div className="space-y-[3px] overflow-hidden">
      {Array.from({ length: rows }, (_, r) => (
        <div key={r} className="flex items-center gap-[3px]">
          <Skeleton className="h-3 shrink-0" style={{ width: NAME_W - 12 }} />
          <span className="w-3 shrink-0" />
          {Array.from({ length: Math.max(weeks, 1) }, (_, c) => (
            <Skeleton key={c} className="shrink-0 rounded-[4px]" style={{ width: CELL_W, height: CELL_H }} />
          ))}
        </div>
      ))}
    </div>
  );
}
