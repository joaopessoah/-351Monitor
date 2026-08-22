// =============================================================================
// Linha 3 da Visão Geral (F3.2, Seção 8.4): dois gráficos ECharts da semana -
// "Horas ativas por dia" (barras empilhadas ativo/ocioso + linha de referência
// da jornada do tenant) e "Top 10 apps da semana" (barras horizontais com cor
// pela classificação da categoria). Semana = segunda a domingo no fuso da
// ORGANIZAÇÃO; seletor "Semana atual | Semana anterior" compartilhado pelos
// dois cards. Cada card tem toggle "Ver dados" para a tabela acessível com os
// MESMOS números (o gráfico fica oculto e aria-hidden enquanto a tabela está
// visível). Polling de 60s SÓ quando a semana exibida inclui hoje. O recorte de
// equipe da Visão Geral (?tag=) desce como prop e entra nas três consultas: os
// gráficos falam da MESMA equipe que os cards acima.
// =============================================================================

import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import { AlertTriangle, ChartColumn, Table } from "lucide-react";
import type { EChartsOption } from "echarts";
import { api } from "@/lib/api";
import { BRAND } from "@/lib/brandTheme";
import { classificationColor, classificationLabel } from "@/lib/classification";
import { formatDuration, localDateOf, parseHmToMinutes } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type {
  BusinessHours,
  DashboardSummaryResponse,
  MeResponse,
  TopAppItem,
  TopAppsResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { tagParam } from "@/components/filters/TeamTagSelect";
import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { EChart } from "@/components/charts/EChart";
import {
  DeltaBadge,
  DeviceCountNotice,
  useComparisonRange,
} from "@/components/dashboard/comparison";

/** Altura fixa dos gráficos - skeleton/vazio/erro com a mesma geometria. */
const CHART_H = 280;

// Cores canônicas da marca (lib/brandTheme.ts): ativo/ocioso iguais ao canvas
// da timeline e à legenda do site; classificação vem da mesma paleta de
// dataviz (verde de atividade, azul neutro, âmbar, cinza-azulado).
const COLOR = {
  active: BRAND.vizProdutivo,
  idle: BRAND.vizOcioso,
  workRelated: BRAND.vizProdutivo,
  neutral: BRAND.vizNeutro,
  notWorkRelated: BRAND.vizImprodutivo,
  uncategorized: BRAND.slate,
  axisText: BRAND.chartText,
  grid: BRAND.chartGrid,
} as const;

type WeekChoice = "current" | "previous";
type CardView = "chart" | "table";

interface WeekRange {
  from: string;
  to: string;
}

/** Soma `days` a uma data local yyyy-MM-dd (aritmética em UTC, imune a DST local). */
function addDays(dateStr: string, days: number): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d + days));
  const mm = String(dt.getUTCMonth() + 1).padStart(2, "0");
  const dd = String(dt.getUTCDate()).padStart(2, "0");
  return `${dt.getUTCFullYear()}-${mm}-${dd}`;
}

/** Semana segunda-domingo (no fuso da org) que contém `todayStr`, ou a anterior. */
function weekRange(todayStr: string, which: WeekChoice): WeekRange {
  const [y, m, d] = todayStr.split("-").map(Number);
  const dow = new Date(Date.UTC(y, m - 1, d)).getUTCDay(); // 0 = domingo
  const monday = addDays(todayStr, -((dow + 6) % 7));
  const from = which === "current" ? monday : addDays(monday, -7);
  return { from, to: addDays(from, 6) };
}

/** "seg 09/06" - fins de semana com rótulo normal (feriados estão fora do MVP). */
function dayLabel(dateStr: string): string {
  const [y, m, d] = dateStr.split("-").map(Number);
  const weekday = new Intl.DateTimeFormat("pt-BR", { weekday: "short", timeZone: "UTC" })
    .format(new Date(Date.UTC(y, m - 1, d)))
    .replace(".", "");
  return `${weekday} ${String(d).padStart(2, "0")}/${String(m).padStart(2, "0")}`;
}

/** "09/06" a partir de yyyy-MM-dd. */
function ddmm(dateStr: string): string {
  const [, m, d] = dateStr.split("-");
  return `${d}/${m}`;
}

