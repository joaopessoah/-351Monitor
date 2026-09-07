// =============================================================================
// COLABORADORES (/colaboradores - F6, decisão 2 do spec de 07/09/2026): a lista
// que o site promete desde sempre ("análise por colaborador") e que o portal não
// tinha. Uma linha por PESSOA (windows_sid resolvido pela mesclagem), com os
// tempos do período, o índice e a cobertura JÁ calculados pelo servidor.
//
// LINHAS VERMELHAS DO PRODUTO (decisão 2, base TST/LGPD da seção 8 do spec):
//  - a ordem default é ALFABÉTICA. Ordenar por índice é ferramenta do gestor
//    autorizado, nunca placar: não existe medalha, pódio, cor de "pior" nem
//    destaque de topo/fundo da lista;
//  - a leitura é dado pessoal e é AUDITADA no servidor (view_report com o
//    recorte no detalhe) - a tela diz isso em voz alta;
//  - os chips de atenção sempre trazem VARIAÇÃO E CONTEXTO ("18% menos tempo
//    ativo que no período anterior"), jamais julgamento sobre a pessoa;
//  - ocioso é estado de MÁQUINA e nunca é somado como improdutivo.
//
// ÍNDICE E COBERTURA VÊM DO SERVIDOR (decisão 4): o portal só formata, e `null`
// imprime "–" - sem dado nunca vira 0%.
//
// COMPARAÇÃO E TENDÊNCIA sem uma consulta por pessoa:
//  - variação: UMA consulta de /people no período imediatamente anterior, de
//    mesma duração, juntada por windows_sid;
//  - tendência: 12 consultas de /people (uma por DIA da janela, não uma por
//    pessoa), juntadas pelo mesmo SID. O custo é fixo, independe do tamanho da
//    organização, e cada leitura deixa rastro de auditoria - por isso a coluna
//    tem interruptor, com a preferência guardada no navegador.
//
// COSTURA DE ROTA (temporária): /people identifica a pessoa pelo windows_sid,
// mas a visão individual existente é /pessoas/:id e espera o device_user_id
// (UUID do par dispositivo+conta). Enquanto as duas identidades não se unificam
// (fase seguinte), o clique na linha resolve o UUID sob demanda em
// GET /device-users?q={nome} e avisa na tela quando não encontra.
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  ArrowUpDown,
  Download,
  MonitorSmartphone,
  Search,
  ShieldCheck,
  Sparkles,
  UserCog,
} from "lucide-react";
import { api } from "@/lib/api";
import { BRAND } from "@/lib/brandTheme";
import { addDays, ddmm, formatDuration } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import {
  deltaPct,
  formatPct,
  MAX_PERIOD_DAYS,
  PERIOD_CODEC,
  PERIOD_LABELS,
  periodQuery,
  resolvePeriod,
} from "@/lib/period";
import type { PeriodPreset, ResolvedPeriod } from "@/lib/period";
import { isAdmin } from "@/lib/roles";
import { deviceUserLabel } from "@/lib/types";
import type {
  DeviceUserItem,
  MeResponse,
  PagedResponse,
  PeopleReportResponse,
  PersonPatchRequest,
  PersonRow,
} from "@/lib/types";
import { useUrlState } from "@/lib/useUrlState";
import type { UrlStateCodec } from "@/lib/useUrlState";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { TeamTagSelect, useTeamTags } from "@/components/filters/TeamTagSelect";

/** page_size da lista - padrão dos relatórios (o teto do contrato é 200). */
const PAGE_SIZE = 50;

/** Dias da coluna de tendência: uma consulta por dia, nunca uma por pessoa. */
const TREND_DAYS = 12;

/** Teto do page_size do contrato - usado nas consultas de apoio (dia/anterior). */
const SUPPORT_PAGE_SIZE = 200;

/** Preferência da coluna de tendência (o resto do estado vive na URL). */
const TREND_PREF_KEY = "m351.colaboradores.tendencia";

/** Teto do apelido no PATCH /people/{sid} (MaxDisplayNameLength do backend). */
const MAX_DISPLAY_NAME = 120;

/** Abaixo disso a curadoria da classificação já merece um chip de contexto. */
const COVERAGE_ATTENTION = 0.85;

/** Queda de tempo ativo que vira chip de contexto (regra 3 da seção 4 do spec). */
const ACTIVITY_DROP_ATTENTION = -0.15;

// -----------------------------------------------------------------------------
// Ordenação: allow-list ESPELHADA da do backend (SortColumns do PeopleController)
// -----------------------------------------------------------------------------

type SortKey =
  | "name"
  | "seconds_on"
  | "seconds_active"
  | "productivity_index"
  | "seconds_idle"
  | "seconds_unclassified";

interface SortOption {
  key: SortKey;
  /** Rótulo do seletor e do cabeçalho da coluna. */
  label: string;
  /** Direção que faz sentido no PRIMEIRO clique (nome sobe, tempo desce). */
  first: "asc" | "desc";
}

const SORT_OPTIONS: SortOption[] = [
  { key: "name", label: "Nome", first: "asc" },
  { key: "seconds_on", label: "Ligada", first: "desc" },
  { key: "seconds_active", label: "Ativa", first: "desc" },
  { key: "productivity_index", label: "Índice", first: "desc" },
  { key: "seconds_idle", label: "Ociosa", first: "desc" },
  { key: "seconds_unclassified", label: "Sem classificação", first: "desc" },
];

