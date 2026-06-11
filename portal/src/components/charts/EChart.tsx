// =============================================================================
// Wrapper fino do Apache ECharts (sem lib de terceiros): init/dispose no mount,
// setOption com notMerge (a option descreve o gráfico INTEIRO a cada render),
// resize via ResizeObserver (mesmo padrão do TimelineCanvas) e aria.enabled
// por padrão em todas as options.
// =============================================================================

import { useEffect, useRef } from "react";
import * as echarts from "echarts/core";
import { BarChart } from "echarts/charts";
import {
  AriaComponent,
  GraphicComponent,
  GridComponent,
  MarkLineComponent,
  TooltipComponent,
} from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
// Imports SÓ de tipo do pacote completo - apagados na compilação, sem custo de bundle.
import type { ECElementEvent, EChartsOption, EChartsType } from "echarts";

// Registro modular (tree-shaking): apenas o que o portal usa. Novos tipos de
// gráfico exigem registrar o chart/component correspondente aqui.
echarts.use([
  BarChart,
  AriaComponent,
  GraphicComponent,
  GridComponent,
  MarkLineComponent,
  TooltipComponent,
  CanvasRenderer,
]);

export interface EChartProps {
  option: EChartsOption;
  /** Altura fixa em px - geometria estável para skeleton/vazio/erro. */
  height: number;
  className?: string;
  /** true quando a tabela "Ver dados" está visível - o gráfico vira decorativo. */
  ariaHidden?: boolean;
  /** Clique em um elemento do gráfico (ex.: barra de série). */
  onItemClick?: (params: ECElementEvent) => void;
}

export function EChart({ option, height, className, ariaHidden = false, onItemClick }: EChartProps) {
  const elRef = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<EChartsType | null>(null);
  // Callback em ref: o listener de clique é registrado UMA única vez no init.
  const clickRef = useRef<EChartProps["onItemClick"]>(onItemClick);
  clickRef.current = onItemClick;

  // init/dispose + redesenho em resize (ResizeObserver, padrão do TimelineCanvas).
  useEffect(() => {
    const el = elRef.current;
    if (el === null) return;
    const chart = echarts.init(el);
    chartRef.current = chart;
    chart.on("click", (params) => clickRef.current?.(params as ECElementEvent));
    const ro = new ResizeObserver((entries) => {
      const entry = entries[0];
      // Largura 0 = container oculto (toggle de tabela) - redesenha ao voltar.
      if (entry !== undefined && entry.contentRect.width > 0) chart.resize();
    });
    ro.observe(el);
    return () => {
      ro.disconnect();
      chartRef.current = null;
      chart.dispose();
    };
  }, []);

  // notMerge: cada option substitui a anterior por completo (sem resíduos de
  // séries/markLines de renders passados). aria.enabled é o default do portal;
  // uma option com `aria` próprio ainda pode sobrescrever.
  useEffect(() => {
    chartRef.current?.setOption({ aria: { enabled: true }, ...option }, { notMerge: true });
  }, [option]);

  return (
    <div
      ref={elRef}
      className={className}
      style={{ height }}
      aria-hidden={ariaHidden || undefined}
    />
  );
}
