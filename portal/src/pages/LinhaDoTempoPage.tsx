// =============================================================================
// Linha do Tempo — modo device (F2, Seção 8.5): reconstruir o dia de uma
// pessoa/máquina em 5 segundos de olhar. Canvas de estados + sub-faixa de apps,
// fallback tabular com os MESMOS intervalos (zero fetch extra) e rodapé de
// resumo vindo PRONTO do summary da API — nunca recalculado no front e nunca
// rotulado como registro de ponto.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import type { CSSProperties, ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  AlertTriangle,
  CalendarClock,
  ChevronLeft,
  ChevronRight,
  Globe,
  MonitorSmartphone,
  Table,
} from "lucide-react";
import { api } from "@/lib/api";
import { formatDuration, formatHm, localDateOf, stateLabels } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type { DeviceItem, MeResponse, PagedResponse, TimelineResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  TimelineCanvas,
  TIMELINE_CANVAS_HEIGHT,
  tzOffsetMinutes,
} from "@/components/timeline/TimelineCanvas";
import { TimelineTable } from "@/components/timeline/TimelineTable";

/** Soma `days` a uma data local yyyy-MM-dd (aritmética em UTC, imune a DST local). */
function addDays(dateStr: string, days: number): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d + days));
  const mm = String(dt.getUTCMonth() + 1).padStart(2, "0");
  const dd = String(dt.getUTCDate()).padStart(2, "0");
  return `${dt.getUTCFullYear()}-${mm}-${dd}`;
}

/** "terça-feira, 10 de junho" — rótulo humano do dia exibido. */
function formatDateLabel(dateStr: string): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  return new Intl.DateTimeFormat("pt-BR", {
    weekday: "long",
    day: "2-digit",
    month: "long",
    timeZone: "UTC",
  }).format(new Date(Date.UTC(y, m - 1, d)));
}

/** "GMT-4", "GMT+5:30" a partir do offset em minutos. */
function gmtLabel(offsetMin: number): string {
  const sign = offsetMin < 0 ? "-" : "+";
  const abs = Math.abs(offsetMin);
  const h = Math.floor(abs / 60);
  const m = abs % 60;
  return m === 0 ? `GMT${sign}${h}` : `GMT${sign}${h}:${String(m).padStart(2, "0")}`;
}

const deviceStatusSuffix: Record<DeviceItem["status"], string> = {
  active: "",
  paused: " · pausado",
  archived: " · arquivado",
  revoked: " · revogado",
};

/** Hachura diagonal vermelha do no_data — redundância NÃO-cromática (Seção 8.5). */
const noDataHatch: CSSProperties = {
  backgroundImage:
    "repeating-linear-gradient(45deg, #dc2626 0px, #dc2626 2px, #fecaca 2px, #fecaca 4px)",
};

