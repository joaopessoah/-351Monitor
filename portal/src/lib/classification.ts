// =============================================================================
// Vocabulário e cores CANÔNICOS da classificação (Princípio 8, Seção 8.7):
// SEMPRE "Relacionado ao trabalho" (1) / "Neutro" (0) / "Não relacionado ao
// trabalho" (-1) / "Não categorizado" (app sem mapeamento) - jamais os
// adjetivos vetados de produtividade. Cores: +1 verde, 0 cinza-azulado,
// -1 vermelho suave; Não categorizado usa o mesmo #94a3b8 da faixa de apps
// da timeline. Única fonte para gráficos, tabelas e selects do portal.
// =============================================================================

import type { UsageCategoryItem } from "./types";

export const classificationColors = {
  workRelated: "#16a34a",
  neutral: "#64748b",
  notWorkRelated: "#f87171",
  uncategorized: "#94a3b8",
} as const;

/**
 * Rótulo canônico do app sem categoria. É TAMBÉM o nome da categoria seedada
 * na criação da organização (classification 0): para o usuário as duas coisas
 * são o mesmo conceito, então gráficos e selects tratam a seedada como o
 * próprio balde "Não categorizado" (ver mergeUncategorizedRows).
 */
export const UNCATEGORIZED_LABEL = "Não categorizado";

/**
 * Mescla as linhas de `group_by=category` cujo rótulo resolve para
 * "Não categorizado" - o balde null (apps sem mapeamento) E a categoria
 * seedada de mesmo nome - numa única linha com a cara do balde null
 * (campos null - cor e rótulo canônicos). Sem dupla contagem: os conjuntos
 * de apps (mapeados na seedada vs sem mapeamento) são disjuntos, então
 * seconds_active e app_count somam. UNIQUE (tenant_id, name) garante no
 * máximo uma categoria com esse nome por tenant.
 */
export function mergeUncategorizedRows(items: UsageCategoryItem[]): UsageCategoryItem[] {
  const rest: UsageCategoryItem[] = [];
  let found = false;
  let seconds = 0;
  let apps = 0;
  for (const item of items) {
    if ((item.name ?? UNCATEGORIZED_LABEL) !== UNCATEGORIZED_LABEL) {
      rest.push(item);
      continue;
    }
    found = true;
    seconds += item.seconds_active;
    apps += item.app_count;
  }
  if (!found) return rest;
  return [
    ...rest,
    { category_id: null, name: null, classification: null, color: null, seconds_active: seconds, app_count: apps },
  ];
}

/** Rótulo fixo da classificação - null = app sem categoria mapeada. */
export function classificationLabel(classification: number | null): string {
  if (classification === 1) return "Relacionado ao trabalho";
  if (classification === 0) return "Neutro";
  if (classification === -1) return "Não relacionado ao trabalho";
  return "Não categorizado";
}

/** Cor canônica da classificação - null = Não categorizado. */
export function classificationColor(classification: number | null): string {
  if (classification === 1) return classificationColors.workRelated;
  if (classification === 0) return classificationColors.neutral;
  if (classification === -1) return classificationColors.notWorkRelated;
  return classificationColors.uncategorized;
}
