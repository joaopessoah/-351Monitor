// =============================================================================
// Linha do Tempo (Seção 8.5): reconstruir o dia em 5 segundos de olhar.
// - Modo EQUIPE (F3.4, default sem ?device= na URL): uma lane de 28px por
//   device via GET /timeline/team; clique na lane leva ao modo device.
// - Modo DEVICE (F2, com ?device=): canvas de estados + sub-faixa de apps e
//   rodapé de resumo vindo PRONTO do summary da API - nunca recalculado no
//   front e nunca rotulado como registro de ponto.
// Ambos com fallback tabular com os MESMOS intervalos (zero fetch extra).
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import type { CSSProperties, ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  AlertTriangle,
  CalendarClock,
  ChevronLeft,
  ChevronRight,
  Globe,
  Table,
  Users,
} from "lucide-react";
import { api } from "@/lib/api";
import {
  formatDuration,
  formatHm,
  gmtLabel,
  localDateOf,
  parseHmToMinutes,
  stateLabels,
} from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type {
  DeviceItem,
  MeResponse,
  PagedResponse,
  TeamTimelineResponse,
  TimelineResponse,
} from "@/lib/types";
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
import {
  TeamTimelineCanvas,
  teamTimelineCanvasHeight,
} from "@/components/timeline/TeamTimelineCanvas";
import { TeamTimelineTable, TimelineTable } from "@/components/timeline/TimelineTable";

/** Soma `days` a uma data local yyyy-MM-dd (aritmética em UTC, imune a DST local). */
function addDays(dateStr: string, days: number): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d + days));
  const mm = String(dt.getUTCMonth() + 1).padStart(2, "0");
  const dd = String(dt.getUTCDate()).padStart(2, "0");
  return `${dt.getUTCFullYear()}-${mm}-${dd}`;
}

/** "terça-feira, 10 de junho" - rótulo humano do dia exibido. */
function formatDateLabel(dateStr: string): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  return new Intl.DateTimeFormat("pt-BR", {
    weekday: "long",
    day: "2-digit",
    month: "long",
    timeZone: "UTC",
  }).format(new Date(Date.UTC(y, m - 1, d)));
}

const deviceStatusSuffix: Record<DeviceItem["status"], string> = {
  active: "",
  paused: " · pausado",
  archived: " · arquivado",
  revoked: " · revogado",
};

/** Hachura diagonal vermelha do no_data - redundância NÃO-cromática (Seção 8.5). */
const noDataHatch: CSSProperties = {
  backgroundImage:
    "repeating-linear-gradient(45deg, #dc2626 0px, #dc2626 2px, #fecaca 2px, #fecaca 4px)",
};

// Classes do grupo segmentado (mesmo padrão visual do toggle de janela).
const segmentedButton = "rounded-[5px] px-3 text-xs font-medium transition-colors";
const segmentedOn = "bg-primary/10 text-primary";
const segmentedOff = "text-muted-foreground hover:bg-accent hover:text-accent-foreground";

// Altura do skeleton do modo equipe antes da primeira resposta (n de lanes
// desconhecido): chuta 6 lanes - NÃO usa TIMELINE_CANVAS_HEIGHT (modo device).
const TEAM_SKELETON_LANES = 6;

