// =============================================================================
// Comparativos com o período anterior: dado um range from/to, o período de
// comparação é o range imediatamente ANTERIOR de mesma duração (semana vs
// semana anterior, 30 dias vs os 30 dias antes deles). O DeltaBadge indica a
// direção com seta e o percentual com 0 casas, SEMPRE em cor neutra (muted):
// queda de ociosidade é boa e queda de horas ativas pode ser ruim - o portal
// não emite juízo de valor, apenas indica a variação. Vocabulário fixo:
// "vs período anterior", "12% menor que no período anterior".
// =============================================================================

import { useMemo } from "react";
import { ArrowDown, ArrowUp } from "lucide-react";
import { addDays, ddmm } from "@/lib/format";
import { cn } from "@/lib/utils";

export interface ComparisonRange {
  from: string;
  to: string;
}

/** Epoch UTC (ms) de uma data local yyyy-MM-dd - aritmética imune a DST. */
function epochUtc(dateStr: string): number {
  const [y, m, d] = dateStr.split("-").map(Number);
  return Date.UTC(y, m - 1, d);
}

/** Range imediatamente anterior com a MESMA duração (função pura). */
export function comparisonRangeOf(range: ComparisonRange): ComparisonRange {
  const days = Math.round((epochUtc(range.to) - epochUtc(range.from)) / 86_400_000) + 1;
  const prevTo = addDays(range.from, -1);
  return { from: addDays(prevTo, -(days - 1)), to: prevTo };
}

/** Versão memoizada por from/to - null enquanto o range corrente não resolveu. */
export function useComparisonRange(range: ComparisonRange | null): ComparisonRange | null {
  return useMemo(
    () => (range !== null ? comparisonRangeOf(range) : null),
    // Deps pelos campos: o objeto range costuma ser recriado a cada render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [range?.from, range?.to],
  );
}

export interface DeltaBadgeProps {
  /** Valor do período corrente. */
  current: number;
  /** Valor do período anterior - null quando a base ainda não carregou. */
  previous: number | null;
  /** Range anterior, para o title explicar a base de comparação. */
  previousRange?: ComparisonRange | null;
  /** Propaga o aviso de dados incompletos no tooltip (title). */
  incomplete?: boolean;
  /** Inclui o rótulo "vs período anterior" no texto visível (default true). */
  showLabel?: boolean;
  className?: string;
}

/**
 * Seta de direção + percentual (0 casas) da variação vs o período anterior.
 * Cor NEUTRA nos dois sentidos (nunca verde/vermelho por direção); texto
 * acessível completo via role="img" + aria-label (idioma dos ícones lucide
 * com aria-label já usado no portal); tooltip nativo (title) explica a base.
 */
export function DeltaBadge({
  current,
  previous,
  previousRange = null,
  incomplete = false,
  showLabel = true,
  className,
}: DeltaBadgeProps) {
  const baseTitle =
    previousRange !== null
      ? `Comparação com o período imediatamente anterior, de ${ddmm(previousRange.from)} a ${ddmm(previousRange.to)}.`
      : "Comparação com o período imediatamente anterior, de mesma duração.";
  const title = incomplete ? `${baseTitle} Há dados incompletos em um dos períodos.` : baseTitle;
  const badgeClass = cn(
    "inline-flex items-center gap-0.5 text-xs tabular-nums text-muted-foreground",
    className,
  );

  // Sem base: período anterior ainda não carregou ou não tem o item/valor.
  if (previous === null || previous <= 0) {
    return (
      <span
        role="img"
        aria-label="Sem dados no período anterior para comparar"
        title={`${title} Sem dados no período anterior para comparar.`}
        className={badgeClass}
      >
        <span aria-hidden>sem base</span>
      </span>
    );
  }

  const pct = Math.round(((current - previous) / previous) * 100);
  const label = showLabel ? " vs período anterior" : "";

  if (pct === 0) {
    return (
      <span
        role="img"
        aria-label="Sem variação em relação ao período anterior"
        title={title}
        className={badgeClass}
      >
        <span aria-hidden>{`0%${label}`}</span>
      </span>
    );
  }

  const up = pct > 0;
  const abs = Math.abs(pct);
  return (
    <span
      role="img"
      aria-label={`${abs}% ${up ? "maior" : "menor"} que no período anterior`}
      title={title}
      className={badgeClass}
    >
      {up ? (
        <ArrowUp className="h-3 w-3 shrink-0" aria-hidden />
      ) : (
        <ArrowDown className="h-3 w-3 shrink-0" aria-hidden />
      )}
      <span aria-hidden>{`${abs}%${label}`}</span>
    </span>
  );
}

/**
 * Aviso sutil quando o número de dispositivos difere entre os períodos
 * comparados - a comparação continua válida, mas a base mudou de tamanho.
 */
export function DeviceCountNotice({
  current,
  previous,
  className,
}: {
  current: number;
  previous: number;
  className?: string;
}) {
  if (current === previous) return null;
  return (
    <p className={cn("text-xs text-muted-foreground", className)}>
      Os períodos comparados têm quantidades diferentes de dispositivos ({current} agora,{" "}
      {previous} no anterior).
    </p>
  );
}
