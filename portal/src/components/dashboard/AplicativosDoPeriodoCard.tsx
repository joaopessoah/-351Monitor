// =============================================================================
// "Aplicativos e sites" — top 8 do PERÍODO GLOBAL na Visão Geral (F6, linha 3
// do mockup aprovado), agora com DUAS LENTES sobre a mesma pergunta.
//
// POR QUE ELE SUBSTITUI O CARD ANTIGO: o "Top 10 apps da semana" do
// WeeklyChartsRow tinha janela PRÓPRIA (semana atual / semana anterior, com
// seletor só dele). Numa tela cujo período é global e vive na URL, um bloco com
// recorte particular é uma mentira de leitura: o gestor escolhe "Este mês" no
// cabeçalho e o card continua respondendo pela semana. Aqui a janela é a mesma
// dos outros blocos — o link reproduz o recorte visível inteiro.
//
// POR QUE SITE É UMA LENTE DESTE CARD, E NÃO UM CARD NOVO: a pergunta é a
// MESMA ("onde o tempo foi"), e as duas listas não se somam — o tempo dos sites
// já está dentro do tempo do navegador na lista de aplicativos. Lado a lado,
// em dois cards, alguém somaria os dois totais mais cedo ou mais tarde. Aqui a
// troca de lente deixa explícito que é o mesmo tempo, visto de outro jeito, e o
// rodapé muda o denominador junto.
//
// POR QUE 8 E NÃO 10: é o número do mockup aprovado, e é o que caberia sem
// truncar nomes na coluna span4 da linha 3.
//
// NÃO É RANKING DE PESSOAS: a lista é de APLICATIVOS ou de SITES. O total do
// rodapé é o tempo de TODOS eles no período (o denominador honesto), não a soma
// das oito barras — o servidor devolve os dois separados exatamente por isso.
// =============================================================================

import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import type { EChartsOption } from "echarts";
import { ArrowRight } from "lucide-react";

