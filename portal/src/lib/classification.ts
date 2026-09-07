// =============================================================================
// Vocabulário e cores da classificação. Única fonte para gráficos, tabelas e
// selects do portal.
//
// F6, decisão 1 do spec de 07/09/2026: o vocabulário deixou de ser fixo e
// passou a ser ESCOLHA DA ORGANIZAÇÃO (organizations.classification_vocabulary,
// no /me e no /organization):
//   - "produtividade" (default): Produtivo / Neutro / Improdutivo /
//     Sem classificação - padrão da categoria e o que o site promete;
//   - "trabalho": Relacionado ao trabalho / Neutro / Não relacionado ao
//     trabalho / Não categorizado - o conjunto neutro.
// Os dados são os MESMOS nos dois (classification +1/0/-1 e o balde sem
// classificação); só o rótulo muda, e trocar não reagrega nada.
//
// COMPATIBILIDADE: classificationLabel/classificationColor/UNCATEGORIZED_LABEL
// continuam existindo com a assinatura antiga e o conjunto "trabalho" - são
// consumidos por várias telas. Quem tem o vocabulário em mão usa as variantes
// *In(vocabulary, ...) desta mesma fonte.
//
// LINHAS QUE NÃO CRUZAMOS: o julgamento é do cliente sobre APLICATIVOS, nunca
// sobre pessoas (daí o enquadramento CLASSIFICATION_FRAMING acompanhar os
// rótulos na interface), e estado de máquina fica FORA deste vocabulário -
// ocioso é estado da máquina e nunca conta como improdutivo.
//
// Cores: paleta de dataviz da marca (BRAND em lib/brandTheme.ts, a mesma
// legenda do site) - +1 verde de atividade, 0 azul neutro, -1 âmbar, sem
// classificação cinza-azulado. A COR NÃO muda com o vocabulário: é a mesma
// grandeza, e trocar rótulo não pode remexer a leitura dos gráficos.
// =============================================================================

import { BRAND } from "./brandTheme";
import type { UsageCategoryItem } from "./types";