function sortOptionOf(key: SortKey): SortOption {
  return SORT_OPTIONS.find((o) => o.key === key) ?? SORT_OPTIONS[0];
}

interface PeopleUrlState {
  sort: SortKey;
  dir: "asc" | "desc";
  /** Etiqueta de equipe do recorte; null = organização inteira. */
  tag: string | null;
  page: number;
}

// sort + dir + tag + page num ÚNICO codec: são interdependentes (trocar
// ordenação ou equipe zera a página numa escrita atômica). O período tem codec
// próprio (PERIOD_CODEC, compartilhado com as outras telas de análise) e o reset
// de página dele acontece no efeito de filtros, como na UsoPage.
const PEOPLE_CODEC: UrlStateCodec<PeopleUrlState> = {
  parse: (params) => {
    const rawSort = params.get("sort");
    const sort = SORT_OPTIONS.some((o) => o.key === rawSort) ? (rawSort as SortKey) : "name";
    const rawDir = params.get("dir");
    const rawPage = Number(params.get("page"));
    const rawTag = params.get("tag");
    return {
      sort,
      // Sem ?dir= vale a direção natural da coluna (nome asc, tempos desc).
      dir: rawDir === "asc" || rawDir === "desc" ? rawDir : sortOptionOf(sort).first,
      tag: rawTag !== null && rawTag.trim().length > 0 ? rawTag : null,
      page: Number.isInteger(rawPage) && rawPage > 1 ? rawPage : 1,
    };
  },
  serialize: (value) => ({
    // Ordem alfabética é o DEFAULT do produto: some da URL.
    sort: value.sort !== "name" ? value.sort : null,
    dir: value.dir !== sortOptionOf(value.sort).first ? value.dir : null,
    tag: value.tag,
    page: value.page > 1 ? String(value.page) : null,
  }),
};

// -----------------------------------------------------------------------------
// Régua do período anterior (a MESMA do backend: intervalo imediatamente
// anterior, de igual duração) e contagem de dias/dias úteis da janela.
// -----------------------------------------------------------------------------

/** Epoch UTC (ms) de uma data local yyyy-MM-dd - aritmética imune a DST. */
function epochUtc(dateStr: string): number {
  const [y, m, d] = dateStr.split("-").map(Number);
  return Date.UTC(y, m - 1, d);
}

function daysBetween(from: string, to: string): number {
  return Math.round((epochUtc(to) - epochUtc(from)) / 86_400_000) + 1;
}

/** Intervalo anterior de mesma duração - base única da variação da tela. */
function previousRangeOf(period: ResolvedPeriod): { from: string; to: string } {
  const days = daysBetween(period.from, period.to);
  const to = addDays(period.from, -1);
  return { from: addDays(to, -(days - 1)), to };
}

/**
 * Dias úteis (segunda a sexta) do intervalo. É calendário, não métrica de
 * produto: serve só para o chip "dado em X de Y dias úteis" não acusar falta de
 * dado num sábado. Feriados não são conhecidos pelo portal, por isso o chip
 * exige uma folga de dois dias antes de aparecer.
 */
function weekdaysBetween(from: string, to: string): number {
  let count = 0;
  for (let ms = epochUtc(from); ms <= epochUtc(to); ms += 86_400_000) {
    const dow = new Date(ms).getUTCDay();
    if (dow !== 0 && dow !== 6) count += 1;
  }
  return count;
}

/** Os TREND_DAYS dias que terminam no último dia do período (mais antigo primeiro). */
function trendDaysOf(period: ResolvedPeriod): string[] {
  const days: string[] = [];
  for (let i = TREND_DAYS - 1; i >= 0; i -= 1) days.push(addDays(period.to, -i));
  return days;
}

// -----------------------------------------------------------------------------
// Chips de atenção: SEMPRE variação e contexto, jamais julgamento da pessoa
// -----------------------------------------------------------------------------

interface Attention {
  key: string;
  label: string;
  title: string;
  /** "att" = âmbar de contexto; "info" = azul neutro. Nunca vermelho de culpa. */
  tone: "att" | "info";
}

function attentionsOf(
  row: PersonRow,
  previous: PersonRow | undefined,
  period: ResolvedPeriod,
  prevRange: { from: string; to: string },
  hasPrevious: boolean,
): Attention[] {
  const chips: Attention[] = [];

  // 1. Queda de tempo ativo vs o período anterior (regra 3 da seção 4 do spec).
  const delta = hasPrevious ? deltaPct(row.seconds_active, previous?.seconds_active ?? null) : null;
  if (delta !== null && delta <= ACTIVITY_DROP_ATTENTION) {
    chips.push({
      key: "atividade",
      tone: "att",
      label: `${Math.round(Math.abs(delta) * 100)}% menos tempo ativo`,
      title:
        `Tempo ativo de ${formatDuration(row.seconds_active)} no período contra ` +
        `${formatDuration(previous?.seconds_active ?? 0)} de ${ddmm(prevRange.from)} a ${ddmm(prevRange.to)}.`,
    });
  }

  // 2. Cobertura da classificação: o valor é o do SERVIDOR, exibido cru.
  if (row.classification_coverage !== null && row.classification_coverage < COVERAGE_ATTENTION) {
    chips.push({
      key: "cobertura",
      tone: "info",
      label: `classificação cobre ${formatPct(row.classification_coverage)}`,
      title:
        "Parte do tempo ativo está em aplicativos sem categoria na sua empresa, então fica fora " +
        "do índice. Categorizar esses aplicativos melhora a leitura.",
    });
  }

  // 3. Dias sem dado - contexto de coleta, não de comportamento.
  const weekdays = weekdaysBetween(period.from, period.to);
  if (row.days_with_data === 0) {
    chips.push({
      key: "sem-dado",
      tone: "info",
      label: "nenhum dia com dado",
      title: "Nenhum dia do período tem dado coletado para esta pessoa.",
    });
  } else if (weekdays >= 3 && row.days_with_data <= weekdays - 2) {
    chips.push({
      key: "dias",
      tone: "info",
      label: `dado em ${row.days_with_data} de ${weekdays} dias úteis`,
      title:
        "Dias úteis sem dado podem ser férias, folga, feriado ou dispositivo sem comunicação. " +
        "Confira o estado do dispositivo em Dispositivos.",
    });
  }

  return chips;
}