/** Duração diária da jornada em segundos: business_hours da org; fallback 8h. */
function jornadaSeconds(businessHours: BusinessHours | null): number {
  if (businessHours !== null) {
    const start = parseHmToMinutes(businessHours.start);
    const end = parseHmToMinutes(businessHours.end);
    if (start !== null && end !== null && end > start) return (end - start) * 60;
  }
  return 8 * 3600;
}

/** Escapa HTML para os tooltips do ECharts (nomes de app vêm de dados). */
function escapeHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

interface DayRow {
  date: string;
  /** Dia ainda no futuro (fuso da org): sem barra e sem números na tabela. */
  future: boolean;
  secondsActive: number;
  secondsIdle: number;
  incomplete: boolean;
}

/** Linha 3 da Visão Geral: os dois cards de gráficos da semana, lado a lado. */
export function WeeklyChartsRow({ tag = null }: { tag?: string | null }) {
  const [week, setWeek] = useState<WeekChoice>("current");

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const businessHours = meQuery.data?.organization.business_hours ?? null;

  // Semana sempre calculada no FUSO DA ORGANIZAÇÃO (não no fuso do navegador).
  const todayStr = timezone !== null ? localDateOf(new Date(), timezone) : null;
  const range = todayStr !== null ? weekRange(todayStr, week) : null;
  const includesToday =
    range !== null && todayStr !== null && range.from <= todayStr && todayStr <= range.to;

  const summaryQuery = useQuery({
    queryKey: ["dashboard", "summary", range?.from, range?.to, tag],
    queryFn: () =>
      api<DashboardSummaryResponse>(
        `/dashboard/summary?from=${range?.from ?? ""}&to=${range?.to ?? ""}${tagParam(tag)}`,
      ),
    enabled: range !== null,
    // Polling de 60s APENAS quando a semana exibida inclui o dia de hoje.
    refetchInterval: includesToday ? 60_000 : false,
    refetchIntervalInBackground: false,
    // Troca de semana/refetch nunca re-mostra skeleton: mantém o desenho anterior.
    placeholderData: (prev) => prev,
  });

  // Período de comparação: a semana imediatamente anterior à exibida. Dado
  // imutável (sempre no passado): mesma queryKey do summary, sem polling.
  const prevRange = useComparisonRange(range);
  const prevSummaryQuery = useQuery({
    queryKey: ["dashboard", "summary", prevRange?.from, prevRange?.to, tag],
    queryFn: () =>
      api<DashboardSummaryResponse>(
        `/dashboard/summary?from=${prevRange?.from ?? ""}&to=${prevRange?.to ?? ""}${tagParam(tag)}`,
      ),
    enabled: prevRange !== null,
    placeholderData: (prev) => prev,
  });

  const topAppsQuery = useQuery({
    queryKey: ["dashboard", "top-apps", range?.from, range?.to, tag],
    queryFn: () =>
      api<TopAppsResponse>(
        `/dashboard/top-apps?from=${range?.from ?? ""}&to=${range?.to ?? ""}&limit=10${tagParam(tag)}`,
      ),
    enabled: range !== null,
    refetchInterval: includesToday ? 60_000 : false,
    refetchIntervalInBackground: false,
    placeholderData: (prev) => prev,
  });

  // /me nunca carregou: sem fuso não há semana - os dois cards mostram erro.
  const meBlocked = meQuery.isError && meQuery.data === undefined;
  const retryMe = (): void => {
    void meQuery.refetch();
  };

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <HorasAtivasCard
        query={summaryQuery}
        prevQuery={prevSummaryQuery}
        range={range}
        prevRange={prevRange}
        todayStr={todayStr}
        businessHours={businessHours}
        week={week}
        onWeekChange={setWeek}
        meBlocked={meBlocked}
        onRetryMe={retryMe}
      />
      <TopAppsCard query={topAppsQuery} range={range} meBlocked={meBlocked} onRetryMe={retryMe} />
    </div>
  );
}

// -----------------------------------------------------------------------------
// Card "Horas ativas por dia"
// -----------------------------------------------------------------------------

