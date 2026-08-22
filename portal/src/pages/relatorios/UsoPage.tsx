// =============================================================================
// Relatório de Uso de aplicativos (/relatorios/uso - F3.5): GET /reports/usage
// com seletor segmentado de agrupamento (Aplicativo | Categoria | Dispositivo
// | Pessoa) e os mesmos filtros de período/devices das demais telas.
// - Ordenação CLIENT-SIDE por qualquer coluna numérica (asc/desc, indicador no
//   cabeçalho, aria-sort). Agrupamento/ordenação/página vivem na URL
//   (?group_by=device&sort=seconds_idle&dir=desc&page=2), lidos E escritos de
//   volta (replace, sem histórico): o atalho do hub de Relatórios responde
//   "quem ficou mais tempo ocioso esta semana?" em 2 cliques (Relatórios ->
//   atalho), dentro do gate "< 3 cliques" do DoD F3.
//   A página busca page_size=100 (máximo do contrato); com mais itens que isso
//   a ordenação vale para a página corrente (nota exibida).
// - device/device_user trazem Ativo/Ocioso/Bloqueado/Ligada + os baldes de
//   classificação (vocabulário FIXO - jamais produtivo/improdutivo).
// - Coluna Pessoa renderiza display_name JÁ resolvido pelo backend (lane
//   máquina/DSR inclusos) - nunca reimplementar a regra no cliente - e linka
//   para a visão individual (/pessoas/{device_user_id}), exceto na lane-máquina.
// - Exportar CSV (admin E viewer): POST /exports {kind:"usage_csv"} com o
//   group_by corrente; acompanhamento em /relatorios/exportacoes.
// - Aba "Fora do horário" (?aba=fora-do-horario): atividade fora do horário de
//   trabalho declarado, reusando os MESMOS filtros de período e dispositivos da
//   aba de uso. Indicador de EQUILÍBRIO - jamais hora extra ou banco de horas -
//   com o disclaimer da Portaria 671/MTE herdado do relatório de Jornada e
//   exportação própria (kind fora_horario_csv), desabilitada quando a
//   organização não tem horário declarado ou restringe a coleta a ele.
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, ArrowDown, ArrowUp, ArrowUpDown } from "lucide-react";
import { api } from "@/lib/api";
import { classificationColor, classificationLabel, mergeUncategorizedRows } from "@/lib/classification";
import { ddmm, formatDuration } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type {
  ForaDoHorarioResponse,
  MeResponse,
  UsageAppItem,
  UsageCategoryItem,
  UsageDeviceItem,
  UsageDeviceUserItem,
  UsageReportResponse,
} from "@/lib/types";
import { useUrlState } from "@/lib/useUrlState";
import type { UrlStateCodec } from "@/lib/useUrlState";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  DeviceMultiSelect,
  PeriodPresetGroup,
  segmentedButton,
  segmentedOff,
  segmentedOn,
  useFilterDevices,
  useReportRange,
} from "@/components/reports/filters";
import { ExportCsvBanner, ExportCsvButton, useCsvExport } from "@/components/reports/ExportCsv";
import { foraDoHorarioKey, foraDoHorarioUrl } from "@/components/reports/ForaDoHorario";
import { TeamTagSelect, tagParam, useTeamTags } from "@/components/filters/TeamTagSelect";
import { FORA_PAGE_SIZE, ForaDoHorarioPanel } from "@/components/reports/ForaDoHorarioPanel";
import { DeltaBadge, useComparisonRange } from "@/components/dashboard/comparison";

/** page_size máximo do contrato - mais linhas sob a ordenação client-side. */
const PAGE_SIZE = 100;

/** UUID zero = lane-máquina (sem usuário Windows): não é pessoa, não tem página. */
const MACHINE_LANE = "00000000-0000-0000-0000-000000000000";

/** Abas da tela: o uso de aplicativos e o painel de fora do horário de trabalho. */
type UsoTab = "uso" | "fora-do-horario";

const TAB_OPTIONS: { value: UsoTab; label: string }[] = [
  { value: "uso", label: "Uso de aplicativos" },
  { value: "fora-do-horario", label: "Fora do horário de trabalho" },
];

type GroupBy = "app" | "category" | "device" | "device_user";

