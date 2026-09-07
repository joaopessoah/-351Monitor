// =============================================================================
// Peças compartilhadas pelos blocos da Visão Geral nova (F6): os seis baldes da
// composição (cor + rótulo + redundância não-cromática), o cartão de bloco com
// cabeçalho e ferramentas, o toggle "Ver dados", os estados de skeleton/erro e
// o selo de variação em PONTOS.
//
// Redundância NÃO-cromática (Seção 8.5) dos dois baldes que não são cor cheia:
//  - OCIOSO: hachura a 45° (o cinza escuro sozinho se confundia com o fundo, e
//    o âmbar que ele usava na timeline significa "Improdutivo" no site);
//  - BLOQUEADO: só contorno, porque é estado ESPERADO da máquina, não um dado
//    de trabalho - e ocioso/bloqueado NUNCA contam como improdutivo.
// =============================================================================

import type { CSSProperties, ReactNode } from "react";
import { AlertTriangle, ArrowDown, ArrowUp, ChartColumn, Table } from "lucide-react";
import { BRAND } from "@/lib/brandTheme";
import {
  CLASSIFICATION_FRAMING,
  SEM_CLASSIFICACAO_COLOR,
  productivityLabel,
} from "@/lib/classification";
import { deltaPoints } from "@/lib/period";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

/** Tooltip pedagógico do Ocioso (Seção 8.4) - texto VERBATIM da tela atual. */
export const IDLE_HINT =
  "Ocioso significa sem uso de teclado/mouse. Reuniões, chamadas e leitura podem aparecer como ociosidade.";

/** Enquadramento da classificação, repetido de lib/classification.ts por conveniência. */
export const FRAMING = CLASSIFICATION_FRAMING;

/** Cores dos seis baldes da composição das horas ligadas (paleta validada da F6). */
export const VIZ = {
  produtivo: BRAND.vizProdutivo,
  neutro: BRAND.vizNeutro,
  improdutivo: BRAND.vizImprodutivo,
  semClassificacao: SEM_CLASSIFICACAO_COLOR,
  ocioso: BRAND.vizOcioso,
  bloqueado: "transparent",
  contorno: BRAND.line2,
  axisText: BRAND.chartText,
  grid: BRAND.chartGrid,
  ink: BRAND.ink,
  ink2: BRAND.ink2,
} as const;

/** Rótulos dos seis baldes - os quatro de classificação vêm de classification.ts. */
export const BUCKET_LABELS = {
  produtivo: productivityLabel(1),
  neutro: productivityLabel(0),
  improdutivo: productivityLabel(-1),
  semClassificacao: productivityLabel(null),
  ocioso: "Ocioso",
  bloqueado: "Bloqueado",
} as const;

/** Hachura a 45° do balde Ocioso, em CSS (swatches de legenda e de tabela). */
export const HATCH_STYLE: CSSProperties = {
  backgroundImage: `repeating-linear-gradient(45deg, ${BRAND.vizOcioso} 0px, ${BRAND.vizOcioso} 3px, ${BRAND.line2} 3px, ${BRAND.line2} 6px)`,
};

/** Hachura equivalente para ECharts (decal do AriaComponent, já registrado). */
export const HATCH_DECAL = {
  color: BRAND.line2,
  dashArrayX: [1, 0],
  dashArrayY: [3, 3],
  rotation: -Math.PI / 4,
} as const;

/** Hachura diagonal vermelha do "sem comunicação" (mesma da tela atual). */
export const NO_DATA_HATCH: CSSProperties = {
  backgroundImage:
    "repeating-linear-gradient(45deg, #dc2626 0px, #dc2626 2px, #fecaca 2px, #fecaca 4px)",
};

/** Altura padrão dos gráficos de bloco - skeleton/vazio/erro com a mesma geometria. */
export const CHART_H = 216;

// -----------------------------------------------------------------------------
// Cartão de bloco
// -----------------------------------------------------------------------------

/**
 * Cartão de um bloco da tela: título, subtítulo e ferramentas à direita. Usa
 * `div` cru com padding próprio em vez de CardContent porque o `cn()` do repo
 * não tem tailwind-merge e o p-6 default venceria o padding custom (mesma
 * armadilha documentada no WeeklyChartsRow).
 */
export function BlockCard({
  title,
  hint,
  tools,
  children,
  className,
}: {
  title: string;
  hint?: ReactNode;
  tools?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <Card className={cn("flex min-w-0 flex-col p-4", className)}>
      <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 space-y-0.5">
          <h2 className="font-display text-sm font-semibold leading-tight">{title}</h2>
          {hint !== undefined && (
            <p className="text-xs leading-snug text-muted-foreground">{hint}</p>
          )}
        </div>
        {tools !== undefined && <div className="flex shrink-0 items-center gap-2">{tools}</div>}
      </div>
      <div className="min-w-0 flex-1">{children}</div>
    </Card>
  );
}

/** Toggle "Ver dados" / "Ver gráfico" - mesmo padrão do WeeklyChartsRow. */
export function ViewToggle({
  view,
  onChange,
}: {
  view: "chart" | "table";
  onChange: (view: "chart" | "table") => void;
}) {
  return (
    <Button
      variant="outline"
      size="sm"
      className="h-7 px-2 text-xs"
      onClick={() => onChange(view === "chart" ? "table" : "chart")}
    >
      {view === "chart" ? (
        <>
          <Table className="h-3.5 w-3.5" aria-hidden />
          Ver dados
        </>
      ) : (
        <>
          <ChartColumn className="h-3.5 w-3.5" aria-hidden />
          Ver gráfico
        </>
      )}
    </Button>
  );
}