function HorasAtivasCard({
  query,
  prevQuery,
  range,
  prevRange,
  todayStr,
  businessHours,
  week,
  onWeekChange,
  meBlocked,
  onRetryMe,
}: {
  query: UseQueryResult<DashboardSummaryResponse>;
  prevQuery: UseQueryResult<DashboardSummaryResponse>;
  range: WeekRange | null;
  prevRange: WeekRange | null;
  todayStr: string | null;
  businessHours: BusinessHours | null;
  week: WeekChoice;
  onWeekChange: (week: WeekChoice) => void;
  meBlocked: boolean;
  onRetryMe: () => void;
}) {
  const [view, setView] = useState<CardView>("chart");
  const data = query.data;
  const prevData = prevQuery.data;

  // 7 dias seg-dom SEMPRE presentes: o endpoint não devolve dias sem linhas,
  // então o portal preenche zeros; dias futuros ficam sem barra.
  const rows = useMemo<DayRow[]>(() => {
    if (range === null) return [];
    const byDate = new Map((data?.days ?? []).map((d) => [d.date, d]));
    return Array.from({ length: 7 }, (_, i) => {
      const date = addDays(range.from, i);
      const day = byDate.get(date);
      return {
        date,
        future: todayStr !== null && date > todayStr,
        secondsActive: day?.seconds_active ?? 0,
        secondsIdle: day?.seconds_idle ?? 0,
        incomplete: day?.data_incomplete ?? false,
      };
    });
  }, [data, range, todayStr]);

  // Segundos ATIVOS por dia da semana anterior, alinhados por posição (o dia i
  // da semana exibida compara com o dia i da anterior) - null enquanto a
  // resposta da comparação não chegou.
  const prevActiveByDay = useMemo<number[] | null>(() => {
    if (prevRange === null || prevData === undefined) return null;
    const byDate = new Map(prevData.days.map((d) => [d.date, d.seconds_active]));
    return Array.from({ length: 7 }, (_, i) => byDate.get(addDays(prevRange.from, i)) ?? 0);
  }, [prevData, prevRange]);

  const empty = data !== undefined && data.days.length === 0;
  const jornadaSec = jornadaSeconds(businessHours);
  const option = useMemo<EChartsOption>(
    () => buildHorasOption(rows, empty, jornadaSec, prevActiveByDay),
    [rows, empty, jornadaSec, prevActiveByDay],
  );

  const failed = meBlocked || (query.isError && data === undefined);
  const refetchFailed = query.isError && data !== undefined;

  function retry(): void {
    if (meBlocked) {
      onRetryMe();
      return;
    }
    void query.refetch();
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="space-y-1.5">
            <CardTitle className="text-base">Horas ativas por dia</CardTitle>
            <CardDescription className="tabular-nums">
              {range !== null
                ? `Semana de ${ddmm(range.from)} a ${ddmm(range.to)} · segunda a domingo`
                : "Carregando semana…"}
            </CardDescription>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <WeekToggle week={week} onChange={onWeekChange} />
            <ViewToggle view={view} onChange={setView} />
          </div>
        </div>
      </CardHeader>
      {/* div crua com padding próprio: o p-6/pt-0 default do CardContent venceria
          padding custom (cn() sem tailwind-merge - commits d09d980/eef85fa). */}
      <div className="px-6 pb-6">
        {refetchFailed && <RefetchAlert onRetry={() => void query.refetch()} />}
        {failed ? (
          <ErrorState message={genericErrorMessage(query.error)} onRetry={retry} />
        ) : data === undefined ? (
          // Skeleton com a geometria final do gráfico (nunca spinner).
          <Skeleton className="w-full" style={{ height: CHART_H }} />
        ) : (
          <>
            <div
              className={cn(
                query.isPlaceholderData && "opacity-70 transition-opacity",
                view === "table" && "hidden",
              )}
              aria-hidden={view === "table"}
            >
              <EChart option={option} height={CHART_H} ariaHidden={view === "table"} />
              {prevActiveByDay !== null && (
                <p className="mt-2 text-xs text-muted-foreground">
                  Linha cinza: horas ativas na semana anterior.
                </p>
              )}
            </div>
            {view === "table" && <HorasTable rows={rows} prevActiveByDay={prevActiveByDay} />}

            {/* Totais da semana com o comparativo neutro vs período anterior. */}
            <div className="mt-3 space-y-1 border-t pt-3 text-sm tabular-nums text-muted-foreground">
              <p>
                Ativo na semana: {formatDuration(data.totals.seconds_active)}{" "}
                <DeltaBadge
                  current={data.totals.seconds_active}
                  previous={prevData?.totals.seconds_active ?? null}
                  previousRange={prevRange}
                  incomplete={data.totals.data_incomplete || prevData?.totals.data_incomplete === true}
                />
              </p>
              <p>
                Ocioso na semana: {formatDuration(data.totals.seconds_idle)}{" "}
                <DeltaBadge
                  current={data.totals.seconds_idle}
                  previous={prevData?.totals.seconds_idle ?? null}
                  previousRange={prevRange}
                  incomplete={data.totals.data_incomplete || prevData?.totals.data_incomplete === true}
                />
              </p>
              {prevData !== undefined && (
                <DeviceCountNotice
                  current={data.totals.device_count}
                  previous={prevData.totals.device_count}
                />
              )}
            </div>
          </>
        )}
      </div>
    </Card>
  );
}

