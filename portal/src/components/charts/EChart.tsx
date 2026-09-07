// =============================================================================
// Wrapper fino do Apache ECharts (sem lib de terceiros): init/dispose no mount,
// setOption com notMerge (a option descreve o gráfico INTEIRO a cada render),
// resize via ResizeObserver (mesmo padrão do TimelineCanvas) e aria.enabled
// por padrão em todas as options.
// =============================================================================

import { useEffect, useRef } from "react";
import * as echarts from "echarts/core";
import { BarChart, LineChart, PieChart } from "echarts/charts";
import {
  AriaComponent,
  GraphicComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  TooltipComponent,
} from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
import { BRAND, ECHARTS_BRAND_THEME } from "@/lib/brandTheme";
// Imports SÓ de tipo do pacote completo - apagados na compilação, sem custo de bundle.
import type { ECElementEvent, EChartsOption, EChartsType } from "echarts";

// Registro modular (tree-shaking): apenas o que o portal usa. Novos tipos de
// gráfico exigem registrar o chart/component correspondente aqui.
//
// CAUSA RAIZ do cartão "Atividade ao longo do dia" nascer em branco (07/09/2026):
// o `LineChart` NÃO estava nesta lista, e aquele cartão é o único da Visão Geral
// cujas DUAS séries são `type: "line"`. O ECharts não lança erro nesse caso: ele
// DESCARTA a série, escreve "[ECharts] Series line is used but not imported." no
// console e desenha o que sobrou. Sem nenhuma série, sobrou só o eixo X - o eixo
// Y é de valor e, sem série, não tem extensão para escalar, então nem os rótulos
// nem as linhas de grade saem. E o texto de vazio também não aparecia, porque do
// ponto de vista do cartão HAVIA dado: o `vazio` dele era falso. Resultado: um
// retângulo em branco, sem erro na tela e sem estado vazio. Medido com a mesma
// option fora do navegador: série `line` renderizou 1 path, a mesma em `bar`
// renderizou 30.
//
// O `LegendComponent` faltava pelo mesmo motivo e caía do mesmo jeito (a legenda
// da composição por dia da pessoa simplesmente não era desenhada), e o
// `LineChart` ausente também comia a linha fantasma da semana anterior no
// WeeklyChartsRow - ali as barras seguravam o gráfico e ninguém notou.
//
// O guard mais abaixo (`aplicarGuardDeSerie`) existe para que a CLASSE inteira
// desse problema pare de ser muda: quem esquecer um registro daqui vai ver
// eixos + texto honesto no cartão e um console.error nomeando o import que
// falta, nunca mais um retângulo em branco.
echarts.use([
  BarChart,
  LineChart,
  PieChart,
  AriaComponent,
  GraphicComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  TooltipComponent,
  CanvasRenderer,
]);

/** Nome do import de cada tipo de série do ECharts (o guard sugere o que falta). */
const IMPORT_DA_SERIE: Readonly<Record<string, string>> = {
  bar: "BarChart",
  line: "LineChart",
  pie: "PieChart",
  scatter: "ScatterChart",
  effectScatter: "EffectScatterChart",
  radar: "RadarChart",
  tree: "TreeChart",
  treemap: "TreemapChart",
  sunburst: "SunburstChart",
  boxplot: "BoxplotChart",
  candlestick: "CandlestickChart",
  heatmap: "HeatmapChart",
  map: "MapChart",
  parallel: "ParallelChart",
  lines: "LinesChart",
  graph: "GraphChart",
  sankey: "SankeyChart",
  funnel: "FunnelChart",
  gauge: "GaugeChart",
  pictorialBar: "PictorialBarChart",
  themeRiver: "ThemeRiverChart",
  custom: "CustomChart",
};

/** Altura/estilo do texto de vazio - mesmo desenho do EMPTY_GRAPHIC do kit. */
const TEXTO_VAZIO = {
  type: "text" as const,
  left: "center" as const,
  top: "middle" as const,
  silent: true,
  style: {
    fill: BRAND.chartText,
    font: "13px system-ui, -apple-system, sans-serif",
  },
};

type SerieBruta = { type?: unknown; data?: unknown };
type EixoBruto = { type?: unknown; min?: unknown; max?: unknown; data?: unknown };

function comoArray<T>(valor: T | readonly T[] | undefined | null): T[] {
  if (valor === undefined || valor === null) return [];
  return Array.isArray(valor) ? [...(valor as readonly T[])] : [valor as T];
}

/**
 * Eixo de VALOR não desenha nada sem série que lhe dê extensão. Este patch
 * completa a ponta que faltar, e só em eixo de valor: em eixo de categoria
 * `min`/`max` são ÍNDICE, e mexer ali esconderia categorias.
 *
 * Completa ponta A ponta de propósito: metade dos cartões declara só o `max`
 * (para a markLine da jornada caber), e um eixo com `max` e sem `min` continua
 * sumindo quando não há série. `padrao` respeita o default do ECharts, que é
 * `category` no xAxis e `value` no yAxis.
 */
