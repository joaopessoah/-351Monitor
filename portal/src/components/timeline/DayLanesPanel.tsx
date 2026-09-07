// =============================================================================
// Nível DIA da Linha do Tempo (F4, Seção 3 do spec de 07/09/2026): as faixas do
// dia com o RESUMO à direita de cada uma, no desenho do mockup aprovado.
//
// POR QUE A FAIXA AINDA É POR DISPOSITIVO, E NÃO POR PESSOA
// O mockup pede uma faixa por PESSOA. `GET /api/v1/timeline/team` devolve uma
// lane por DISPOSITIVO e a lane não carrega nenhuma identidade de gente:
// `TeamTimelineLane` tem device_id, device_name, device_tz_offset_min,
// data_incomplete e intervals - nada de windows_sid (o backend lê
// i.device_user_id na query, mas não o expõe no contrato). Agrupar por pessoa
// no cliente exigiria adivinhar a partir do nome da máquina, o que produziria
// junção errada em qualquer tenant com duas pessoas na mesma máquina (ou uma
// pessoa em duas). Então a faixa CONTINUA por dispositivo e o agrupamento por
// pessoa depende do `windows_sid` na lane, que entra na fase seguinte.
// Enquanto isso, o índice POR PESSOA do dia - que é o número da coluna
// "Índice" do mockup - vem do servidor em `DayPeopleSummary` (uma consulta a
// `GET /people?from=D&to=D`), nunca calculado aqui.
//
// O resumo da faixa (Ligada / Ativa / Ociosa) é derivado dos MESMOS intervalos
// que o canvas desenha, com a MESMA definição do backend
// (TimelineSummaryResponse: seconds_on = active + idle + locked; off_clean e
// no_data não são jornada). Por isso canvas, resumo e fallback tabular
// carregam sempre os mesmos números - e nada aqui é rotulado como ponto,
// expediente, entrada ou saída.
// =============================================================================

import { useMemo } from "react";
import { AlertTriangle } from "lucide-react";
import { formatDuration } from "@/lib/format";
import { formatHours, formatPct } from "@/lib/period";
import type { PersonRow, TeamTimelineLane } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Skeleton } from "@/components/ui/skeleton";
import {
  TEAM_AXIS_H,
  TEAM_LANE_GAP,
  TEAM_LANE_H,
  TeamTimelineCanvas,
} from "./TeamTimelineCanvas";

/** Resumo de UMA faixa, derivado dos intervalos da própria lane. */
export interface LaneSummary {
  secondsOn: number;
  secondsActive: number;
  secondsIdle: number;
}

/**
 * Soma as durações dos intervalos por estado. `seconds_on` repete a definição
 * do backend (ativo + ocioso + bloqueado): a máquina desligada e o trecho sem
 * comunicação NÃO entram.
 */
export function laneSummary(lane: TeamTimelineLane): LaneSummary {
  let active = 0;
  let idle = 0;
  let locked = 0;
  for (const iv of lane.intervals) {
    const seconds = (Date.parse(iv.ended_at) - Date.parse(iv.started_at)) / 1000;
    if (!Number.isFinite(seconds) || seconds <= 0) continue;
    if (iv.state === "active") active += seconds;
    else if (iv.state === "idle") idle += seconds;
    else if (iv.state === "locked") locked += seconds;
  }
  return { secondsOn: active + idle + locked, secondsActive: active, secondsIdle: idle };
}

const SUMMARY_COL_W = 210;

export interface DayLanesPanelProps {
  lanes: TeamTimelineLane[];
  timezone: string;
  windowStartHour: number;
  windowEndHour: number;
  date: string;
  isToday: boolean;
  serverTime: string | null;
  /** Clique na faixa (canvas, rótulo ou resumo): abre o nível DISPOSITIVO. */
  onSelectDevice: (deviceId: string) => void;
}

/**
 * Faixas do dia + coluna de resumo. A coluna é HTML (não canvas) e alinha
 * linha a linha pela MESMA geometria das lanes (TEAM_AXIS_H de topo, faixas de
 * TEAM_LANE_H com TEAM_LANE_GAP entre elas).
 */
