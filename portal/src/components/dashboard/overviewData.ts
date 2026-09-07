// =============================================================================
// Consultas COMPARTILHADAS da Visão Geral nova (F6). Cada hook é a fonte única
// da queryKey e da URL de um endpoint: dois blocos da tela que precisam do
// mesmo recorte declaram o mesmo hook e o TanStack Query resolve os dois
// observadores do MESMO cache, sem requisição extra - o padrão que a barra de
// meta já usava com os gráficos da semana e que o sino de pendências usa com as
// telas de origem.
//
// Sem polling aqui de propósito: estes são agregados de PERÍODO, materializados
// pela agregação diária, e não mudam a cada minuto. O único bloco que se
// atualiza sozinho é a faixa "Agora" (presença, 60 s pausados em aba oculta).
// =============================================================================

import { useQuery } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { addDays } from "@/lib/format";
import { periodQuery } from "@/lib/period";
import type { ResolvedPeriod } from "@/lib/period";
import type {
  ActivityByHourResponse,
  ForaDoHorarioResponse,
  OverviewResponse,
} from "@/lib/types";
import { foraDoHorarioKey, foraDoHorarioUrl } from "@/components/reports/ForaDoHorario";
import { comparisonRangeOf } from "@/components/dashboard/comparison";

/** Dias da série dos minigráficos de KPI (12 colunas, como no mockup aprovado). */
export const MINI_SERIES_DAYS = 12;

/**
 * Período imediatamente anterior de MESMA duração - a régua da comparação é a
 * do backend (`compare=true`); aqui ela só é reconstruída para os endpoints que
 * NÃO têm `compare` (atividade por hora e fora do horário).
 */
export function previousPeriodOf(period: ResolvedPeriod | null): ResolvedPeriod | null {
  if (period === null) return null;
  const prev = comparisonRangeOf({ from: period.from, to: period.to });
  return { preset: period.preset, from: prev.from, to: prev.to, label: "período anterior" };
}

/**
 * Janela de 12 dias TERMINANDO no fim do período: uma consulta só alimenta os
 * seis minigráficos de KPI. Sem isto seriam 6 séries × N dias de requisições.
 */
export function miniSeriesPeriodOf(period: ResolvedPeriod | null): ResolvedPeriod | null {
  if (period === null) return null;
  return {
    preset: period.preset,
    from: addDays(period.to, -(MINI_SERIES_DAYS - 1)),
    to: period.to,
    label: `últimos ${MINI_SERIES_DAYS} dias`,
  };
}

/**
 * Query string do recorte, vazia enquanto o período não resolveu (o fuso vem do
 * /me). Nenhuma consulta chega a sair nesse estado - todo hook usa
 * `enabled: period !== null` -, e assim nenhum callback carrega `period!`.
 */
function rangeQuery(period: ResolvedPeriod | null, tag: string | null, compare = false): string {
  return period !== null ? periodQuery(period, tag, compare) : "";
}

/** `GET /dashboard/overview` - índice e cobertura JÁ calculados pelo servidor. */
export function useOverviewQuery(
  period: ResolvedPeriod | null,
  tag: string | null,
  compare: boolean,
): UseQueryResult<OverviewResponse> {
  return useQuery({
    queryKey: ["dashboard", "overview", period?.from, period?.to, tag, compare],
    queryFn: () =>
      api<OverviewResponse>(`/dashboard/overview?${rangeQuery(period, tag, compare)}`),
    enabled: period !== null,
    // Troca de período/equipe mantém o desenho anterior no lugar do skeleton.
    placeholderData: (prev) => prev,
  });
}

/** `GET /dashboard/activity-by-hour` - as 24 horas locais vêm sempre. */
export function useActivityByHourQuery(
  period: ResolvedPeriod | null,
  tag: string | null,
): UseQueryResult<ActivityByHourResponse> {
  return useQuery({
    queryKey: ["dashboard", "activity-by-hour", period?.from, period?.to, tag],
    queryFn: () =>
      api<ActivityByHourResponse>(`/dashboard/activity-by-hour?${rangeQuery(period, tag)}`),
    enabled: period !== null,
    placeholderData: (prev) => prev,
  });
}

/**
 * `GET /reports/fora-do-horario` agregado do período. SEM include_devices: é um
 * indicador de EQUIPE, e por isso a leitura não gera `view_report` (o recorte
 * por dispositivo só existe na aba do relatório de Uso).
 */
export function useForaDoHorarioQuery(
  period: ResolvedPeriod | null,
  tag: string | null,
): UseQueryResult<ForaDoHorarioResponse> {
  const params = {
    from: period?.from ?? "",
    to: period?.to ?? "",
    deviceIdsKey: "",
    tag,
    page: 1,
    includeDevices: false,
    pageSize: 1,
  };
  return useQuery({
    queryKey: foraDoHorarioKey(params),
    queryFn: () => api<ForaDoHorarioResponse>(foraDoHorarioUrl(params)),
    enabled: period !== null,
    staleTime: 5 * 60 * 1000,
  });
}
