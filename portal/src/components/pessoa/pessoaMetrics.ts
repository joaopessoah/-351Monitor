// =============================================================================
// Cálculos derivados da visão individual da PESSOA (F6). Duas contas puras,
// sem estado e sem chamada de rede - a página só passa os baldes já buscados.
// =============================================================================

import type { BusinessHours, DashboardSummaryTotals } from "@/lib/types";

export interface PersonIndicators {
  /** produtivo ÷ (produtivo + neutro + improdutivo); null sem base (nada classificado). */
  productivityIndex: number | null;
  /** (ativo − sem classificação) ÷ ativo; null sem tempo ativo no período. */
  classificationCoverage: number | null;
}

/**
 * Índice de produtividade e cobertura da classificação, a partir dos baldes
 * de `GET /dashboard/summary`.
 *
 * DUPLICAÇÃO DELIBERADA (temporária): a fórmula é a MESMA do servidor -
 * decisão 4 do spec de 07/09/2026 ("produtivo ÷ (produtivo + neutro +
 * improdutivo)", com a cobertura sempre exibida ao lado, nunca um sem o
 * outro) - já usada em `GET /dashboard/overview` e em `GET /people`
 * (productivity_index/classification_coverage prontos). O summary é
 * ANTERIOR a essa fase e não devolve os dois campos calculados, por isso
 * esta função reproduz a conta aqui. Quando o summary passar a devolver
 * productivity_index/classification_coverage prontos, apague esta função e
 * consuma o valor do servidor, como as outras telas já fazem.
 */
export function personIndicators(totals: DashboardSummaryTotals): PersonIndicators {
  const classifiedBase =
    totals.seconds_work_related + totals.seconds_neutral + totals.seconds_not_work_related;
  const productivityIndex = classifiedBase > 0 ? totals.seconds_work_related / classifiedBase : null;

  const activeBase = totals.seconds_active;
  const classificationCoverage =
    activeBase > 0 ? (activeBase - totals.seconds_unclassified) / activeBase : null;

  return { productivityIndex, classificationCoverage };
}

/** "HH:mm" -> horas fracionárias; null quando o formato não bate. */
function parseHm(value: string): number | null {
  const match = /^(\d{1,2}):(\d{2})$/.exec(value);
  if (match === null) return null;
  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  if (Number.isNaN(hours) || Number.isNaN(minutes)) return null;
  return hours + minutes / 60;
}

/**
 * Duração da jornada declarada (para a linha de referência da composição por
 * dia): fim − início de `business_hours` do `/me`. Sem configuração da
 * organização, ou com horário mal formado, cai no fallback de 8h do spec -
 * a referência nunca deve desaparecer do gráfico por falta de dado.
 */
export function journeyHoursOf(businessHours: BusinessHours | null): number {
  if (businessHours === null) return 8;
  const start = parseHm(businessHours.start);
  const end = parseHm(businessHours.end);
  if (start === null || end === null) return 8;
  const diff = end - start;
  return diff > 0 ? diff : 8;
}

/** Aviso pedagógico do Ocioso (regra invíolavel) - texto verbatim da tela. */
export const IDLE_DISCLAIMER =
  "Ocioso significa sem uso de teclado e mouse. Reuniões, chamadas e leitura podem aparecer como ociosidade.";