function buildHorasOption(
  rows: DayRow[],
  empty: boolean,
  jornadaSec: number,
  prevActiveByDay: number[] | null,
): EChartsOption {
  const jornadaH = jornadaSec / 3600;
  const dataMaxH = rows.reduce(
    (mx, r) => Math.max(mx, (r.secondsActive + r.secondsIdle) / 3600),
    0,
  );
  const prevMaxH = (prevActiveByDay ?? []).reduce((mx, s) => Math.max(mx, s / 3600), 0);
  // markLine não entra no cálculo automático do eixo: max explícito garante a
  // linha de referência sempre visível (com 1h de folga acima dela).
  const yMax = Math.max(Math.ceil(dataMaxH), Math.ceil(prevMaxH), Math.ceil(jornadaH) + 1);
  // "-" é o valor vazio do ECharts: dia futuro fica sem barra (não é zero).
  const barValue = (r: DayRow, seconds: number): number | string =>
    r.future ? "-" : Math.round((seconds / 3600) * 100) / 100;

  return {
    aria: { enabled: true },
    animation: false,
    grid: { left: 40, right: 16, top: 20, bottom: 24 },
    tooltip: {
      trigger: "axis",
      formatter: (params: unknown): string => {
        const list = params as Array<{ dataIndex: number }>;
        const first: { dataIndex: number } | undefined = list[0];
        const row: DayRow | undefined = first !== undefined ? rows[first.dataIndex] : undefined;
        if (row === undefined) return "";
        const prevLine =
          prevActiveByDay !== null && first !== undefined
            ? `Ativo na semana anterior: ${formatDuration(prevActiveByDay[first.dataIndex] ?? 0)}`
            : null;
        if (row.future) {
          const futureLines = [`<strong>${dayLabel(row.date)}</strong>`, "Dia futuro"];
          if (prevLine !== null) futureLines.push(prevLine);
          return futureLines.join("<br/>");
        }
        const lines = [
          `<strong>${dayLabel(row.date)}</strong>`,
          `Ativo: ${formatDuration(row.secondsActive)}`,
          `Ocioso: ${formatDuration(row.secondsIdle)}`,
        ];
        if (prevLine !== null) lines.push(prevLine);
        if (row.incomplete) lines.push("Dados incompletos neste dia");
        return lines.join("<br/>");
      },
    },
    xAxis: {
      type: "category",
      data: rows.map((r) => dayLabel(r.date)),
      axisTick: { show: false },
      axisLine: { lineStyle: { color: COLOR.grid } },
      axisLabel: { color: COLOR.axisText, fontSize: 11 },
    },
    yAxis: {
      type: "value",
      max: yMax,
      minInterval: 1,
      axisLabel: { formatter: "{value}h", color: COLOR.axisText, fontSize: 11 },
      splitLine: { lineStyle: { color: COLOR.grid } },
    },
    series: [
      // Linha fantasma da semana anterior ATRÁS das barras (z abaixo do z=2
      // default das barras): cor muted, opacidade baixa, sem legenda chamativa.
      ...(prevActiveByDay !== null && !empty
        ? [
            {
              name: "Ativo na semana anterior",
              type: "line" as const,
              z: 1,
              silent: true,
              symbol: "circle",
              symbolSize: 4,
              data: prevActiveByDay.map((s) => Math.round((s / 3600) * 100) / 100),
              lineStyle: { color: COLOR.axisText, width: 1.5, opacity: 0.45 },
              itemStyle: { color: COLOR.axisText, opacity: 0.45 },
            },
          ]
        : []),
      {
        name: "Ativo",
        type: "bar",
        stack: "horas",
        data: rows.map((r) => barValue(r, r.secondsActive)),
        itemStyle: { color: COLOR.active },
        barMaxWidth: 36,
        // Linha de referência tracejada = duração diária da jornada do tenant.
        markLine: empty
          ? undefined
          : {
              silent: true,
              symbol: "none",
              lineStyle: { type: "dashed", color: COLOR.axisText, width: 1 },
              label: {
                formatter: `Jornada ${formatDuration(jornadaSec)}`,
                position: "insideEndTop",
                color: COLOR.axisText,
                fontSize: 10,
              },
              data: [{ yAxis: jornadaH }],
            },
      },
      {
        name: "Ocioso",
        type: "bar",
        stack: "horas",
        data: rows.map((r) => barValue(r, r.secondsIdle)),
        itemStyle: { color: COLOR.idle },
        barMaxWidth: 36,
      },
    ],
    // Estado vazio 8.9: eixos desenhados + texto central.
    graphic: empty
      ? [
          {
            type: "text",
            left: "center",
            top: "middle",
            silent: true,
            style: {
              text: "Sem dados no período",
              fill: COLOR.axisText,
              font: "13px system-ui, -apple-system, sans-serif",
            },
          },
        ]
      : undefined,
  };
}