const GROUP_OPTIONS: { value: GroupBy; label: string }[] = [
  { value: "app", label: "Aplicativo" },
  { value: "category", label: "Categoria" },
  { value: "device", label: "Dispositivo" },
  { value: "device_user", label: "Pessoa" },
];

type UsageRow = UsageAppItem | UsageCategoryItem | UsageDeviceItem | UsageDeviceUserItem;

interface NumericColumn {
  key: string;
  label: string;
}

const DEVICE_NUMERIC: NumericColumn[] = [
  { key: "seconds_active", label: "Tempo ativo" },
  { key: "seconds_idle", label: "Tempo ocioso" },
  { key: "seconds_locked", label: "Tempo bloqueado" },
  { key: "seconds_on", label: "Tempo ligada" },
  { key: "seconds_work_related", label: "Relacionado ao trabalho" },
  { key: "seconds_neutral", label: "Neutro" },
  { key: "seconds_not_work_related", label: "Não relacionado ao trabalho" },
];

const NUMERIC_COLUMNS: Record<GroupBy, NumericColumn[]> = {
  app: [
    { key: "seconds_active", label: "Tempo ativo" },
    { key: "device_count", label: "Dispositivos" },
  ],
  category: [
    { key: "seconds_active", label: "Tempo ativo" },
    { key: "app_count", label: "Apps" },
  ],
  device: DEVICE_NUMERIC,
  device_user: DEVICE_NUMERIC,
};

interface SortState {
  key: string;
  dir: "asc" | "desc";
}

/** group_by da URL validado contra as opções - default "app". */
function groupByFromUrl(params: URLSearchParams): GroupBy {
  const value = params.get("group_by");
  return GROUP_OPTIONS.some((o) => o.value === value) ? (value as GroupBy) : "app";
}

/**
 * Ordenação da URL (?sort=seconds_idle&dir=desc) validada contra as colunas
 * numéricas do agrupamento - permite deep-links como o atalho "quem ficou mais
 * tempo ocioso esta semana?" do hub de Relatórios.
 */
function sortFromUrl(params: URLSearchParams): SortState | null {
  const key = params.get("sort");
  if (key === null || !NUMERIC_COLUMNS[groupByFromUrl(params)].some((c) => c.key === key)) {
    return null;
  }
  return { key, dir: params.get("dir") === "asc" ? "asc" : "desc" };
}

/** Estado da tela na URL - ciclo completo: a URL é lida E escrita (replace). */
interface UsoUrlState {
  tab: UsoTab;
  groupBy: GroupBy;
  sort: SortState | null;
  /** Etiqueta de equipe do recorte (F5); null = organização inteira. */
  tag: string | null;
  page: number;
}

// group_by + sort + dir + tag + page num único codec: são interdependentes
// (trocar o agrupamento ou a equipe zera ordenação e página numa escrita atômica).
const USO_CODEC: UrlStateCodec<UsoUrlState> = {
  parse: (params) => {
    const rawPage = Number(params.get("page"));
    const rawTag = params.get("tag");
    return {
      tab: params.get("aba") === "fora-do-horario" ? "fora-do-horario" : "uso",
      groupBy: groupByFromUrl(params),
      sort: sortFromUrl(params),
      tag: rawTag !== null && rawTag.trim().length > 0 ? rawTag : null,
      page: Number.isInteger(rawPage) && rawPage > 1 ? rawPage : 1,
    };
  },
  serialize: (value) => ({
    aba: value.tab !== "uso" ? value.tab : null,
    group_by: value.groupBy !== "app" ? value.groupBy : null,
    sort: value.sort !== null ? value.sort.key : null,
    // desc é o default do sortFromUrl: só "asc" precisa ir para a URL.
    dir: value.sort !== null && value.sort.dir === "asc" ? "asc" : null,
    tag: value.tag,
    page: value.page > 1 ? String(value.page) : null,
  }),
};

/** Cabeçalho ordenável: clique alterna desc -> asc -> ordem do servidor. */
function SortableTh({
  col,
  sort,
  onToggle,
}: {
  col: NumericColumn;
  sort: SortState | null;
  onToggle: (key: string) => void;
}) {
  const active = sort !== null && sort.key === col.key;
  return (
    <th
      scope="col"
      aria-sort={active ? (sort.dir === "asc" ? "ascending" : "descending") : "none"}
      className="px-3 py-2 text-right"
    >
      <button
        type="button"
        onClick={() => onToggle(col.key)}
        className="inline-flex items-center gap-1 rounded-sm uppercase tracking-wide transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        {col.label}
        {active ? (
          sort.dir === "asc" ? (
            <ArrowUp className="h-3 w-3 shrink-0" aria-hidden />
          ) : (
            <ArrowDown className="h-3 w-3 shrink-0" aria-hidden />
          )
        ) : (
          <ArrowUpDown className="h-3 w-3 shrink-0 opacity-40" aria-hidden />
        )}
      </button>
    </th>
  );
}

