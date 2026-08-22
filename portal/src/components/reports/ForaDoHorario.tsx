// =============================================================================
// Atividade fora do horário de trabalho (GET /reports/fora-do-horario):
// helpers compartilhados pela aba do relatório de Uso e pelo card da Visão
// Geral, para que as duas telas falem exatamente a mesma língua.
//
// LINHA VERMELHA DO PRODUTO: é um indicador de EQUILÍBRIO da equipe. O
// vocabulário é SEMPRE "atividade fora do horário de trabalho". Jamais hora
// extra, jornada extraordinária, adicional noturno ou banco de horas - o
// produto não calcula nada disso e a tela não pode sugerir que calcula.
// =============================================================================

import { Scale } from "lucide-react";
import type { BusinessHours, ForaDoHorarioResponse } from "@/lib/types";
import { JORNADA_DISCLAIMER } from "@/lib/messages";

/** Nomes dos dias ISO (1 = segunda … 7 = domingo) para descrever a escala. */
const ISO_DAY_LABELS = ["", "seg", "ter", "qua", "qui", "sex", "sáb", "dom"];

/** "seg a sex, 08:00 às 18:00" - descrição curta da janela declarada. */
export function businessHoursLabel(hours: BusinessHours): string {
  const days = [...hours.days].filter((d) => d >= 1 && d <= 7).sort((a, b) => a - b);
  const nomes = days.map((d) => ISO_DAY_LABELS[d]);
  // sequência contígua vira "seg a sex"; qualquer outro recorte lista os dias
  const contigua = days.length > 2 && days.every((d, i) => i === 0 || d === days[i - 1] + 1);
  const escala =
    days.length === 0
      ? "nenhum dia"
      : contigua
        ? `${nomes[0]} a ${nomes[nomes.length - 1]}`
        : nomes.join(", ");
  return `${escala}, ${hours.start} às ${hours.end}`;
}

/**
 * Disclaimer da Portaria 671/MTE herdado do relatório de Jornada - texto
 * VERBATIM da constante compartilhada, banner fixo e não-dispensável.
 */
export function ForaDoHorarioDisclaimer() {
  return (
    <div
      role="note"
      className="flex items-start gap-2 rounded-md border bg-muted/50 px-4 py-3 text-xs text-muted-foreground"
    >
      <Scale className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
      <span>{JORNADA_DISCLAIMER}</span>
    </div>
  );
}

/**
 * Explicação dos estados em que NÃO há número a mostrar. Devolve null quando o
 * painel pode exibir os valores (status "ok").
 *
 * Os dois casos existem porque zero seria uma resposta falsa:
 *  - sem horário de trabalho declarado não existe "fora dele";
 *  - com a coleta restrita ao horário de trabalho, o agente NÃO coleta fora da
 *    janela por decisão da própria organização, então não há o que somar.
 */
export function foraDoHorarioEmptyState(
  data: ForaDoHorarioResponse,
): { titulo: string; explicacao: string; acao: { label: string; to: string } | null } | null {
  if (data.status === "horario_nao_configurado") {
    return {
      titulo: "Horário de trabalho não configurado",
      explicacao:
        "Sem um horário de trabalho declarado não há como saber o que ficou fora dele. " +
        "Defina a escala e os limites da organização para acompanhar este indicador.",
      acao: { label: "Configurar horário de trabalho", to: "/configuracoes/organizacao" },
    };
  }
  if (data.status === "coleta_restrita_ao_horario") {
    return {
      titulo: "Coleta restrita ao horário de trabalho",
      explicacao:
        "A organização escolheu coletar apenas dentro do horário de trabalho, então fora dele " +
        "não existe registro nenhum, por decisão de configuração. Mostrar zero aqui seria enganoso.",
      acao: { label: "Ver janela de coleta", to: "/configuracoes/coleta" },
    };
  }
  return null;
}

/**
 * Recorte da consulta. Vive aqui para que a aba do relatório e o botão de
 * exportar (que precisa saber o status antes de habilitar) compartilhem a MESMA
 * queryKey - o TanStack Query resolve os dois observadores do mesmo cache, sem
 * requisição extra, como a barra de meta faz com os gráficos da semana.
 */
export interface ForaDoHorarioQuery {
  from: string;
  to: string;
  /** device_ids já normalizado (ordenado e unido por vírgula); "" sem filtro. */
  deviceIdsKey: string;
  page: number;
  includeDevices: boolean;
  pageSize: number;
}

export function foraDoHorarioKey(q: ForaDoHorarioQuery) {
  return ["reports", "fora-do-horario", q] as const;
}

export function foraDoHorarioUrl(q: ForaDoHorarioQuery): string {
  const devices = q.deviceIdsKey.length > 0 ? `&device_ids=${q.deviceIdsKey}` : "";
  return (
    `/reports/fora-do-horario?from=${q.from}&to=${q.to}${devices}` +
    `&include_devices=${q.includeDevices}&page=${q.page}&page_size=${q.pageSize}`
  );
}

/** Percentual do tempo ativo que caiu fora do horário (null sem tempo ativo). */
export function foraDoHorarioPct(secondsOutside: number, secondsActive: number): number | null {
  if (secondsActive <= 0) return null;
  return Math.round((secondsOutside / secondsActive) * 100);
}
