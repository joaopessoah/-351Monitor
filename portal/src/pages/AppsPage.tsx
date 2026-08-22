// =============================================================================
// Aplicativos (/apps - F3.3, Seção 8.6): uso por app no período + curadoria.
// - Filtros: período com presets de 7/14/30/92 dias (?from&to da URL têm
//   precedência - é o link que a Visão Geral envia), devices multi-select,
//   categoria e classificação.
// - Donut por CLASSIFICAÇÃO + barras por categoria: GET /reports/usage
//   group_by=category. Os gráficos refletem período + devices; os filtros de
//   categoria/classificação se aplicam SÓ à tabela (o endpoint não filtra por
//   categoria - decisão documentada).
// - Tabela: GET /reports/usage group_by=app com paginação no servidor; com
//   filtro de categoria/classificação ativo a página busca os 100 apps com
//   mais tempo (page_size máximo do contrato) e filtra no cliente, com nota
//   quando o total passa disso.
// - Edição inline de categoria (admin/owner): CategoryInlineSelect - vale
//   para a organização inteira e reagrega os últimos 30 dias no backend.
// - Drill-down do app: GET /app-catalog/{id}/titles (dado pessoal - o backend
//   audita todo acesso); títulos mascarados viram uma linha única.
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import { AlertTriangle, ChartColumn, Info, Table, Tags } from "lucide-react";
import type { EChartsOption } from "echarts";
import { api } from "@/lib/api";
import { BRAND } from "@/lib/brandTheme";
import {
  classificationColor,
  classificationColors,
  classificationLabel,
  mergeUncategorizedRows,
  UNCATEGORIZED_LABEL,
} from "@/lib/classification";
import { addDays, ddmm, formatDuration, isIsoDate, localDateOf } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type {
  AppCatalogResponse,
  AppTitlesResponse,
  CategoriesResponse,
  CategoryItem,
  MeResponse,
  UsageAppItem,
  UsageCategoryItem,
  UsageReportResponse,
} from "@/lib/types";
import { useUrlState } from "@/lib/useUrlState";
import type { UrlStateCodec } from "@/lib/useUrlState";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { EChart } from "@/components/charts/EChart";
import { CategoryInlineSelect } from "@/components/apps/CategoryInlineSelect";
import { DeltaBadge, useComparisonRange } from "@/components/dashboard/comparison";
import {
  DeviceMultiSelect,
  PERIOD_PRESETS,
  PeriodPresetGroup,
  useFilterDevices,
} from "@/components/reports/filters";

/** Altura fixa dos gráficos - skeleton/vazio/erro com a mesma geometria. */
const CHART_H = 260;

/** Página da tabela no modo paginado pelo servidor. */
const PAGE_SIZE = 50;

/** page_size máximo do contrato - usado no modo de filtro client-side. */
const MAX_PAGE_SIZE = 100;

const AXIS_TEXT = BRAND.chartText;
const GRID_LINE = BRAND.chartGrid;

const selectClass = cn(
  "h-9 rounded-md border border-input bg-card px-3 text-sm",
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
);

type CardView = "chart" | "table";

/** "all" | "1" | "0" | "-1" | "none" (none = Não categorizado). */
type ClassificationFilter = "all" | "1" | "0" | "-1" | "none";

const CLASSIFICATION_FILTERS: readonly string[] = ["all", "1", "0", "-1", "none"];

interface DateRange {
  from: string;
  to: string;
}

/** Filtros da tela na URL (deep-link/compartilhável) - from/to ficam à parte. */
interface AppsFilters {
  deviceIds: string[];
  /** Id da categoria ou "all". */
  category: string;
  classification: ClassificationFilter;
  page: number;
}

const APPS_FILTERS_CODEC: UrlStateCodec<AppsFilters> = {
  parse: (params) => {
    const rawDevices = params.get("device_ids");
    const rawClassification = params.get("classification");
    const rawPage = Number(params.get("page"));
    return {
      deviceIds: rawDevices !== null ? rawDevices.split(",").filter((id) => id.length > 0) : [],
      category: params.get("category") ?? "all",
      classification:
        rawClassification !== null && CLASSIFICATION_FILTERS.includes(rawClassification)
          ? (rawClassification as ClassificationFilter)
          : "all",
      page: Number.isInteger(rawPage) && rawPage > 1 ? rawPage : 1,
    };
  },
  serialize: (value) => ({
    device_ids: value.deviceIds.length > 0 ? value.deviceIds.join(",") : null,
    category: value.category !== "all" ? value.category : null,
    classification: value.classification !== "all" ? value.classification : null,
    page: value.page > 1 ? String(value.page) : null,
  }),
};

interface DonutSlice {
  label: string;
  value: number;
  color: string;
}

/** Escapa HTML para os tooltips do ECharts (nomes vêm de dados do tenant). */
function escapeHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/** "12,3%" pt-BR sobre o total - nunca decimal com ponto na UI. */
function formatPercent(seconds: number, totalSeconds: number): string {
  if (totalSeconds <= 0) return "-";
  const pct = (seconds / totalSeconds) * 100;
  if (pct > 0 && pct < 0.1) return "<0,1%";
  return `${pct.toLocaleString("pt-BR", { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`;
}

