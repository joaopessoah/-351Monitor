// =============================================================================
// Composição por dia da pessoa (F6): colunas empilhadas com os quatro baldes
// de classificação mais o Ocioso (SEM o Bloqueado - fora do pedido desta
// tela), linha tracejada da jornada declarada como referência, e toggle
// "Ver dados" com a tabela equivalente (mesma dupla gráfico/tabela do resto
// do portal - EChart com ariaHidden quando a tabela está visível).
//
// Ocioso é estado de MÁQUINA, nunca improdutivo (regra invíolavel): por isso
// vive fora de lib/classification.ts e ganha hachura a 45° (BRAND.vizOcioso +
// decal), a mesma redundância não-cromática da paleta de dataviz validada no
// spec (Seção 3) - só que reproduzida aqui em vez de importada de
// components/dashboard/ (fora do escopo desta tela; outro agente mexe lá).
// =============================================================================

import { useMemo, useState } from "react";
import type { EChartsOption } from "echarts";
import { ChartColumn, Table } from "lucide-react";
import { EChart } from "@/components/charts/EChart";
import { BRAND } from "@/lib/brandTheme";
import { productivityColor, productivityLabel } from "@/lib/classification";
import { ddmm, formatDuration } from "@/lib/format";
import type { DashboardSummaryDay } from "@/lib/types";
import { cn } from "@/lib/utils";

const CHART_HEIGHT = 260;

type SecondsKey =
  | "seconds_work_related"
  | "seconds_neutral"
  | "seconds_not_work_related"
  | "seconds_unclassified";

/** Os quatro baldes de classificação na ordem canônica da legenda (+1, 0, -1, sem classificação). */
const CLASSIFICATION_SERIES: { key: SecondsKey; classification: number | null }[] = [
  { key: "seconds_work_related", classification: 1 },
  { key: "seconds_neutral", classification: 0 },
  { key: "seconds_not_work_related", classification: -1 },
  { key: "seconds_unclassified", classification: null },
];

/** Segundos -> horas com 1 casa decimal, para a altura das barras. */
function toHours(seconds: number): number {
  return Math.round((seconds / 3600) * 10) / 10;
}

function buildOption(days: DashboardSummaryDay[], journeyHours: number): EChartsOption {
  const categories = days.map((d) => ddmm(d.date));

  const classificationSeries = CLASSIFICATION_SERIES.map(({ key, classification }) => ({
    name: productivityLabel(classification),
    type: "bar" as const,
    stack: "total",
    itemStyle: { color: productivityColor(classification) },
    data: days.map((d) => toHours(d[key])),
  }));

  const ociosoSeries = {
    name: "Ocioso",
    type: "bar" as const,
    stack: "total",
    itemStyle: {
      color: BRAND.vizOcioso,
      // Hachura a 45° (redundância não-cromática): ocioso nunca é só uma cor
      // cheia, para não se confundir com os outros baldes nem parecer "mais
      // um produtivo" no daltonismo.
      decal: { color: BRAND.line2, dashArrayX: [1, 0], dashArrayY: [3, 3], rotation: -Math.PI / 4 },
    },
    data: days.map((d) => toHours(d.seconds_idle)),
    // Linha tracejada da jornada declarada (business_hours do /me, fallback
    // 8h) - referência fixa, não empilhada; fica no topo da pilha (Ocioso)
    // só porque markLine.yAxis é uma posição absoluta no eixo, não relativa.
    markLine: {
      silent: true,
      symbol: ["none", "none"] as [string, string],
      lineStyle: { type: "dashed" as const, color: BRAND.ink2 },
      label: {
        formatter: `Jornada declarada: ${journeyHours} h`,
        color: BRAND.ink2,
        position: "insideEndTop" as const,
      },
      data: [{ yAxis: journeyHours }],
    },
  };

  const series = [...classificationSeries, ociosoSeries];

  return {
    grid: { left: 48, right: 16, top: 40, bottom: 32, containLabel: true },
    legend: { top: 0, textStyle: { color: BRAND.ink2 }, data: series.map((s) => s.name) },
    xAxis: { type: "category", data: categories },
    yAxis: { type: "value", axisLabel: { formatter: "{value} h" } },
    tooltip: {
      trigger: "axis",
      valueFormatter: (value: unknown) =>
        typeof value === "number" ? `${value.toFixed(1)} h` : String(value ?? ""),
    },
    series,
  };
}

export function PersonDailyComposition({
  days,
  journeyHours,
}: {
  days: DashboardSummaryDay[];
  journeyHours: number;
}) {
  const [showTable, setShowTable] = useState(false);
  const option = useMemo(() => buildOption(days, journeyHours), [days, journeyHours]);

  if (days.length === 0) {
    return (
      <p className="py-6 text-center text-sm text-muted-foreground">
        Nenhum dado coletado para este registro no período.
      </p>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex justify-end">
        <div
          role="group"
          aria-label="Modo de exibição da composição por dia"
          className="inline-flex h-8 items-stretch rounded-md border border-input bg-card p-0.5"
        >
          <button
            type="button"
            aria-pressed={!showTable}
            onClick={() => setShowTable(false)}
            className={cn(
              "flex items-center gap-1.5 rounded-[5px] px-2.5 text-xs font-medium transition-colors",
              !showTable
                ? "bg-primary/10 text-primary"
                : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
            )}
          >
            <ChartColumn className="h-3.5 w-3.5" aria-hidden />
            Gráfico
          </button>
          <button
            type="button"
            aria-pressed={showTable}
            onClick={() => setShowTable(true)}
            className={cn(
              "flex items-center gap-1.5 rounded-[5px] px-2.5 text-xs font-medium transition-colors",
              showTable
                ? "bg-primary/10 text-primary"
                : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
            )}
          >
            <Table className="h-3.5 w-3.5" aria-hidden />
            Ver dados
          </button>
        </div>
      </div>

      {/* O gráfico continua montado (o wrapper redesenha via ResizeObserver
          quando a largura volta de 0) - só o container fica oculto. */}
      <div className={showTable ? "hidden" : undefined}>
        <EChart option={option} height={CHART_HEIGHT} ariaHidden={showTable} />
      </div>

      {showTable && (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <caption className="sr-only">
              Composição por dia deste registro: os quatro baldes de classificação e o tempo
              ocioso, em horas e minutos.
            </caption>
            <thead>
              <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="py-2 pr-3">Dia</th>
                {CLASSIFICATION_SERIES.map(({ classification }) => (
                  <th key={productivityLabel(classification)} scope="col" className="px-3 py-2">
                    {productivityLabel(classification)}
                  </th>
                ))}
                <th scope="col" className="px-3 py-2">Ocioso</th>
              </tr>
            </thead>
            <tbody>
              {days.map((day) => (
                <tr key={day.date} className="border-b last:border-b-0">
                  <td className="whitespace-nowrap py-2 pr-3 font-medium">{ddmm(day.date)}</td>
                  {CLASSIFICATION_SERIES.map(({ key, classification }) => (
                    <td
                      key={productivityLabel(classification)}
                      className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground"
                    >
                      {formatDuration(day[key])}
                    </td>
                  ))}
                  <td className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground">
                    {formatDuration(day.seconds_idle)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
