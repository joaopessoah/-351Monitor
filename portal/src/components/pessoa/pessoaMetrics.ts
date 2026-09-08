// =============================================================================
// Cálculos derivados da visão individual da PESSOA (F6). Contas puras, sem
// estado e sem chamada de rede.
//
// O QUE SAIU DAQUI (F9): personIndicators(), que reproduzia no cliente a fórmula
// do índice e da cobertura porque GET /dashboard/summary é anterior à fase e não
// devolvia os indicadores prontos. GET /people/{sid}/self-view devolve, e a
// duplicação foi apagada como o próprio comentário dela pedia — uma fórmula só,
// no servidor, sem chance de a tela e o relatório divergirem.
// =============================================================================

import type { BusinessHours } from "@/lib/types";

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