export function AppsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  // Devices, categoria, classificação e página vivem na URL (replace, sem
  // histórico) - o link da tela reproduz exatamente o que está no filtro.
  const [filters, setFilters] = useUrlState(APPS_FILTERS_CODEC);
  const { deviceIds, category: categoryFilter, classification: classificationFilter, page } = filters;
  const [titlesApp, setTitlesApp] = useState<{ id: string; name: string } | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const tableSectionRef = useRef<HTMLDivElement | null>(null);

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const admin = isAdmin(meQuery.data);

  // Período: ?from&to válidos na URL têm precedência (link da Visão Geral);
  // sem URL o default são os últimos 7 dias no FUSO DA ORGANIZAÇÃO.
  const todayStr = timezone !== null ? localDateOf(new Date(), timezone) : null;
  const urlFrom = searchParams.get("from");
  const urlTo = searchParams.get("to");
  const range = useMemo<DateRange | null>(() => {
    if (urlFrom !== null && urlTo !== null && isIsoDate(urlFrom) && isIsoDate(urlTo) && urlFrom <= urlTo) {
      return { from: urlFrom, to: urlTo };
    }
    if (todayStr === null) return null;
    return { from: addDays(todayStr, -6), to: todayStr };
  }, [urlFrom, urlTo, todayStr]);

  const activePreset = useMemo<number | null>(() => {
    if (range === null || todayStr === null || range.to !== todayStr) return null;
    return PERIOD_PRESETS.find((d) => addDays(todayStr, -(d - 1)) === range.from) ?? null;
  }, [range, todayStr]);

  function applyPreset(days: number): void {
    if (todayStr === null) return;
    const from = addDays(todayStr, -(days - 1));
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.set("from", from);
        next.set("to", todayStr);
        return next;
      },
      { replace: true },
    );
  }

  // Trocar o período volta a tabela para a primeira página. O guard com o
  // range anterior evita clobber do ?page= de um deep-link no mount e, com o
  // setter checando igualdade antes de escrever, não há loop de history.
  const prevRangeKeyRef = useRef<string | null>(null);
  useEffect(() => {
    if (range === null) return;
    const key = `${range.from}|${range.to}`;
    if (prevRangeKeyRef.current !== null && prevRangeKeyRef.current !== key) {
      setFilters({ ...filters, page: 1 });
    }
    prevRangeKeyRef.current = key;
    // Deps restritas ao range de propósito: o reset acontece SÓ na troca de período.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [range?.from, range?.to]);

  // Devices do filtro (componente compartilhado das telas de relatório):
  // arquivados ficam fora, pausados/revogados entram - podem ter histórico.
  const { devices } = useFilterDevices();

  const categoriesQuery = useQuery({
    queryKey: ["categories"],
    queryFn: () => api<CategoriesResponse>("/categories"),
    staleTime: 60_000,
  });
  const categories: CategoryItem[] = categoriesQuery.data?.items ?? [];

  // Badge "N apps sem categoria": uncategorized_count vem em qualquer resposta
  // de /app-catalog; uncategorized=true mantém o payload pequeno.
  const catalogQuery = useQuery({
    queryKey: ["app-catalog", { uncategorized: true, q: "" }],
    queryFn: () => api<AppCatalogResponse>("/app-catalog?uncategorized=true"),
    staleTime: 60_000,
  });
  const uncatCount = catalogQuery.data?.uncategorized_count ?? 0;

  const deviceIdsKey = useMemo(() => [...deviceIds].sort().join(","), [deviceIds]);
  const deviceParam = deviceIdsKey.length > 0 ? `&device_ids=${deviceIdsKey}` : "";

  const usageByCategory = useQuery({
    queryKey: ["reports", "usage", { group_by: "category", from: range?.from, to: range?.to, devices: deviceIdsKey }],
    queryFn: () =>
      api<UsageReportResponse<UsageCategoryItem>>(
        `/reports/usage?from=${range?.from ?? ""}&to=${range?.to ?? ""}${deviceParam}&group_by=category&page=1&page_size=${MAX_PAGE_SIZE}`,
      ),
    enabled: range !== null,
    placeholderData: (prev) => prev,
  });
  const chartsData = usageByCategory.data;

  // Comparativo do donut: a MESMA consulta por categoria no período
  // imediatamente anterior de mesma duração (mesmos devices).
  const prevRange = useComparisonRange(range);
  const usageByCategoryPrev = useQuery({
    queryKey: [
      "reports",
      "usage",
      { group_by: "category", from: prevRange?.from, to: prevRange?.to, devices: deviceIdsKey },
    ],
    queryFn: () =>
      api<UsageReportResponse<UsageCategoryItem>>(
        `/reports/usage?from=${prevRange?.from ?? ""}&to=${prevRange?.to ?? ""}${deviceParam}&group_by=category&page=1&page_size=${MAX_PAGE_SIZE}`,
      ),
    enabled: prevRange !== null,
    placeholderData: (prev) => prev,
  });

  // Modo da tabela: com filtro de categoria/classificação ativo busca uma
  // única página com os 100 apps de mais tempo e filtra no cliente.
  const tableFilterActive = categoryFilter !== "all" || classificationFilter !== "all";
  const effPage = tableFilterActive ? 1 : page;
  const effSize = tableFilterActive ? MAX_PAGE_SIZE : PAGE_SIZE;
  const usageApps = useQuery({
    queryKey: [
      "reports",
      "usage",
      { group_by: "app", from: range?.from, to: range?.to, devices: deviceIdsKey, page: effPage, page_size: effSize },
    ],
    queryFn: () =>
      api<UsageReportResponse<UsageAppItem>>(
        `/reports/usage?from=${range?.from ?? ""}&to=${range?.to ?? ""}${deviceParam}&group_by=app&page=${effPage}&page_size=${effSize}`,
      ),
    enabled: range !== null,
    placeholderData: (prev) => prev,
  });
  const appsData = usageApps.data;

  const visibleRows = useMemo(() => {
    let items = appsData?.items ?? [];
    if (categoryFilter !== "all") {
      items = items.filter((i) => i.category !== null && i.category.id === categoryFilter);
    }
    if (classificationFilter !== "all") {
      items = items.filter((i) =>
        classificationFilter === "none"
          ? // a categoria seedada "Não categorizado" conta como o próprio balde
            i.category === null || i.category.name === UNCATEGORIZED_LABEL
          : i.category !== null && i.category.classification === Number(classificationFilter),
      );
    }
    return items;
  }, [appsData, categoryFilter, classificationFilter]);

  // Linhas de group_by=category com a categoria seedada "Não categorizado"
  // mesclada no balde null - a tabela exibe esses apps com o mesmo rótulo, os
  // gráficos precisam contar do mesmo jeito.
  const mergedCategoryItems = useMemo(
    () => mergeUncategorizedRows(chartsData?.items ?? []),
    [chartsData],
  );

  // Donut por classificação: agrega as linhas de group_by=category em 4 fatias.
  const classTotals = useMemo(() => {
    const totals = { work: 0, neutral: 0, notWork: 0, uncategorized: 0 };
    for (const item of mergedCategoryItems) {
      if (item.classification === 1) totals.work += item.seconds_active;
      else if (item.classification === 0) totals.neutral += item.seconds_active;
      else if (item.classification === -1) totals.notWork += item.seconds_active;
      else totals.uncategorized += item.seconds_active;
    }
    return totals;
  }, [mergedCategoryItems]);

  const donutSlices = useMemo<DonutSlice[]>(
    () => [
      { label: "Relacionado ao trabalho", value: classTotals.work, color: classificationColors.workRelated },
      { label: "Neutro", value: classTotals.neutral, color: classificationColors.neutral },
      { label: "Não relacionado ao trabalho", value: classTotals.notWork, color: classificationColors.notWorkRelated },
      { label: "Não categorizado", value: classTotals.uncategorized, color: classificationColors.uncategorized },
    ],
    [classTotals],
  );
  const donutTotal = donutSlices.reduce((s, x) => s + x.value, 0);

  // Baldes do período ANTERIOR na MESMA ordem das fatias do donut - null
  // enquanto a resposta da comparação não chegou.
  const prevClassValues = useMemo<number[] | null>(() => {
    if (usageByCategoryPrev.data === undefined) return null;
    const totals = { work: 0, neutral: 0, notWork: 0, uncategorized: 0 };
    for (const item of mergeUncategorizedRows(usageByCategoryPrev.data.items)) {
      if (item.classification === 1) totals.work += item.seconds_active;
      else if (item.classification === 0) totals.neutral += item.seconds_active;
      else if (item.classification === -1) totals.notWork += item.seconds_active;
      else totals.uncategorized += item.seconds_active;
    }
    return [totals.work, totals.neutral, totals.notWork, totals.uncategorized];
  }, [usageByCategoryPrev.data]);

  const categoryBars = useMemo(() => {
    const items = [...mergedCategoryItems].sort((a, b) => b.seconds_active - a.seconds_active);
    return items.slice(0, 10);
  }, [mergedCategoryItems]);

  // Card CTA de curadoria (estado vazio da 1ª semana, Seção 8.9): há uso no
  // período ou apps no catálogo, mas NADA categorizado ainda.
  const showCuradoria =
    chartsData !== undefined &&
    uncatCount > 0 &&
    classTotals.work + classTotals.neutral + classTotals.notWork === 0;

  const anyFilterActive = deviceIds.length > 0 || tableFilterActive;

  function clearFilters(): void {
    setFilters({ deviceIds: [], category: "all", classification: "all", page: 1 });
  }

  function toggleDevice(id: string): void {
    const next = deviceIds.includes(id) ? deviceIds.filter((d) => d !== id) : [...deviceIds, id];
    setFilters({ ...filters, deviceIds: next, page: 1 });
  }

  function goToUncategorized(): void {
    setFilters({ ...filters, category: "all", classification: "none", page: 1 });
    tableSectionRef.current?.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  // Drill-down de títulos do app selecionado (aberto = dialog visível).
  const titlesQuery = useQuery({
    queryKey: ["app-catalog", "titles", titlesApp?.id, range?.from, range?.to],
    queryFn: () =>
      api<AppTitlesResponse>(
        `/app-catalog/${encodeURIComponent(titlesApp?.id ?? "")}/titles?from=${range?.from ?? ""}&to=${range?.to ?? ""}`,
      ),
    enabled: titlesApp !== null && range !== null,
  });

  const meBlocked = meQuery.isError && meQuery.data === undefined && range === null;
  const refetchFailed =
    (usageByCategory.isError && chartsData !== undefined) ||
    (usageApps.isError && appsData !== undefined);

  function retryAll(): void {
    if (usageByCategory.isError) void usageByCategory.refetch();
    if (usageApps.isError) void usageApps.refetch();
  }

  const header = (
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Aplicativos</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Tempo de uso por aplicativo no período e curadoria de categorias.
        </p>
      </div>
      {uncatCount > 0 && (
        <button
          type="button"
          onClick={goToUncategorized}
          className={cn(
            "inline-flex items-center gap-1.5 rounded-full border border-viz-improdutivo/40 bg-viz-improdutivo/10 px-2.5 py-0.5 text-xs text-viz-improdutivo",
            "transition-colors hover:bg-viz-improdutivo/15 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
          )}
        >
          <Tags className="h-3.5 w-3.5 shrink-0" aria-hidden />
          {uncatCount === 1 ? "1 app sem categoria · revisar" : `${uncatCount} apps sem categoria · revisar`}
        </button>
      )}
    </div>
  );

  // /me falhou e não há ?from&to na URL: sem fuso não há período - erro com retry.
  if (meBlocked) {
    return (
      <div className="space-y-6">
        {header}
        <Card>
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(meQuery.error)}</p>
            <Button variant="outline" onClick={() => void meQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        </Card>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {header}

      {/* Barra de filtros (todos os controles em h-9, padrão da timeline). */}
      <Card>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
          <PeriodPresetGroup active={activePreset} onSelect={applyPreset} disabled={todayStr === null} />
          {range !== null && (
            <span className="text-xs tabular-nums text-muted-foreground">
              {ddmm(range.from)} a {ddmm(range.to)}
            </span>
          )}

          <DeviceMultiSelect
            devices={devices}
            selected={deviceIds}
            onToggle={toggleDevice}
            onClear={() => setFilters({ ...filters, deviceIds: [], page: 1 })}
          />

          <select
            aria-label="Categoria"
            value={categoryFilter}
            onChange={(e) => setFilters({ ...filters, category: e.target.value, page: 1 })}
            className={selectClass}
          >
            <option value="all">Todas as categorias</option>
            {/* a seedada "Não categorizado" fica fora: é o filtro de classificação "none" */}
            {categories
              .filter((c) => c.name !== UNCATEGORIZED_LABEL)
              .map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
          </select>

          <select
            aria-label="Classificação"
            value={classificationFilter}
            onChange={(e) =>
              setFilters({ ...filters, classification: e.target.value as ClassificationFilter, page: 1 })
            }
            className={selectClass}
          >
            <option value="all">Todas as classificações</option>
            <option value="1">Relacionado ao trabalho</option>
            <option value="0">Neutro</option>
            <option value="-1">Não relacionado ao trabalho</option>
            <option value="none">Não categorizado</option>
          </select>

          {anyFilterActive && (
            <Button variant="ghost" size="sm" className="h-9" onClick={clearFilters}>
              Limpar filtros
            </Button>
          )}
        </div>
      </Card>

      {refetchFailed && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={retryAll}>
            Tentar novamente
          </Button>
        </div>
      )}

      {showCuradoria && (
        <Card>
          <div className="flex flex-col items-center gap-3 px-6 py-10 text-center">
            <span className="flex h-12 w-12 items-center justify-center rounded-full bg-muted">
              <Tags className="h-6 w-6 text-muted-foreground" aria-hidden />
            </span>
            <p className="text-base font-medium">
              {uncatCount === 1
                ? "1 app sem categoria para revisar"
                : `${uncatCount} apps sem categoria para revisar`}
            </p>
            <p className="max-w-md text-sm text-muted-foreground">
              Categorize os apps mais usados para os gráficos de classificação refletirem a
              organização. A categoria vale para toda a organização.
            </p>
            <div className="flex flex-wrap justify-center gap-2">
              {admin && (
                <Link
                  to="/configuracoes/categorias"
                  className="inline-flex h-9 items-center justify-center gap-2 whitespace-nowrap rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
                >
                  Abrir mapeamento de apps
                </Link>
              )}
              <Button variant="outline" size="sm" className="h-9" onClick={goToUncategorized}>
                Revisar nesta tela
              </Button>
            </div>
          </div>
        </Card>
      )}

      {/* Gráficos do período: donut por classificação + barras por categoria. */}
      <div className="grid gap-4 lg:grid-cols-2">
        <DonutCard
          query={usageByCategory}
          slices={donutSlices}
          total={donutTotal}
          range={range}
          prev={
            prevClassValues !== null && prevRange !== null
              ? { values: prevClassValues, range: prevRange }
              : null
          }
        />
        <CategoryBarsCard query={usageByCategory} items={categoryBars} range={range} />
      </div>

      <div ref={tableSectionRef} className="scroll-mt-6 space-y-3">
        {/* Aviso fixo da edição inline (vocabulário exato do contrato). */}
        {admin && (
          <div
            role="note"
            className="flex items-start gap-2 rounded-md border border-viz-neutro/30 bg-viz-neutro/10 px-3 py-2 text-sm text-viz-neutro"
          >
            <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
            <span>
              A categoria vale para toda a organização e reagrega os últimos 30 dias. Histórico
              anterior mantém a classificação antiga.
            </span>
          </div>
        )}

        {saveError !== null && (
          <div
            role="alert"
            className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
          >
            <span>{saveError}</span>
            <Button variant="outline" size="sm" onClick={() => setSaveError(null)}>
              Fechar
            </Button>
          </div>
        )}

        <Card>
          <CardHeader className="pb-3">
            <CardTitle className="text-base">Apps no período</CardTitle>
            <CardDescription>
              Ordenado por tempo ativo. O percentual é sobre o tempo ativo total do período.
            </CardDescription>
          </CardHeader>
          <div className="pb-0">
            {usageApps.isError && appsData === undefined ? (
              <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
                <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
                <p className="text-sm text-muted-foreground">{genericErrorMessage(usageApps.error)}</p>
                <Button variant="outline" onClick={() => void usageApps.refetch()}>
                  Tentar novamente
                </Button>
              </div>
            ) : (
              <div className={cn("overflow-x-auto", usageApps.isPlaceholderData && "opacity-70 transition-opacity")}>
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                      <th scope="col" className="px-6 py-2">App</th>
                      <th scope="col" className="px-3 py-2">Categoria</th>
                      <th scope="col" className="px-3 py-2 text-right">Tempo ativo</th>
                      <th scope="col" className="px-3 py-2 text-right">%</th>
                      <th scope="col" className="px-6 py-2 text-right">Dispositivos</th>
                    </tr>
                  </thead>
                  <tbody>
                    {appsData === undefined ? (
                      Array.from({ length: 6 }, (_, i) => (
                        <tr key={i} className="border-b last:border-b-0">
                          <td colSpan={5} className="px-6 py-2">
                            <Skeleton className="h-8 w-full" />
                          </td>
                        </tr>
                      ))
                    ) : visibleRows.length === 0 ? (
                      <tr>
                        <td colSpan={5} className="px-6 py-10 text-center text-sm text-muted-foreground">
                          {anyFilterActive ? (
                            <span className="inline-flex flex-col items-center gap-2">
                              <span>Nenhum resultado</span>
                              <Button variant="outline" size="sm" onClick={clearFilters}>
                                Limpar filtros
                              </Button>
                            </span>
                          ) : (
                            "Nenhum dado no período."
                          )}
                        </td>
                      </tr>
                    ) : (
                      visibleRows.map((item) => (
                        <tr key={item.app_id} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                          <td className="px-6 py-2">
                            <button
                              type="button"
                              title="Ver títulos de janela do app"
                              onClick={() =>
                                setTitlesApp({
                                  id: item.app_id,
                                  name: item.custom_display_name ?? item.display_name,
                                })
                              }
                              className="group block max-w-[20rem] rounded-sm text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                            >
                              <span className="block truncate font-medium group-hover:underline">
                                {item.custom_display_name ?? item.display_name}
                              </span>
                              <span className="block truncate text-xs text-muted-foreground">
                                {item.process_name}
                              </span>
                            </button>
                          </td>
                          <td className="px-3 py-2">
                            {admin ? (
                              <CategoryInlineSelect
                                appId={item.app_id}
                                categoryId={item.category?.id ?? null}
                                categoryName={item.category?.name ?? null}
                                customDisplayName={item.custom_display_name}
                                categories={categories}
                                onError={() =>
                                  setSaveError("Não foi possível salvar a categoria. Tente novamente.")
                                }
                              />
                            ) : (
                              <span className="flex items-center gap-2">
                                <span
                                  aria-hidden
                                  className="h-2.5 w-2.5 shrink-0 rounded-full"
                                  style={{
                                    backgroundColor:
                                      item.category?.color ??
                                      classificationColor(item.category?.classification ?? null),
                                  }}
                                />
                                <span className="max-w-[14rem] truncate">
                                  {item.category?.name ?? "Não categorizado"}
                                </span>
                              </span>
                            )}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatDuration(item.seconds_active)}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatPercent(item.seconds_active, appsData.total_seconds_active)}
                          </td>
                          <td className="whitespace-nowrap px-6 py-2 text-right tabular-nums">
                            {item.device_count}
                          </td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
            )}

            {/* Paginação do servidor (modo sem filtro de categoria/classificação). */}
            {!tableFilterActive && appsData !== undefined && appsData.total > PAGE_SIZE && (
              <div className="flex flex-wrap items-center justify-between gap-2 border-t px-6 py-3 text-sm">
                <span className="tabular-nums text-muted-foreground">
                  {`${(page - 1) * PAGE_SIZE + 1} a ${Math.min(page * PAGE_SIZE, appsData.total)} de ${appsData.total} apps`}
                </span>
                <div className="flex gap-2">
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={page <= 1}
                    onClick={() => setFilters({ ...filters, page: Math.max(1, page - 1) })}
                  >
                    Anterior
                  </Button>
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={page >= Math.ceil(appsData.total / PAGE_SIZE)}
                    onClick={() => setFilters({ ...filters, page: page + 1 })}
                  >
                    Próxima
                  </Button>
                </div>
              </div>
            )}

            {/* Nota do modo client-side: a janela de filtro é o top 100 do período. */}
            {tableFilterActive && appsData !== undefined && appsData.total > appsData.items.length && (
              <p className="border-t px-6 py-3 text-xs text-muted-foreground">
                Filtros de categoria e classificação são aplicados sobre os {appsData.items.length}{" "}
                apps com mais tempo ativo do período.
              </p>
            )}
          </div>
        </Card>
      </div>

      {/* Drill-down: top títulos de janela do app (dado pessoal, auditado). */}
      <Dialog
        open={titlesApp !== null}
        onOpenChange={(open) => {
          if (!open) setTitlesApp(null);
        }}
      >
        <DialogContent className="max-w-xl">
          <DialogHeader>
            <DialogTitle className="pr-8">{titlesApp?.name ?? ""}</DialogTitle>
            <DialogDescription>
              Títulos de janela com mais tempo ativo
              {range !== null && ` de ${ddmm(range.from)} a ${ddmm(range.to)}`}. Esta consulta fica
              registrada na trilha de auditoria.
            </DialogDescription>
          </DialogHeader>
          {titlesQuery.isPending ? (
            <div className="space-y-2">
              {Array.from({ length: 5 }, (_, i) => (
                <Skeleton key={i} className="h-6 w-full" />
              ))}
            </div>
          ) : titlesQuery.isError ? (
            <div className="flex flex-col items-center gap-3 py-6 text-center">
              <p className="text-sm text-muted-foreground">{genericErrorMessage(titlesQuery.error)}</p>
              <Button variant="outline" size="sm" onClick={() => void titlesQuery.refetch()}>
                Tentar novamente
              </Button>
            </div>
          ) : titlesQuery.data !== undefined ? (
            <>
              {titlesQuery.data.items.length === 0 && titlesQuery.data.masked_seconds === 0 ? (
                <p className="py-4 text-center text-sm text-muted-foreground">
                  Nenhum título no período.
                </p>
              ) : (
                <div className="max-h-80 overflow-y-auto">
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                        <th scope="col" className="px-3 py-2">Título</th>
                        <th scope="col" className="px-3 py-2 text-right">Tempo ativo</th>
                      </tr>
                    </thead>
                    <tbody>
                      {titlesQuery.data.items.map((t, i) => (
                        <tr key={i} className="border-b last:border-b-0">
                          <td className="max-w-[24rem] truncate px-3 py-1.5" title={t.window_title}>
                            {t.window_title}
                          </td>
                          <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                            {formatDuration(t.seconds_active)}
                          </td>
                        </tr>
                      ))}
                      {titlesQuery.data.masked_seconds > 0 && (
                        <tr className="border-b last:border-b-0">
                          <td className="px-3 py-1.5 text-muted-foreground">
                            Títulos mascarados pela política de privacidade
                            {admin && (
                              <>
                                {" · "}
                                <Link
                                  to="/configuracoes/privacidade"
                                  onClick={() => setTitlesApp(null)}
                                  className="underline underline-offset-2 hover:text-foreground"
                                >
                                  ver política
                                </Link>
                              </>
                            )}
                          </td>
                          <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums text-muted-foreground">
                            {formatDuration(titlesQuery.data.masked_seconds)}
                          </td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              )}
              <p className="border-t pt-3 text-sm tabular-nums text-muted-foreground">
                Tempo ativo total do app no período: {formatDuration(titlesQuery.data.total_seconds)}
              </p>
            </>
          ) : null}
        </DialogContent>
      </Dialog>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Card do donut por classificação
// -----------------------------------------------------------------------------

function DonutCard({
  query,
  slices,
  total,
  range,
  prev,
}: {
  query: UseQueryResult<UsageReportResponse<UsageCategoryItem>>;
  slices: DonutSlice[];
  total: number;
  range: DateRange | null;
  /** Baldes do período anterior na MESMA ordem das fatias - null sem base. */
  prev: { values: number[]; range: DateRange } | null;
}) {
  const [view, setView] = useState<CardView>("chart");
  const data = query.data;
  const empty = data !== undefined && total === 0;
  const option = useMemo<EChartsOption>(() => buildDonutOption(slices, total, empty), [slices, total, empty]);
  const failed = query.isError && data === undefined;

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="space-y-1.5">
            <CardTitle className="text-base">Tempo por classificação</CardTitle>
            <CardDescription className="tabular-nums">
              {range !== null ? `De ${ddmm(range.from)} a ${ddmm(range.to)}` : "Carregando período…"}
            </CardDescription>
          </div>
          <ViewToggle view={view} onChange={setView} />
        </div>
      </CardHeader>
      {/* div crua com padding próprio (CardContent p-6/pt-0 venceria, cn() sem tailwind-merge). */}
      <div className="px-6 pb-6">
        {failed ? (
          <ErrorState message={genericErrorMessage(query.error)} onRetry={() => void query.refetch()} />
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
              <EChart option={option} height={CHART_H} ariaHidden={view === "table"} />
              <ClassificationLegend />
            </div>
            {view === "table" && <DonutTable slices={slices} total={total} />}

            {/* Bloco de resumo do comparativo - FORA do gráfico, cor neutra. */}
            {prev !== null && !empty && (
              <div className="mt-3 space-y-1 border-t pt-3 text-xs">
                <p className="font-medium uppercase tracking-wide text-muted-foreground">
                  vs período anterior
                </p>
                {slices.map((s, i) => (
                  <p key={s.label} className="flex items-center justify-between gap-2">
                    <span className="text-muted-foreground">{s.label}</span>
                    <DeltaBadge
                      current={s.value}
                      previous={prev.values[i] ?? null}
                      previousRange={prev.range}
                      showLabel={false}
                    />
                  </p>
                ))}
              </div>
            )}
          </>
        )}
      </div>
    </Card>
  );
}

function buildDonutOption(slices: DonutSlice[], total: number, empty: boolean): EChartsOption {
  return {
    aria: { enabled: true },
    animation: false,
    tooltip: {
      trigger: "item",
      formatter: (params: unknown): string => {
        const p = params as { name: string; value: number; percent?: number };
        const pct = typeof p.percent === "number" ? ` · ${String(p.percent).replace(".", ",")}%` : "";
        return `<strong>${escapeHtml(p.name)}</strong><br/>${formatDuration(p.value)}${pct}`;
      },
    },
    series: [
      {
        type: "pie",
        radius: ["52%", "78%"],
        center: ["50%", "50%"],
        label: { show: false },
        labelLine: { show: false },
        emphasis: { scale: false },
        data: slices
          .filter((s) => s.value > 0)
          .map((s) => ({ name: s.label, value: s.value, itemStyle: { color: s.color } })),
      },
    ],
    graphic: [
      {
        type: "text",
        left: "center",
        top: "middle",
        silent: true,
        style: empty
          ? {
              text: "Sem dados no período",
              fill: AXIS_TEXT,
              font: "13px system-ui, -apple-system, sans-serif",
            }
          : {
              text: `${formatDuration(total)}\ntempo ativo`,
              align: "center",
              fill: BRAND.ink,
              font: '600 13px "Space Grotesk", "Segoe UI", sans-serif',
            },
      },
    ],
  };
}

/** Tabela acessível do donut - os MESMOS números do gráfico. */
function DonutTable({ slices, total }: { slices: DonutSlice[]; total: number }) {
  return (
    <div className="overflow-x-auto" style={{ minHeight: CHART_H }}>
      <table className="w-full text-sm" aria-label="Tempo por classificação, em tabela">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">Classificação</th>
            <th scope="col" className="px-3 py-2 text-right">Tempo ativo</th>
            <th scope="col" className="px-3 py-2 text-right">%</th>
          </tr>
        </thead>
        <tbody>
          {slices.map((s) => (
            <tr key={s.label} className="border-b last:border-b-0">
              <td className="whitespace-nowrap px-3 py-1.5">
                <span className="flex items-center gap-2">
                  <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={{ backgroundColor: s.color }} />
                  <span>{s.label}</span>
                </span>
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {formatDuration(s.value)}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                {formatPercent(s.value, total)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Card de barras horizontais por categoria
// -----------------------------------------------------------------------------

function CategoryBarsCard({
  query,
  items,
  range,
}: {
  query: UseQueryResult<UsageReportResponse<UsageCategoryItem>>;
  items: UsageCategoryItem[];
  range: DateRange | null;
}) {
  const [view, setView] = useState<CardView>("chart");
  const data = query.data;
  const empty = data !== undefined && items.length === 0;
  const option = useMemo<EChartsOption>(() => buildCategoryBarsOption(items, empty), [items, empty]);
  const failed = query.isError && data === undefined;

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="space-y-1.5">
            <CardTitle className="text-base">Tempo por categoria</CardTitle>
            <CardDescription className="tabular-nums">
              {range !== null
                ? `De ${ddmm(range.from)} a ${ddmm(range.to)} · 10 maiores`
                : "Carregando período…"}
            </CardDescription>
          </div>
          <ViewToggle view={view} onChange={setView} />
        </div>
      </CardHeader>
      {/* div crua com padding próprio (mesma armadilha do CardContent). */}
      <div className="px-6 pb-6">
        {failed ? (
          <ErrorState message={genericErrorMessage(query.error)} onRetry={() => void query.refetch()} />
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
              <EChart option={option} height={CHART_H} ariaHidden={view === "table"} />
            </div>
            {view === "table" && <CategoryBarsTable items={items} />}
          </>
        )}
      </div>
    </Card>
  );
}

function buildCategoryBarsOption(items: UsageCategoryItem[], empty: boolean): EChartsOption {
  const maxH = items.reduce((mx, i) => Math.max(mx, i.seconds_active / 3600), 0);
  return {
    aria: { enabled: true },
    animation: false,
    grid: { left: 132, right: 24, top: 8, bottom: 24 },
    tooltip: {
      trigger: "item",
      formatter: (params: unknown): string => {
        const p = params as { dataIndex: number };
        const item: UsageCategoryItem | undefined = items[p.dataIndex];
        if (item === undefined) return "";
        return [
          `<strong>${escapeHtml(item.name ?? "Não categorizado")}</strong>`,
          classificationLabel(item.classification),
          `Tempo ativo: ${formatDuration(item.seconds_active)}`,
          item.app_count === 1 ? "1 app" : `${item.app_count} apps`,
        ].join("<br/>");
      },
    },
    xAxis: {
      type: "value",
      max: Math.max(Math.ceil(maxH), 1),
      minInterval: 1,
      axisLabel: { formatter: "{value}h", color: AXIS_TEXT, fontSize: 11 },
      splitLine: { lineStyle: { color: GRID_LINE } },
    },
    yAxis: {
      type: "category",
      inverse: true, // maior tempo ativo no topo
      data: items.map((i) => i.name ?? "Não categorizado"),
      axisTick: { show: false },
      axisLine: { lineStyle: { color: GRID_LINE } },
      axisLabel: { color: BRAND.ink2, fontSize: 11, width: 118, overflow: "truncate" },
    },
    series: [
      {
        type: "bar",
        barMaxWidth: 16,
        data: items.map((i) => ({
          value: Math.round((i.seconds_active / 3600) * 100) / 100,
          itemStyle: { color: i.color ?? classificationColor(i.classification) },
        })),
      },
    ],
    graphic: empty
      ? [
          {
            type: "text",
            left: "center",
            top: "middle",
            silent: true,
            style: {
              text: "Sem dados no período",
              fill: AXIS_TEXT,
              font: "13px system-ui, -apple-system, sans-serif",
            },
          },
        ]
      : undefined,
  };
}

/** Tabela acessível das barras por categoria - os MESMOS números do gráfico. */
function CategoryBarsTable({ items }: { items: UsageCategoryItem[] }) {
  return (
    <div className="overflow-x-auto" style={{ minHeight: CHART_H }}>
      <table className="w-full text-sm" aria-label="Tempo por categoria, em tabela">
        <thead>
          <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2">Categoria</th>
            <th scope="col" className="px-3 py-2">Classificação</th>
            <th scope="col" className="px-3 py-2 text-right">Tempo ativo</th>
            <th scope="col" className="px-3 py-2 text-right">Apps</th>
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
              <tr key={item.category_id ?? "uncategorized"} className="border-b last:border-b-0">
                <td className="max-w-[14rem] truncate px-3 py-1.5 font-medium">
                  <span className="flex items-center gap-2">
                    <span
                      aria-hidden
                      className="h-2.5 w-2.5 shrink-0 rounded-full"
                      style={{ backgroundColor: item.color ?? classificationColor(item.classification) }}
                    />
                    <span className="truncate">{item.name ?? "Não categorizado"}</span>
                  </span>
                </td>
                <td className="whitespace-nowrap px-3 py-1.5">{classificationLabel(item.classification)}</td>
                <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">
                  {formatDuration(item.seconds_active)}
                </td>
                <td className="whitespace-nowrap px-3 py-1.5 text-right tabular-nums">{item.app_count}</td>
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Controles e estados compartilhados pelos cards
// -----------------------------------------------------------------------------

/** Legenda da classificação - vocabulário FIXO (Seção 8.7). */
function ClassificationLegend() {
  return (
    <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-muted-foreground">
      <LegendItem color={classificationColors.workRelated} label="Relacionado ao trabalho" />
      <LegendItem color={classificationColors.neutral} label="Neutro" />
      <LegendItem color={classificationColors.notWorkRelated} label="Não relacionado ao trabalho" />
      <LegendItem color={classificationColors.uncategorized} label="Não categorizado" />
    </div>
  );
}

function LegendItem({ color, label }: { color: string; label: string }) {
  return (
    <span className="flex items-center gap-1.5">
      <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={{ backgroundColor: color }} />
      <span>{label}</span>
    </span>
  );
}

/** Toggle "Ver dados" / "Ver gráfico" - mesmo padrão da Visão Geral. */
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