export const classificationColors = {
  workRelated: BRAND.vizProdutivo,
  neutral: BRAND.vizNeutro,
  notWorkRelated: BRAND.vizImprodutivo,
  uncategorized: BRAND.slate,
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

// =============================================================================
// F6 - vocabulário de PRODUTIVIDADE (decisão 1 do spec de 07/09/2026, aprovada
// pelo dono): a Visão Geral e as telas de análise falam "Produtivo / Neutro /
// Improdutivo / Sem classificação", SEMPRE acompanhados do enquadramento
// "classificação definida pela sua empresa" - o julgamento é da organização,
// sobre APLICATIVOS, nunca sobre pessoas.
//
// Os rótulos antigos ("Relacionado ao trabalho" etc.) continuam existindo acima
// porque são o conjunto alternativo da organização e ainda são a língua das
// telas de curadoria e dos relatórios de uso; nada foi renomeado.
// =============================================================================

/**
 * Cinza-azulado do balde "Sem classificação" na paleta de dataviz VALIDADA da
 * F6 (spec, seção 3): sobe de #5B6982 (BRAND.slate) para #8593AD para não se
 * confundir com o cinza escuro do ocioso, que agora usa hachura a 45°. Vive
 * aqui - e não em brandTheme.ts - porque é uma cor de CLASSIFICAÇÃO, e é este
 * módulo que os gráficos consultam para pintar os quatro baldes.
 */
export const SEM_CLASSIFICACAO_COLOR = "#8593AD";

/** Rótulo de produtividade - null = tempo ativo em app sem categoria mapeada. */
export function productivityLabel(classification: number | null): string {
  if (classification === 1) return "Produtivo";
  if (classification === 0) return "Neutro";
  if (classification === -1) return "Improdutivo";
  return "Sem classificação";
}

/** Cor do balde de produtividade - null = "Sem classificação" (paleta da F6). */
export function productivityColor(classification: number | null): string {
  if (classification === 1) return classificationColors.workRelated;
  if (classification === 0) return classificationColors.neutral;
  if (classification === -1) return classificationColors.notWorkRelated;
  return SEM_CLASSIFICACAO_COLOR;
}

// -----------------------------------------------------------------------------
// F6 - vocabulário configurável da organização
// -----------------------------------------------------------------------------

/** Os dois conjuntos de rótulos aceitos pelo backend (CHECK em organizations). */
export type ClassificationVocabulary = "produtividade" | "trabalho";

/** Default do backend: toda organização nasce em "produtividade". */
export const DEFAULT_CLASSIFICATION_VOCABULARY: ClassificationVocabulary = "produtividade";

/**
 * Enquadramento OBRIGATÓRIO ao lado dos rótulos na interface (decisão 1 do
 * spec): quem julga é a empresa, e o julgamento é sobre aplicativos.
 */
export const CLASSIFICATION_FRAMING = "classificação definida pela sua empresa";

/** Rótulos dos quatro baldes por vocabulário - a tabela inteira num lugar só. */
const VOCABULARY_LABELS: Record<
  ClassificationVocabulary,
  { workRelated: string; neutral: string; notWorkRelated: string; unclassified: string }
> = {
  produtividade: {
    workRelated: "Produtivo",
    neutral: "Neutro",
    notWorkRelated: "Improdutivo",
    unclassified: "Sem classificação",
  },
  trabalho: {
    workRelated: "Relacionado ao trabalho",
    neutral: "Neutro",
    notWorkRelated: "Não relacionado ao trabalho",
    unclassified: "Não categorizado",
  },
};

/** Nome de exibição do conjunto (para o seletor em Configurações). */
export const VOCABULARY_OPTIONS: { value: ClassificationVocabulary; label: string; sample: string }[] = [
  {
    value: "produtividade",
    label: "Produtividade",
    sample: "Produtivo · Neutro · Improdutivo · Sem classificação",
  },
  {
    value: "trabalho",
    label: "Relação com o trabalho",
    sample: "Relacionado ao trabalho · Neutro · Não relacionado ao trabalho · Não categorizado",
  },
];

/**
 * Normaliza o que veio da API num vocabulário válido. Valor desconhecido (API
 * mais nova que o portal, ou campo ainda ausente na resposta) cai no default em
 * vez de renderizar `undefined` na tela.
 */
export function asClassificationVocabulary(value: unknown): ClassificationVocabulary {
  return value === "trabalho" || value === "produtividade" ? value : DEFAULT_CLASSIFICATION_VOCABULARY;
}

/**
 * Vocabulário da organização a partir da resposta do /me, defensivo de
 * propósito: a leitura acontece em toda tela que mostra classificação e não
 * pode explodir enquanto o /me está carregando.
 */
export function classificationVocabularyOf(me: unknown): ClassificationVocabulary {
  const organization = (me as { organization?: { classification_vocabulary?: unknown } } | null | undefined)
    ?.organization;
  return asClassificationVocabulary(organization?.classification_vocabulary);
}

/**
 * Rótulo da classificação NO VOCABULÁRIO da organização - null = quarto balde
 * (app sem categoria mapeada). Variante de classificationLabel para quem já
 * tem o vocabulário em mão.
 */
export function classificationLabelIn(
  vocabulary: ClassificationVocabulary,
  classification: number | null,
): string {
  const labels = VOCABULARY_LABELS[vocabulary];
  if (classification === 1) return labels.workRelated;
  if (classification === 0) return labels.neutral;
  if (classification === -1) return labels.notWorkRelated;
  return labels.unclassified;
}

/**
 * Rótulo do QUARTO balde (tempo ativo em app sem categoria): "Sem classificação"
 * em produtividade, "Não categorizado" em trabalho. É o mesmo conceito do
 * UNCATEGORIZED_LABEL, agora dependente do vocabulário.
 */
export function unclassifiedLabelIn(vocabulary: ClassificationVocabulary): string {
  return VOCABULARY_LABELS[vocabulary].unclassified;
}

/** Os quatro baldes na ordem canônica da legenda (+1, 0, -1, sem classificação). */
export function classificationLegendIn(
  vocabulary: ClassificationVocabulary,
): { classification: number | null; label: string; color: string }[] {
  return [
    { classification: 1, label: classificationLabelIn(vocabulary, 1), color: classificationColors.workRelated },
    { classification: 0, label: classificationLabelIn(vocabulary, 0), color: classificationColors.neutral },
    { classification: -1, label: classificationLabelIn(vocabulary, -1), color: classificationColors.notWorkRelated },
    { classification: null, label: unclassifiedLabelIn(vocabulary), color: classificationColors.uncategorized },
  ];
}
