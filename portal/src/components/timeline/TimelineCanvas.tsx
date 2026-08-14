// =============================================================================
// Canvas da linha do tempo de um device (Seção 8.5): eixo de horas no fuso da
// organização, faixa de estados (48px) e sub-faixa de apps (32px). DPI-aware
// (devicePixelRatio) com redesenho em resize via ResizeObserver; hover por
// busca binária no array de intervalos; tooltip em div absoluta FORA do canvas.
// Redundância NÃO-cromática (daltonismo): off_clean apenas contorno; no_data
// hachura diagonal a 45° + triângulo de alerta; ausência total de intervalos é
// hachura cinza-claríssima "Sem dados" — nunca pintada como ocioso.
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import type { MouseEvent as ReactMouseEvent } from "react";
import type { TimelineInterval } from "@/lib/types";
import { BRAND } from "@/lib/brandTheme";
import { formatDuration, formatHm, stateLabels } from "@/lib/format";

// -----------------------------------------------------------------------------
// Helpers de fuso (Intl) — também usados pela página (badge de fuso divergente).
// -----------------------------------------------------------------------------

/** Minutos que `timezone` está à frente do UTC no instante `at` (São Paulo = -180). */
export function tzOffsetMinutes(at: Date, timezone: string): number {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: timezone,
    hour12: false,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).formatToParts(at);
  const get = (type: string): number => Number(parts.find((p) => p.type === type)?.value ?? "0");
  // hour12:false pode reportar "24" à meia-noite em alguns runtimes — normaliza.
  const asUtc = Date.UTC(get("year"), get("month") - 1, get("day"), get("hour") % 24, get("minute"), get("second"));
  return Math.round((asUtc - at.getTime()) / 60000);
}

/**
 * Epoch (ms) da hora local `hour` (0–24) do dia `date` (yyyy-MM-dd) em
 * `timezone`. Dupla passada para corrigir transições de horário de verão.
 */
export function zonedEpoch(date: string, hour: number, timezone: string): number {
  const [y, m, d] = date.split("-").map(Number);
  const utcGuess = Date.UTC(y, m - 1, d, hour, 0, 0);
  const first = utcGuess - tzOffsetMinutes(new Date(utcGuess), timezone) * 60000;
  return utcGuess - tzOffsetMinutes(new Date(first), timezone) * 60000;
}

// -----------------------------------------------------------------------------
// Geometria (px CSS) — alterar exige revisar o hit-testing do hover abaixo.
// -----------------------------------------------------------------------------

const AXIS_H = 20; // rótulos HH:mm acima das faixas
const STATE_H = 48; // faixa de estados
const LANE_GAP = 8;
const APP_H = 32; // sub-faixa de apps (só intervalos active)
const PAD_BOTTOM = 6;
export const TIMELINE_CANVAS_HEIGHT = AXIS_H + STATE_H + LANE_GAP + APP_H + PAD_BOTTOM;

// Cores canônicas da Seção 8.5 — também usadas pelo TeamTimelineCanvas (F3.4).
// Paleta da marca (lib/brandTheme.ts, a mesma legenda do site): ativo verde de
// atividade, ocioso grafite, bloqueado cinza-azulado; a linha "agora" usa o
// verde vivo de AÇÃO da marca (mesma cor do indicador de presença ao vivo).
export const COLOR = {
  active: BRAND.vizProdutivo,
  idle: BRAND.vizOcioso,
  locked: BRAND.slate,
  offCleanStroke: BRAND.ink3,
  noData: BRAND.red,
  // Sub-faixa de apps: cor única "Não categorizado" — cores por categoria
  // chegam na F3.
  appUncategorized: BRAND.slate,
  emptyHatch: BRAND.line,
  now: BRAND.green,
  grid: BRAND.chartGrid,
  laneBg: BRAND.panel,
  text: BRAND.chartText,
} as const;

const TOOLTIP_W = 280;

export interface TimelineCanvasProps {
  intervals: TimelineInterval[];
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
  /** server_time da última resposta — referência da linha "agora" (null = relógio local). */
  serverTime: string | null;
  /** true enquanto a primeira resposta não chegou: desenha eixo+lanes vazios, sem "Sem dados". */
  pending?: boolean;
  /** Notifica o intervalo sob o cursor (null ao sair) — além do tooltip interno. */
  onHoverInterval?: (interval: TimelineInterval | null) => void;
}

interface IndexedInterval {
  startMs: number;
  endMs: number;
  iv: TimelineInterval;
}

interface TooltipState {
  x: number;
  y: number;
  iv: TimelineInterval;
}