import { api } from "@/lib/api";
import { BRAND } from "@/lib/brandTheme";
import { productivityColor, productivityLabel } from "@/lib/classification";
import { formatDuration } from "@/lib/format";
import { periodQuery } from "@/lib/period";
import type { ResolvedPeriod } from "@/lib/period";
import type { TopAppCategory, TopAppItem, TopAppsResponse, TopSiteItem, TopSitesResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { EChart } from "@/components/charts/EChart";
import {
  BUCKET_LABELS,
  BlockCard,
  CHART_H,
  ChartSkeleton,
  EMPTY_GRAPHIC,
  InlineError,
  LegendItem,
  LegendRow,
  RefetchAlert,
  VIZ,
  ViewToggle,
  escapeHtml,
} from "@/components/dashboard/overviewKit";

/** Quantidade de itens do bloco — o número do mockup aprovado. */
export const TOP_APPS_LIMIT = 8;

/** As duas lentes do card. */
type Dimension = "app" | "site";

/**
 * `GET /dashboard/top-apps` no período GLOBAL. Hook exportado para que qualquer
 * outro bloco que precise da mesma lista some ao MESMO cache em vez de abrir
 * uma segunda requisição (o padrão de overviewData.ts).
 */
export function useTopAppsQuery(
  period: ResolvedPeriod | null,
  tag: string | null,
  limit = TOP_APPS_LIMIT,
): UseQueryResult<TopAppsResponse> {
  return useQuery({
    queryKey: ["dashboard", "top-apps", period?.from, period?.to, tag, limit],
    queryFn: () =>
      api<TopAppsResponse>(
        `/dashboard/top-apps?${period !== null ? periodQuery(period, tag) : ""}&limit=${limit}`,
      ),
    enabled: period !== null,
    // Troca de período/equipe mantém o desenho anterior no lugar do skeleton.
    placeholderData: (prev) => prev,
  });
}

/** `GET /dashboard/top-sites` — mesma disciplina do hook acima. */
export function useTopSitesQuery(
  period: ResolvedPeriod | null,
  tag: string | null,
  limit = TOP_APPS_LIMIT,
  enabled = true,
): UseQueryResult<TopSitesResponse> {
  return useQuery({
    queryKey: ["dashboard", "top-sites", period?.from, period?.to, tag, limit],
    queryFn: () =>
      api<TopSitesResponse>(
        `/dashboard/top-sites?${period !== null ? periodQuery(period, tag) : ""}&limit=${limit}`,
      ),
    enabled: enabled && period !== null,
    placeholderData: (prev) => prev,
  });
}

/** Linha normalizada: as duas lentes desenham exatamente o mesmo gráfico e a mesma tabela. */
interface Linha {
  key: string;
  label: string;
  /** Linha de baixo do rótulo (process_name / domínio) quando difere do label. */
  sublabel: string | null;
  seconds: number;
  category: TopAppCategory | null;
  deviceCount: number;
}

/** Nome que o usuário vê: apelido do tenant vence o nome do catálogo. */
function appLinha(item: TopAppItem): Linha {
  const label = item.custom_display_name ?? item.display_name;
  return {
    key: item.app_id,
    label,
    sublabel: label !== item.process_name ? item.process_name : null,
    seconds: item.seconds_active,
    category: item.category,
    deviceCount: item.device_count,
  };
}

function siteLinha(item: TopSiteItem): Linha {
  const label = item.custom_display_name ?? item.display_name;
  return {
    key: item.site_id,
    label,
    sublabel: label !== item.domain ? item.domain : null,
    seconds: item.seconds_active,
    category: item.category,
    deviceCount: item.device_count,
  };
}

/** Segmentado de lente — mesma geometria do ViewToggle ao lado. */
function DimensionToggle({
  dimension,
  onChange,
}: {
  dimension: Dimension;
  onChange: (d: Dimension) => void;
}) {
  return (
    <div
      role="group"
      aria-label="Ver por"
      className="inline-flex h-7 items-stretch rounded-md border border-input bg-card p-0.5"
    >
      {(["app", "site"] as const).map((value) => (
        <button
          key={value}
          type="button"
          aria-pressed={dimension === value}
          onClick={() => onChange(value)}
          className={cn(
            "rounded-[3px] px-2 text-xs font-medium transition-colors",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            dimension === value
              ? "bg-secondary text-foreground"
              : "text-muted-foreground hover:text-foreground",
          )}
        >
          {value === "app" ? "Aplicativos" : "Sites"}
        </button>
      ))}
    </div>
  );
}

export function AplicativosDoPeriodoCard({
  className,
  period,
  tag,
}: {
  className?: string;
  period: ResolvedPeriod | null;
  tag: string | null;
}) {
  const [view, setView] = useState<"chart" | "table">("chart");
  const [dimension, setDimension] = useState<Dimension>("app");

  const appsQuery = useTopAppsQuery(period, tag);
  // A lente de sites só abre requisição quando o gestor troca para ela: a Visão
  // Geral já é a tela com mais consultas do produto.
  const sitesQuery = useTopSitesQuery(period, tag, TOP_APPS_LIMIT, dimension === "site");

  const porApp = dimension === "app";
  const query = porApp ? appsQuery : sitesQuery;
  const totalSeconds = porApp
    ? appsQuery.data?.total_seconds_active
    : sitesQuery.data?.total_seconds_active;
  const carregado = porApp ? appsQuery.data !== undefined : sitesQuery.data !== undefined;

  const items = useMemo<Linha[]>(
    () =>
      porApp
        ? (appsQuery.data?.items ?? []).map(appLinha)
        : (sitesQuery.data?.items ?? []).map(siteLinha),
    [porApp, appsQuery.data, sitesQuery.data],
  );
  const vazio = carregado && items.length === 0;

  const option = useMemo<EChartsOption>(() => {
    const maxH = items.reduce((mx, i) => Math.max(mx, i.seconds / 3600), 0);
    const totalAtivo = totalSeconds ?? 0;

    return {
      aria: { enabled: true },
      animation: false,
      grid: { left: 126, right: 52, top: 6, bottom: 24 },
      tooltip: {
        trigger: "item",
        formatter: (params: unknown): string => {
          const p = params as { dataIndex: number };
          const item = items[p.dataIndex];
          if (item === undefined) return "";
          // % do total da LENTE corrente (todos os apps, ou toda a navegação):
          // com o denominador do top, oito linhas somariam sempre 100%.
          const share = totalAtivo > 0 ? `${Math.round((item.seconds / totalAtivo) * 100)}%` : "–";
          const classificacao =
            item.category !== null
              ? `${productivityLabel(item.category.classification)} · ${escapeHtml(item.category.name)}`
              : productivityLabel(null);
          return [
            `<strong>${escapeHtml(item.label)}</strong>`,
            item.sublabel !== null ? escapeHtml(item.sublabel) : null,
            classificacao,
            `Tempo ativo: ${formatDuration(item.seconds)}`,
            porApp ? `Do tempo ativo do período: ${share}` : `Da navegação do período: ${share}`,
            item.deviceCount === 1 ? "1 dispositivo" : `${item.deviceCount} dispositivos`,
          ]
            .filter((l) => l !== null)
            .join("<br/>");
        },
      },
      xAxis: {
        type: "value",
        max: Math.max(Math.ceil(maxH), 1),
        minInterval: 1,
        axisLabel: { formatter: "{value}h", color: VIZ.axisText, fontSize: 10 },
        splitLine: { lineStyle: { color: VIZ.grid } },
      },
      yAxis: {
        type: "category",
        inverse: true, // maior tempo ativo no topo
        data: items.map((i) => i.label),
        axisTick: { show: false },
        axisLine: { lineStyle: { color: VIZ.grid } },
        axisLabel: { color: VIZ.ink2, fontSize: 11, width: 112, overflow: "truncate" },
      },
      series: vazio
        ? []
        : [
            {
              type: "bar",
              barMaxWidth: 14,
              data: items.map((i) => ({
                value: Math.round((i.seconds / 3600) * 100) / 100,
                itemStyle: { color: productivityColor(i.category?.classification ?? null) },
              })),
              label: {
                show: true,
                position: "right",
                color: BRAND.ink2,
                fontSize: 10,
                formatter: (params: unknown): string => {
                  const p = params as { dataIndex: number };
                  const item = items[p.dataIndex];
                  return item !== undefined ? formatDuration(item.seconds) : "";
                },
              },
            },
          ],
      graphic: vazio ? EMPTY_GRAPHIC : undefined,
    };
  }, [items, vazio, totalSeconds, porApp]);

  const verTodos = porApp
    ? period !== null
      ? `/apps?from=${encodeURIComponent(period.from)}&to=${encodeURIComponent(period.to)}`
      : null
    : "/sites";

  return (
    <BlockCard
      className={className}
      title="Aplicativos e sites"
      hint={
        porApp
          ? `Top ${TOP_APPS_LIMIT} aplicativos por tempo ativo no período · cor pela classificação`
          : `Top ${TOP_APPS_LIMIT} sites por tempo de navegação no período · cor pela classificação`
      }
      tools={
        <>
          <DimensionToggle dimension={dimension} onChange={setDimension} />
          <ViewToggle view={view} onChange={setView} />
          {verTodos !== null && (
            <Link
              to={verTodos}
              className="inline-flex h-7 items-center gap-1 rounded-md border border-input px-2 text-xs font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              Ver todos
              <ArrowRight className="h-3.5 w-3.5" aria-hidden />
            </Link>
          )}
        </>
      }
    >
      {query.isError && carregado && <RefetchAlert onRetry={() => void query.refetch()} />}

      {query.isPending && !carregado ? (
        <ChartSkeleton />
      ) : !carregado ? (
        <InlineError
          message={
            porApp
              ? "Não foi possível carregar os aplicativos do período."
              : "Não foi possível carregar os sites do período."
          }
          onRetry={() => void query.refetch()}
        />
      ) : (
        <>
          {view === "chart" ? (
            <>
              <EChart option={option} height={CHART_H} />
              <LegendRow>
                <LegendItem color={VIZ.produtivo} label={BUCKET_LABELS.produtivo} />
                <LegendItem color={VIZ.neutro} label={BUCKET_LABELS.neutro} />
                <LegendItem color={VIZ.improdutivo} label={BUCKET_LABELS.improdutivo} />
                <LegendItem color={VIZ.semClassificacao} label={BUCKET_LABELS.semClassificacao} />
              </LegendRow>
            </>
          ) : (
            <div className="max-h-[268px] overflow-auto" style={{ minHeight: CHART_H }}>
              <table
                className="w-full text-xs"
                aria-label={
                  porApp
                    ? `Top ${TOP_APPS_LIMIT} aplicativos do período, em tabela`
                    : `Top ${TOP_APPS_LIMIT} sites do período, em tabela`
                }
              >
                <thead className="sticky top-0 bg-card">
                  <tr className="border-b text-left text-[10px] uppercase tracking-wide text-muted-foreground">
                    <th scope="col" className="py-1.5 pr-2">{porApp ? "App" : "Site"}</th>
                    <th scope="col" className="py-1.5 pr-2">Classificação</th>
                    <th scope="col" className="py-1.5 pr-2 text-right">Tempo ativo</th>
                    <th scope="col" className="py-1.5 text-right">
                      {porApp ? "Do período" : "Da navegação"}
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {items.length === 0 ? (
                    <tr>
                      <td colSpan={4} className="py-3 text-center text-muted-foreground">
                        {porApp ? "Sem dados no período" : "Sem navegação registrada no período"}
                      </td>
                    </tr>
                  ) : (
                    items.map((item) => (
                      <tr key={item.key} className="border-b last:border-b-0">
                        <th scope="row" className="py-1 pr-2 text-left font-normal">
                          {item.label}
                        </th>
                        <td className="py-1 pr-2">
                          {item.category !== null
                            ? productivityLabel(item.category.classification)
                            : productivityLabel(null)}
                        </td>
                        <td className="py-1 pr-2 text-right tabular-nums">
                          {formatDuration(item.seconds)}
                        </td>
                        <td className="py-1 text-right tabular-nums">
                          {/* Sem denominador não existe percentual: "–", nunca 0%. */}
                          {(totalSeconds ?? 0) > 0
                            ? `${Math.round((item.seconds / (totalSeconds ?? 1)) * 100)}%`
                            : "–"}
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          )}

          <p className="mt-3 border-t pt-2.5 text-xs tabular-nums text-muted-foreground">
            {porApp
              ? "Tempo ativo total do período (todos os aplicativos): "
              : "Tempo de navegação total do período (todos os sites): "}
            <span className="font-display font-semibold text-foreground">
              {formatDuration(totalSeconds ?? 0)}
            </span>
            {!porApp && (
              <span className="mt-1 block text-[11px] leading-snug">
                Já contido no tempo dos aplicativos, sob o navegador — as duas listas não se somam.
              </span>
            )}
          </p>
        </>
      )}
    </BlockCard>
  );
}