export function LinhaDoTempoPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const deviceId = searchParams.get("device");
  // Modo derivado da URL: sem ?device= a página abre direto em modo EQUIPE;
  // com ?device= é o modo device (F2) intacto.
  const mode: "team" | "device" = deviceId !== null ? "device" : "team";

  const [dateOverride, setDateOverride] = useState<string | null>(null);
  // Janela "Horário de trabalho" = business_hours da org com 1h de folga de
  // cada lado (Seção 8.5); fallback 05:00-21:00 quando a org não definiu.
  const [windowMode, setWindowMode] = useState<"work" | "full">("work");
  const [view, setView] = useState<"canvas" | "table">("canvas");

  // Último device visitado: o segmento "Dispositivo" volta para ele.
  const lastDeviceRef = useRef<string | null>(null);
  useEffect(() => {
    if (deviceId !== null) lastDeviceRef.current = deviceId;
  }, [deviceId]);

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const businessHours = meQuery.data?.organization.business_hours ?? null;

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
  // placeholderData carrega dados entre chaves de query: o guard por modo evita
  // que o desenho do último device vaze para o modo equipe (e vice-versa).
  const data = mode === "device" ? timelineQuery.data : undefined;

  const teamQuery = useQuery({
    queryKey: ["timeline", "team", dateStr],
    queryFn: () => api<TeamTimelineResponse>(`/timeline/team?date=${dateStr ?? ""}`),
    enabled: mode === "team" && dateStr !== null,
    // Polling de 60s SÓ quando hoje (dias passados são imutáveis, N18).
    refetchInterval: isToday ? 60_000 : false,
    refetchIntervalInBackground: false,
    placeholderData: (prev) => prev,
  });
  const teamData = mode === "team" ? teamQuery.data : undefined;

  const devices = useMemo(() => {
    const items = devicesQuery.data?.items ?? [];
    return [...items].sort((a, b) =>
      (a.display_name ?? a.hostname).localeCompare(b.display_name ?? b.hostname, "pt-BR"),
    );
  }, [devicesQuery.data]);

  const selectedDevice = devices.find((d) => d.id === deviceId);

  // Teclas ← → mudam o dia - exceto quando o foco está em campos de formulário.
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

  // Segmento "Dispositivo": volta ao último device visitado; sem histórico,
  // cai no primeiro da lista ordenada.
  function showDeviceMode(): void {
    if (deviceId !== null) return;
    const remembered = lastDeviceRef.current;
    const target =
      remembered !== null && devices.some((d) => d.id === remembered)
        ? remembered
        : devices[0]?.id;
    if (target !== undefined) selectDevice(target);
  }

  function goToDate(next: string): void {
    if (todayStr !== null && next > todayStr) return; // a API não tem nada a dizer sobre amanhã
    setDateOverride(next);
  }

  // Janela "Horário de trabalho": business_hours do tenant arredondada para a
  // hora cheia, com 1h de folga de cada lado; fallback 05-21 sem configuração.
  const workWindow = useMemo(() => {
    if (businessHours !== null) {
      const startMin = parseHmToMinutes(businessHours.start);
      const endMin = parseHmToMinutes(businessHours.end);
      if (startMin !== null && endMin !== null && endMin > startMin) {
        return {
          start: Math.max(Math.floor(startMin / 60) - 1, 0),
          end: Math.min(Math.ceil(endMin / 60) + 1, 24),
        };
      }
    }
    return { start: 5, end: 21 };
  }, [businessHours]);

  const windowStartHour = windowMode === "work" ? workWindow.start : 0;
  const windowEndHour = windowMode === "work" ? workWindow.end : 24;

  // Badge de fuso divergente (modo device): offset do device vs offset do tenant.
  // No modo equipe o badge por lane fica no tooltip do canvas.
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

  const deviceLoadFailed = mode === "device" && timelineQuery.isError && data === undefined;
  const teamLoadFailed = mode === "team" && teamQuery.isError && teamData === undefined;
  const teamSkeletonHeight = teamTimelineCanvasHeight(TEAM_SKELETON_LANES);

  return (
    <div className="space-y-6">
      {/* Cabeçalho */}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Linha do Tempo</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            O dia da equipe ou de uma máquina, hora a hora, no fuso da organização.
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

      {/* Controles: modo (Equipe | Dispositivo), device, data (Hoje/Ontem/◀/▶ +
          teclas ← →), janela e visão */}
      <Card>
        {/* div com padding explícito: o p-6/pt-0 default do CardContent venceria o py-3
            (cn() sem tailwind-merge - a ordem do stylesheet decide, não a da className) */}
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
          <div
            role="group"
            aria-label="Modo de visualização"
            className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
          >
            <button
              type="button"
              aria-pressed={mode === "team"}
              onClick={() => selectDevice("")}
              className={cn(segmentedButton, mode === "team" ? segmentedOn : segmentedOff)}
            >
              <span className="flex items-center gap-1.5">
                <Users className="h-3.5 w-3.5 shrink-0" aria-hidden />
                Equipe
              </span>
            </button>
            <button
              type="button"
              aria-pressed={mode === "device"}
              disabled={mode === "team" && devices.length === 0}
              onClick={showDeviceMode}
              className={cn(
                segmentedButton,
                mode === "device" ? segmentedOn : segmentedOff,
                "disabled:pointer-events-none disabled:opacity-40",
              )}
            >
              Dispositivo
            </button>
          </div>

          {mode === "device" && (
            <select
              aria-label="Dispositivo"
              value={deviceId ?? ""}
              onChange={(e) => selectDevice(e.target.value)}
              className={cn(
                "h-9 min-w-[14rem] rounded-md border border-input bg-card px-3 text-sm",
                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
              )}
            >
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
          )}
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
              className={cn(segmentedButton, windowMode === "work" ? segmentedOn : segmentedOff)}
            >
              Horário de trabalho
            </button>
            <button
              type="button"
              aria-pressed={windowMode === "full"}
              onClick={() => setWindowMode("full")}
              className={cn(segmentedButton, windowMode === "full" ? segmentedOn : segmentedOff)}
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
        </div>
      </Card>

      {/* Aviso global de trechos incompletos (data_incomplete da resposta do device). */}
      {data?.data_incomplete === true && (
        <div
          role="status"
          className="flex items-center gap-2 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800"
        >
          <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
          Há trechos com dados incompletos neste dia.
        </div>
      )}

      {/* Cap N21 atingido: o servidor parou de adicionar lanes inteiras. */}
      {teamData?.truncated === true && (
        <div
          role="status"
          className="flex items-center gap-2 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800"
        >
          <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
          Dia com dados demais - mostrando os {teamData.lanes.length} primeiros dispositivos.
        </div>
      )}

      {/* Falha de refetch com dado em cache: aviso inline, desenho anterior preservado. */}
      {mode === "device" && timelineQuery.isError && data !== undefined && (
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
      {mode === "team" && teamQuery.isError && teamData !== undefined && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void teamQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      {mode === "team" ? (
        <Card>
          <CardHeader className="pb-4">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <CardTitle className="text-base">
                Equipe
                {teamData !== undefined &&
                  teamData.lanes.length > 0 &&
                  ` · ${teamData.lanes.length} ${teamData.lanes.length === 1 ? "dispositivo" : "dispositivos"}`}
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
            {teamLoadFailed ? (
              // Erro sem nenhum dado: estado inline no widget com retry.
              <div
                className="flex flex-col items-center justify-center gap-3 text-center"
                style={{ minHeight: teamSkeletonHeight }}
              >
                <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
                <p className="text-sm text-muted-foreground">
                  {genericErrorMessage(teamQuery.error)}
                </p>
                <Button variant="outline" onClick={() => void teamQuery.refetch()}>
                  Tentar novamente
                </Button>
              </div>
            ) : teamData !== undefined && teamData.lanes.length === 0 ? (
              // Estado vazio 8.9: tenant sem devices não-arquivados.
              <div className="flex flex-col items-center gap-3 px-6 py-14 text-center">
                <span className="flex h-14 w-14 items-center justify-center rounded-full bg-muted">
                  <Users className="h-7 w-7 text-muted-foreground" aria-hidden />
                </span>
                <p className="text-base font-medium">Nenhum dispositivo para mostrar</p>
                <p className="text-sm text-muted-foreground">
                  Quando houver dispositivos ativos na organização, cada um aparece aqui como uma
                  linha do dia.
                </p>
              </div>
            ) : view === "canvas" ? (
              <>
                <div className={cn(teamQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
                  {timezone !== null && dateStr !== null && teamData !== undefined ? (
                    <TeamTimelineCanvas
                      lanes={teamData.lanes}
                      timezone={timezone}
                      windowStartHour={windowStartHour}
                      windowEndHour={windowEndHour}
                      date={dateStr}
                      isToday={isToday}
                      serverTime={teamData.server_time}
                      onSelectDevice={selectDevice}
                    />
                  ) : (
                    // Antes da 1ª resposta o nº de lanes é desconhecido: skeleton.
                    <Skeleton className="w-full" style={{ height: teamSkeletonHeight }} />
                  )}
                </div>
                {/* Fallback de screen reader: a mesma tabela agrupada, invisível. */}
                {timezone !== null && teamData !== undefined && (
                  <div className="sr-only">
                    <TeamTimelineTable lanes={teamData.lanes} timezone={timezone} />
                  </div>
                )}
                <Legend showAppLane={false} />
              </>
            ) : timezone !== null && teamData !== undefined ? (
              <TeamTimelineTable lanes={teamData.lanes} timezone={timezone} />
            ) : (
              <Skeleton className="w-full" style={{ height: teamSkeletonHeight }} />
            )}
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
            {deviceLoadFailed ? (
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
            {!deviceLoadFailed && data !== undefined && data.intervals.length === 0 && (
              <div className="mt-4 flex flex-wrap items-center justify-center gap-3 text-sm text-muted-foreground">
                <span>{data.device_name} não ligou neste dia.</span>
                {dateStr !== null && (
                  <Button variant="outline" size="sm" onClick={() => goToDate(addDays(dateStr, -1))}>
                    Ver ontem
                  </Button>
                )}
              </div>
            )}

            {/* Rodapé de resumo: vem PRONTO do summary da API - nunca recalculado
                no front; travessão quando o evento é null. Sem rótulos de ponto
                ou expediente: sempre "Primeiro evento" / "Último evento". */}
            <div className="mt-4 border-t pt-3">
              {data !== undefined && timezone !== null ? (
                <p className="text-sm tabular-nums text-muted-foreground">
                  Primeiro evento{" "}
                  {data.summary.first_event_at !== null
                    ? formatHm(data.summary.first_event_at, timezone)
                    : "-"}
                  {" · "}Último evento{" "}
                  {data.summary.last_event_at !== null
                    ? formatHm(data.summary.last_event_at, timezone)
                    : "-"}
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

/** Legenda dos estados do canvas, com as mesmas redundâncias não-cromáticas.
    `showAppLane=false` no modo equipe (lá não existe sub-faixa de apps). */
function Legend({ showAppLane = true }: { showAppLane?: boolean }) {
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
      {showAppLane && (
        <LegendItem
          swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm bg-[#94a3b8]" />}
          label="Faixa de baixo: apps em uso (não categorizado)"
        />
      )}
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