export function TimelineCanvas({
  intervals,
  timezone,
  windowStartHour,
  windowEndHour,
  date,
  isToday,
  serverTime,
  pending = false,
  onHoverInterval,
}: TimelineCanvasProps) {
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const lastHoverRef = useRef<TimelineInterval | null>(null);
  const [width, setWidth] = useState(0);
  const [tooltip, setTooltip] = useState<TooltipState | null>(null);

  // Índice ordenado por started_at — base da busca binária do hover.
  const indexed = useMemo<IndexedInterval[]>(
    () =>
      intervals
        .map((iv) => ({ startMs: Date.parse(iv.started_at), endMs: Date.parse(iv.ended_at), iv }))
        .sort((a, b) => a.startMs - b.startMs),
    [intervals],
  );

  const winStartMs = useMemo(
    () => zonedEpoch(date, windowStartHour, timezone),
    [date, windowStartHour, timezone],
  );
  const winEndMs = useMemo(
    () => zonedEpoch(date, windowEndHour, timezone),
    [date, windowEndHour, timezone],
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
    canvas.height = Math.round(TIMELINE_CANVAS_HEIGHT * dpr);
    const ctx = canvas.getContext("2d");
    if (ctx === null) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const w = width;
    const spanMs = winEndMs - winStartMs;
    const xOf = (t: number): number => ((t - winStartMs) / spanMs) * w;
    const stateY = AXIS_H;
    const appY = AXIS_H + STATE_H + LANE_GAP;

    ctx.clearRect(0, 0, w, TIMELINE_CANVAS_HEIGHT);

    // Fundo das lanes (sempre visível: o esqueleto do desenho aparece imediatamente).
    ctx.fillStyle = COLOR.laneBg;
    ctx.fillRect(0, stateY, w, STATE_H);
    ctx.fillRect(0, appY, w, APP_H);

    // Eixo de horas: marcas por hora, rótulos HH:mm no fuso do tenant.
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
      ctx.lineTo(x, TIMELINE_CANVAS_HEIGHT - PAD_BOTTOM);
      ctx.stroke();
      if ((h - windowStartHour) % labelStep === 0) {
        ctx.fillStyle = COLOR.text;
        ctx.textAlign = h === windowStartHour ? "left" : h === windowEndHour ? "right" : "center";
        ctx.fillText(`${String(h % 24).padStart(2, "0")}:00`, x, AXIS_H - 7);
      }
    }

    if (!pending && indexed.length === 0) {
      // Ausência total de intervalos: hachura cinza-claríssima + "Sem dados".
      // NUNCA pintar como ocioso (Seção 8.5).
      ctx.fillStyle = makeHatch(ctx, COLOR.emptyHatch, null);
      ctx.fillRect(0, stateY, w, STATE_H);
      ctx.font = "12px system-ui, -apple-system, sans-serif";
      ctx.fillStyle = COLOR.text;
      ctx.textAlign = "center";
      ctx.fillText("Sem dados", w / 2, stateY + STATE_H / 2 + 4);
    }

    if (indexed.length > 0) {
      const noDataHatch = makeHatch(ctx, COLOR.noData, "rgba(255, 139, 139, 0.08)");
      for (const { startMs, endMs, iv } of indexed) {
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
            ctx.fillRect(x0, stateY, bw, STATE_H);
            break;
          case "off_clean":
            // Desligada/suspensa é estado ESPERADO: sem preenchimento, só contorno.
            ctx.strokeStyle = COLOR.offCleanStroke;
            ctx.lineWidth = 1.5;
            ctx.strokeRect(x0 + 0.75, stateY + 0.75, Math.max(bw - 1.5, 0.5), STATE_H - 1.5);
            break;
          case "no_data":
            // Hachura diagonal vermelha a 45° + triângulo de alerta (não-cromático).
            ctx.fillStyle = noDataHatch;
            ctx.fillRect(x0, stateY, bw, STATE_H);
            if (bw >= 14) drawAlertTriangle(ctx, x0 + bw / 2, stateY + STATE_H / 2);
            break;
        }
        // Sub-faixa de apps: SÓ intervalos active, cor única "Não categorizado".
        if (iv.state === "active") {
          ctx.fillStyle = COLOR.appUncategorized;
          ctx.fillRect(x0, appY, bw, APP_H);
        }
      }
    }

    // Contorno das lanes por cima dos preenchimentos (definição visual).
    ctx.strokeStyle = COLOR.grid;
    ctx.lineWidth = 1;
    ctx.strokeRect(0.5, stateY + 0.5, w - 1, STATE_H - 1);
    ctx.strokeRect(0.5, appY + 0.5, w - 1, APP_H - 1);

    // Linha vertical "agora" — só quando o dia exibido é hoje.
    if (isToday) {
      const nowMs = serverTime !== null ? Date.parse(serverTime) : Date.now();
      if (nowMs >= winStartMs && nowMs <= winEndMs) {
        const x = Math.round(xOf(nowMs)) + 0.5;
        ctx.strokeStyle = COLOR.now;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x, AXIS_H - 4);
        ctx.lineTo(x, TIMELINE_CANVAS_HEIGHT - PAD_BOTTOM);
        ctx.stroke();
        ctx.fillStyle = COLOR.now;
        ctx.beginPath();
        ctx.arc(x, AXIS_H - 4, 2.5, 0, Math.PI * 2);
        ctx.fill();
      }
    }
  }, [width, indexed, winStartMs, winEndMs, windowStartHour, windowEndHour, date, timezone, isToday, serverTime, pending]);

  function reportHover(iv: TimelineInterval | null): void {
    if (lastHoverRef.current !== iv) {
      lastHoverRef.current = iv;
      onHoverInterval?.(iv);
    }
  }

  function clearHover(): void {
    setTooltip(null);
    reportHover(null);
  }

  function handleMouseMove(e: ReactMouseEvent<HTMLCanvasElement>): void {
    if (width <= 0 || indexed.length === 0) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    const inStateLane = y >= AXIS_H && y <= AXIS_H + STATE_H;
    const appTop = AXIS_H + STATE_H + LANE_GAP;
    const inAppLane = y >= appTop && y <= appTop + APP_H;
    if (!inStateLane && !inAppLane) {
      clearHover();
      return;
    }
    // Posição x → tempo; busca binária pelo maior started_at <= t.
    const t = winStartMs + (x / width) * (winEndMs - winStartMs);
    let lo = 0;
    let hi = indexed.length - 1;
    let found = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (indexed[mid].startMs <= t) {
        found = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    const hit = found >= 0 && t < indexed[found].endMs ? indexed[found] : null;
    // Na sub-faixa de apps só há conteúdo em intervalos active.
    const iv = hit !== null && (!inAppLane || hit.iv.state === "active") ? hit.iv : null;
    if (iv === null) {
      clearHover();
      return;
    }
    setTooltip({ x, y, iv });
    reportHover(iv);
  }

  return (
    <div ref={wrapRef} className="relative">
      {/* A informação acessível fica no fallback tabular (sr-only na página). */}
      <canvas
        ref={canvasRef}
        aria-hidden="true"
        className="block w-full"
        style={{ height: TIMELINE_CANVAS_HEIGHT }}
        onMouseMove={handleMouseMove}
        onMouseLeave={clearHover}
      />
      {tooltip !== null && (
        <div
          className="pointer-events-none absolute z-20 max-w-[280px] space-y-0.5 rounded-md border bg-card px-2.5 py-1.5 text-xs shadow-md"
          style={{
            left: Math.min(Math.max(tooltip.x + 12, 4), Math.max(width - TOOLTIP_W, 4)),
            top: tooltip.y + 16,
          }}
        >
          <p className="font-medium tabular-nums">
            {formatHm(tooltip.iv.started_at, timezone)} – {formatHm(tooltip.iv.ended_at, timezone)} ·{" "}
            {formatDuration((Date.parse(tooltip.iv.ended_at) - Date.parse(tooltip.iv.started_at)) / 1000)}
          </p>
          <p className="text-muted-foreground">{stateLabels[tooltip.iv.state]}</p>
          {tooltip.iv.app !== null && <p className="truncate font-medium">{tooltip.iv.app.display_name}</p>}
          {tooltip.iv.window_title !== null && (
            <p className="truncate text-muted-foreground">{tooltip.iv.window_title}</p>
          )}
          {tooltip.iv.data_incomplete && <p className="text-viz-improdutivo">⚠ dados incompletos</p>}
        </div>
      )}
    </div>
  );
}