function AttentionChip({ chip }: { chip: Attention }) {
  return (
    <span
      title={chip.title}
      className={cn(
        "inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2 py-0.5 text-[11px]",
        chip.tone === "att"
          ? "border-viz-improdutivo/40 bg-viz-improdutivo/10 text-viz-improdutivo"
          : "border-viz-neutro/40 bg-viz-neutro/10 text-viz-neutro",
      )}
    >
      <span
        aria-hidden
        className={cn(
          "h-1.5 w-1.5 shrink-0 rounded-full",
          chip.tone === "att" ? "bg-viz-improdutivo" : "bg-viz-neutro",
        )}
      />
      {chip.label}
    </span>
  );
}

// -----------------------------------------------------------------------------
// Sparkline SVG à mão (sem ECharts): 12 pontos, geometria fixa, decorativa -
// o texto acessível vai no aria-label da célula.
// -----------------------------------------------------------------------------

function Sparkline({ values }: { values: number[] }) {
  const width = 92;
  const height = 24;
  if (values.length < 2) return <span className="text-muted-foreground">–</span>;

  const max = Math.max(...values);
  const min = Math.min(...values);
  const span = max - min;
  const points = values.map((v, i) => {
    const x = 3 + (i * (width - 6)) / (values.length - 1);
    // Série constante (inclusive toda zerada) desenha na linha de base.
    const y = span === 0 ? height - 4 : height - 3 - ((v - min) / span) * (height - 8);
    return [x, y] as const;
  });
  const line = points.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
  const last = points[points.length - 1];

  return (
    <svg
      viewBox={`0 0 ${width} ${height}`}
      width={width}
      height={height}
      aria-hidden
      className="block"
      preserveAspectRatio="none"
    >
      <path
        d={`M${line.split(" ").join(" L")} L${last[0].toFixed(1)},${height} L${points[0][0].toFixed(1)},${height} Z`}
        fill={BRAND.vizProdutivo}
        opacity={0.12}
      />
      <polyline
        points={line}
        fill="none"
        stroke={BRAND.vizProdutivo}
        strokeWidth={1.5}
        strokeLinejoin="round"
      />
      <circle
        cx={last[0]}
        cy={last[1]}
        r={2.5}
        fill={BRAND.vizProdutivo}
        stroke={BRAND.panel}
        strokeWidth={1.5}
      />
    </svg>
  );
}

/** Barra fina + valor do índice. `null` imprime "–", nunca 0%. */
function IndexCell({ value }: { value: number | null }) {
  return (
    <span className="flex items-center justify-end gap-2">
      <span
        aria-hidden
        className="hidden h-1.5 w-14 shrink-0 overflow-hidden rounded-full bg-viz-ocioso/50 sm:block"
      >
        <span
          className="block h-full rounded-full bg-viz-produtivo"
          style={{ width: value !== null ? `${Math.round(value * 100)}%` : "0%" }}
        />
      </span>
      <span className="tabular-nums">{formatPct(value)}</span>
    </span>
  );
}