/** Tabela acessível do card de horas - os MESMOS números do gráfico,
    incluindo a série da semana anterior quando disponível. */
function HorasTable({
  rows,
  prevActiveByDay,
}: {
  rows: DayRow[];
  prevActiveByDay: number[] | null;
}) {
  return (
    <div className="overflow-x-auto" style={{ minHeight: CHART_H }}>
      <table className="w-full text-sm" aria-label="Horas ativas por dia, em tabela">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">Dia</th>
            <th scope="col" className="px-3 py-2 text-right">Ativo</th>
            {prevActiveByDay !== null && (
              <th scope="col" className="px-3 py-2 text-right">Ativo na semana anterior</th>
            )}
            <th scope="col" className="px-3 py-2 text-right">Ocioso</th>
            <th scope="col" className="px-3 py-2">Observação</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={r.date} className="border-b last:border-b-0">
              <td className="whitespace-nowrap px-3 py-1.5">{dayLabel(r.date)}</td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {r.future ? "-" : formatDuration(r.secondsActive)}
              </td>
              {prevActiveByDay !== null && (
                <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums text-muted-foreground">
                  {formatDuration(prevActiveByDay[i] ?? 0)}
                </td>
              )}
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {r.future ? "-" : formatDuration(r.secondsIdle)}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-muted-foreground">
                {r.future ? "Dia futuro" : r.incomplete ? "Dados incompletos" : "-"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Card "Top 10 apps da semana"
// -----------------------------------------------------------------------------

function TopAppsCard({
  query,
  range,
  meBlocked,
  onRetryMe,
}: {
  query: UseQueryResult<TopAppsResponse>;
  range: WeekRange | null;
  meBlocked: boolean;
  onRetryMe: () => void;
}) {
  const navigate = useNavigate();
  const [view, setView] = useState<CardView>("chart");
  const data = query.data;

  const items = useMemo(() => data?.items ?? [], [data]);
  const empty = data !== undefined && items.length === 0;
  const option = useMemo<EChartsOption>(() => buildTopAppsOption(items, empty), [items, empty]);

  const failed = meBlocked || (query.isError && data === undefined);
  const refetchFailed = query.isError && data !== undefined;

  function retry(): void {
    if (meBlocked) {
      onRetryMe();
      return;
    }
    void query.refetch();
  }

  // Clique na barra → /apps com o período (a tela /apps chega na fatia 3.3:
  // aqui só passamos os params do período no padrão from/to).
  function openApps(): void {
    if (range !== null) {
      navigate(`/apps?from=${encodeURIComponent(range.from)}&to=${encodeURIComponent(range.to)}`);
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="space-y-1.5">
            <CardTitle className="text-base">Top 10 apps da semana</CardTitle>
            <CardDescription className="tabular-nums">
              {range !== null
                ? `Semana de ${ddmm(range.from)} a ${ddmm(range.to)} · clique em um app para abrir Aplicativos`
                : "Carregando semana…"}
            </CardDescription>
          </div>
          <ViewToggle view={view} onChange={setView} />
        </div>
      </CardHeader>
      {/* div crua com padding próprio (mesma armadilha do CardContent acima). */}
      <div className="px-6 pb-6">
        {refetchFailed && <RefetchAlert onRetry={() => void query.refetch()} />}
        {failed ? (
          <ErrorState message={genericErrorMessage(query.error)} onRetry={retry} />
        ) : data === undefined ? (
          <Skeleton className="w-full" style={{ height: CHART_H }} />
        ) : (
          <>
            <div
              className={cn(
                query.isPlaceholderData && "opacity-70 transition-opacity",
                view === "table" && "hidden",
              )}
              aria-hidden={view === "table"}
            >
              <EChart
                option={option}
                height={CHART_H}
                ariaHidden={view === "table"}
                onItemClick={(params) => {
                  if (params.componentType === "series") openApps();
                }}
              />
              <ClassificationLegend />
            </div>
            {view === "table" && <TopAppsTable items={items} />}
            <p className="mt-3 border-t pt-3 text-sm tabular-nums text-muted-foreground">
              Tempo ativo total no período (todos os apps):{" "}
              {formatDuration(data.total_seconds_active)}
            </p>
          </>
        )}
      </div>
    </Card>
  );
}

function buildTopAppsOption(items: TopAppItem[], empty: boolean): EChartsOption {
  const maxH = items.reduce((mx, i) => Math.max(mx, i.seconds_active / 3600), 0);
  return {
    aria: { enabled: true },
    animation: false,
    grid: { left: 132, right: 24, top: 8, bottom: 24 },
    tooltip: {
      trigger: "item",
      formatter: (params: unknown): string => {
        const p = params as { dataIndex: number };
        const item: TopAppItem | undefined = items[p.dataIndex];
        if (item === undefined) return "";
        const category =
          item.category !== null
            ? `${classificationLabel(item.category.classification)} · ${escapeHtml(item.category.name)}`
            : classificationLabel(null);
        return [
          `<strong>${escapeHtml(item.custom_display_name ?? item.display_name)}</strong>`,
          category,
          `Tempo ativo: ${formatDuration(item.seconds_active)}`,
          item.device_count === 1 ? "1 dispositivo" : `${item.device_count} dispositivos`,
        ].join("<br/>");
      },
    },
    xAxis: {
      type: "value",
      max: Math.max(Math.ceil(maxH), 1),
      minInterval: 1,
      axisLabel: { formatter: "{value}h", color: COLOR.axisText, fontSize: 11 },
      splitLine: { lineStyle: { color: COLOR.grid } },
    },
    yAxis: {
      type: "category",
      inverse: true, // maior tempo ativo no topo
      data: items.map((i) => i.custom_display_name ?? i.display_name),
      axisTick: { show: false },
      axisLine: { lineStyle: { color: COLOR.grid } },
      axisLabel: { color: BRAND.ink2, fontSize: 11, width: 118, overflow: "truncate" },
    },
    series: [
      {
        type: "bar",
        cursor: "pointer",
        barMaxWidth: 16,
        data: items.map((i) => ({
          value: Math.round((i.seconds_active / 3600) * 100) / 100,
          itemStyle: { color: classificationColor(i.category?.classification ?? null) },
        })),
      },
    ],
    // Estado vazio 8.9: eixos desenhados + texto central.
    graphic: empty
      ? [
          {
            type: "text",
            left: "center",
            top: "middle",
            silent: true,
            style: {
              text: "Sem dados no período",
              fill: COLOR.axisText,
              font: "13px system-ui, -apple-system, sans-serif",
            },
          },
        ]
      : undefined,
  };
}

/** Tabela acessível do top de apps - os MESMOS números do gráfico. */
function TopAppsTable({ items }: { items: TopAppItem[] }) {
  return (
    <div className="overflow-x-auto" style={{ minHeight: CHART_H }}>
      <table className="w-full text-sm" aria-label="Top 10 apps da semana, em tabela">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">App</th>
            <th scope="col" className="px-3 py-2">Classificação</th>
            <th scope="col" className="px-3 py-2 text-right">Tempo ativo</th>
            <th scope="col" className="px-3 py-2 text-right">Dispositivos</th>
          </tr>
        </thead>
        <tbody>
          {items.length === 0 ? (
            <tr>
              <td colSpan={4} className="px-3 py-8 text-center text-muted-foreground">
                Sem dados no período
              </td>
            </tr>
          ) : (
            items.map((item) => (
              <tr key={item.app_id} className="border-b last:border-b-0">
                <td className="max-w-[16rem] truncate px-3 py-1.5 font-medium">
                  {item.custom_display_name ?? item.display_name}
                </td>
                <td className="whitespace-nowrap px-3 py-1.5">
                  <span className="flex items-center gap-2">
                    <span
                      aria-hidden
                      className="h-2.5 w-2.5 shrink-0 rounded-sm"
                      style={{
                        backgroundColor: classificationColor(item.category?.classification ?? null),
                      }}
                    />
                    <span>{classificationLabel(item.category?.classification ?? null)}</span>
                  </span>
                </td>
                <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                  {formatDuration(item.seconds_active)}
                </td>
                <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                  {item.device_count}
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

/** Legenda da classificação - vocabulário FIXO (Seção 8.7). */
function ClassificationLegend() {
  return (
    <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-muted-foreground">
      <LegendItem color={COLOR.workRelated} label="Relacionado ao trabalho" />
      <LegendItem color={COLOR.neutral} label="Neutro" />
      <LegendItem color={COLOR.notWorkRelated} label="Não relacionado ao trabalho" />
      <LegendItem color={COLOR.uncategorized} label="Não categorizado" />
    </div>
  );
}

function LegendItem({ color, label }: { color: string; label: string }) {
  return (
    <span className="flex items-center gap-1.5">
      <span
        aria-hidden
        className="h-2.5 w-2.5 shrink-0 rounded-sm"
        style={{ backgroundColor: color }}
      />
      <span>{label}</span>
    </span>
  );
}

// -----------------------------------------------------------------------------
// Controles e estados compartilhados pelos dois cards
// -----------------------------------------------------------------------------

/** Seletor segmentado de semana (h-9, padrão das barras de controle da timeline). */
function WeekToggle({ week, onChange }: { week: WeekChoice; onChange: (w: WeekChoice) => void }) {
  return (
    <div
      role="group"
      aria-label="Semana exibida"
      className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
    >
      <button
        type="button"
        aria-pressed={week === "current"}
        onClick={() => onChange("current")}
        className={segmentClass(week === "current")}
      >
        Semana atual
      </button>
      <button
        type="button"
        aria-pressed={week === "previous"}
        onClick={() => onChange("previous")}
        className={segmentClass(week === "previous")}
      >
        Semana anterior
      </button>
    </div>
  );
}

function segmentClass(selected: boolean): string {
  return cn(
    "rounded-[5px] px-3 text-xs font-medium transition-colors",
    selected
      ? "bg-primary/10 text-primary"
      : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
  );
}

/** Toggle "Ver dados" / "Ver gráfico" - padrão do toggle canvas/tabela da timeline. */
function ViewToggle({ view, onChange }: { view: CardView; onChange: (v: CardView) => void }) {
  return (
    <Button
      variant="outline"
      size="sm"
      className="h-9"
      onClick={() => onChange(view === "chart" ? "table" : "chart")}
    >
      {view === "chart" ? (
        <>
          <Table className="h-4 w-4" aria-hidden />
          Ver dados
        </>
      ) : (
        <>
          <ChartColumn className="h-4 w-4" aria-hidden />
          Ver gráfico
        </>
      )}
    </Button>
  );
}

/** Erro sem nenhum dado: estado inline por widget com retry (Seção 8.9). */
function ErrorState({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <div
      className="flex flex-col items-center justify-center gap-3 text-center"
      style={{ minHeight: CHART_H }}
    >
      <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
      <p className="text-sm text-muted-foreground">{message}</p>
      <Button variant="outline" onClick={onRetry}>
        Tentar novamente
      </Button>
    </div>
  );
}

/** Falha de refetch com dado em cache: aviso inline, desenho anterior preservado. */
function RefetchAlert({ onRetry }: { onRetry: () => void }) {
  return (
    <div
      role="alert"
      className="mb-3 flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
    >
      <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
      <Button variant="outline" size="sm" onClick={onRetry}>
        Tentar novamente
      </Button>
    </div>
  );
}
