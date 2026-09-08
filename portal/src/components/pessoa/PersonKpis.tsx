// =============================================================================
// KPIs da pessoa no período (F6): horas com a máquina ligada, horas ativas,
// índice de produtividade, ociosidade e sem classificação. Componente burro -
// só formata o que a página já buscou em GET /dashboard/summary.
// =============================================================================

import { CLASSIFICATION_FRAMING } from "@/lib/classification";
import { formatHours, formatPct } from "@/lib/period";
import type { DashboardSummaryTotals } from "@/lib/types";
import { IDLE_DISCLAIMER } from "./pessoaMetrics";

interface Tile {
  label: string;
  value: string;
  caption?: string;
}

/**
 * Índice e cobertura chegam PRONTOS do servidor, dentro do MESMO `totals` — são
 * `GET /dashboard/summary`, calculados sobre exatamente estes baldes.
 *
 * POR QUE DO SUMMARY E NÃO DO SELF-VIEW: esta página navega por device_user_id e
 * os ladrilhos ao lado ("Horas ativas", "Sem classificação") são de UMA lane. O
 * self-view agrega a PESSOA inteira, todos os dispositivos. Misturar os dois
 * colocaria "Sem classificação 95%" ao lado de "Cobertura 76%" — a mesma
 * grandeza invertida — se contradizendo na mesma linha. Um escopo por linha.
 *
 * A conta que esta tela refazia no cliente foi apagada: uma fórmula só, no
 * servidor. `null` imprime "–", nunca 0%.
 */
export function PersonKpis({ totals }: { totals: DashboardSummaryTotals }) {
  const productivityIndex = totals.productivity_index;
  const classificationCoverage = totals.classification_coverage;

  // Ociosidade e "sem classificação" são proporções simples (sem fórmula do
  // servidor para duplicar) - mesma regra do null: sem base, imprime "–".
  const idlePct = totals.seconds_on > 0 ? totals.seconds_idle / totals.seconds_on : null;
  const unclassifiedPct =
    totals.seconds_active > 0 ? totals.seconds_unclassified / totals.seconds_active : null;

  const tiles: Tile[] = [
    { label: "Horas com a máquina ligada", value: `${formatHours(totals.seconds_on)} h` },
    { label: "Horas ativas", value: `${formatHours(totals.seconds_active)} h` },
    {
      label: "Índice de produtividade",
      value: formatPct(productivityIndex ?? null),
      // Decisão 4 do spec: a cobertura nunca aparece sem o índice, nem o
      // índice sem a cobertura.
      caption: `Cobertura da classificação: ${formatPct(classificationCoverage ?? null)}`,
    },
    { label: "Ociosidade", value: formatPct(idlePct) },
    { label: "Sem classificação", value: formatPct(unclassifiedPct) },
  ];

  return (
    <div className="space-y-3">
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
        {tiles.map((tile) => (
          <div key={tile.label} className="rounded-md border p-4">
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
              {tile.label}
            </p>
            <p className="mt-1 text-2xl font-semibold tabular-nums">{tile.value}</p>
            {tile.caption !== undefined && (
              <p className="mt-1 text-xs text-muted-foreground">{tile.caption}</p>
            )}
          </div>
        ))}
      </div>
      {/* Enquadramento (decisão 1) e aviso do ocioso (regra invíolavel) - uma
          única vez na tela, aqui onde os quatro baldes e a ociosidade
          aparecem pela primeira vez em números. */}
      <p className="text-xs text-muted-foreground">
        Produtivo, Neutro, Improdutivo e Sem classificação: {CLASSIFICATION_FRAMING}.
      </p>
      <p className="text-xs text-muted-foreground">{IDLE_DISCLAIMER}</p>
    </div>
  );
}
