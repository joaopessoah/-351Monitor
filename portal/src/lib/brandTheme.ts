// =============================================================================
// Fonte ÚNICA das cores e fontes da identidade +351 Monitor no portal - os
// mesmos valores de site/assets/css/home.css (identidade aprovada) e dos
// tokens HSL de index.css. Canvas e ECharts não leem CSS vars: importam daqui.
// O verde #B6FF3C é AÇÃO/destaque de interface; a paleta de dataviz usa o
// verde de atividade #A4E84D (mais assentado para grandes áreas de dado).
// =============================================================================

export const BRAND = {
  bg: "#05070D",
  bg2: "#070B13",
  panel: "#0A101B",
  panel2: "#0A111D",
  line: "#131D2E",
  line2: "#1C2940",
  green: "#B6FF3C",
  greenInk: "#081109",
  greenSoft: "#D8FFA3",
  ink: "#F2F6FC",
  ink2: "#A9B4C8",
  ink3: "#66738C",
  red: "#FF8B8B",
  /** Paleta de dataviz (legenda do site: Produtivo/Neutro/Improdutivo/Ocioso). */
  vizProdutivo: "#A4E84D",
  vizNeutro: "#6FA9DD",
  vizImprodutivo: "#F2B45A",
  vizOcioso: "#3A455C",
  /** Cinza-azulado para "sem categoria"/"offline" - legível sobre o grafite. */
  slate: "#5B6982",
  /** Linhas de grade de gráficos (mais discretas que --line). */
  chartGrid: "#15202F",
  /** Texto de eixos e rótulos de gráfico. */
  chartText: "#63718C",
  fontDisplay: '"Space Grotesk", "Segoe UI", sans-serif',
  fontBody: '"Open Sans", "Segoe UI", system-ui, sans-serif',
} as const;

/**
 * Tema ECharts da marca, registrado uma vez no wrapper EChart. As options dos
 * componentes continuam mandando (notMerge); o tema cobre os defaults: fundo
 * transparente, tipografia, eixos discretos e tooltip escuro.
 */
export const ECHARTS_BRAND_THEME = {
  color: [BRAND.vizProdutivo, BRAND.vizNeutro, BRAND.vizImprodutivo, BRAND.vizOcioso, BRAND.slate],
  backgroundColor: "transparent",
  textStyle: { fontFamily: BRAND.fontBody, color: BRAND.ink2 },
  categoryAxis: {
    axisLine: { lineStyle: { color: BRAND.line2 } },
    axisTick: { show: false },
    axisLabel: { color: BRAND.chartText },
    splitLine: { show: false },
  },
  valueAxis: {
    axisLine: { show: false },
    axisTick: { show: false },
    axisLabel: { color: BRAND.chartText },
    splitLine: { lineStyle: { color: BRAND.chartGrid, type: [3, 4] } },
  },
  tooltip: {
    backgroundColor: BRAND.panel2,
    borderColor: BRAND.line2,
    textStyle: { color: BRAND.ink, fontFamily: BRAND.fontBody },
  },
  legend: { textStyle: { color: BRAND.ink2 } },
} as const;
