// =============================================================================
// Canvas da linha do tempo da EQUIPE (F3.4, Seção 8.5): uma lane de 28px por
// device, visão do dia, eixo de horas idêntico ao modo device. Coluna de
// rótulos HTML à esquerda (~160px, truncados com title=) com botão por lane —
// clique leva ao modo device e dá acesso por teclado SEM cursor no canvas.
// Reusa COLOR/makeHatch/drawAlertTriangle do TimelineCanvas (nada duplicado).
// Hover com hit-testing por lane (índice por lane + busca binária por X);
// tooltip com device, horário, estado, app/categoria, fuso divergente e
// "dados incompletos". Cortes do MVP (corte 11): SEM zoom, SEM drag-select,
// SEM virtualização, SEM navegação por teclado dentro do canvas.
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import type { MouseEvent as ReactMouseEvent } from "react";
import { AlertTriangle } from "lucide-react";
import type { TeamTimelineLane, TimelineInterval } from "@/lib/types";
import { formatDuration, formatHm, gmtLabel, stateLabels } from "@/lib/format";
import { cn } from "@/lib/utils";
import {
  COLOR,
  drawAlertTriangle,
  makeHatch,
  tzOffsetMinutes,
  zonedEpoch,
} from "./TimelineCanvas";

// -----------------------------------------------------------------------------
// Geometria própria do modo equipe (px CSS). A altura do canvas é FUNÇÃO do
// número de lanes — não usa TIMELINE_CANVAS_HEIGHT (fixa do modo device e
// referenciada pelos skeletons daquele modo).
// -----------------------------------------------------------------------------

const AXIS_H = 20; // rótulos HH:mm acima das lanes (igual ao modo device)
export const TEAM_LANE_H = 28; // lane por device (Seção 8.5, linha 924)
export const TEAM_LANE_GAP = 6;
const PAD_BOTTOM = 6;
export const TEAM_LABEL_COL_W = 160; // coluna de rótulos à esquerda

const TOOLTIP_W = 280;

/** Altura total do canvas para `laneCount` lanes (mínimo 1 para manter o eixo visível). */
export function teamTimelineCanvasHeight(laneCount: number): number {
  const n = Math.max(laneCount, 1);
  return AXIS_H + n * TEAM_LANE_H + (n - 1) * TEAM_LANE_GAP + PAD_BOTTOM;
}

export interface TeamTimelineCanvasProps {
  /** Lanes na ordem do servidor (nome de exibição asc). */
  lanes: TeamTimelineLane[];
  /** Fuso da organização (IANA) — todo o eixo é desenhado nele. */
  timezone: string;
  /** Hora local de início da janela (0–24). */
  windowStartHour: number;
  /** Hora local de fim da janela (0–24, exclusive no desenho). */
  windowEndHour: number;
  /** Dia exibido (yyyy-MM-dd) no fuso da organização. */
  date: string;
  /** true quando o dia exibido é hoje — habilita a linha vertical "agora". */
  isToday: boolean;
  /** server_time da última resposta — referência da linha "agora" e do fuso divergente. */
  serverTime: string | null;
  /** Clique na lane (canvas ou rótulo) — a página leva ao modo device (?device=...). */
  onSelectDevice: (deviceId: string) => void;
}

interface IndexedInterval {
  startMs: number;
  endMs: number;
  iv: TimelineInterval;
}

interface IndexedLane {
  lane: TeamTimelineLane;
  /** Ordenado por started_at — base da busca binária por X dentro da lane. */
  items: IndexedInterval[];
}

interface TooltipState {
  x: number;
  y: number;
  lane: TeamTimelineLane;
  iv: TimelineInterval;
}