/** Skeleton com a geometria final do gráfico (nunca spinner de página inteira). */
export function ChartSkeleton({ height = CHART_H }: { height?: number }) {
  return <Skeleton className="w-full" style={{ height }} />;
}

/** Erro sem nenhum dado em cache: estado inline do bloco, com retry. */
export function InlineError({
  message,
  onRetry,
  height = CHART_H,
}: {
  message: string;
  onRetry: () => void;
  height?: number;
}) {
  return (
    <div
      className="flex flex-col items-center justify-center gap-3 text-center"
      style={{ minHeight: height }}
    >
      <AlertTriangle className="h-7 w-7 text-destructive" aria-hidden />
      <p className="text-sm text-muted-foreground">{message}</p>
      <Button variant="outline" size="sm" onClick={onRetry}>
        Tentar novamente
      </Button>
    </div>
  );
}

/** Falha de refetch COM dado em cache: aviso inline, desenho anterior preservado. */
export function RefetchAlert({ onRetry }: { onRetry: () => void }) {
  return (
    <div
      role="alert"
      className="mb-3 flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
    >
      <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
      <Button variant="outline" size="sm" className="h-7 px-2 text-xs" onClick={onRetry}>
        Tentar novamente
      </Button>
    </div>
  );
}

/** Texto central de gráfico vazio, com os eixos desenhados (Seção 8.9). */
export const EMPTY_GRAPHIC = [
  {
    type: "text" as const,
    left: "center" as const,
    top: "middle" as const,
    silent: true,
    style: {
      text: "Sem dados no período",
      fill: BRAND.chartText,
      font: "13px system-ui, -apple-system, sans-serif",
    },
  },
];

// -----------------------------------------------------------------------------
// Legenda e variação
// -----------------------------------------------------------------------------

/** Um item de legenda: swatch (cor cheia, hachura ou contorno) + rótulo. */
export function LegendSwatch({
  color,
  hatch = false,
  outline = false,
  className,
}: {
  color?: string;
  hatch?: boolean;
  outline?: boolean;
  className?: string;
}) {
  const style: CSSProperties = hatch
    ? HATCH_STYLE
    : outline
      ? { backgroundColor: "transparent", border: `1.5px solid ${VIZ.contorno}` }
      : { backgroundColor: color };
  return (
    <span
      aria-hidden
      className={cn("h-2.5 w-2.5 shrink-0 rounded-sm", className)}
      style={style}
    />
  );
}

export function LegendRow({ children }: { children: ReactNode }) {
  return (
    <ul className="m-0 flex list-none flex-wrap gap-x-3.5 gap-y-1 p-0 text-xs text-muted-foreground">
      {children}
    </ul>
  );
}

export function LegendItem({
  label,
  color,
  hatch = false,
  outline = false,
}: {
  label: string;
  color?: string;
  hatch?: boolean;
  outline?: boolean;
}) {
  return (
    <li className="inline-flex items-center gap-1.5">
      <LegendSwatch color={color} hatch={hatch} outline={outline} />
      <span>{label}</span>
    </li>
  );
}

/**
 * Variação em PONTOS de um indicador que já é percentual (índice, cobertura).
 * Cor sempre NEUTRA: o portal indica a variação, não emite juízo de valor -
 * queda de ociosidade é boa e queda de horas ativas pode ser ruim. `null` em
 * um dos lados imprime "sem base", jamais "+100%".
 */
export function PointsBadge({
  current,
  previous,
  suffix = "pts",
  title,
  className,
}: {
  current: number | null;
  previous: number | null | undefined;
  suffix?: string;
  title?: string;
  className?: string;
}) {
  const pts = deltaPoints(current, previous);
  const base = title ?? "Comparação com o período imediatamente anterior, de mesma duração.";
  const cls = cn(
    "inline-flex items-center gap-0.5 text-xs tabular-nums text-muted-foreground",
    className,
  );

  if (pts === null) {
    return (
      <span
        role="img"
        aria-label="Sem base no período anterior para comparar"
        title={`${base} Sem dados no período anterior para comparar.`}
        className={cls}
      >
        <span aria-hidden>sem base</span>
      </span>
    );
  }
  if (pts === 0) {
    return (
      <span
        role="img"
        aria-label="Sem variação em relação ao período anterior"
        title={base}
        className={cls}
      >
        <span aria-hidden>{`0 ${suffix}`}</span>
      </span>
    );
  }
  const up = pts > 0;
  return (
    <span
      role="img"
      aria-label={`${Math.abs(pts)} ${suffix} ${up ? "acima" : "abaixo"} do período anterior`}
      title={base}
      className={cls}
    >
      {up ? (
        <ArrowUp className="h-3 w-3 shrink-0" aria-hidden />
      ) : (
        <ArrowDown className="h-3 w-3 shrink-0" aria-hidden />
      )}
      <span aria-hidden>{`${Math.abs(pts)} ${suffix}`}</span>
    </span>
  );
}

/** Horas com uma casa quando o número é pequeno: "8,1 h", "908 h". */
export function hoursLabel(seconds: number): string {
  const hours = seconds / 3600;
  if (hours > 0 && hours < 10) return `${hours.toLocaleString("pt-BR", { maximumFractionDigits: 1 })} h`;
  return `${Math.round(hours).toLocaleString("pt-BR")} h`;
}

/** Escapa HTML para tooltips do ECharts (nomes de app e de equipe vêm de dados). */
export function escapeHtml(value: string): string {
  return value
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}
