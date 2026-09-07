// =============================================================================
// "Aplicativos" — top 8 do PERÍODO GLOBAL na Visão Geral (F6, linha 3 do
// mockup aprovado).
//
// POR QUE ELE SUBSTITUI O CARD ANTIGO: o "Top 10 apps da semana" do
// WeeklyChartsRow tinha janela PRÓPRIA (semana atual / semana anterior, com
// seletor só dele). Numa tela cujo período é global e vive na URL, um bloco com
// recorte particular é uma mentira de leitura: o gestor escolhe "Este mês" no
// cabeçalho e o card continua respondendo pela semana. Aqui a janela é a mesma
// dos outros blocos — o link reproduz o recorte visível inteiro.
//
// POR QUE 8 E NÃO 10: é o número do mockup aprovado, e é o que caberia sem
// truncar nomes na coluna span4 da linha 3.
//
// NÃO É RANKING DE PESSOAS: a lista é de APLICATIVOS. O total do rodapé é o
// tempo ativo de TODOS os apps do período (o denominador honesto), não a soma
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
import type { TopAppItem, TopAppsResponse } from "@/lib/types";
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

/** Quantidade de apps do bloco — o número do mockup aprovado. */
export const TOP_APPS_LIMIT = 8;

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

/** Nome que o usuário vê: apelido do tenant vence o nome do catálogo. */
function appLabel(item: TopAppItem): string {
  return item.custom_display_name ?? item.display_name;
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
  const query = useTopAppsQuery(period, tag);
  const data = query.data;

  const items = useMemo(() => data?.items ?? [], [data]);
  const vazio = data !== undefined && items.length === 0;

  const option = useMemo<EChartsOption>(() => {
    const maxH = items.reduce((mx, i) => Math.max(mx, i.seconds_active / 3600), 0);
    const totalAtivo = data?.total_seconds_active ?? 0;

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
          // % do tempo ativo TOTAL do período (todos os apps), não do top 8:
          // com o denominador do top, oito apps somariam sempre 100%.
          const share =
            totalAtivo > 0 ? `${Math.round((item.seconds_active / totalAtivo) * 100)}%` : "–";
          const classificacao =
            item.category !== null
              ? `${productivityLabel(item.category.classification)} · ${escapeHtml(item.category.name)}`
              : productivityLabel(null);
          return [
            `<strong>${escapeHtml(appLabel(item))}</strong>`,
            classificacao,
            `Tempo ativo: ${formatDuration(item.seconds_active)}`,
            `Do tempo ativo do período: ${share}`,
            item.device_count === 1 ? "1 dispositivo" : `${item.device_count} dispositivos`,
          ].join("<br/>");
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
        data: items.map((i) => appLabel(i)),
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
                value: Math.round((i.seconds_active / 3600) * 100) / 100,
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
                  return item !== undefined ? formatDuration(item.seconds_active) : "";
                },
              },
            },
          ],
      graphic: vazio ? EMPTY_GRAPHIC : undefined,
    };
  }, [items, vazio, data]);

  return (
    <BlockCard
      className={className}
      title="Aplicativos"
      hint={`Top ${TOP_APPS_LIMIT} por tempo ativo no período · cor pela classificação`}
      tools={
        <>
          <ViewToggle view={view} onChange={setView} />
          {period !== null && (
            <Link
              to={`/apps?from=${encodeURIComponent(period.from)}&to=${encodeURIComponent(period.to)}`}
              className="inline-flex h-7 items-center gap-1 rounded-md border border-input px-2 text-xs font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              Ver todos
              <ArrowRight className="h-3.5 w-3.5" aria-hidden />
            </Link>
          )}
        </>
      }
    >
      {query.isError && data !== undefined && <RefetchAlert onRetry={() => void query.refetch()} />}

      {query.isPending && data === undefined ? (
        <ChartSkeleton />
      ) : data === undefined ? (
        <InlineError
          message="Não foi possível carregar os aplicativos do período."
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
                aria-label={`Top ${TOP_APPS_LIMIT} aplicativos do período, em tabela`}
              >
                <thead className="sticky top-0 bg-card">
                  <tr className="border-b text-left text-[10px] uppercase tracking-wide text-muted-foreground">
                    <th scope="col" className="py-1.5 pr-2">App</th>
                    <th scope="col" className="py-1.5 pr-2">Classificação</th>
                    <th scope="col" className="py-1.5 pr-2 text-right">Tempo ativo</th>
                    <th scope="col" className="py-1.5 text-right">Do período</th>
                  </tr>
                </thead>
                <tbody>
                  {items.length === 0 ? (
                    <tr>
                      <td colSpan={4} className="py-3 text-center text-muted-foreground">
                        Sem dados no período
                      </td>
                    </tr>
                  ) : (
                    items.map((item) => (
                      <tr key={item.app_id} className="border-b last:border-b-0">
                        <th scope="row" className="py-1 pr-2 text-left font-normal">
                          {appLabel(item)}
                        </th>
                        <td className="py-1 pr-2">
                          {item.category !== null
                            ? productivityLabel(item.category.classification)
                            : productivityLabel(null)}
                        </td>
                        <td className="py-1 pr-2 text-right tabular-nums">
                          {formatDuration(item.seconds_active)}
                        </td>
                        <td className="py-1 text-right tabular-nums">
                          {/* Sem denominador não existe percentual: "–", nunca 0%. */}
                          {data.total_seconds_active > 0
                            ? `${Math.round((item.seconds_active / data.total_seconds_active) * 100)}%`
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
            Tempo ativo total do período (todos os aplicativos):{" "}
            <span className="font-display font-semibold text-foreground">
              {formatDuration(data.total_seconds_active)}
            </span>
          </p>
        </>
      )}
    </BlockCard>
  );
}
