// =============================================================================
// F6 - indicador compacto da cobertura da classificação: o MESMO badge no
// topo da AppsPage e (maior, com o texto do gap ao lado) no cabeçalho da Fila
// de classificação, para o número nunca divergir visualmente entre as duas
// telas. `coverage` já vem calculado (classificationCoverage.ts) - este
// componente só formata e desenha a barrinha, igual ao padrão de IndexCell em
// ColaboradoresPage.tsx (bg-secondary de fundo + preenchimento colorido).
// =============================================================================

import { Link } from "react-router-dom";
import { formatPct } from "@/lib/period";
import { cn } from "@/lib/utils";

export function CoverageBadge({
  coverage,
  linkTo,
}: {
  /** Fração 0..1 - null imprime "–" (sem tempo ativo no período, nunca 0%). */
  coverage: number | null;
  /** Quando informado, o indicador vira link (ex.: da AppsPage para a fila). */
  linkTo?: string;
}) {
  const content = (
    <span className="inline-flex items-center gap-2 rounded-full border border-input bg-card px-2.5 py-1 text-xs">
      <span aria-hidden className="h-1.5 w-10 shrink-0 overflow-hidden rounded-full bg-secondary">
        <span
          className="block h-full rounded-full bg-viz-produtivo"
          style={{ width: coverage !== null ? `${Math.round(coverage * 100)}%` : "0%" }}
        />
      </span>
      <span className="tabular-nums">Cobertura da classificação: {formatPct(coverage)}</span>
    </span>
  );

  if (linkTo === undefined) return content;

  return (
    <Link
      to={linkTo}
      title="Ver fila de classificação"
      className={cn(
        "rounded-full transition-opacity hover:opacity-80",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
      )}
    >
      {content}
    </Link>
  );
}