/** Cabeçalho ordenável: clique alterna a direção (a coluna nunca "desliga"). */
function SortableTh({
  option,
  sort,
  dir,
  align,
  onSort,
}: {
  option: SortOption;
  sort: SortKey;
  dir: "asc" | "desc";
  align: "left" | "right";
  onSort: (key: SortKey) => void;
}) {
  const active = sort === option.key;
  return (
    <th
      scope="col"
      aria-sort={active ? (dir === "asc" ? "ascending" : "descending") : "none"}
      className={cn("py-2", align === "left" ? "px-6 text-left" : "px-3 text-right")}
    >
      <button
        type="button"
        onClick={() => onSort(option.key)}
        className={cn(
          "inline-flex items-center gap-1 rounded-sm uppercase tracking-wide transition-colors",
          "hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          active && "text-foreground",
        )}
      >
        {option.label}
        {active ? (
          dir === "asc" ? (
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

// -----------------------------------------------------------------------------
// Tela
// -----------------------------------------------------------------------------

export function ColaboradoresPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [urlState, setUrlState] = useUrlState(PEOPLE_CODEC);
  const { sort, dir, tag, page } = urlState;
  const [periodState, setPeriodState] = useUrlState(PERIOD_CODEC);

  // Busca com debounce de 300 ms (padrão da CategoriasPage). Fica em estado
  // local, não na URL: um link compartilhado carrega o recorte de período e
  // equipe, e o texto digitado é passageiro.
  const [search, setSearch] = useState("");
  const [q, setQ] = useState("");
  useEffect(() => {
    const id = window.setTimeout(() => setQ(search.trim()), 300);
    return () => window.clearTimeout(id);
  }, [search]);

  // Preferência da coluna de tendência: 12 leituras extras (uma por dia) que o
  // servidor AUDITA, então o gestor pode desligar. localStorage sempre em
  // try/catch (modo privado e cotas cheias lançam).
  const [showTrend, setShowTrend] = useState<boolean>(() => {
    try {
      return window.localStorage.getItem(TREND_PREF_KEY) !== "0";
    } catch {
      return true;
    }
  });
  useEffect(() => {
    try {
      window.localStorage.setItem(TREND_PREF_KEY, showTrend ? "1" : "0");
    } catch {
      // Preferência é conveniência: sem armazenamento a tela segue funcionando.
    }
  }, [showTrend]);

  const [notice, setNotice] = useState<string | null>(null);
  const [editing, setEditing] = useState<PersonRow | null>(null);

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const canEdit = isAdmin(meQuery.data);
  const period = useMemo(() => resolvePeriod(periodState, timezone), [periodState, timezone]);
  const { tags } = useTeamTags();

  const listQueryString =
    period !== null
      ? `${periodQuery(period, tag)}${q.length > 0 ? `&q=${encodeURIComponent(q)}` : ""}` +
        `&sort=${sort}&dir=${dir}&page=${page}&page_size=${PAGE_SIZE}`
      : "";

  const peopleQuery = useQuery({
    queryKey: ["people", { from: period?.from, to: period?.to, tag, q, sort, dir, page, page_size: PAGE_SIZE }],
    queryFn: () => api<PeopleReportResponse>(`/people?${listQueryString}`),
    enabled: period !== null,
    placeholderData: (prev) => prev,
  });
  const data = peopleQuery.data;
  const rows = data?.items ?? [];

  // Trocar período/equipe/busca volta para a primeira página. O guard com a
  // chave anterior preserva o ?page= de um deep-link no mount, e o setter da URL
  // checa igualdade antes de escrever (sem loop de histórico).
  const prevFilterKeyRef = useRef<string | null>(null);
  useEffect(() => {
    if (period === null) return;
    const key = `${period.from}|${period.to}|${tag ?? ""}|${q}`;
    if (prevFilterKeyRef.current !== null && prevFilterKeyRef.current !== key) {
      setUrlState({ ...urlState, page: 1 });
    }
    prevFilterKeyRef.current = key;
    // Deps restritas aos filtros: o reset acontece SÓ quando eles mudam.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [period?.from, period?.to, tag, q]);

  // UMA consulta do período anterior (mesma duração, mesmos filtros), juntada
  // por windows_sid: a variação de todas as linhas sai daqui, nunca de uma
  // consulta por pessoa. Ordem alfabética e page_size no teto do contrato.
  const prevRange = useMemo(() => (period !== null ? previousRangeOf(period) : null), [period]);
  const previousQuery = useQuery({
    queryKey: [
      "people",
      { from: prevRange?.from, to: prevRange?.to, tag, q, sort: "name", dir: "asc", page: 1, page_size: SUPPORT_PAGE_SIZE },
    ],
    queryFn: () =>
      api<PeopleReportResponse>(
        `/people?from=${prevRange?.from ?? ""}&to=${prevRange?.to ?? ""}` +
          `${tag !== null ? `&tag=${encodeURIComponent(tag)}` : ""}` +
          `${q.length > 0 ? `&q=${encodeURIComponent(q)}` : ""}` +
          `&sort=name&dir=asc&page=1&page_size=${SUPPORT_PAGE_SIZE}`,
      ),
    enabled: prevRange !== null,
    staleTime: 60_000,
  });
  const previousBySid = useMemo(() => {
    const map = new Map<string, PersonRow>();
    for (const row of previousQuery.data?.items ?? []) map.set(row.windows_sid, row);
    return map;
  }, [previousQuery.data]);
  const hasPrevious = previousQuery.data !== undefined;

  // Tendência: uma consulta por DIA da janela (12 no total), independente do
  // tamanho da organização. Cada dia é uma queryKey própria, então trocar
  // página ou ordenação não refaz nada.
  const trendDays = useMemo(() => (period !== null ? trendDaysOf(period) : []), [period]);
  const trendQueries = useQueries({
    queries: trendDays.map((day) => ({
      queryKey: [
        "people",
        { from: day, to: day, tag, q, sort: "name", dir: "asc", page: 1, page_size: SUPPORT_PAGE_SIZE },
      ],
      queryFn: () =>
        api<PeopleReportResponse>(
          `/people?from=${day}&to=${day}` +
            `${tag !== null ? `&tag=${encodeURIComponent(tag)}` : ""}` +
            `${q.length > 0 ? `&q=${encodeURIComponent(q)}` : ""}` +
            `&sort=name&dir=asc&page=1&page_size=${SUPPORT_PAGE_SIZE}`,
        ),
      enabled: showTrend && period !== null,
      staleTime: 5 * 60 * 1000,
    })),
  });
  const trendReady = showTrend && trendQueries.length > 0 && trendQueries.every((r) => r.data !== undefined);
  const trendBySid = useMemo(() => {
    if (!trendReady) return null;
    const map = new Map<string, number[]>();
    trendQueries.forEach((result, index) => {
      for (const row of result.data?.items ?? []) {
        let series = map.get(row.windows_sid);
        if (series === undefined) {
          series = new Array<number>(trendQueries.length).fill(0);
          map.set(row.windows_sid, series);
        }
        series[index] = row.seconds_active;
      }
    });
    return map;
    // trendQueries é recriado a cada render; a identidade dos dados está nos dias.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [trendReady, trendDays, trendQueries.map((r) => r.dataUpdatedAt).join(",")]);

  // Chips por linha e o contador de "N com atenção" (escopo: a página visível).
  const attentionByRow = useMemo(() => {
    if (period === null || prevRange === null) return new Map<string, Attention[]>();
    const map = new Map<string, Attention[]>();
    for (const row of rows) {
      map.set(row.windows_sid, attentionsOf(row, previousBySid.get(row.windows_sid), period, prevRange, hasPrevious));
    }
    return map;
  }, [rows, previousBySid, period, prevRange, hasPrevious]);
  const attentionCount = useMemo(
    () => [...attentionByRow.values()].filter((chips) => chips.length > 0).length,
    [attentionByRow],
  );

  const total = data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const hasFilters = q.length > 0 || tag !== null;

  function applySort(key: SortKey): void {
    // Mesma coluna inverte a direção; coluna nova entra na direção natural dela.
    const next: "asc" | "desc" =
      sort === key ? (dir === "asc" ? "desc" : "asc") : sortOptionOf(key).first;
    setUrlState({ ...urlState, sort: key, dir: next, page: 1 });
  }

  function setPreset(preset: PeriodPreset): void {
    if (preset === "custom" && period !== null) {
      // "Personalizado" começa do intervalo que está na tela, para o usuário
      // ajustar as pontas em vez de recomeçar de um campo vazio.
      setPeriodState({ preset, from: period.from, to: period.to });
      return;
    }
    setPeriodState({ preset, from: null, to: null });
  }

  /**
   * COSTURA DE ROTA (temporária, ver comentário do topo): /people devolve o
   * windows_sid e a visão individual espera o device_user_id. O UUID é resolvido
   * sob demanda por GET /device-users?q={nome}; sem correspondência a tela
   * explica em vez de navegar para um 404.
   */
  async function openPerson(row: PersonRow): Promise<void> {
    setNotice(null);
    try {
      const found = await queryClient.fetchQuery({
        queryKey: ["device-users", { q: row.display_name }],
        queryFn: () =>
          api<PagedResponse<DeviceUserItem>>(
            `/device-users?q=${encodeURIComponent(row.display_name)}&page_size=100`,
          ),
        staleTime: 60_000,
      });
      const items = found.items;
      const match =
        items.find((i) => deviceUserLabel(i) === row.display_name) ??
        items.find((i) => i.windows_username === row.display_name) ??
        items[0];
      if (match === undefined) {
        setNotice(
          `Não foi possível abrir a visão individual de ${row.display_name}: nenhum registro de ` +
            "dispositivo corresponde a este nome. A unificação das duas identidades (pessoa e " +
            "registro por dispositivo) entra na fase seguinte.",
        );
        return;
      }
      navigate(`/pessoas/${match.id}`);
    } catch (err) {
      setNotice(genericErrorMessage(err));
    }
  }

  const header = (
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Colaboradores</h1>
        <p className="mt-1 max-w-3xl text-sm text-muted-foreground">
          Uma linha por pessoa {period !== null ? `na ${period.label}` : "no período"} · ordenação
          alfabética por padrão · clique na linha para abrir a visão individual.
        </p>
      </div>
      {/* Não existe CSV de pessoas: os kinds do POST /exports são usage_csv,
          jornada_csv e fora_horario_csv. O botão fica desabilitado e explica -
          inventar endpoint aqui seria pior que a ausência. */}
      <Button
        variant="outline"
        size="sm"
        className="h-9"
        disabled
        title="O CSV de colaboradores entra na fase de relatórios. Hoje a exportação existe para uso de aplicativos, jornada e fora do horário."
      >
        <Download className="h-4 w-4" aria-hidden />
        Exportar CSV
      </Button>
    </div>
  );

  // GET /me falhou: sem fuso não há período - erro com retry (padrão das telas).
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

  const colCount = 7 + (showTrend ? 1 : 0) + (canEdit ? 1 : 0);

  return (
    <div className="space-y-4">
      {header}

      {/* Barra de ferramentas: período, busca, ordenação, equipe, tendência. */}
      <Card>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
          <div
            role="group"
            aria-label="Período"
            className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
          >
            {(["dia", "semana", "mes", "custom"] as const).map((preset) => (
              <button
                key={preset}
                type="button"
                aria-pressed={periodState.preset === preset}
                disabled={timezone === null}
                onClick={() => setPreset(preset)}
                className={cn(
                  "rounded-[5px] px-3 text-xs font-medium transition-colors disabled:opacity-40",
                  periodState.preset === preset
                    ? "bg-primary/10 text-primary"
                    : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
                )}
              >
                {PERIOD_LABELS[preset]}
              </button>
            ))}
          </div>

          {periodState.preset === "custom" && period !== null && (
            <span className="inline-flex items-center gap-1.5">
              <Input
                type="date"
                aria-label="De"
                value={period.from}
                max={period.to}
                onChange={(e) =>
                  setPeriodState({ preset: "custom", from: e.target.value, to: period.to })
                }
                className="h-9 w-[9.5rem]"
              />
              <span className="text-xs text-muted-foreground">a</span>
              <Input
                type="date"
                aria-label="Até"
                value={period.to}
                min={period.from}
                onChange={(e) =>
                  setPeriodState({ preset: "custom", from: period.from, to: e.target.value })
                }
                className="h-9 w-[9.5rem]"
              />
            </span>
          )}

          <span className="relative">
            <Search
              className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
              aria-hidden
            />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Buscar pessoa"
              aria-label="Buscar pessoa"
              className="h-9 w-52 pl-8"
            />
          </span>

          <span className="inline-flex items-center gap-1.5">
            <label htmlFor="people-sort" className="text-xs text-muted-foreground">
              Ordenar por
            </label>
            <select
              id="people-sort"
              value={sort}
              onChange={(e) => applySort(e.target.value as SortKey)}
              className={cn(
                "h-9 min-w-[11rem] rounded-md border border-input bg-card px-3 text-sm",
                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
              )}
            >
              {SORT_OPTIONS.map((opt) => (
                <option key={opt.key} value={opt.key}>
                  {opt.label}
                </option>
              ))}
            </select>
          </span>

          <TeamTagSelect
            tags={tags}
            value={tag}
            onChange={(next) => setUrlState({ ...urlState, tag: next, page: 1 })}
          />

          <Button
            variant="outline"
            size="sm"
            className={cn("h-9", showTrend && "border-primary/50 bg-primary/10 text-primary")}
            aria-pressed={showTrend}
            title={`Tendência de ${TREND_DAYS} dias do tempo ativo. Exige uma leitura por dia da janela, e toda leitura de dados de pessoas é registrada na auditoria.`}
            onClick={() => setShowTrend((v) => !v)}
          >
            <Sparkles className="h-4 w-4" aria-hidden />
            Tendência {TREND_DAYS} dias
          </Button>

          {data !== undefined && (
            <span
              className={cn(
                "ml-auto inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 py-1 text-xs",
                attentionCount > 0
                  ? "border-viz-improdutivo/40 bg-viz-improdutivo/10 text-viz-improdutivo"
                  : "border-border text-muted-foreground",
              )}
              title="Pessoas desta página com pelo menos um chip de contexto (variação de atividade, cobertura da classificação ou dias sem dado)."
            >
              <span
                aria-hidden
                className={cn(
                  "h-1.5 w-1.5 rounded-full",
                  attentionCount > 0 ? "bg-viz-improdutivo" : "bg-muted-foreground",
                )}
              />
              {attentionCount} com atenção
            </span>
          )}
        </div>
      </Card>

      {/* A leitura desta tela é dado pessoal e deixa rastro (decisão 2). */}
      <p className="flex items-start gap-2 px-1 text-xs text-muted-foreground">
        <ShieldCheck className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
        <span>
          Métricas do período por pessoa, com a classificação definida pela sua empresa. O índice e
          a cobertura são calculados no servidor; ocioso é estado da máquina e não conta como
          improdutivo. Esta consulta e a visão individual ficam registradas na auditoria (quem viu,
          quando e com qual recorte).
        </span>
      </p>

      {notice !== null && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-viz-improdutivo/30 bg-viz-improdutivo/10 px-3 py-2 text-sm text-viz-improdutivo"
        >
          <span>{notice}</span>
          <Button variant="ghost" size="sm" onClick={() => setNotice(null)}>
            Fechar
          </Button>
        </div>
      )}

      {periodState.preset === "custom" && period !== null && daysBetween(period.from, period.to) > MAX_PERIOD_DAYS && (
        <div
          role="alert"
          className="rounded-md border border-viz-improdutivo/30 bg-viz-improdutivo/10 px-3 py-2 text-sm text-viz-improdutivo"
        >
          O intervalo personalizado é limitado a {MAX_PERIOD_DAYS} dias. Reduza as datas para ver os
          dados.
        </div>
      )}

      {peopleQuery.isError && data !== undefined && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void peopleQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      <Card>
        {peopleQuery.isError && data === undefined ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(peopleQuery.error)}</p>
            <Button variant="outline" onClick={() => void peopleQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : (
          <div className={cn("overflow-x-auto", peopleQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <SortableTh
                    option={{ key: "name", label: "Colaborador", first: "asc" }}
                    sort={sort}
                    dir={dir}
                    align="left"
                    onSort={applySort}
                  />
                  {SORT_OPTIONS.filter((o) => o.key !== "name").map((opt) => (
                    <SortableTh
                      key={opt.key}
                      option={opt}
                      sort={sort}
                      dir={dir}
                      align="right"
                      onSort={applySort}
                    />
                  ))}
                  {/* "Fora do horário" NÃO entra: GET /people não devolve esse
                      recorte (só o relatório dedicado tem), e coluna vazia
                      mentiria. Fica em Relatórios → Fora do horário. */}
                  {showTrend && (
                    <th scope="col" className="px-3 py-2 text-left">
                      Tendência {TREND_DAYS} dias
                    </th>
                  )}
                  <th scope="col" className="px-3 py-2 text-left">
                    Atenção
                  </th>
                  {canEdit && (
                    <th scope="col" className="px-3 py-2 text-right">
                      <span className="sr-only">Ações</span>
                    </th>
                  )}
                </tr>
              </thead>
              <tbody>
                {data === undefined ? (
                  // Skeleton com a geometria final: 8 linhas da mesma altura.
                  Array.from({ length: 8 }, (_, i) => (
                    <tr key={i} className="border-b last:border-b-0">
                      <td colSpan={colCount} className="px-6 py-2">
                        <Skeleton className="h-9 w-full" />
                      </td>
                    </tr>
                  ))
                ) : rows.length === 0 ? (
                  <tr>
                    <td colSpan={colCount} className="px-6 py-14">
                      <EmptyState
                        hasFilters={hasFilters}
                        onClearFilters={() => {
                          setSearch("");
                          setQ("");
                          setUrlState({ ...urlState, tag: null, page: 1 });
                        }}
                      />
                    </td>
                  </tr>
                ) : (
                  rows.map((row) => {
                    const chips = attentionByRow.get(row.windows_sid) ?? [];
                    const series = trendBySid?.get(row.windows_sid) ?? null;
                    return (
                      <tr
                        key={row.windows_sid}
                        tabIndex={0}
                        role="link"
                        onClick={() => void openPerson(row)}
                        onKeyDown={(e) => {
                          if (e.key === "Enter" || e.key === " ") {
                            e.preventDefault();
                            void openPerson(row);
                          }
                        }}
                        className={cn(
                          "cursor-pointer border-b transition-colors last:border-b-0 hover:bg-accent/50",
                          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
                        )}
                      >
                        <td className="px-6 py-2">
                          {/* display_name JÁ resolvido pelo backend (apelido >
                              nome da lane > usuário do Windows > SID). */}
                          <span className="block max-w-[18rem] truncate font-medium">
                            {row.display_name}
                          </span>
                          <span className="mt-0.5 flex flex-wrap items-center gap-1">
                            {row.teams.slice(0, 3).map((team) => (
                              <span
                                key={team}
                                className="rounded-sm border border-border px-1.5 py-px text-[10px] uppercase tracking-wide text-muted-foreground"
                              >
                                {team}
                              </span>
                            ))}
                            {row.teams.length > 3 && (
                              <span className="text-[10px] text-muted-foreground">
                                +{row.teams.length - 3}
                              </span>
                            )}
                            {row.device_count > 1 && (
                              <span
                                className="inline-flex items-center gap-1 text-[10px] text-muted-foreground"
                                title={`Somada em ${row.device_count} dispositivos no período.`}
                              >
                                <MonitorSmartphone className="h-3 w-3" aria-hidden />
                                {row.device_count}
                              </span>
                            )}
                          </span>
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                          {formatDuration(row.seconds_on)}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                          {formatDuration(row.seconds_active)}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right">
                          <IndexCell value={row.productivity_index} />
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                          {formatDuration(row.seconds_idle)}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                          {formatDuration(row.seconds_unclassified)}
                        </td>
                        {showTrend && (
                          <td
                            className="px-3 py-2"
                            aria-label={
                              series !== null
                                ? `Tendência do tempo ativo nos últimos ${TREND_DAYS} dias, de ${formatDuration(series[0])} a ${formatDuration(series[series.length - 1])} por dia.`
                                : "Tendência ainda carregando."
                            }
                          >
                            {series !== null ? (
                              <Sparkline values={series} />
                            ) : (
                              <Skeleton className="h-6 w-[92px]" />
                            )}
                          </td>
                        )}
                        <td className="px-3 py-2">
                          {chips.length === 0 ? (
                            <span className="text-muted-foreground">–</span>
                          ) : (
                            <span className="flex flex-wrap gap-1">
                              {chips.map((chip) => (
                                <AttentionChip key={chip.key} chip={chip} />
                              ))}
                            </span>
                          )}
                        </td>
                        {canEdit && (
                          <td className="px-3 py-2 text-right">
                            {/* Apelido e mesclagem são de Admin+ (PATCH /people). */}
                            <Button
                              variant="ghost"
                              size="sm"
                              title="Definir apelido ou mesclar com outra pessoa"
                              onClick={(e) => {
                                e.stopPropagation();
                                setEditing(row);
                              }}
                            >
                              <UserCog className="h-4 w-4" aria-hidden />
                              <span className="sr-only">Apelido e mesclagem</span>
                            </Button>
                          </td>
                        )}
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>

            {data !== undefined && rows.length > 0 && (
              <div className="flex flex-wrap items-center justify-between gap-2 border-t px-6 py-3 text-sm">
                <span className="tabular-nums text-muted-foreground">
                  {total > PAGE_SIZE
                    ? `${(page - 1) * PAGE_SIZE + 1} a ${Math.min(page * PAGE_SIZE, total)} de ${total} pessoas`
                    : `${total} ${total === 1 ? "pessoa" : "pessoas"} no período`}
                </span>
                {total > PAGE_SIZE && (
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
                      disabled={page >= totalPages}
                      onClick={() => setUrlState({ ...urlState, page: page + 1 })}
                    >
                      Próxima
                    </Button>
                  </div>
                )}
              </div>
            )}

            {/* Base das colunas de apoio: as consultas de dia e de período
                anterior trazem no máximo SUPPORT_PAGE_SIZE pessoas (teto do
                contrato), em ordem alfabética. */}
            {data !== undefined && rows.length > 0 && prevRange !== null && (
              <p className="border-t px-6 py-3 text-xs text-muted-foreground">
                Variação comparada ao período imediatamente anterior, de {ddmm(prevRange.from)} a{" "}
                {ddmm(prevRange.to)}.
                {total > SUPPORT_PAGE_SIZE
                  ? ` A comparação e a tendência cobrem as primeiras ${SUPPORT_PAGE_SIZE} pessoas em ordem alfabética.`
                  : ""}
              </p>
            )}
          </div>
        )}
      </Card>

      {editing !== null && (
        <PersonIdentityDialog
          person={editing}
          others={rows.filter((r) => r.windows_sid !== editing.windows_sid)}
          onClose={() => setEditing(null)}
        />
      )}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Estado vazio desenhado
// -----------------------------------------------------------------------------

function EmptyState({
  hasFilters,
  onClearFilters,
}: {
  hasFilters: boolean;
  onClearFilters: () => void;
}) {
  if (hasFilters) {
    return (
      <div className="flex flex-col items-center gap-3 text-center">
        <p className="text-sm font-medium">Nenhuma pessoa com esses filtros</p>
        <p className="max-w-md text-sm text-muted-foreground">
          A busca é pelo nome exibido, e o recorte de equipe usa as etiquetas dos dispositivos.
        </p>
        <Button variant="outline" size="sm" onClick={onClearFilters}>
          Limpar filtros
        </Button>
      </div>
    );
  }
  return (
    <div className="flex flex-col items-center gap-3 text-center">
      <span className="flex h-11 w-11 items-center justify-center rounded-full bg-muted">
        <MonitorSmartphone className="h-5 w-5 text-muted-foreground" aria-hidden />
      </span>
      <p className="text-sm font-medium">Nenhuma pessoa com dado no período</p>
      <p className="max-w-md text-sm text-muted-foreground">
        As pessoas aparecem aqui a partir do que os dispositivos com o agente instalado enviam.
        Verifique se há dispositivos ativos e comunicando.
      </p>
      <a
        href="/dispositivos"
        className="text-sm font-medium text-primary underline underline-offset-2"
      >
        Ver Dispositivos
      </a>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Apelido e mesclagem (Admin+): PATCH /people/{sid}
// -----------------------------------------------------------------------------

/**
 * A mesclagem é o conserto manual do caso que o SID não resolve sozinho: a mesma
 * pessoa com duas contas do Windows. O backend recusa cadeia (A→B→C) e mesclar
 * consigo mesma, então aqui só oferecemos as OUTRAS pessoas da página.
 */
function PersonIdentityDialog({
  person,
  others,
  onClose,
}: {
  person: PersonRow;
  others: PersonRow[];
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(person.display_name);
  const [mergeInto, setMergeInto] = useState("");

  const mutation = useMutation({
    mutationFn: (body: PersonPatchRequest) =>
      api(`/people/${encodeURIComponent(person.windows_sid)}`, { method: "PATCH", body }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["people"] });
      onClose();
    },
  });

  const trimmed = name.trim();

  return (
    <Dialog open onOpenChange={(open) => (!open ? onClose() : undefined)}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Identidade de {person.display_name}</DialogTitle>
          <DialogDescription>
            O apelido substitui o nome exibido em todas as telas. A mesclagem soma duas contas do
            Windows na mesma pessoa. As duas ações ficam registradas na auditoria.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="space-y-1.5">
            <label htmlFor="person-name" className="text-sm font-medium">
              Apelido
            </label>
            <Input
              id="person-name"
              value={name}
              maxLength={MAX_DISPLAY_NAME}
              onChange={(e) => setName(e.target.value)}
              placeholder="Nome exibido"
            />
            <p className="text-xs text-muted-foreground">
              Em branco volta a exibir o nome resolvido pelo servidor (conta do Windows).
            </p>
          </div>

          <div className="space-y-1.5">
            <label htmlFor="person-merge" className="text-sm font-medium">
              Mesclar nesta pessoa
            </label>
            <select
              id="person-merge"
              value={mergeInto}
              onChange={(e) => setMergeInto(e.target.value)}
              className={cn(
                "h-9 w-full rounded-md border border-input bg-card px-3 text-sm",
                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
              )}
            >
              <option value="">Não mesclar</option>
              {others.map((other) => (
                <option key={other.windows_sid} value={other.windows_sid}>
                  {other.display_name}
                </option>
              ))}
            </select>
            <p className="text-xs text-muted-foreground">
              A lista traz as pessoas desta página. Depois de mesclada, esta conta passa a somar na
              pessoa escolhida.
            </p>
          </div>

          {mutation.isError && (
            <p role="alert" className="text-sm text-destructive">
              {genericErrorMessage(mutation.error)}
            </p>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancelar
          </Button>
          <Button
            disabled={mutation.isPending}
            onClick={() =>
              mutation.mutate({
                display_name: trimmed.length > 0 ? trimmed : null,
                ...(mergeInto.length > 0 ? { merged_into_sid: mergeInto } : {}),
              })
            }
          >
            {mutation.isPending ? "Salvando…" : "Salvar"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