/** Padrão de hachura diagonal a 45° (tile 8×8 repetido) — reusado pelo modo equipe. */
export function makeHatch(
  ctx: CanvasRenderingContext2D,
  stroke: string,
  background: string | null,
): CanvasPattern | string {
  const size = 8;
  const tile = document.createElement("canvas");
  tile.width = size;
  tile.height = size;
  const tctx = tile.getContext("2d");
  if (tctx === null) return stroke;
  if (background !== null) {
    tctx.fillStyle = background;
    tctx.fillRect(0, 0, size, size);
  }
  tctx.strokeStyle = stroke;
  tctx.lineWidth = 1.5;
  tctx.beginPath();
  tctx.moveTo(-2, size + 2);
  tctx.lineTo(size + 2, -2);
  tctx.moveTo(-2, 2);
  tctx.lineTo(2, -2);
  tctx.moveTo(size - 2, size + 2);
  tctx.lineTo(size + 2, size - 2);
  tctx.stroke();
  return ctx.createPattern(tile, "repeat") ?? stroke;
}

/** Pequeno triângulo de alerta (redundância não-cromática do no_data) — reusado pelo modo equipe. */
export function drawAlertTriangle(ctx: CanvasRenderingContext2D, cx: number, cy: number): void {
  ctx.beginPath();
  ctx.moveTo(cx, cy - 5);
  ctx.lineTo(cx + 5.5, cy + 4);
  ctx.lineTo(cx - 5.5, cy + 4);
  ctx.closePath();
  ctx.fillStyle = BRAND.red;
  ctx.fill();
  ctx.strokeStyle = BRAND.bg;
  ctx.lineWidth = 1;
  ctx.stroke();
  ctx.fillStyle = BRAND.bg;
  ctx.fillRect(cx - 0.75, cy - 2.5, 1.5, 3.5);
  ctx.fillRect(cx - 0.75, cy + 1.75, 1.5, 1.5);
}