/**
 * Chave de junção entre períodos para a coluna "vs anterior" - APENAS para
 * app/categoria/dispositivo. Pessoas (device_user) nunca são comparadas.
 */
function comparisonKeyOf(groupBy: GroupBy, row: UsageRow): string {
  if (groupBy === "app") return (row as UsageAppItem).app_id;
  if (groupBy === "category") return (row as UsageCategoryItem).category_id ?? "uncategorized";
  return (row as UsageDeviceItem).device_id;
}

function CategoryDot({
  name,
  color,
  classification,
}: {
  name: string | null;
  color: string | null;
  classification: number | null;
}) {
  return (
    <span className="flex items-center gap-2">
      <span
        aria-hidden
        className="h-2.5 w-2.5 shrink-0 rounded-full"
        style={{ backgroundColor: color ?? classificationColor(classification) }}
      />
      <span className="max-w-[14rem] truncate">{name ?? "Não categorizado"}</span>
    </span>
  );
}

/** Célula da coluna "vs anterior" - "-" enquanto a base não carregou. */
function ComparisonCell({
  seconds,
  rowKey,
  prevSecondsByKey,
  prevRange,
}: {
  seconds: number;
  rowKey: string;
  prevSecondsByKey: Map<string, number> | null;
  prevRange: { from: string; to: string } | null;
}) {
  return (
    <td className="whitespace-nowrap px-3 py-2 text-right">
      {prevSecondsByKey === null ? (
        <span className="text-muted-foreground">-</span>
      ) : (
        <DeltaBadge
          current={seconds}
          previous={prevSecondsByKey.get(rowKey) ?? null}
          previousRange={prevRange}
          showLabel={false}
        />
      )}
    </td>
  );
}