export function DayLanesPanel({
  lanes,
  timezone,
  windowStartHour,
  windowEndHour,
  date,
  isToday,
  serverTime,
  onSelectDevice,
}: DayLanesPanelProps) {
  const summaries = useMemo(() => lanes.map(laneSummary), [lanes]);

  return (
    <div className="flex items-start gap-3">
      <div className="min-w-0 flex-1">
        <TeamTimelineCanvas
          lanes={lanes}
          timezone={timezone}
          windowStartHour={windowStartHour}
          windowEndHour={windowEndHour}
          date={date}
          isToday={isToday}
          serverTime={serverTime}
          onSelectDevice={onSelectDevice}
        />
      </div>

      {/* Resumo à direita: números do dia por faixa, alinhados às lanes. */}
      <div className="shrink-0" style={{ width: SUMMARY_COL_W }}>
        <div
          className="flex items-end gap-2 text-[10.5px] font-semibold uppercase tracking-wide text-muted-foreground"
          style={{ height: TEAM_AXIS_H }}
        >
          <span className="flex-1 text-right">Ligada</span>
          <span className="flex-1 text-right">Ativa</span>
          <span className="flex-1 text-right">Ociosa</span>
        </div>
        {lanes.map((lane, i) => (
          <button
            key={lane.device_id}
            type="button"
            onClick={() => onSelectDevice(lane.device_id)}
            className={cn(
              "flex w-full items-center gap-2 rounded-sm px-1 text-right text-xs tabular-nums",
              "transition-colors hover:bg-accent hover:text-accent-foreground",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            )}
            style={{ height: TEAM_LANE_H, marginTop: i === 0 ? 0 : TEAM_LANE_GAP }}
          >
            <span className="flex-1 font-display">{formatDuration(summaries[i].secondsOn)}</span>
            <span className="flex-1 font-display">{formatDuration(summaries[i].secondsActive)}</span>
            <span className="flex-1 text-muted-foreground">
              {formatDuration(summaries[i].secondsIdle)}
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}

/**
 * Fallback tabular do RESUMO das faixas (obrigatório, Seção 8.5): os mesmos
 * números da coluna da direita. Acompanha a TeamTimelineTable, que traz os
 * intervalos um a um.
 */
export function DayLanesSummaryTable({ lanes }: { lanes: TeamTimelineLane[] }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <caption className="px-3 py-2 text-left text-xs text-muted-foreground">
          Resumo do dia por dispositivo · Ligada = ativa + ociosa + bloqueada
        </caption>
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">Dispositivo</th>
            <th scope="col" className="px-3 py-2 text-right">Ligada</th>
            <th scope="col" className="px-3 py-2 text-right">Ativa</th>
            <th scope="col" className="px-3 py-2 text-right">Ociosa</th>
            <th scope="col" className="px-3 py-2">Observação</th>
          </tr>
        </thead>
        <tbody>
          {lanes.length === 0 ? (
            <tr>
              <td colSpan={5} className="px-3 py-8 text-center text-muted-foreground">
                Nenhum dispositivo para mostrar.
              </td>
            </tr>
          ) : (
            lanes.map((lane) => {
              const s = laneSummary(lane);
              return (
                <tr key={lane.device_id} className="border-b last:border-b-0">
                  <th
                    scope="row"
                    className="max-w-[14rem] truncate px-3 py-1.5 text-left font-medium"
                    title={lane.device_name}
                  >
                    {lane.device_name}
                  </th>
                  <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                    {formatDuration(s.secondsOn)}
                  </td>
                  <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                    {formatDuration(s.secondsActive)}
                  </td>
                  <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                    {formatDuration(s.secondsIdle)}
                  </td>
                  <td className="whitespace-nowrap px-3 py-1.5">
                    {lane.data_incomplete ? (
                      <span className="flex items-center gap-1 text-viz-improdutivo">
                        <AlertTriangle className="h-3.5 w-3.5 shrink-0" aria-hidden />
                        dados incompletos
                      </span>
                    ) : (
                      <span className="text-muted-foreground">-</span>
                    )}
                  </td>
                </tr>
              );
            })
          )}
        </tbody>
      </table>
    </div>
  );
}

export interface DayPeopleSummaryProps {
  /** `GET /people?from=D&to=D` do dia exibido - undefined enquanto carrega. */
  people: PersonRow[] | undefined;
  /** true quando a consulta falhou sem nenhum dado em cache. */
  failed: boolean;
  onRetry: () => void;
}

/**
 * Resumo do dia POR PESSOA - a parte "por pessoa" que a lane ainda não permite.
 * Ligada, ativa, índice e cobertura vêm PRONTOS de `GET /people` (decisão 4 do
 * spec: o portal nunca recalcula o índice, só formata) e a ordem é ALFABÉTICA,
 * como o backend já devolve por padrão. Sem ranking, sem realce de "pior" e
 * sem posição - é leitura de composição, não placar.
 */
export function DayPeopleSummary({ people, failed, onRetry }: DayPeopleSummaryProps) {
  if (failed) {
    return (
      <div
        role="alert"
        className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
      >
        <span>Não foi possível carregar o resumo por pessoa deste dia.</span>
        <button
          type="button"
          onClick={onRetry}
          className="rounded-md border border-destructive/40 px-2 py-1 text-xs font-medium"
        >
          Tentar novamente
        </button>
      </div>
    );
  }

  if (people === undefined) {
    // Skeleton com a geometria final: 4 linhas de 28px na mesma grade.
    return (
      <div className="space-y-1.5">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-7 w-full" />
        ))}
      </div>
    );
  }

  if (people.length === 0) {
    return (
      <p className="py-6 text-center text-sm text-muted-foreground">
        Nenhuma pessoa com dados registrados neste dia.
      </p>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">Colaborador</th>
            <th scope="col" className="px-3 py-2 text-right">Ligada</th>
            <th scope="col" className="px-3 py-2 text-right">Ativa</th>
            <th scope="col" className="px-3 py-2 text-right">Índice</th>
            <th scope="col" className="px-3 py-2 text-right">Cobertura</th>
            <th scope="col" className="px-3 py-2">Equipes</th>
          </tr>
        </thead>
        <tbody>
          {people.map((p) => (
            <tr key={p.windows_sid} className="border-b last:border-b-0">
              <th
                scope="row"
                className="max-w-[14rem] truncate px-3 py-1.5 text-left font-medium"
                title={p.display_name}
              >
                {p.display_name}
              </th>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {formatHours(p.seconds_on)} h
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {formatHours(p.seconds_active)} h
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right font-display tabular-nums">
                {formatPct(p.productivity_index)}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums text-muted-foreground">
                {formatPct(p.classification_coverage)}
              </td>
              <td className="max-w-[12rem] truncate px-3 py-1.5 text-muted-foreground">
                {p.teams.length > 0 ? p.teams.join(", ") : "-"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="px-3 pt-2 text-xs text-muted-foreground">
        Índice = produtivo ÷ (produtivo + neutro + improdutivo), com a cobertura da classificação
        sempre ao lado. Classificação definida pela sua empresa.
      </p>
    </div>
  );
}
