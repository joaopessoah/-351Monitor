// =============================================================================
// Fallback tabular da linha do tempo (Seção 8.5): os MESMOS intervalos do
// canvas, zero fetch extra. Também serve de fallback de screen reader - a
// página o renderiza em sr-only enquanto o canvas está visível.
// TimelineTable = modo device; TeamTimelineTable (F3.4) = modo equipe, com
// coluna Dispositivo (rowSpan por grupo) e as MESMAS células de intervalo.
// =============================================================================

import { Fragment } from "react";
import type { CSSProperties } from "react";
import { AlertTriangle } from "lucide-react";
import { formatDuration, formatHm, stateLabels } from "@/lib/format";
import type { IntervalState, TeamTimelineLane, TimelineInterval } from "@/lib/types";
import { cn } from "@/lib/utils";

/** Hachura diagonal vermelha do no_data - redundância NÃO-cromática (Seção 8.5). */
const noDataHatch: CSSProperties = {
  backgroundImage:
    "repeating-linear-gradient(45deg, #dc2626 0px, #dc2626 2px, #fecaca 2px, #fecaca 4px)",
};

/** Cabeçalhos compartilhados das colunas de intervalo (modo device e equipe). */
function IntervalHeadCells() {
  return (
    <>
      <th scope="col" className="px-3 py-2">Início</th>
      <th scope="col" className="px-3 py-2">Fim</th>
      <th scope="col" className="px-3 py-2 text-right">Duração</th>
      <th scope="col" className="px-3 py-2">Estado</th>
      <th scope="col" className="px-3 py-2">App</th>
      <th scope="col" className="px-3 py-2">Título</th>
      <th scope="col" className="px-3 py-2">Observação</th>
    </>
  );
}

/** Células de UM intervalo - exatamente as mesmas nos dois modos. */
function IntervalCells({ iv, timezone }: { iv: TimelineInterval; timezone: string }) {
  return (
    <>
      <td className="whitespace-nowrap px-3 py-1.5 tabular-nums">
        {formatHm(iv.started_at, timezone)}
      </td>
      <td className="whitespace-nowrap px-3 py-1.5 tabular-nums">
        {formatHm(iv.ended_at, timezone)}
      </td>
      <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
        {formatDuration((Date.parse(iv.ended_at) - Date.parse(iv.started_at)) / 1000)}
      </td>
      <td className="whitespace-nowrap px-3 py-1.5">
        <span className="flex items-center gap-2">
          <StateSwatch state={iv.state} />
          <span>{stateLabels[iv.state]}</span>
        </span>
      </td>
      <td className="max-w-[14rem] truncate px-3 py-1.5">
        {iv.app !== null ? iv.app.display_name : <span className="text-muted-foreground">-</span>}
      </td>
      <td className="max-w-[22rem] truncate px-3 py-1.5 text-muted-foreground">
        {iv.window_title ?? "-"}
      </td>
      <td className="whitespace-nowrap px-3 py-1.5">
        {iv.data_incomplete ? (
          <span className="flex items-center gap-1 text-viz-improdutivo">
            <AlertTriangle className="h-3.5 w-3.5 shrink-0" aria-hidden />
            dados incompletos
          </span>
        ) : (
          <span className="text-muted-foreground">-</span>
        )}
      </td>
    </>
  );
}

export interface TimelineTableProps {
  intervals: TimelineInterval[];
  /** Fuso da organização (IANA) - horários sempre convertidos para ele. */
  timezone: string;
}

export function TimelineTable({ intervals, timezone }: TimelineTableProps) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <IntervalHeadCells />
          </tr>
        </thead>
        <tbody>
          {intervals.length === 0 ? (
            <tr>
              <td colSpan={7} className="px-3 py-8 text-center text-muted-foreground">
                Sem intervalos neste dia.
              </td>
            </tr>
          ) : (
            intervals.map((iv, i) => (
              <tr key={`${iv.started_at}-${i}`} className="border-b last:border-b-0">
                <IntervalCells iv={iv} timezone={timezone} />
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

export interface TeamTimelineTableProps {
  lanes: TeamTimelineLane[];
  /** Fuso da organização (IANA) - horários sempre convertidos para ele. */
  timezone: string;
}

/**
 * Fallback tabular do modo EQUIPE (obrigatório, Seção 8.5): mesmos intervalos
 * da resposta do canvas, agrupados por device via rowSpan na coluna Dispositivo.
 * Device sem intervalos vira uma única linha "Sem intervalos neste dia."
 */
export function TeamTimelineTable({ lanes, timezone }: TeamTimelineTableProps) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">Dispositivo</th>
            <IntervalHeadCells />
          </tr>
        </thead>
        <tbody>
          {lanes.length === 0 ? (
            <tr>
              <td colSpan={8} className="px-3 py-8 text-center text-muted-foreground">
                Nenhum dispositivo para mostrar.
              </td>
            </tr>
          ) : (
            lanes.map((lane) => (
              <Fragment key={lane.device_id}>
                {lane.intervals.length === 0 ? (
                  <tr className="border-b last:border-b-0">
                    <DeviceCell lane={lane} rowSpan={1} />
                    <td colSpan={7} className="px-3 py-1.5 text-muted-foreground">
                      Sem intervalos neste dia.
                    </td>
                  </tr>
                ) : (
                  lane.intervals.map((iv, i) => (
                    <tr key={`${iv.started_at}-${i}`} className="border-b last:border-b-0">
                      {i === 0 && <DeviceCell lane={lane} rowSpan={lane.intervals.length} />}
                      <IntervalCells iv={iv} timezone={timezone} />
                    </tr>
                  ))
                )}
              </Fragment>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

/** Célula do device (rowSpan no grupo) com alerta de dados incompletos da lane. */
function DeviceCell({ lane, rowSpan }: { lane: TeamTimelineLane; rowSpan: number }) {
  return (
    <td rowSpan={rowSpan} className="max-w-[12rem] px-3 py-1.5 align-top">
      <span className="block truncate font-medium" title={lane.device_name}>
        {lane.device_name}
      </span>
      {lane.data_incomplete && (
        <span className="mt-0.5 flex items-center gap-1 text-xs text-viz-improdutivo">
          <AlertTriangle className="h-3 w-3 shrink-0" aria-hidden />
          dados incompletos
        </span>
      )}
    </td>
  );
}

/** Swatch do estado com redundância não-cromática (off_clean contorno; no_data hachura). */
function StateSwatch({ state }: { state: IntervalState }) {
  if (state === "no_data") {
    return <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={noDataHatch} />;
  }
  if (state === "off_clean") {
    return <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-full border-2 border-[#9ca3af]" />;
  }
  const solid: Record<"active" | "idle" | "locked", string> = {
    active: "bg-[#16a34a]",
    idle: "bg-[#d97706]",
    locked: "bg-[#64748b]",
  };
  return <span aria-hidden className={cn("h-2.5 w-2.5 shrink-0 rounded-full", solid[state])} />;
}