export function UsoPage() {
  // Agrupamento, ordenação e página vivem na URL (deep-link do hub de
  // Relatórios) - lidos no mount E escritos de volta a cada interação.
  const [urlState, setUrlState] = useUrlState(USO_CODEC);
  const { tab, groupBy, sort, tag, page } = urlState;
  const foraTab = tab === "fora-do-horario";
  const [deviceIds, setDeviceIds] = useState<string[]>([]);
  // Coluna "vs anterior": toggle DESLIGADO por padrão e indisponível no
  // agrupamento por pessoa (nunca comparar pessoas entre si).
  const [compareOn, setCompareOn] = useState(false);
  const compareActive = compareOn && groupBy !== "device_user" && !foraTab;

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const { todayStr, range, activePreset, applyPreset } = useReportRange(timezone);

  const { devices } = useFilterDevices();
  const { tags } = useTeamTags();
  const deviceIdsKey = useMemo(() => [...deviceIds].sort().join(","), [deviceIds]);
  const deviceParam = deviceIdsKey.length > 0 ? `&device_ids=${deviceIdsKey}` : "";

  // Trocar período/devices volta para a primeira página (agrupamento zera a
  // página na própria escrita atômica do toggle). O guard com a chave anterior
  // preserva o ?page= de deep-links no mount; o setter checa igualdade antes
  // de escrever, então não há loop de history.
  const prevFilterKeyRef = useRef<string | null>(null);
  useEffect(() => {
    if (range === null) return;
    const key = `${range.from}|${range.to}|${deviceIdsKey}`;
    if (prevFilterKeyRef.current !== null && prevFilterKeyRef.current !== key) {
      setUrlState({ ...urlState, page: 1 });
    }
    prevFilterKeyRef.current = key;
    // Deps restritas aos filtros de propósito: reset SÓ quando eles mudam. A
    // equipe fica de fora porque já zera a página na própria escrita atômica.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [range?.from, range?.to, deviceIdsKey]);

  const usageQuery = useQuery({
    queryKey: [
      "reports",
      "usage",
      {
        group_by: groupBy,
        from: range?.from,
        to: range?.to,
        devices: deviceIdsKey,
        tag,
        page,
        page_size: PAGE_SIZE,
      },
    ],
    queryFn: () =>
      api<UsageReportResponse<UsageRow>>(
        `/reports/usage?from=${range?.from ?? ""}&to=${range?.to ?? ""}${deviceParam}${tagParam(tag)}` +
          `&group_by=${groupBy}&page=${page}&page_size=${PAGE_SIZE}`,
      ),
    enabled: range !== null && !foraTab,
    // Mantém a leitura anterior ao trocar página/período/devices, mas NÃO ao
    // trocar o agrupamento: as linhas têm formato diferente por group_by e
    // renderizar app como device (ou vice-versa) mostraria NaN no flash.
    placeholderData: (prev, prevQuery) => {
      const prevParams = prevQuery?.queryKey[2] as { group_by?: GroupBy } | undefined;
      return prevParams?.group_by === groupBy ? prev : undefined;
    },
  });
  const data = usageQuery.data;

  // Consulta do período imediatamente anterior (mesma duração, mesmos filtros)
  // para a coluna "vs anterior" - só roda com o toggle ligado.
  const prevRange = useComparisonRange(range);
  const prevUsageQuery = useQuery({
    queryKey: [
      "reports",
      "usage",
      {
        group_by: groupBy,
        from: prevRange?.from,
        to: prevRange?.to,
        devices: deviceIdsKey,
        tag,
        page: 1,
        page_size: PAGE_SIZE,
      },
    ],
    queryFn: () =>
      api<UsageReportResponse<UsageRow>>(
        `/reports/usage?from=${prevRange?.from ?? ""}&to=${prevRange?.to ?? ""}${deviceParam}${tagParam(tag)}` +
          `&group_by=${groupBy}&page=1&page_size=${PAGE_SIZE}`,
      ),
    enabled: compareActive && prevRange !== null,
    placeholderData: (prev, prevQuery) => {
      const prevParams = prevQuery?.queryKey[2] as { group_by?: GroupBy } | undefined;
      return prevParams?.group_by === groupBy ? prev : undefined;
    },
  });

  // Tempo ativo do período anterior indexado pela chave de junção - null
  // enquanto a base não carregou (as células mostram "-").
  const prevSecondsByKey = useMemo<Map<string, number> | null>(() => {
    if (!compareActive || prevUsageQuery.data === undefined) return null;
    let items: UsageRow[] = prevUsageQuery.data.items;
    if (groupBy === "category") items = mergeUncategorizedRows(items as UsageCategoryItem[]);
    const map = new Map<string, number>();
    for (const row of items) map.set(comparisonKeyOf(groupBy, row), row.seconds_active);
    return map;
  }, [compareActive, prevUsageQuery.data, groupBy]);

  // group_by=category: mescla a categoria seedada "Não categorizado" no balde
  // null (mesma regra dos gráficos da AppsPage - sem dupla contagem).
  const baseItems = useMemo<UsageRow[]>(() => {
    const items = data?.items ?? [];
    if (groupBy !== "category") return items;
    return mergeUncategorizedRows(items as UsageCategoryItem[]);
  }, [data, groupBy]);

  const sortedItems = useMemo<UsageRow[]>(() => {
    if (sort === null) return baseItems;
    const dir = sort.dir === "asc" ? 1 : -1;
    return [...baseItems].sort((a, b) => {
      const av = Number((a as unknown as Record<string, unknown>)[sort.key] ?? 0);
      const bv = Number((b as unknown as Record<string, unknown>)[sort.key] ?? 0);
      return (av - bv) * dir;
    });
  }, [baseItems, sort]);

  function toggleSort(key: string): void {
    const next: SortState | null =
      sort === null || sort.key !== key
        ? { key, dir: "desc" }
        : sort.dir === "desc"
          ? { key, dir: "asc" }
          : null;
    setUrlState({ ...urlState, sort: next });
  }

  const numericCols = NUMERIC_COLUMNS[groupBy];
  // Colunas de texto à esquerda, por agrupamento.
  const textHeaders: string[] =
    groupBy === "app"
      ? ["App", "Categoria"]
      : groupBy === "category"
        ? ["Categoria", "Classificação"]
        : groupBy === "device"
          ? ["Dispositivo"]
          : ["Dispositivo", "Usuário"];
  const colCount = textHeaders.length + numericCols.length + (compareActive ? 1 : 0);

  // MESMA queryKey do ForaDoHorarioPanel: o TanStack resolve do cache, sem
  // requisição extra. O botão de exportar precisa do status para não oferecer um
  // CSV que o backend recusaria com 409 (organização sem horário declarado ou
  // com a coleta restrita ao horário de trabalho).
  const foraParams = {
    from: range?.from ?? "",
    to: range?.to ?? "",
    deviceIdsKey,
    tag,
    page: 1,
    includeDevices: true,
    pageSize: FORA_PAGE_SIZE,
  };
  const foraStatusQuery = useQuery({
    queryKey: foraDoHorarioKey(foraParams),
    queryFn: () => api<ForaDoHorarioResponse>(foraDoHorarioUrl(foraParams)),
    enabled: range !== null && foraTab,
    placeholderData: (prev) => prev,
  });
  const foraExportavel = foraStatusQuery.data?.status === "ok";

  const exportMutation = useCsvExport();
  const exportRequest =
    range === null
      ? null
      : foraTab
        ? {
            kind: "fora_horario_csv" as const,
            params: {
              from: range.from,
              to: range.to,
              ...(deviceIds.length > 0 ? { device_ids: deviceIds } : {}),
              ...(tag !== null ? { tag } : {}),
            },
          }
        : {
            kind: "usage_csv" as const,
            params: {
              from: range.from,
              to: range.to,
              group_by: groupBy,
              ...(deviceIds.length > 0 ? { device_ids: deviceIds } : {}),
              ...(tag !== null ? { tag } : {}),
            },
          };

  const header = (
    <div>
      <h1 className="text-2xl font-semibold tracking-tight">Uso de aplicativos</h1>
      <p className="mt-1 text-sm text-muted-foreground">
        {foraTab
          ? "Atividade fora do horário de trabalho declarado pela organização, por dispositivo, com exportação em CSV."
          : "Relatório tabular de uso por aplicativo, categoria, dispositivo ou pessoa, com exportação em CSV."}
      </p>
    </div>
  );

  // GET /me falhou: sem fuso não há período - erro com retry (padrão da AppsPage).
  if (meQuery.isError && meQuery.data === undefined) {
    return (
      <div className="space-y-4">
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
    <div className="space-y-4">
      {header}

      {/* Abas da tela: uso de aplicativos | fora do horário de trabalho. */}
      <div role="tablist" aria-label="Visão do relatório" className="flex flex-wrap gap-1 border-b">
        {TAB_OPTIONS.map((opt) => (
          <button
            key={opt.value}
            type="button"
            role="tab"
            aria-selected={tab === opt.value}
            onClick={() => setUrlState({ ...urlState, tab: opt.value, sort: null, page: 1 })}
            className={cn(
              "-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
              tab === opt.value
                ? "border-primary text-primary"
                : "border-transparent text-muted-foreground hover:text-foreground",
            )}
          >
            {opt.label}
          </button>
        ))}
      </div>

      {/* Barra de filtros (controles em h-9): agrupamento, período, devices, export.
          Período e dispositivos servem as DUAS abas; o agrupamento só faz sentido
          no uso de aplicativos. */}
      <Card>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
          {!foraTab && (
            <div
              role="group"
              aria-label="Agrupar por"
              className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
            >
              {GROUP_OPTIONS.map((opt) => (
                <button
                  key={opt.value}
                  type="button"
                  aria-pressed={groupBy === opt.value}
                  onClick={() => setUrlState({ ...urlState, groupBy: opt.value, sort: null, page: 1 })}
                  className={cn(segmentedButton, groupBy === opt.value ? segmentedOn : segmentedOff)}
                >
                  {opt.label}
                </button>
              ))}
            </div>
          )}

          <PeriodPresetGroup active={activePreset} onSelect={applyPreset} disabled={todayStr === null} />
          {range !== null && (
            <span className="text-xs tabular-nums text-muted-foreground">
              {ddmm(range.from)} a {ddmm(range.to)}
            </span>
          )}
          <TeamTagSelect
            tags={tags}
            value={tag}
            onChange={(next) => setUrlState({ ...urlState, tag: next, page: 1 })}
          />
          <DeviceMultiSelect
            devices={devices}
            selected={deviceIds}
            onToggle={(id) =>
              setDeviceIds((prev) => (prev.includes(id) ? prev.filter((d) => d !== id) : [...prev, id]))
            }
            onClear={() => setDeviceIds([])}
          />
          {!foraTab && groupBy !== "device_user" && (
            <Button
              variant="outline"
              size="sm"
              className={cn("h-9", compareOn && "border-primary/50 bg-primary/10 text-primary")}
              aria-pressed={compareOn}
              title="Compara o tempo ativo de cada linha com o período imediatamente anterior, de mesma duração"
              onClick={() => setCompareOn((v) => !v)}
            >
              vs período anterior
            </Button>
          )}
          <div className="ml-auto">
            {/* Na aba de fora do horário o CSV só existe com janela declarada e
                coleta contínua: sem isso o botão fica desabilitado, e o painel
                logo abaixo explica o motivo. */}
            <ExportCsvButton
              mutation={exportMutation}
              request={exportRequest}
              disabled={foraTab && !foraExportavel}
            />
          </div>
        </div>
      </Card>

      <ExportCsvBanner mutation={exportMutation} />

      {/* Aba "Fora do horário": painel próprio, mesmos filtros de período e
          dispositivos da aba de uso. */}
      {foraTab && (
        <ForaDoHorarioPanel
          range={range}
          deviceIdsKey={deviceIdsKey}
          tag={tag}
          onClearFilters={() => {
            setDeviceIds([]);
            setUrlState({ ...urlState, tag: null, page: 1 });
          }}
        />
      )}

      {!foraTab && usageQuery.isError && data !== undefined && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void usageQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      {!foraTab && (
        <Card>
          {usageQuery.isError && data === undefined ? (
            <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
              <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
              <p className="text-sm text-muted-foreground">{genericErrorMessage(usageQuery.error)}</p>
              <Button variant="outline" onClick={() => void usageQuery.refetch()}>
                Tentar novamente
              </Button>
            </div>
          ) : (
            <div className={cn("overflow-x-auto", usageQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                    {textHeaders.map((label, i) => (
                      <th scope="col" key={label} className={cn("py-2", i === 0 ? "px-6" : "px-3")}>
                        {label}
                      </th>
                    ))}
                    {numericCols.map((col) => (
                      <SortableTh key={col.key} col={col} sort={sort} onToggle={toggleSort} />
                    ))}
                    {compareActive && (
                      <th
                        scope="col"
                        className="px-3 py-2 text-right"
                        title="Tempo ativo comparado ao período imediatamente anterior, de mesma duração"
                      >
                        vs anterior
                      </th>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {data === undefined ? (
                    Array.from({ length: 8 }, (_, i) => (
                      <tr key={i} className="border-b last:border-b-0">
                        <td colSpan={colCount} className="px-6 py-2">
                          <Skeleton className="h-8 w-full" />
                        </td>
                      </tr>
                    ))
                  ) : sortedItems.length === 0 ? (
                    <tr>
                      <td colSpan={colCount} className="px-6 py-10 text-center text-sm text-muted-foreground">
                        {deviceIds.length > 0 ? (
                          <span className="inline-flex flex-col items-center gap-2">
                            <span>Nenhum resultado</span>
                            <Button variant="outline" size="sm" onClick={() => setDeviceIds([])}>
                              Limpar filtros
                            </Button>
                          </span>
                        ) : (
                          "Nenhum dado no período."
                        )}
                      </td>
                    </tr>
                  ) : (
                    sortedItems.map((row) => {
                      if (groupBy === "app") {
                        const item = row as UsageAppItem;
                        return (
                          <tr key={item.app_id} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                            <td className="px-6 py-2">
                              <span className="block max-w-[20rem] truncate font-medium">
                                {item.custom_display_name ?? item.display_name}
                              </span>
                              <span className="block max-w-[20rem] truncate text-xs text-muted-foreground">
                                {item.process_name}
                              </span>
                            </td>
                            <td className="px-3 py-2">
                              <CategoryDot
                                name={item.category?.name ?? null}
                                color={item.category?.color ?? null}
                                classification={item.category?.classification ?? null}
                              />
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {formatDuration(item.seconds_active)}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {item.device_count}
                            </td>
                            {compareActive && (
                              <ComparisonCell
                                seconds={item.seconds_active}
                                rowKey={comparisonKeyOf(groupBy, item)}
                                prevSecondsByKey={prevSecondsByKey}
                                prevRange={prevRange}
                              />
                            )}
                          </tr>
                        );
                      }
                      if (groupBy === "category") {
                        const item = row as UsageCategoryItem;
                        return (
                          <tr
                            key={item.category_id ?? "uncategorized"}
                            className="border-b transition-colors last:border-b-0 hover:bg-accent/50"
                          >
                            <td className="px-6 py-2 font-medium">
                              <CategoryDot name={item.name} color={item.color} classification={item.classification} />
                            </td>
                            <td className="whitespace-nowrap px-3 py-2">
                              {classificationLabel(item.classification)}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {formatDuration(item.seconds_active)}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">{item.app_count}</td>
                            {compareActive && (
                              <ComparisonCell
                                seconds={item.seconds_active}
                                rowKey={comparisonKeyOf(groupBy, item)}
                                prevSecondsByKey={prevSecondsByKey}
                                prevRange={prevRange}
                              />
                            )}
                          </tr>
                        );
                      }
                      const item = row as UsageDeviceItem;
                      const user =
                        groupBy === "device_user" ? (row as UsageDeviceUserItem) : null;
                      return (
                        <tr
                          key={user !== null ? `${item.device_id}:${user.device_user_id}` : item.device_id}
                          className="border-b transition-colors last:border-b-0 hover:bg-accent/50"
                        >
                          <td className="max-w-[16rem] truncate px-6 py-2 font-medium">{item.device_name}</td>
                          {user !== null && (
                            // display_name resolvido pelo backend: nome amigável de
                            // device_users, lane máquina e titular removido (DSR) inclusos.
                            // Vira link para a visão individual, exceto na lane-máquina
                            // (UUID zero é sintética - não existe pessoa para abrir).
                            <td className="max-w-[12rem] truncate px-3 py-2">
                              {user.device_user_id === MACHINE_LANE ? (
                                user.display_name
                              ) : (
                                <Link
                                  to={`/pessoas/${user.device_user_id}`}
                                  className="underline decoration-dotted underline-offset-4 hover:text-primary"
                                >
                                  {user.display_name}
                                </Link>
                              )}
                            </td>
                          )}
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatDuration(item.seconds_active)}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatDuration(item.seconds_idle)}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatDuration(item.seconds_locked)}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatDuration(item.seconds_on)}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatDuration(item.seconds_work_related)}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatDuration(item.seconds_neutral)}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatDuration(item.seconds_not_work_related)}
                          </td>
                          {/* compareActive é sempre false em device_user (nunca comparar pessoas). */}
                          {compareActive && (
                            <ComparisonCell
                              seconds={item.seconds_active}
                              rowKey={comparisonKeyOf(groupBy, item)}
                              prevSecondsByKey={prevSecondsByKey}
                              prevRange={prevRange}
                            />
                          )}
                        </tr>
                      );
                    })
                  )}
                </tbody>
              </table>

              {data !== undefined && (
                <div className="flex flex-wrap items-center justify-between gap-2 border-t px-6 py-3 text-sm">
                  <span className="tabular-nums text-muted-foreground">
                    {data.total > PAGE_SIZE
                      ? `${(page - 1) * PAGE_SIZE + 1} a ${Math.min(page * PAGE_SIZE, data.total)} de ${data.total} itens · a ordenação vale para esta página`
                      : `Tempo ativo total do período: ${formatDuration(data.total_seconds_active)}`}
                  </span>
                  {data.total > PAGE_SIZE && (
                    <div className="flex gap-2">
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={page <= 1}
                        onClick={() => setUrlState({ ...urlState, page: Math.max(1, page - 1) })}
                      >
                        Anterior
                      </Button>
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={page >= Math.ceil(data.total / PAGE_SIZE)}
                        onClick={() => setUrlState({ ...urlState, page: page + 1 })}
                      >
                        Próxima
                      </Button>
                    </div>
                  )}
                </div>
              )}

              {/* Nota da base do comparativo: página 1 (top 100) do período anterior. */}
              {compareActive && prevRange !== null && data !== undefined && (
                <p className="border-t px-6 py-3 text-xs text-muted-foreground">
                  A comparação considera os {PAGE_SIZE} itens com mais tempo ativo do período
                  anterior, de {ddmm(prevRange.from)} a {ddmm(prevRange.to)}.
                </p>
              )}
            </div>
          )}
        </Card>
      )}
    </div>
  );
}