export function TeamTimelineCanvas({
  lanes,
  timezone,
  windowStartHour,
  windowEndHour,
  date,
  isToday,
  serverTime,
  onSelectDevice,
}: TeamTimelineCanvasProps) {
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [width, setWidth] = useState(0);
  const [tooltip, setTooltip] = useState<TooltipState | null>(null);
  const [hoverLane, setHoverLane] = useState<number | null>(null);

  const height = teamTimelineCanvasHeight(lanes.length);

  // Índice por lane: hit-testing em dois passos (Y → lane; X → busca binária).
  const indexedLanes = useMemo<IndexedLane[]>(
    () =>
      lanes.map((lane) => ({
        lane,
        items: lane.intervals
          .map((iv) => ({ startMs: Date.parse(iv.started_at), endMs: Date.parse(iv.ended_at), iv }))
          .sort((a, b) => a.startMs - b.startMs),
      })),
    [lanes],
  );

  const winStartMs = useMemo(
    () => zonedEpoch(date, windowStartHour, timezone),
    [date, windowStartHour, timezone],
  );
  const winEndMs = useMemo(
    () => zonedEpoch(date, windowEndHour, timezone),
    [date, windowEndHour, timezone],
  );

  // Offset do tenant no instante da resposta — base do badge de fuso divergente
  // por lane, que no modo equipe vive no TOOLTIP (não polui o rótulo).
  const tenantOffsetMin = useMemo(
    () => tzOffsetMinutes(serverTime !== null ? new Date(serverTime) : new Date(), timezone),
    [serverTime, timezone],
  );

  // Redesenho em resize: ResizeObserver dispara com a largura CSS atual.
  useEffect(() => {
    const el = wrapRef.current;
    if (el === null) return;
    const ro = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (entry !== undefined) setWidth(entry.contentRect.width);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Desenho completo — DPI-aware (backing store em devicePixelRatio).
  useEffect(() => {
    const canvas = canvasRef.current;
    if (canvas === null || width <= 0) return;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    const ctx = canvas.getContext("2d");
    if (ctx === null) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const w = width;
    const spanMs = winEndMs - winStartMs;
    const xOf = (t: number): number => ((t - winStartMs) / spanMs) * w;
    const laneYOf = (i: number): number => AXIS_H + i * (TEAM_LANE_H + TEAM_LANE_GAP);

    ctx.clearRect(0, 0, w, height);

    // Eixo de horas: marcas por hora, rótulos HH:mm no fuso do tenant (igual ao device).
    const pxPerHour = w / Math.max(windowEndHour - windowStartHour, 1);
    const labelStep = pxPerHour >= 40 ? 1 : pxPerHour >= 20 ? 2 : 3;
    ctx.font = "11px system-ui, -apple-system, sans-serif";
    ctx.textBaseline = "alphabetic";
    for (let h = windowStartHour; h <= windowEndHour; h += 1) {
      const x = Math.round(xOf(zonedEpoch(date, h, timezone))) + 0.5;
      ctx.strokeStyle = COLOR.grid;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(x, AXIS_H - 3);
      ctx.lineTo(x, height - PAD_BOTTOM);
      ctx.stroke();
      if ((h - windowStartHour) % labelStep === 0) {
        ctx.fillStyle = COLOR.text;
        ctx.textAlign = h === windowStartHour ? "left" : h === windowEndHour ? "right" : "center";
        ctx.fillText(`${String(h % 24).padStart(2, "0")}:00`, x, AXIS_H - 7);
      }
    }

    const emptyHatch = makeHatch(ctx, COLOR.emptyHatch, null);
    const noDataHatch = makeHatch(ctx, COLOR.noData, "rgba(220, 38, 38, 0.08)");

    indexedLanes.forEach(({ items }, i) => {
      const laneY = laneYOf(i);

      // Fundo da lane (o esqueleto do desenho aparece imediatamente).
      ctx.fillStyle = COLOR.laneBg;
      ctx.fillRect(0, laneY, w, TEAM_LANE_H);

      if (items.length === 0) {
        // Device sem intervalos no dia: hachura cinza-claríssima + "Sem dados".
        // NUNCA pintar como ocioso (Seção 8.5).
        ctx.fillStyle = emptyHatch;
        ctx.fillRect(0, laneY, w, TEAM_LANE_H);
        ctx.font = "11px system-ui, -apple-system, sans-serif";
        ctx.fillStyle = COLOR.text;
        ctx.textAlign = "center";
        ctx.fillText("Sem dados", w / 2, laneY + TEAM_LANE_H / 2 + 4);
      } else {
        for (const { startMs, endMs, iv } of items) {
          const rx0 = xOf(startMs);
          const rx1 = xOf(endMs);
          if (rx1 <= 0 || rx0 >= w) continue; // fora da janela exibida
          const x0 = Math.max(rx0, 0);
          const bw = Math.max(Math.min(rx1, w) - x0, 1);
          switch (iv.state) {
            case "active":
            case "idle":
            case "locked":
              ctx.fillStyle = COLOR[iv.state];
              ctx.fillRect(x0, laneY, bw, TEAM_LANE_H);
              break;
            case "off_clean":
              // Desligada/suspensa é estado ESPERADO: sem preenchimento, só contorno.
              ctx.strokeStyle = COLOR.offCleanStroke;
              ctx.lineWidth = 1.5;
              ctx.strokeRect(x0 + 0.75, laneY + 0.75, Math.max(bw - 1.5, 0.5), TEAM_LANE_H - 1.5);
              break;
            case "no_data":
              // Hachura diagonal vermelha a 45° + triângulo de alerta (não-cromático).
              ctx.fillStyle = noDataHatch;
              ctx.fillRect(x0, laneY, bw, TEAM_LANE_H);
              if (bw >= 14) drawAlertTriangle(ctx, x0 + bw / 2, laneY + TEAM_LANE_H / 2);
              break;
          }
        }
      }

      // Contorno da lane por cima dos preenchimentos (definição visual).
      ctx.strokeStyle = COLOR.grid;
      ctx.lineWidth = 1;
      ctx.strokeRect(0.5, laneY + 0.5, w - 1, TEAM_LANE_H - 1);
    });

    // Linha vertical "agora" — só quando o dia exibido é hoje.
    if (isToday) {
      const nowMs = serverTime !== null ? Date.parse(serverTime) : Date.now();
      if (nowMs >= winStartMs && nowMs <= winEndMs) {
        const x = Math.round(xOf(nowMs)) + 0.5;
        ctx.strokeStyle = COLOR.now;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x, AXIS_H - 4);
        ctx.lineTo(x, height - PAD_BOTTOM);
        ctx.stroke();
        ctx.fillStyle = COLOR.now;
        ctx.beginPath();
        ctx.arc(x, AXIS_H - 4, 2.5, 0, Math.PI * 2);
        ctx.fill();
      }
    }
  }, [width, height, indexedLanes, winStartMs, winEndMs, windowStartHour, windowEndHour, date, timezone, isToday, serverTime]);

  /** Y (px CSS) → índice da lane, ou null quando cai no eixo, no gap ou abaixo. */
  function laneIndexAt(y: number): number | null {
    const rel = y - AXIS_H;
    if (rel < 0) return null;
    const idx = Math.floor(rel / (TEAM_LANE_H + TEAM_LANE_GAP));
    if (idx >= indexedLanes.length) return null;
    const within = rel - idx * (TEAM_LANE_H + TEAM_LANE_GAP);
    return within <= TEAM_LANE_H ? idx : null;
  }

  function clearHover(): void {
    setTooltip(null);
    setHoverLane(null);
  }

  function handleMouseMove(e: ReactMouseEvent<HTMLCanvasElement>): void {
    if (width <= 0 || indexedLanes.length === 0) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    const laneIdx = laneIndexAt(y);
    if (laneIdx === null) {
      clearHover();
      return;
    }
    setHoverLane(laneIdx);
    // Posição x → tempo; busca binária pelo maior started_at <= t DENTRO da lane.
    const { lane, items } = indexedLanes[laneIdx];
    const t = winStartMs + (x / width) * (winEndMs - winStartMs);
    let lo = 0;
    let hi = items.length - 1;
    let found = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (items[mid].startMs <= t) {
        found = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    const hit = found >= 0 && t < items[found].endMs ? items[found] : null;
    if (hit === null) {
      setTooltip(null);
      return;
    }
    setTooltip({ x, y, lane, iv: hit.iv });
  }

  function handleClick(e: ReactMouseEvent<HTMLCanvasElement>): void {
    const rect = e.currentTarget.getBoundingClientRect();
    const laneIdx = laneIndexAt(e.clientY - rect.top);
    if (laneIdx !== null) onSelectDevice(indexedLanes[laneIdx].lane.device_id);
  }

  const divergentTz =
    tooltip !== null &&
    tooltip.lane.device_tz_offset_min !== null &&
    tooltip.lane.device_tz_offset_min !== tenantOffsetMin
      ? gmtLabel(tooltip.lane.device_tz_offset_min)
      : null;

  return (
    <div className="flex">
      {/* Coluna de rótulos: HTML (não canvas) — truncamento com title= e botão
          acessível por teclado para ir ao modo device (sem cursor no canvas). */}
      <div className="shrink-0" style={{ width: TEAM_LABEL_COL_W, paddingTop: AXIS_H }}>
        {lanes.map((lane, i) => (
          <button
            key={lane.device_id}
            type="button"
            title={lane.device_name}
            onClick={() => onSelectDevice(lane.device_id)}
            className={cn(
              "flex w-full items-center gap-1.5 rounded-sm pl-1 pr-2 text-left text-xs",
              "transition-colors hover:bg-accent hover:text-accent-foreground",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
              hoverLane === i && "bg-accent text-accent-foreground",
            )}
            style={{ height: TEAM_LANE_H, marginTop: i === 0 ? 0 : TEAM_LANE_GAP }}
          >
            <span className="min-w-0 flex-1 truncate font-medium">{lane.device_name}</span>
            {lane.data_incomplete && (
              <AlertTriangle
                className="h-3.5 w-3.5 shrink-0 text-amber-600"
                aria-label="dados incompletos"
              />
            )}
          </button>
        ))}
      </div>

      <div ref={wrapRef} className="relative min-w-0 flex-1">
        {/* A informação acessível fica no fallback tabular (sr-only na página). */}
        <canvas
          ref={canvasRef}
          aria-hidden="true"
          className={cn("block w-full", hoverLane !== null && "cursor-pointer")}
          style={{ height }}
          onMouseMove={handleMouseMove}
          onMouseLeave={clearHover}
          onClick={handleClick}
        />
        {tooltip !== null && (
          <div
            className="pointer-events-none absolute z-20 max-w-[280px] space-y-0.5 rounded-md border bg-card px-2.5 py-1.5 text-xs shadow-md"
            style={{
              left: Math.min(Math.max(tooltip.x + 12, 4), Math.max(width - TOOLTIP_W, 4)),
              top: tooltip.y + 16,
            }}
          >
            <p className="truncate font-medium">{tooltip.lane.device_name}</p>
            <p className="tabular-nums">
              {formatHm(tooltip.iv.started_at, timezone)} – {formatHm(tooltip.iv.ended_at, timezone)} ·{" "}
              {formatDuration((Date.parse(tooltip.iv.ended_at) - Date.parse(tooltip.iv.started_at)) / 1000)}
            </p>
            <p className="text-muted-foreground">{stateLabels[tooltip.iv.state]}</p>
            {tooltip.iv.app !== null && (
              <p className="truncate">
                {tooltip.iv.app.display_name}
                {tooltip.iv.app.category !== null && (
                  <span className="text-muted-foreground"> · {tooltip.iv.app.category}</span>
                )}
              </p>
            )}
            {divergentTz !== null && (
              <p className="text-muted-foreground">Máquina em {divergentTz}</p>
            )}
            {tooltip.iv.data_incomplete && <p className="text-amber-700">⚠ dados incompletos</p>}
          </div>
        )}
      </div>
    </div>
  );
}
