// =============================================================================
// F6 - Cobertura da classificação (decisão 4 do spec de 07/09/2026): a MESMA
// conta usada no indicador compacto da AppsPage e no cabeçalho da Fila de
// classificação (Configurações -> Classificação), para o número nunca
// divergir entre as duas telas. Fonte: GET /app-catalog
// (uncategorized_seconds_active e total_seconds_active, janela de 30 dias).
//
// NÃO confundir com `classification_coverage` do /dashboard e da timeline
// (mesmo conceito, fonte e janela diferentes - cada tela já lê a sua com
// lib/period.ts#formatPct, e não editamos essa fonte aqui).
// =============================================================================

/** Item mínimo o bastante para "fechar o gap" contando impacto (ver appsToCloseGap). */
interface ImpactItem {
  seconds_active_30d: number;
}

/**
 * Fração 0..1 pronta para `formatPct` (lib/period.ts) - null quando não há
 * tempo ativo no período (sem dado NUNCA vira 0%, mesma regra de IndexCell/
 * KpisRow).
 */
export function classificationCoverage(
  totalSecondsActive: number,
  uncategorizedSecondsActive: number,
): number | null {
  if (totalSecondsActive <= 0) return null;
  return (totalSecondsActive - uncategorizedSecondsActive) / totalSecondsActive;
}

/** Meta de cobertura da fila (Seção 5, item 2 do spec): abaixo disso a fila tem trabalho relevante. */
export const COVERAGE_TARGET = 0.95;

export interface CoverageGap {
  /** Segundos sem categoria ALÉM do que a meta tolera - 0 quando já bateu a meta. */
  secondsToGo: number;
  /** true quando a cobertura já atingiu ou passou COVERAGE_TARGET (ou não há dado no período). */
  metTarget: boolean;
}

/** Quanto falta em segundos para chegar à meta - a base do "faltam Xh e N apps". */
export function coverageGap(totalSecondsActive: number, uncategorizedSecondsActive: number): CoverageGap {
  if (totalSecondsActive <= 0) return { secondsToGo: 0, metTarget: true };
  const allowed = totalSecondsActive * (1 - COVERAGE_TARGET);
  const secondsToGo = Math.max(0, uncategorizedSecondsActive - allowed);
  return { secondsToGo, metTarget: secondsToGo <= 0 };
}

/**
 * Quantos apps (do topo da fila, JÁ ordenada por impacto - sort=impacto)
 * fecham o gap em segundos. Aproximação declarada: assume que categorizar um
 * app tira TODO o tempo dele do balde "sem classificação" - sempre verdade,
 * porque a cobertura não olha o SINAL da classificação (produtivo/neutro/
 * improdutivo), só se existe uma. Se a fila visível não alcançar o gap (teto
 * de 500 itens do catálogo), devolve a contagem toda disponível com
 * `exact: false`, para a tela dizer "pelo menos N" em vez de um número exato.
 */
export function appsToCloseGap(
  queueSortedByImpact: readonly ImpactItem[],
  secondsToGo: number,
): { count: number; exact: boolean } {
  if (secondsToGo <= 0) return { count: 0, exact: true };
  let acc = 0;
  for (let i = 0; i < queueSortedByImpact.length; i++) {
    acc += queueSortedByImpact[i].seconds_active_30d;
    if (acc >= secondsToGo) return { count: i + 1, exact: true };
  }
  return { count: queueSortedByImpact.length, exact: false };
}