export function LinhaDoTempoPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const deviceId = searchParams.get("device");

  const [dateOverride, setDateOverride] = useState<string | null>(null);
  // Janela default "Horário de trabalho" = 05:00–21:00 local (06–20 com 1h de
  // folga de cada lado). TODO(F3): substituir pela business_hours configurada
  // por tenant quando o backend a expuser.
  const [windowMode, setWindowMode] = useState<"work" | "full">("work");
  const [view, setView] = useState<"canvas" | "table">("canvas");

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;

  const devicesQuery = useQuery({
    queryKey: ["devices", { page_size: 100 }],
    queryFn: () => api<PagedResponse<DeviceItem>>("/devices?page_size=100"),
    staleTime: 60_000,
  });

  // Data default = dia corrente no FUSO DA ORGANIZAÇÃO (não no fuso do navegador).
  const todayStr = timezone !== null ? localDateOf(new Date(), timezone) : null;
  const dateStr = dateOverride ?? todayStr;
  const isToday = dateStr !== null && dateStr === todayStr;

  const timelineQuery = useQuery({
    queryKey: ["timeline", "device", deviceId, dateStr],
    queryFn: () =>
      api<TimelineResponse>(
        `/timeline/device?device_id=${encodeURIComponent(deviceId ?? "")}&date=${dateStr ?? ""}`,
      ),
    // Só dispara com device selecionado e fuso conhecido (data resolvida).
    enabled: deviceId !== null && dateStr !== null,
    // Polling de 60s APENAS quando a data exibida é hoje no fuso da org.
    refetchInterval: isToday ? 60_000 : false,
    refetchIntervalInBackground: false,
    // Nunca re-mostrar skeleton em refetch/troca de dia: mantém o desenho anterior.
    placeholderData: (prev) => prev,
  });
  const data = timelineQuery.data;

  const devices = useMemo(() => {
    const items = devicesQuery.data?.items ?? [];
    return [...items].sort((a, b) =>
      (a.display_name ?? a.hostname).localeCompare(b.display_name ?? b.hostname, "pt-BR"),
    );
  }, [devicesQuery.data]);

  const selectedDevice = devices.find((d) => d.id === deviceId);

  // Teclas ← → mudam o dia — exceto quando o foco está em campos de formulário.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent): void {
      if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
      const target = e.target;
      if (target instanceof HTMLElement) {
        const tag = target.tagName;
        if (tag === "INPUT" || tag === "SELECT" || tag === "TEXTAREA" || target.isContentEditable) {
          return;
        }
      }
      if (dateStr === null) return;
      const next = addDays(dateStr, e.key === "ArrowLeft" ? -1 : 1);
      if (todayStr !== null && next > todayStr) return; // sem futuro
      e.preventDefault();
      setDateOverride(next);
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [dateStr, todayStr]);

  function selectDevice(id: string): void {
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        if (id === "") {
          next.delete("device");
        } else {
          next.set("device", id);
        }
        return next;
      },
      { replace: true },
    );
  }

  function goToDate(next: string): void {
    if (todayStr !== null && next > todayStr) return; // a API não tem nada a dizer sobre amanhã
    setDateOverride(next);
  }

  const windowStartHour = windowMode === "work" ? 5 : 0;
  const windowEndHour = windowMode === "work" ? 21 : 24;

  // Badge de fuso divergente: offset reportado pelo device vs offset do tenant.
  const tenantOffsetMin =
    timezone !== null && data !== undefined
      ? tzOffsetMinutes(new Date(data.server_time), timezone)
      : null;
  const deviceTzBadge =
    data !== undefined &&
    data.device_tz_offset_min !== null &&
    tenantOffsetMin !== null &&
    data.device_tz_offset_min !== tenantOffsetMin
      ? gmtLabel(data.device_tz_offset_min)
      : null;

  const firstLoadFailed = timelineQuery.isError && data === undefined;

  return (
    <div className="space-y-6">
      {/* Cabeçalho */}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Linha do Tempo</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            O dia de uma máquina, hora a hora, no fuso da organização.
          </p>
        </div>
        {deviceTzBadge !== null && (
          <span
            title="convertido para o fuso da organização"
            className="inline-flex items-center gap-1.5 rounded-full border border-amber-300 bg-amber-50 px-2.5 py-0.5 text-xs text-amber-800"
          >
            <Globe className="h-3.5 w-3.5 shrink-0" aria-hidden />
            Máquina em {deviceTzBadge}
          </span>
        )}
      </div>

      {/* Controles: device, data (Hoje/Ontem/◀/▶ + teclas ← →), janela e visão */}
      <Card>
        <CardContent className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
          <select
            aria-label="Dispositivo"
            value={deviceId ?? ""}
            onChange={(e) => selectDevice(e.target.value)}
            className={cn(
              "h-9 min-w-[14rem] rounded-md border border-input bg-card px-3 text-sm",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
            )}
          >
            <option value="">Selecione um dispositivo…</option>
            {deviceId !== null && selectedDevice === undefined && (
              // Mantém o valor do query param válido enquanto a lista carrega.
              <option value={deviceId}>{data?.device_name ?? "Dispositivo selecionado"}</option>
            )}
            {devices.map((d) => (
              <option key={d.id} value={d.id}>
                {(d.display_name ?? d.hostname) + deviceStatusSuffix[d.status]}
              </option>
            ))}
          </select>
          {devicesQuery.isError && (
            <Button variant="outline" size="sm" onClick={() => void devicesQuery.refetch()}>
              Recarregar dispositivos
            </Button>
          )}

          <div className="flex items-center gap-1.5">
            {/* ◀ data ▶ como controle segmentado único (mesma altura h-9 de todos os controles) */}
            <div className="flex h-9 items-stretch overflow-hidden rounded-md border border-input bg-card">
              <button
                type="button"
                aria-label="Dia anterior (tecla ←)"
                title="Dia anterior (tecla ←)"
                disabled={dateStr === null}
                onClick={() => {
                  if (dateStr !== null) goToDate(addDays(dateStr, -1));
                }}
                className="flex w-8 items-center justify-center border-r border-input text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40"
              >
                <ChevronLeft className="h-4 w-4" aria-hidden />
              </button>
              <Input
                type="date"
                aria-label="Data"
                className="h-full w-36 rounded-none border-0 tabular-nums focus-visible:ring-0 focus-visible:ring-offset-0"
                value={dateStr ?? ""}
                max={todayStr ?? undefined}
                onChange={(e) => {
                  if (e.target.value !== "") goToDate(e.target.value);
                }}
              />
              <button
                type="button"
                aria-label="Próximo dia (tecla →)"
                title="Próximo dia (tecla →)"
                disabled={dateStr === null || isToday}
                onClick={() => {
                  if (dateStr !== null) goToDate(addDays(dateStr, 1));
                }}
                className="flex w-8 items-center justify-center border-l border-input text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40"
              >
                <ChevronRight className="h-4 w-4" aria-hidden />
              </button>
            </div>
            <Button
              variant="outline"
              size="sm"
              className="h-9"
              disabled={todayStr === null || isToday}
              onClick={() => setDateOverride(null)}
            >
              Hoje
            </Button>
            <Button
              variant="outline"
              size="sm"
              className="h-9"
              disabled={todayStr === null}
              onClick={() => {
                if (todayStr !== null) setDateOverride(addDays(todayStr, -1));
              }}
            >
              Ontem
            </Button>
          </div>

          <div
            role="group"
            aria-label="Janela de horário"
            className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
          >
            <button
              type="button"
              aria-pressed={windowMode === "work"}
              onClick={() => setWindowMode("work")}
              className={cn(
                "rounded-[5px] px-3 text-xs font-medium transition-colors",
                windowMode === "work"
                  ? "bg-primary/10 text-primary"
                  : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
              )}
            >
              Horário de trabalho
            </button>
            <button
              type="button"
              aria-pressed={windowMode === "full"}
              onClick={() => setWindowMode("full")}
              className={cn(
                "rounded-[5px] px-3 text-xs font-medium transition-colors",
                windowMode === "full"
                  ? "bg-primary/10 text-primary"
                  : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
              )}
            >
              24h
            </button>
          </div>

          {/* Fallback tabular obrigatório: alterna canvas↔tabela, mesmos intervalos. */}
          <Button
            variant="outline"
            size="sm"
            className="ml-auto h-9"
            onClick={() => setView((v) => (v === "canvas" ? "table" : "canvas"))}
          >
            {view === "canvas" ? (
              <>
                <Table className="h-4 w-4" aria-hidden />
                Ver como tabela
              </>
            ) : (
              <>
                <CalendarClock className="h-4 w-4" aria-hidden />
                Ver como gráfico
              </>
            )}
          </Button>
        </CardContent>
      </Card>

      {/* Aviso global de trechos incompletos (data_incomplete da resposta). */}
      {data?.data_incomplete === true && (
        <div
          role="status"
          className="flex items-center gap-2 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800"
        >
          <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
          Há trechos com dados incompletos neste dia.
        </div>
      )}

      {/* Falha de refetch com dado em cache: aviso inline, desenho anterior preservado. */}
      {timelineQuery.isError && data !== undefined && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void timelineQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      {deviceId === null ? (
        // Estado vazio 8.9: sem device selecionado → instrução.
        <Card>
          <CardContent className="flex flex-col items-center gap-3 px-6 py-14 text-center">
            <span className="flex h-14 w-14 items-center justify-center rounded-full bg-muted">
              <MonitorSmartphone className="h-7 w-7 text-muted-foreground" aria-hidden />
            </span>
            <p className="text-base font-medium">Selecione um dispositivo</p>
            <p className="text-sm text-muted-foreground">
              Escolha uma máquina no seletor acima para reconstruir o dia dela na linha do tempo.
            </p>
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardHeader className="pb-4">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <CardTitle className="text-base">
                {data?.device_name ?? selectedDevice?.display_name ?? selectedDevice?.hostname ?? "Dispositivo"}
              </CardTitle>
              {dateStr !== null && (
                <span className="text-sm text-muted-foreground">
                  {formatDateLabel(dateStr)}
                  {isToday && " · hoje"}
                </span>
              )}
            </div>
          </CardHeader>
          <CardContent>
            {firstLoadFailed ? (
              // Erro sem nenhum dado: estado inline no widget com retry.
              <div
                className="flex flex-col items-center justify-center gap-3 text-center"
                style={{ minHeight: TIMELINE_CANVAS_HEIGHT }}
              >
                <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
                <p className="text-sm text-muted-foreground">
                  {genericErrorMessage(timelineQuery.error)}
                </p>
                <Button variant="outline" onClick={() => void timelineQuery.refetch()}>
                  Tentar novamente
                </Button>
              </div>
            ) : view === "canvas" ? (
              <>
                <div className={cn(timelineQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
                  {timezone !== null && dateStr !== null ? (
                    // Loading: eixo+lanes desenhados imediatamente (pending);
                    // placeholderData mantém o desenho do dia anterior no refetch.
                    <TimelineCanvas
                      intervals={data?.intervals ?? []}
                      timezone={timezone}
                      windowStartHour={windowStartHour}
                      windowEndHour={windowEndHour}
                      date={dateStr}
                      isToday={isToday}
                      serverTime={data?.server_time ?? null}
                      pending={data === undefined}
                    />
                  ) : (
                    <Skeleton className="w-full" style={{ height: TIMELINE_CANVAS_HEIGHT }} />
                  )}
                </div>
                {/* Fallback de screen reader: a mesma tabela, invisível. */}
                {timezone !== null && data !== undefined && (
                  <div className="sr-only">
                    <TimelineTable intervals={data.intervals} timezone={timezone} />
                  </div>
                )}
                <Legend />
              </>
            ) : timezone !== null ? (
              <TimelineTable intervals={data?.intervals ?? []} timezone={timezone} />
            ) : (
              <Skeleton className="w-full" style={{ height: TIMELINE_CANVAS_HEIGHT }} />
            )}

            {/* Estado vazio 8.9: device existe, dia sem intervalos. */}
            {!firstLoadFailed && data !== undefined && data.intervals.length === 0 && (
              <div className="mt-4 flex flex-wrap items-center justify-center gap-3 text-sm text-muted-foreground">
                <span>{data.device_name} não ligou neste dia.</span>
                {dateStr !== null && (
                  <Button variant="outline" size="sm" onClick={() => goToDate(addDays(dateStr, -1))}>
                    Ver ontem
                  </Button>
                )}
              </div>
            )}

            {/* Rodapé de resumo: vem PRONTO do summary da API — nunca recalculado
                no front; travessão quando o evento é null. Sem rótulos de ponto
                ou expediente: sempre "Primeiro evento" / "Último evento". */}
            <div className="mt-4 border-t pt-3">
              {data !== undefined && timezone !== null ? (
                <p className="text-sm tabular-nums text-muted-foreground">
                  Primeiro evento{" "}
                  {data.summary.first_event_at !== null
                    ? formatHm(data.summary.first_event_at, timezone)
                    : "—"}
                  {" · "}Último evento{" "}
                  {data.summary.last_event_at !== null
                    ? formatHm(data.summary.last_event_at, timezone)
                    : "—"}
                  {" · "}Ligada {formatDuration(data.summary.seconds_on)}
                  {" · "}Ativa {formatDuration(data.summary.seconds_active)}
                  {" · "}Ociosa {formatDuration(data.summary.seconds_idle)}
                  {" · "}Bloqueada {formatDuration(data.summary.seconds_locked)}
                </p>
              ) : (
                <Skeleton className="h-5 w-full max-w-2xl" />
              )}
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  );
}

/** Legenda dos estados do canvas, com as mesmas redundâncias não-cromáticas. */
function Legend() {
  return (
    <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-muted-foreground">
      <LegendItem
        swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm bg-[#16a34a]" />}
        label={stateLabels.active}
      />
      <LegendItem
        swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm bg-[#d97706]" />}
        label={stateLabels.idle}
      />
      <LegendItem
        swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm bg-[#64748b]" />}
        label={stateLabels.locked}
      />
      <LegendItem
        swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm border-2 border-[#9ca3af]" />}
        label={stateLabels.off_clean}
      />
      <LegendItem
        swatch={
          <span className="flex items-center gap-1">
            <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={noDataHatch} />
            <AlertTriangle className="h-3 w-3 shrink-0 text-red-600" aria-hidden />
          </span>
        }
        label={stateLabels.no_data}
      />
      <LegendItem
        swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm bg-[#94a3b8]" />}
        label="Faixa de baixo: apps em uso (não categorizado)"
      />
    </div>
  );
}

function LegendItem({ swatch, label }: { swatch: ReactNode; label: string }) {
  return (
    <span className="flex items-center gap-1.5">
      {swatch}
      <span>{label}</span>
    </span>
  );
}