function patchDeEscala(
  eixos: EixoBruto[],
  padrao: "value" | "category",
): Array<Record<string, unknown>> {
  return eixos.map((eixo) => {
    const tipo = typeof eixo.type === "string" ? eixo.type : padrao;
    if (tipo !== "value" || eixo.data !== undefined) return {};
    const patch: Record<string, unknown> = {};
    if (eixo.min === undefined) patch.min = 0;
    if (eixo.max === undefined) patch.max = 1;
    return patch;
  });
}

/**
 * GUARD DA CLASSE DO PROBLEMA. Roda depois de todo `setOption` e compara o que
 * a option PEDIU com o que o ECharts realmente instanciou:
 *
 *  - série pedida que sumiu do modelo = tipo não registrado no `use()` acima.
 *    Grita no console nomeando o import e desenha "Não foi possível desenhar o
 *    gráfico" - mentir "sem dados" aqui seria pior, porque dado existe.
 *  - nenhuma série com dado (o cartão mandou `series: []`, ou todas vieram
 *    vazias) = estado vazio legítimo. Respeita o `graphic` que o cartão já
 *    tenha passado e só garante a escala dos eixos.
 *
 * Nos dois caminhos o resultado é o mesmo contrato visual: EIXOS DESENHADOS +
 * TEXTO, nunca um retângulo em branco.
 */
function aplicarGuardDeSerie(chart: EChartsType, option: EChartsOption): void {
  const pedidas = comoArray<SerieBruta>(option.series as SerieBruta | SerieBruta[] | undefined);
  const vivas = comoArray<SerieBruta>(
    (chart.getOption() as { series?: SerieBruta | SerieBruta[] }).series,
  );

  // `data` ausente não é sinal de vazio (série por dataset/encode): só conta
  // como vazia a que declara um array e o array está vazio.
  const comDado = vivas.filter(
    (s) => !(Array.isArray(s.data) && (s.data as unknown[]).length === 0),
  ).length;

  const descartadas = pedidas.length - vivas.length;
  if (descartadas <= 0 && comDado > 0) return;

  if (descartadas > 0) {
    // Diferença de MULTICONJUNTO entre os tipos pedidos e os que sobreviveram.
    // Deliberadamente não há lista de tipos registrados mantida à mão aqui: a
    // lista é que envelheceria em silêncio junto com o `echarts.use()` acima,
    // e era justamente esse tipo de desencontro que produziu o defeito.
    const tipoDe = (s: SerieBruta): string => (typeof s.type === "string" ? s.type : "?");
    const restantes = new Map<string, number>();
    for (const s of vivas) restantes.set(tipoDe(s), (restantes.get(tipoDe(s)) ?? 0) + 1);
    const faltando = new Set<string>();
    for (const s of pedidas) {
      const t = tipoDe(s);
      const n = restantes.get(t) ?? 0;
      if (n > 0) restantes.set(t, n - 1);
      else faltando.add(t);
    }
    const tipos = [...faltando];
    const imports = tipos
      .map((t) => IMPORT_DA_SERIE[t] ?? `${t.charAt(0).toUpperCase()}${t.slice(1)}Chart`)
      .join(", ");
    console.error(
      `[EChart] O ECharts descartou ${descartadas} série(s) em silêncio: ` +
        `tipo(s) ${tipos.join(", ") || "desconhecido(s)"} não registrado(s). ` +
        `Adicione ${imports || "o chart correspondente"} ao echarts.use() em ` +
        `components/charts/EChart.tsx.`,
    );
  }

  // Só mexe em eixo quando a option tem eixo (donut/pizza não tem nenhum).
  const patch: Record<string, unknown> = {};
  const temEixos = option.xAxis !== undefined || option.yAxis !== undefined;
  if (temEixos) {
    patch.xAxis = patchDeEscala(
      comoArray<EixoBruto>(option.xAxis as EixoBruto | EixoBruto[] | undefined),
      "category",
    );
    patch.yAxis = patchDeEscala(
      comoArray<EixoBruto>(option.yAxis as EixoBruto | EixoBruto[] | undefined),
      "value",
    );
  }
  // O cartão que já traz o próprio texto de vazio manda nele; o guard só cobre
  // quem não previu o caso - inclusive o cartão que pensava ter dado.
  if (option.graphic === undefined) {
    patch.graphic = [
      {
        ...TEXTO_VAZIO,
        style: {
          ...TEXTO_VAZIO.style,
          text: descartadas > 0 ? "Não foi possível desenhar o gráfico" : "Sem dados no período",
        },
      },
    ];
  }
  if (Object.keys(patch).length > 0) chart.setOption(patch);
}

// Tema da marca (dark + tipografia + eixos discretos + tooltip escuro): os
// defaults de TODO gráfico do portal; as options continuam podendo sobrescrever.
echarts.registerTheme("m351", ECHARTS_BRAND_THEME);

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
    const chart = echarts.init(el, "m351");
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
    const chart = chartRef.current;
    if (chart === null) return;
    try {
      chart.setOption({ aria: { enabled: true }, ...option }, { notMerge: true });
    } catch (erro) {
      // setOption que LANÇA deixaria o cartão em branco do mesmo jeito, e o
      // erro morreria dentro do effect. Registra e segue para o guard, que
      // garante os eixos e o texto.
      console.error("[EChart] setOption falhou; o gráfico ficaria em branco.", erro, option);
    }
    aplicarGuardDeSerie(chart, option);
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
