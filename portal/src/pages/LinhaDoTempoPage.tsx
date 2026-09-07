// =============================================================================
// Linha do Tempo (Seção 8.5 + Seção 3 do spec de produtividade de 07/09/2026):
// reconstruir o mês, o dia e a máquina em 5 segundos de olhar. TRÊS níveis num
// seletor segmentado, todos com estado na URL (?nivel=, ?date=, ?window=,
// ?view=, ?tag=, ?device= - sempre replace, sem histórico):
//
// - MÊS (?nivel=mes): mapa de calor por colaborador × DIA (ordem ALFABÉTICA,
//   jamais por índice - decisão 2 do spec). Clique numa célula abre o nível
//   DIA naquela data. O mês inteiro vem de UMA consulta a /people/daily, que
//   é a quebra (pessoa, dia) que a listagem agregada de /people não tem.
// - DIA (?nivel=dia, DEFAULT): as faixas do dia com o resumo à direita e a
//   navegação de dia (◀ ▶, Hoje, Ontem, teclas ← →). Clique numa faixa abre o
//   nível DISPOSITIVO daquela máquina. Ver DayLanesPanel.tsx para o porquê de
//   a faixa ainda ser por dispositivo e de o índice por pessoa vir do /people.
// - DISPOSITIVO (?nivel=dispositivo, ou ?device= em links antigos): o canvas de
//   estados + sub-faixa de apps e o rodapé de resumo vindo PRONTO do summary
//   da API - comportamento intacto, nunca recalculado no front e nunca
//   rotulado como registro de ponto.
//
// Os três níveis têm fallback tabular obrigatório com os MESMOS números.
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import type { CSSProperties, ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  AlertTriangle,
  CalendarClock,
  CalendarRange,
  ChevronLeft,
  ChevronRight,
  Globe,
  Monitor,
  Table,
  Users,
} from "lucide-react";
import { api } from "@/lib/api";
import {
  addDays,
  formatDuration,
  formatHm,
  gmtLabel,
  isIsoDate,
  localDateOf,
  parseHmToMinutes,
  stateLabels,
} from "@/lib/format";
import { PREF_TIMELINE_VIEW, readPref, writePref } from "@/lib/prefs";
import { TAG_CODEC, TeamTagSelect, tagParam, useTeamTags } from "@/components/filters/TeamTagSelect";
import { useUrlState } from "@/lib/useUrlState";
import type { UrlStateCodec } from "@/lib/useUrlState";
import { genericErrorMessage } from "@/lib/messages";
import type {
  DeviceItem,
  MeResponse,
  PagedResponse,
  PeopleDailyResponse,
  PeopleReportResponse,
  PersonDay,
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
import { teamTimelineCanvasHeight } from "@/components/timeline/TeamTimelineCanvas";
import { TeamTimelineTable, TimelineTable } from "@/components/timeline/TimelineTable";
import {
  DayLanesPanel,
  DayLanesSummaryTable,
  DayPeopleSummary,
} from "@/components/timeline/DayLanesPanel";
import {
  MonthHeatmap,
  MonthHeatmapLegend,
  MonthHeatmapSkeleton,
  MonthTable,
} from "@/components/timeline/MonthHeatmap";
import type { MonthPersonRow } from "@/components/timeline/MonthHeatmap";

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

/** Primeiro dia do mês de `dateStr`. */
function monthFirst(dateStr: string): string {
  return `${dateStr.slice(0, 7)}-01`;
}

/** Último dia do mês de `dateStr` (dia 0 do mês seguinte, aritmética em UTC). */
function monthLast(dateStr: string): string {
  const [y, m] = dateStr.split("-").map(Number);
  const dt = new Date(Date.UTC(y, m, 0));
  return `${dt.getUTCFullYear()}-${String(dt.getUTCMonth() + 1).padStart(2, "0")}-${String(
    dt.getUTCDate(),
  ).padStart(2, "0")}`;
}

/** Soma `n` meses e devolve o DIA 1 do mês resultante (cursor do nível mês). */
function addMonths(dateStr: string, n: number): string {
  const [y, m] = dateStr.split("-").map(Number);
  const dt = new Date(Date.UTC(y, m - 1 + n, 1));
  return `${dt.getUTCFullYear()}-${String(dt.getUTCMonth() + 1).padStart(2, "0")}-01`;
}

/** "setembro de 2026". */
function monthLabel(dateStr: string): string {
  return new Intl.DateTimeFormat("pt-BR", { month: "long", year: "numeric", timeZone: "UTC" })
    .format(new Date(`${monthFirst(dateStr)}T00:00:00Z`));
}

/**
 * Dias do mês de `dateStr`, do dia 1 até hoje (nunca além): são as COLUNAS do
 * mapa. O recorte por hoje é o que impede a tela de desenhar coluna de dia que
 * ainda não aconteceu - e mantém a janela sempre dentro dos 92 dias do
 * endpoint, já que um mês tem no máximo 31.
 */
function monthDays(dateStr: string, todayStr: string): string[] {
  const first = monthFirst(dateStr);
  const last = monthLast(dateStr);
  const end = last > todayStr ? todayStr : last;
  if (end < first) return [];
  const days: string[] = [];
  for (let cursor = first; cursor <= end; cursor = addDays(cursor, 1)) days.push(cursor);
  return days;
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

/** Hachura a 45° do ocioso - o MESMO par de cores do canvas (COLOR.idle/idleHatch). */
const idleHatch: CSSProperties = {
  backgroundImage: "repeating-linear-gradient(45deg, #3A455C 0 2px, #4E5C78 2px 4px)",
};

// Classes do grupo segmentado (mesmo padrão visual do toggle de janela).
const segmentedButton = "rounded-[5px] px-3 text-xs font-medium transition-colors";
const segmentedOn = "bg-primary/10 text-primary";
const segmentedOff = "text-muted-foreground hover:bg-accent hover:text-accent-foreground";

// Altura do skeleton do modo equipe antes da primeira resposta (n de lanes
// desconhecido): chuta 6 lanes - NÃO usa TIMELINE_CANVAS_HEIGHT (modo device).
const TEAM_SKELETON_LANES = 6;

/** Teto de pessoas por consulta ao /people (MaxPageSize do backend). */
const PEOPLE_PAGE_SIZE = 200;

type TimelineView = "canvas" | "table";
type Nivel = "mes" | "dia" | "dispositivo";

/**
 * Navegação da tela num ÚNICO codec: nível, dispositivo e data são
 * interdependentes (clicar numa célula do mês muda nível E data no mesmo tick)
 * e o setSearchParams do react-router não é batched - duas escritas no mesmo
 * tick se atropelariam (ver lib/useUrlState.ts).
 *
 * Compatibilidade: link antigo com ?device= e SEM ?nivel= abre no nível
 * dispositivo, como abria antes.
 */
interface TimelineNav {
  nivel: Nivel;
  /** Só no nível dispositivo; null nos outros (o parâmetro sai da URL). */
  device: string | null;
  /** null = hoje no fuso da organização. */
  date: string | null;
}

const NAV_CODEC: UrlStateCodec<TimelineNav> = {
  parse: (params) => {
    const rawNivel = params.get("nivel");
    const rawDevice = params.get("device");
    const device = rawDevice !== null && rawDevice !== "" ? rawDevice : null;
    const nivel: Nivel =
      rawNivel === "mes" || rawNivel === "dia" || rawNivel === "dispositivo"
        ? rawNivel
        : device !== null
          ? "dispositivo"
          : "dia";
    const rawDate = params.get("date");
    return {
      nivel,
      device: nivel === "dispositivo" ? device : null,
      date: rawDate !== null && isIsoDate(rawDate) ? rawDate : null,
    };
  },
  serialize: (value) => ({
    nivel: value.nivel === "dia" ? null : value.nivel,
    device: value.nivel === "dispositivo" ? value.device : null,
    date: value.date,
  }),
};

// ?window= work|full; "work" é o default e some da URL.
const WINDOW_CODEC: UrlStateCodec<"work" | "full"> = {
  parse: (params) => (params.get("window") === "full" ? "full" : "work"),
  serialize: (value) => ({ window: value === "full" ? "full" : null }),
};

export function LinhaDoTempoPage() {
  const [nav, setNav] = useUrlState(NAV_CODEC);
  const { nivel } = nav;
  const deviceId = nav.device;

  // Janela "Horário de trabalho" = business_hours da org com 1h de folga de
  // cada lado (Seção 8.5); fallback 05:00-21:00 quando a org não definiu.
  const [windowMode, setWindowMode] = useUrlState(WINDOW_CODEC);
  // Recorte de equipe dos níveis MÊS e DIA (?tag=): o nível dispositivo já é um
  // recorte de uma máquina só, então o seletor não aparece lá.
  const [teamTag, setTeamTag] = useUrlState(TAG_CODEC);
  const { tags } = useTeamTags();

  // Visão default sem ?view= na URL: preferência salva do navegador; sem
  // preferência, telas estreitas (até 768px) abrem direto na tabela - o canvas
  // depende de hover fino. Congelada no mount (o codec precisa ser estável).
  const [defaultView] = useState<TimelineView>(() => {
    const stored = readPref(PREF_TIMELINE_VIEW);
    if (stored === "table" || stored === "canvas") return stored;
    try {
      if (window.matchMedia("(max-width: 768px)").matches) return "table";
    } catch {
      // matchMedia indisponível: mantém o default de desktop.
    }
    return "canvas";
  });
  const viewCodec = useMemo<UrlStateCodec<TimelineView>>(
    () => ({
      parse: (params) => {
        const raw = params.get("view");
        return raw === "table" || raw === "canvas" ? raw : defaultView;
      },
      serialize: (value) => ({ view: value === defaultView ? null : value }),
    }),
    [defaultView],
  );
  const [view, setView] = useUrlState(viewCodec);

  /** Alterna canvas↔tabela, gravando a escolha como default do navegador. */
  function toggleView(): void {
    const next: TimelineView = view === "canvas" ? "table" : "canvas";
    writePref(PREF_TIMELINE_VIEW, next);
    setView(next);
  }

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
  // ?date= no futuro (URL digitada) cai para hoje - a API não tem nada a dizer.
  const todayStr = timezone !== null ? localDateOf(new Date(), timezone) : null;
  const dateOverride = nav.date !== null && todayStr !== null && nav.date > todayStr ? null : nav.date;
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
  // placeholderData carrega dados entre chaves de query: o guard por nível evita
  // que o desenho do último device vaze para o nível dia (e vice-versa).
  const data = nivel === "dispositivo" ? timelineQuery.data : undefined;

  const teamQuery = useQuery({
    queryKey: ["timeline", "team", dateStr, teamTag],
    queryFn: () =>
      api<TeamTimelineResponse>(`/timeline/team?date=${dateStr ?? ""}${tagParam(teamTag)}`),
    enabled: nivel === "dia" && dateStr !== null,
    // Polling de 60s SÓ quando hoje (dias passados são imutáveis, N18).
    refetchInterval: isToday ? 60_000 : false,
    refetchIntervalInBackground: false,
    placeholderData: (prev) => prev,
  });
  const teamData = nivel === "dia" ? teamQuery.data : undefined;

  // Resumo do dia POR PESSOA (com o índice do servidor): uma consulta só, o que
  // a lane do /timeline/team ainda não consegue entregar por pessoa.
  const dayPeopleQuery = useQuery({
    queryKey: ["timeline", "day-people", dateStr, teamTag],
    queryFn: () =>
      api<PeopleReportResponse>(
        `/people?from=${dateStr ?? ""}&to=${dateStr ?? ""}&page_size=${PEOPLE_PAGE_SIZE}${tagParam(teamTag)}`,
      ),
    enabled: nivel === "dia" && dateStr !== null,
    staleTime: 60_000,
    placeholderData: (prev) => prev,
  });
  const dayPeople = nivel === "dia" ? dayPeopleQuery.data : undefined;

  // ------------------------------------------------------------------ nível mês
  const monthDaysList = useMemo(
    () => (dateStr !== null && todayStr !== null ? monthDays(dateStr, todayStr) : []),
    [dateStr, todayStr],
  );
  const monthRange = useMemo(() => {
    if (dateStr === null || todayStr === null) return null;
    const first = monthFirst(dateStr);
    const last = monthLast(dateStr);
    return { first, end: last > todayStr ? todayStr : last };
  }, [dateStr, todayStr]);

  // O MAPA sai de UMA consulta: /people/daily devolve (pessoa, dia) do mês
  // inteiro de uma vez. A segunda chamada, ao /people agregado, não é do mapa -
  // é do TOTAL do mês (índice e horas do período), que o portal não pode
  // derivar dos dias porque índice não se soma nem se tira média; e é dela que
  // vem o `total` do aviso de truncagem.
  const monthQuery = useQuery({
    queryKey: ["timeline", "month", monthRange?.first ?? null, monthRange?.end ?? null, teamTag],
    queryFn: async () => {
      const range = monthRange as { first: string; end: string };
      const [daily, total] = await Promise.all([
        api<PeopleDailyResponse>(
          `/people/daily?from=${range.first}&to=${range.end}${tagParam(teamTag)}`,
        ),
        api<PeopleReportResponse>(
          `/people?from=${range.first}&to=${range.end}&page_size=${PEOPLE_PAGE_SIZE}${tagParam(teamTag)}`,
        ),
      ]);
      return { daily, total };
    },
    enabled: nivel === "mes" && monthRange !== null && monthDaysList.length > 0,
    staleTime: 60_000,
    placeholderData: (prev) => prev,
  });
  const monthData = nivel === "mes" ? monthQuery.data : undefined;

  const monthRows = useMemo<MonthPersonRow[]>(() => {
    if (monthData === undefined) return [];
    // Um índice (sid -> dia -> linha) porque a resposta é uma lista plana de
    // (pessoa, dia) e o mapa precisa de acesso por coluna.
    const bySid = new Map<string, Map<string, PersonDay>>();
    for (const day of monthData.daily.items) {
      let porDia = bySid.get(day.windows_sid);
      if (porDia === undefined) {
        porDia = new Map<string, PersonDay>();
        bySid.set(day.windows_sid, porDia);
      }
      porDia.set(day.date, day);
    }
    // Ordem ALFABÉTICA, explícita: é a ordem que o backend já devolve por
    // default, mas a regra "sem ranking" não pode depender de um default de API.
    return [...monthData.total.items]
      .sort((a, b) => a.display_name.localeCompare(b.display_name, "pt-BR"))
      .map((person) => {
        const porDia = bySid.get(person.windows_sid);
        return {
          sid: person.windows_sid,
          displayName: person.display_name,
          month: person,
          // dia sem registro fica null - o mapa desenha ausência, não zero
          cells: monthDaysList.map((date) => porDia?.get(date) ?? null),
        };
      });
  }, [monthData, monthDaysList]);

  const monthTruncated =
    monthData !== undefined && monthData.total.total > monthData.total.items.length;

  const devices = useMemo(() => {
    const items = devicesQuery.data?.items ?? [];
    return [...items].sort((a, b) =>
      (a.display_name ?? a.hostname).localeCompare(b.display_name ?? b.hostname, "pt-BR"),
    );
  }, [devicesQuery.data]);

  const selectedDevice = devices.find((d) => d.id === deviceId);

  /** Troca a data mantendo nível e dispositivo; nunca navega para o futuro. */
  function goToDate(next: string | null): void {
    if (next !== null && todayStr !== null && next > todayStr) return;
    setNav({ ...nav, date: next });
  }

  /** Abre o nível DIA numa data (clique numa célula do mapa do mês). */
  function goToDay(date: string): void {
    setNav({ nivel: "dia", device: null, date: todayStr !== null && date > todayStr ? null : date });
  }

  /** Abre o nível DISPOSITIVO de uma máquina (clique numa faixa do dia). */
  function goToDevice(id: string): void {
    if (id === "") return;
    setNav({ nivel: "dispositivo", device: id, date: nav.date });
  }

  /** Seletor de nível: "dispositivo" precisa de uma máquina para ter o que mostrar. */
  function goToNivel(next: Nivel): void {
    if (next === nivel) return;
    if (next === "dispositivo") {
      const remembered = lastDeviceRef.current;
      const target =
        remembered !== null && devices.some((d) => d.id === remembered) ? remembered : devices[0]?.id;
      if (target === undefined) return;
      setNav({ nivel: "dispositivo", device: target, date: nav.date });
      return;
    }
    setNav({ nivel: next, device: null, date: nav.date });
  }

  // Teclas ← → mudam o recorte (um DIA nos níveis dia/dispositivo, um MÊS no
  // nível mês) - exceto quando o foco está em campos de formulário.
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
      const step = e.key === "ArrowLeft" ? -1 : 1;
      const next = nivel === "mes" ? addMonths(dateStr, step) : addDays(dateStr, step);
      if (todayStr !== null && next > todayStr) return; // sem futuro
      e.preventDefault();
      setNav({ ...nav, date: next });
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
    // nav/setNav nas deps: o listener persiste e o setter mescla sobre a URL
    // corrente - um setter obsoleto descartaria outros parâmetros.
  }, [dateStr, todayStr, nivel, nav, setNav]);

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

  // Badge de fuso divergente (nível dispositivo): offset do device vs do tenant.
  // No nível dia o badge por lane fica no tooltip do canvas.
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

  const deviceLoadFailed = nivel === "dispositivo" && timelineQuery.isError && data === undefined;
  const teamLoadFailed = nivel === "dia" && teamQuery.isError && teamData === undefined;
  const monthLoadFailed = nivel === "mes" && monthQuery.isError && monthData === undefined;
  const teamSkeletonHeight = teamTimelineCanvasHeight(TEAM_SKELETON_LANES);

  const subtitle =
    nivel === "mes"
      ? "O mês da equipe, uma linha por colaborador. Clique numa célula para abrir o dia."
      : nivel === "dia"
        ? "O dia da equipe, hora a hora, no fuso da organização. Clique numa faixa para abrir a máquina."
        : "O dia de uma máquina, hora a hora, no fuso da organização.";

  return (
    <div className="space-y-6">
      {/* Cabeçalho com o seletor de nível */}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Linha do Tempo</h1>
          <p className="mt-1 text-sm text-muted-foreground">{subtitle}</p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          {deviceTzBadge !== null && (
            <span
              title="convertido para o fuso da organização"
              className="inline-flex items-center gap-1.5 rounded-full border border-viz-improdutivo/40 bg-viz-improdutivo/10 px-2.5 py-0.5 text-xs text-viz-improdutivo"
            >
              <Globe className="h-3.5 w-3.5 shrink-0" aria-hidden />
              Máquina em {deviceTzBadge}
            </span>
          )}
          <div
            role="group"
            aria-label="Nível da linha do tempo"
            className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
          >
            <NivelButton
              nivel="mes"
              current={nivel}
              onSelect={goToNivel}
              icon={<CalendarRange className="h-3.5 w-3.5 shrink-0" aria-hidden />}
              label="Mês"
            />
            <NivelButton
              nivel="dia"
              current={nivel}
              onSelect={goToNivel}
              icon={<Users className="h-3.5 w-3.5 shrink-0" aria-hidden />}
              label="Dia"
            />
            <NivelButton
              nivel="dispositivo"
              current={nivel}
              onSelect={goToNivel}
              icon={<Monitor className="h-3.5 w-3.5 shrink-0" aria-hidden />}
              label="Dispositivo"
              disabled={nivel !== "dispositivo" && devices.length === 0}
            />
          </div>
        </div>
      </div>

      {/* Controles: dispositivo/equipe, recorte de data, janela e visão */}
      <Card>
        {/* div com padding explícito: o p-6/pt-0 default do CardContent venceria o py-3
            (cn() sem tailwind-merge - a ordem do stylesheet decide, não a da className) */}
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
          {nivel === "dispositivo" && (
            <select
              aria-label="Dispositivo"
              value={deviceId ?? ""}
              onChange={(e) => goToDevice(e.target.value)}
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
          {nivel !== "dispositivo" && <TeamTagSelect tags={tags} value={teamTag} onChange={setTeamTag} />}
          {devicesQuery.isError && (
            <Button variant="outline" size="sm" onClick={() => void devicesQuery.refetch()}>
              Recarregar dispositivos
            </Button>
          )}

          {nivel === "mes" ? (
            // Navegação de MÊS (◀ ▶ + Este mês) - as teclas ← → seguem a mesma régua.
            <div className="flex items-center gap-1.5">
              <div className="flex h-9 items-stretch overflow-hidden rounded-md border border-input bg-card">
                <button
                  type="button"
                  aria-label="Mês anterior (tecla ←)"
                  title="Mês anterior (tecla ←)"
                  disabled={dateStr === null}
                  onClick={() => {
                    if (dateStr !== null) goToDate(addMonths(dateStr, -1));
                  }}
                  className="flex w-8 items-center justify-center border-r border-input text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40"
                >
                  <ChevronLeft className="h-4 w-4" aria-hidden />
                </button>
                <span className="flex min-w-[10rem] items-center justify-center px-3 text-sm capitalize tabular-nums">
                  {dateStr !== null ? monthLabel(dateStr) : "—"}
                </span>
                <button
                  type="button"
                  aria-label="Próximo mês (tecla →)"
                  title="Próximo mês (tecla →)"
                  disabled={
                    dateStr === null ||
                    todayStr === null ||
                    monthFirst(dateStr) === monthFirst(todayStr)
                  }
                  onClick={() => {
                    if (dateStr !== null) goToDate(addMonths(dateStr, 1));
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
                disabled={
                  todayStr === null || (dateStr !== null && monthFirst(dateStr) === monthFirst(todayStr))
                }
                onClick={() => goToDate(null)}
              >
                Este mês
              </Button>
            </div>
          ) : (
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
                onClick={() => goToDate(null)}
              >
                Hoje
              </Button>
              <Button
                variant="outline"
                size="sm"
                className="h-9"
                disabled={todayStr === null}
                onClick={() => {
                  if (todayStr !== null) goToDate(addDays(todayStr, -1));
                }}
              >
                Ontem
              </Button>
            </div>
          )}

          {nivel !== "mes" && (
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
          )}

          {/* Fallback tabular obrigatório: alterna canvas↔tabela, mesmos números. */}
          <Button variant="outline" size="sm" className="ml-auto h-9" onClick={toggleView}>
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
          className="flex items-center gap-2 rounded-md border border-viz-improdutivo/40 bg-viz-improdutivo/10 px-3 py-2 text-sm text-viz-improdutivo"
        >
          <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
          Há trechos com dados incompletos neste dia.
        </div>
      )}

      {/* Cap N21 atingido: o servidor parou de adicionar lanes inteiras. */}
      {teamData?.truncated === true && (
        <div
          role="status"
          className="flex items-center gap-2 rounded-md border border-viz-improdutivo/40 bg-viz-improdutivo/10 px-3 py-2 text-sm text-viz-improdutivo"
        >
          <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
          Dia com dados demais - mostrando os {teamData.lanes.length} primeiros dispositivos.
        </div>
      )}

      {/* Teto de uma página do /people: o mapa mostra os primeiros 200 nomes. */}
      {monthTruncated && (
        <div
          role="status"
          className="flex items-center gap-2 rounded-md border border-viz-improdutivo/40 bg-viz-improdutivo/10 px-3 py-2 text-sm text-viz-improdutivo"
        >
          <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
          Mês com {monthData?.total.total} colaboradores - mostrando os {PEOPLE_PAGE_SIZE} primeiros
          em ordem alfabética.
        </div>
      )}

      {/* Falha de refetch com dado em cache: aviso inline, desenho anterior preservado. */}
      {nivel === "dispositivo" && timelineQuery.isError && data !== undefined && (
        <RefetchError onRetry={() => void timelineQuery.refetch()} />
      )}
      {nivel === "dia" && teamQuery.isError && teamData !== undefined && (
        <RefetchError onRetry={() => void teamQuery.refetch()} />
      )}
      {nivel === "mes" && monthQuery.isError && monthData !== undefined && (
        <RefetchError onRetry={() => void monthQuery.refetch()} />
      )}

      {nivel === "mes" ? (
        <Card>
          <CardHeader className="pb-4">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <div>
                <CardTitle className="text-base">Mapa do mês por colaborador</CardTitle>
                <p className="mt-1 text-sm text-muted-foreground">
                  Cada célula é um DIA · cor = índice de produtividade · vazio = sem dados (fim de
                  semana, folga, máquina desligada) · clique para abrir o dia
                </p>
              </div>
              {dateStr !== null && (
                <span className="text-sm capitalize text-muted-foreground">
                  {monthLabel(dateStr)}
                  {monthRange !== null && monthRange.end < monthLast(dateStr) && " · até hoje"}
                </span>
              )}
            </div>
          </CardHeader>
          <CardContent>
            {monthLoadFailed ? (
              <InlineError
                message={genericErrorMessage(monthQuery.error)}
                minHeight={teamSkeletonHeight}
                onRetry={() => void monthQuery.refetch()}
              />
            ) : monthData === undefined ? (
              <MonthHeatmapSkeleton days={monthDaysList.length} />
            ) : monthRows.length === 0 ? (
              <div className="flex flex-col items-center gap-3 px-6 py-14 text-center">
                <span className="flex h-14 w-14 items-center justify-center rounded-full bg-muted">
                  <CalendarRange className="h-7 w-7 text-muted-foreground" aria-hidden />
                </span>
                <p className="text-base font-medium">Nenhum colaborador para mostrar</p>
                {teamTag !== null ? (
                  <>
                    <p className="text-sm text-muted-foreground">
                      Nenhuma pessoa com dados na etiqueta “{teamTag}” neste mês.
                    </p>
                    <Button variant="outline" size="sm" onClick={() => setTeamTag(null)}>
                      Ver todas as equipes
                    </Button>
                  </>
                ) : (
                  <p className="text-sm text-muted-foreground">
                    Quando houver dias registrados neste mês, cada pessoa aparece aqui como uma
                    linha de dias.
                  </p>
                )}
              </div>
            ) : view === "canvas" ? (
              <div className={cn(monthQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
                <MonthHeatmap days={monthDaysList} rows={monthRows} onSelectDay={goToDay} />
                <MonthHeatmapLegend />
                {/* Fallback de screen reader: os MESMOS números, invisível. */}
                <div className="sr-only">
                  <MonthTable days={monthDaysList} rows={monthRows} />
                </div>
              </div>
            ) : (
              <MonthTable days={monthDaysList} rows={monthRows} />
            )}
          </CardContent>
        </Card>
      ) : nivel === "dia" ? (
        <>
          <Card>
            <CardHeader className="pb-4">
              <div className="flex flex-wrap items-baseline justify-between gap-2">
                <CardTitle className="text-base">
                  Faixas do dia
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
                <InlineError
                  message={genericErrorMessage(teamQuery.error)}
                  minHeight={teamSkeletonHeight}
                  onRetry={() => void teamQuery.refetch()}
                />
              ) : teamData !== undefined && teamData.lanes.length === 0 ? (
                // Estado vazio 8.9: tenant sem devices não-arquivados - ou, com o
                // seletor de equipe ativo, etiqueta sem nenhum dispositivo (a API
                // devolve recorte vazio, nunca erro).
                <div className="flex flex-col items-center gap-3 px-6 py-14 text-center">
                  <span className="flex h-14 w-14 items-center justify-center rounded-full bg-muted">
                    <Users className="h-7 w-7 text-muted-foreground" aria-hidden />
                  </span>
                  <p className="text-base font-medium">Nenhum dispositivo para mostrar</p>
                  {teamTag !== null ? (
                    <>
                      <p className="text-sm text-muted-foreground">
                        Nenhum dispositivo com a etiqueta “{teamTag}” neste dia.
                      </p>
                      <Button variant="outline" size="sm" onClick={() => setTeamTag(null)}>
                        Ver todas as equipes
                      </Button>
                    </>
                  ) : (
                    <p className="text-sm text-muted-foreground">
                      Quando houver dispositivos ativos na organização, cada um aparece aqui como
                      uma linha do dia.
                    </p>
                  )}
                </div>
              ) : view === "canvas" ? (
                <>
                  <div className={cn(teamQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
                    {timezone !== null && dateStr !== null && teamData !== undefined ? (
                      <DayLanesPanel
                        lanes={teamData.lanes}
                        timezone={timezone}
                        windowStartHour={windowStartHour}
                        windowEndHour={windowEndHour}
                        date={dateStr}
                        isToday={isToday}
                        serverTime={teamData.server_time}
                        onSelectDevice={goToDevice}
                      />
                    ) : (
                      // Antes da 1ª resposta o nº de lanes é desconhecido: skeleton.
                      <Skeleton className="w-full" style={{ height: teamSkeletonHeight }} />
                    )}
                  </div>
                  {/* Fallback de screen reader: os MESMOS números, invisível. */}
                  {timezone !== null && teamData !== undefined && (
                    <div className="sr-only">
                      <DayLanesSummaryTable lanes={teamData.lanes} />
                      <TeamTimelineTable lanes={teamData.lanes} timezone={timezone} />
                    </div>
                  )}
                  <Legend showAppLane={false} />
                </>
              ) : timezone !== null && teamData !== undefined ? (
                <div className="space-y-6">
                  <DayLanesSummaryTable lanes={teamData.lanes} />
                  <TeamTimelineTable lanes={teamData.lanes} timezone={timezone} />
                </div>
              ) : (
                <Skeleton className="w-full" style={{ height: teamSkeletonHeight }} />
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader className="pb-4">
              <CardTitle className="text-base">Resumo do dia por pessoa</CardTitle>
              <p className="mt-1 text-sm text-muted-foreground">
                Ordem alfabética · a mesma pessoa em duas máquinas soma numa linha só
              </p>
            </CardHeader>
            <CardContent>
              <DayPeopleSummary
                people={dayPeople?.items}
                failed={dayPeopleQuery.isError && dayPeople === undefined}
                onRetry={() => void dayPeopleQuery.refetch()}
              />
            </CardContent>
          </Card>
        </>
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
              <InlineError
                message={genericErrorMessage(timelineQuery.error)}
                minHeight={TIMELINE_CANVAS_HEIGHT}
                onRetry={() => void timelineQuery.refetch()}
              />
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

/** Um segmento do seletor de nível. */
function NivelButton({
  nivel,
  current,
  onSelect,
  icon,
  label,
  disabled = false,
}: {
  nivel: Nivel;
  current: Nivel;
  onSelect: (nivel: Nivel) => void;
  icon: ReactNode;
  label: string;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      aria-pressed={current === nivel}
      disabled={disabled}
      onClick={() => onSelect(nivel)}
      className={cn(
        segmentedButton,
        current === nivel ? segmentedOn : segmentedOff,
        "disabled:pointer-events-none disabled:opacity-40",
      )}
    >
      <span className="flex items-center gap-1.5">
        {icon}
        {label}
      </span>
    </button>
  );
}

/** Falha de refetch com dado em cache: aviso inline, desenho anterior preservado. */
function RefetchError({ onRetry }: { onRetry: () => void }) {
  return (
    <div
      role="alert"
      className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
    >
      <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
      <Button variant="outline" size="sm" onClick={onRetry}>
        Tentar novamente
      </Button>
    </div>
  );
}

/** Erro sem nenhum dado: estado inline no widget, com retry e a geometria final. */
function InlineError({
  message,
  minHeight,
  onRetry,
}: {
  message: string;
  minHeight: number;
  onRetry: () => void;
}) {
  return (
    <div
      className="flex flex-col items-center justify-center gap-3 text-center"
      style={{ minHeight }}
    >
      <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
      <p className="text-sm text-muted-foreground">{message}</p>
      <Button variant="outline" onClick={onRetry}>
        Tentar novamente
      </Button>
    </div>
  );
}

/**
 * Legenda dos estados do canvas, com as mesmas redundâncias não-cromáticas e as
 * MESMAS cores do desenho (lib/brandTheme.ts). `showAppLane=false` no nível dia
 * (lá não existe sub-faixa de apps).
 *
 * Estados de máquina são NEUTROS (Seção 2.1): Ativo, Ocioso, Bloqueado,
 * Desligada, Sem comunicação. Ocioso NUNCA é improdutivo - e por isso deixou de
 * ser âmbar: âmbar é "Improdutivo" na legenda de classificação do site, e a
 * colisão está apontada na Seção 1.2 do spec. Ocioso agora é o cinza-azulado da
 * marca com hachura a 45°.
 */
function Legend({ showAppLane = true }: { showAppLane?: boolean }) {
  return (
    <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-muted-foreground">
      <LegendItem
        swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm bg-viz-produtivo" />}
        label={stateLabels.active}
      />
      <LegendItem
        swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={idleHatch} />}
        label={stateLabels.idle}
      />
      <LegendItem
        swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm bg-brand-slate" />}
        label={stateLabels.locked}
      />
      <LegendItem
        swatch={
          <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm border-2 border-brand-slate" />
        }
        label={stateLabels.off_clean}
      />
      <LegendItem
        swatch={
          <span className="flex items-center gap-1">
            <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={noDataHatch} />
            <AlertTriangle className="h-3 w-3 shrink-0 text-brand-red" aria-hidden />
          </span>
        }
        label={stateLabels.no_data}
      />
      {showAppLane && (
        <LegendItem
          swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm bg-brand-slate" />}
          label="Faixa de baixo: apps em uso (sem classificação)"
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
