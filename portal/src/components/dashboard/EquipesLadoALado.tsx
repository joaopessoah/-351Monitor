// =============================================================================
// EQUIPES LADO A LADO (F6, decisão 3 do spec de 07/09/2026).
//
// A decisão anterior era "uma equipe por vez"; o dono aprovou a comparação lado
// a lado COM MÍNIMO DE GRUPO de 3 pessoas. Este componente é essa régua na
// prática: equipe com menos de 3 pessoas mostra os TOTAIS e imprime traço nas
// médias comparativas, com `title` explicando o porquê. Média de equipe com
// duas pessoas é afirmação sobre indivíduo com outro nome, e o produto não faz
// isso fora do opt-in.
//
// UMA CONSULTA, N REQUISIÇÕES: as etiquetas vêm de `useTeamTags()` e um único
// `useQuery` dispara um `Promise.all` de `/dashboard/overview?tag=` por
// etiqueta. Um `useQuery` por etiqueta faria a tabela aparecer em cascata, com
// N estados de erro independentes e nenhuma forma de dizer "a comparação
// falhou"; assim há UM skeleton, UM erro e UM retry para o bloco inteiro.
//
// ÍNDICE E COBERTURA VÊM DO SERVIDOR por etiqueta (decisão 4) — o portal não
// recalcula nem reagrega. O que ele deriva são divisões dos totais que o
// servidor já mandou: horas ativas por pessoa por dia, ociosidade e capacidade
// utilizada. `null` imprime "–", nunca 0%.
//
// Nada aqui ordena equipes por desempenho: a ordem é ALFABÉTICA e fixa. Sem
// ranking, sem pódio, sem cor de "melhor" e "pior" — a barra é a mesma cor para
// todas, e o número ao lado é o dado.
// =============================================================================

import { useMemo } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Info, Users } from "lucide-react";

import { api } from "@/lib/api";
import { formatDuration } from "@/lib/format";
import { formatPct, periodQuery, type ResolvedPeriod } from "@/lib/period";
import type { BusinessHours, OverviewResponse, TeamComparisonRow } from "@/lib/types";
import { cn } from "@/lib/utils";
import {
  BlockCard,
  ChartSkeleton,
  InlineError,
  PointsBadge,
  VIZ,
  hoursLabel,
} from "@/components/dashboard/overviewKit";

/** Mínimo de grupo da decisão 3: abaixo disso, nenhuma média comparativa. */
export const MINIMO_DE_GRUPO = 3;

/** Explicação do traço nas médias — vai no `title` de cada célula suprimida. */
const HINT_MINIMO =
  `Equipe com menos de ${MINIMO_DE_GRUPO} pessoas no período: os totais aparecem, mas as médias ` +
  "comparativas não. Com um grupo tão pequeno a média da equipe seria, na prática, o dado " +
  "individual de alguém — e o produto não faz afirmação sobre indivíduo aqui.";

/** Jornada padrão quando a organização não configurou horário de trabalho. */
const JORNADA_PADRAO_HORAS = 8;

/** Dias úteis padrão (ISO 1..5) quando não há janela configurada. */
const DIAS_UTEIS_PADRAO = [1, 2, 3, 4, 5];

/** "yyyy-MM-dd" → Date em UTC (o recorte já vem no fuso do tenant). */
function parseDay(day: string): Date {
  const [y, m, d] = day.split("-").map(Number);
  return new Date(Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1));
}

/**
 * Horas da jornada declarada e dias úteis do período, a partir do
 * `business_hours` da organização.
 *
 * FERIADOS FICAM DE FORA nesta leva: entram na F7, junto com a entidade
 * `teams` e a jornada por equipe (spec seção 6). Enquanto isso, uma semana com
 * feriado tem a capacidade SUBESTIMADA — o denominador conta um dia que não
 * existiu. O rodapé do bloco diz isso em voz alta, para ninguém tomar decisão
 * de contratação em cima de um número que ainda vai mudar.
 */
function baseDeCapacidade(
  businessHours: BusinessHours | null | undefined,
  period: ResolvedPeriod,
): { jornadaHoras: number; diasUteis: number } {
  const dias = businessHours?.days ?? DIAS_UTEIS_PADRAO;

  let jornadaHoras = JORNADA_PADRAO_HORAS;
  if (businessHours?.start !== undefined && businessHours.end !== undefined) {
    const [hi, mi] = businessHours.start.split(":").map(Number);
    const [hf, mf] = businessHours.end.split(":").map(Number);
    const horas = (hf ?? 0) + (mf ?? 0) / 60 - ((hi ?? 0) + (mi ?? 0) / 60);
    if (horas > 0) jornadaHoras = horas;
  }

  let diasUteis = 0;
  const fim = parseDay(period.to);
  for (const cursor = parseDay(period.from); cursor <= fim; cursor.setUTCDate(cursor.getUTCDate() + 1)) {
    // getUTCDay: 0 = domingo; ISO: 7 = domingo
    const iso = cursor.getUTCDay() === 0 ? 7 : cursor.getUTCDay();
    if (dias.includes(iso)) diasUteis++;
  }

  return { jornadaHoras, diasUteis };
}

/**
 * Uma consulta de overview POR ETIQUETA, resolvidas juntas. `compare: true`
 * porque a variação do índice em PONTOS é parte da linha — e a régua da
 * comparação é a do backend, nunca uma subtração inventada aqui.
 */
export function useEquipesQuery(
  period: ResolvedPeriod | null,
  tags: string[],
  businessHours: BusinessHours | null | undefined,
) {
  return useQuery({
    queryKey: ["dashboard", "equipes", period?.from, period?.to, tags.join("|")],
    queryFn: async (): Promise<TeamComparisonRow[]> => {
      if (period === null) return [];

      const respostas = await Promise.all(
        tags.map((tag) =>
          api<OverviewResponse>(`/dashboard/overview?${periodQuery(period, tag, true)}`),
        ),
      );

      const { jornadaHoras, diasUteis } = baseDeCapacidade(businessHours, period);

      return respostas.map((resposta, i): TeamComparisonRow => {
        const totals = resposta.totals;
        const pessoas = totals.person_count;
        const pessoaDias = totals.person_days;
        const atendeMinimo = pessoas >= MINIMO_DE_GRUPO;

        // denominador da capacidade: jornada declarada x dias úteis x pessoas
        const capacidadeSegundos = jornadaHoras * 3600 * diasUteis * pessoas;

        return {
          tag: tags[i] ?? "",
          people: pessoas,
          person_days: pessoaDias,
          seconds_on: totals.seconds_on,
          seconds_active: totals.seconds_active,
          seconds_idle: totals.seconds_idle,
          seconds_unclassified: totals.seconds_unclassified,
          // do servidor: nunca recalculado no portal
          productivity_index: totals.productivity_index,
          productivity_index_previous: resposta.previous?.productivity_index ?? null,
          classification_coverage: totals.classification_coverage,
          // médias comparativas: só com o mínimo de grupo satisfeito
          active_seconds_per_person_day:
            atendeMinimo && pessoaDias > 0 ? totals.seconds_active / pessoaDias : null,
          idle_share:
            atendeMinimo && totals.seconds_on > 0 ? totals.seconds_idle / totals.seconds_on : null,
          capacity_used:
            atendeMinimo && capacidadeSegundos > 0
              ? totals.seconds_active / capacidadeSegundos
              : null,
          meets_group_minimum: atendeMinimo,
        };
      });
    },
    enabled: period !== null && tags.length > 0,
    placeholderData: (prev) => prev,
  });
}

export function EquipesLadoALado({
  className,
  period,
  tags,
  businessHours,
}: {
  className?: string;
  period: ResolvedPeriod | null;
  tags: string[];
  businessHours: BusinessHours | null | undefined;
}) {
  const query = useEquipesQuery(period, tags, businessHours);

  // ordem ALFABÉTICA, nunca por métrica: ordenar por índice seria um ranking
  const linhas = useMemo(
    () => [...(query.data ?? [])].sort((a, b) => a.tag.localeCompare(b.tag, "pt-BR")),
    [query.data],
  );

  const semMinimo = linhas.filter((l) => !l.meets_group_minimum).length;

  if (tags.length === 0) {
    return (
      <BlockCard
        className={className}
        title="Equipes lado a lado"
        hint="Comparação por etiqueta de equipe"
      >
        <p className="flex items-start gap-2 rounded-md border border-border bg-muted/30 p-3 text-xs text-muted-foreground">
          <Users className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
          <span>
            Nenhuma etiqueta de equipe nos dispositivos ainda. Etiquete as máquinas em{" "}
            <Link to="/dispositivos" className="text-foreground underline underline-offset-2">
              Administração › Dispositivos
            </Link>{" "}
            e a comparação aparece aqui.
          </span>
        </p>
      </BlockCard>
    );
  }

  return (
    <BlockCard
      className={className}
      title="Equipes lado a lado"
      hint={`${tags.length} ${tags.length === 1 ? "etiqueta" : "etiquetas"} · ordem alfabética, sem ranking`}
    >
      {query.isPending && query.data === undefined ? (
        <ChartSkeleton height={200} />
      ) : query.data === undefined ? (
        <InlineError
          message="Não foi possível carregar a comparação de equipes."
          onRetry={() => void query.refetch()}
          height={200}
        />
      ) : (
        <div className="space-y-3">
          <div className="overflow-x-auto">
            <table className="w-full min-w-[720px] text-xs" aria-label="Comparação de equipes">
              <thead>
                <tr className="border-b text-left text-[10px] uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="py-1.5 pr-2">Equipe</th>
                  <th scope="col" className="py-1.5 pr-2 text-right">Pessoas</th>
                  <th scope="col" className="py-1.5 pr-2">Índice</th>
                  <th scope="col" className="py-1.5 pr-2 text-right">
                    <span title="Horas de uso de teclado/mouse por pessoa por dia com dado.">
                      Ativas /pessoa/dia
                    </span>
                  </th>
                  <th scope="col" className="py-1.5 pr-2 text-right">
                    <span title="Ocioso ÷ máquina ligada. Ocioso é estado de máquina: reuniões e leitura aparecem aqui, e nunca conta como improdutivo.">
                      Ociosidade
                    </span>
                  </th>
                  <th scope="col" className="py-1.5 text-right">
                    <span title="Tempo ativo ÷ (jornada declarada × dias úteis × pessoas). Feriados ainda não entram no cálculo.">
                      Capacidade
                    </span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {linhas.map((linha) => (
                  <LinhaEquipe key={linha.tag} linha={linha} />
                ))}
              </tbody>
            </table>
          </div>

          <p className="flex items-start gap-1.5 border-t border-border pt-2 text-[11px] leading-snug text-muted-foreground">
            <Info className="mt-0.5 h-3 w-3 shrink-0" aria-hidden />
            <span>
              Índice e cobertura vêm calculados do servidor por etiqueta. A capacidade usa a
              jornada declarada no horário de trabalho da organização;{" "}
              <span className="text-foreground">feriados ainda não entram no cálculo</span> e
              chegam na próxima fase, então uma semana com feriado aparece com capacidade
              subestimada.
              {semMinimo > 0 && (
                <>
                  {" "}
                  {semMinimo === 1
                    ? "Uma equipe está"
                    : `${semMinimo} equipes estão`}{" "}
                  abaixo de {MINIMO_DE_GRUPO} pessoas: mostram totais, sem médias comparativas.
                </>
              )}
            </span>
          </p>
        </div>
      )}
    </BlockCard>
  );
}

/** Uma equipe: barra do índice (cor única), valor, variação em pontos e totais. */
function LinhaEquipe({ linha }: { linha: TeamComparisonRow }) {
  const indice = linha.productivity_index;
  const largura = indice !== null ? Math.round(Math.min(1, Math.max(0, indice)) * 100) : 0;

  return (
    <tr className="border-b last:border-b-0">
      <td className="py-2 pr-2">
        <Link
          to={`/visao-geral?tag=${encodeURIComponent(linha.tag)}`}
          className="font-medium underline-offset-2 hover:underline"
        >
          {linha.tag}
        </Link>
        <span className="block text-[10px] text-muted-foreground">
          {hoursLabel(linha.seconds_active)} ativas · cobertura {formatPct(linha.classification_coverage)}
        </span>
      </td>

      <td className="py-2 pr-2 text-right tabular-nums">
        {linha.people}
        {!linha.meets_group_minimum && (
          <span
            className="ml-1 cursor-help text-[10px] text-muted-foreground"
            title={HINT_MINIMO}
            aria-label="Abaixo do mínimo de grupo"
          >
            ⚠
          </span>
        )}
      </td>

      {/* barra + valor + variação em PONTOS; a cor é a mesma em toda linha, de
          propósito: cor por desempenho viraria pódio */}
      <td className="py-2 pr-2">
        <div className="flex items-center gap-2">
          <div className="h-1.5 w-16 shrink-0 overflow-hidden rounded-full bg-muted" aria-hidden>
            <div
              className="h-full rounded-full"
              style={{ width: `${largura}%`, background: VIZ.produtivo }}
            />
          </div>
          <span className="font-display tabular-nums">{formatPct(indice)}</span>
          <PointsBadge current={indice} previous={linha.productivity_index_previous} />
        </div>
      </td>

      <CelulaMedia
        valor={
          linha.active_seconds_per_person_day !== null
            ? formatDuration(linha.active_seconds_per_person_day)
            : null
        }
        meetsMinimum={linha.meets_group_minimum}
      />
      <CelulaMedia
        valor={linha.idle_share !== null ? formatPct(linha.idle_share) : null}
        meetsMinimum={linha.meets_group_minimum}
      />
      <CelulaMedia
        valor={linha.capacity_used !== null ? formatPct(linha.capacity_used) : null}
        meetsMinimum={linha.meets_group_minimum}
        last
      />
    </tr>
  );
}

/**
 * Célula de média comparativa. Duas razões distintas para o traço, e o `title`
 * diz QUAL delas: mínimo de grupo (decisão 3) ou ausência de dado. Nunca 0%.
 */
function CelulaMedia({
  valor,
  meetsMinimum,
  last = false,
}: {
  valor: string | null;
  meetsMinimum: boolean;
  last?: boolean;
}) {
  const suprimido = valor === null;
  const motivo = !meetsMinimum
    ? HINT_MINIMO
    : "Sem base suficiente no período para calcular esta média.";

  return (
    <td className={cn("py-2 text-right tabular-nums", !last && "pr-2")}>
      {suprimido ? (
        <span className="cursor-help text-muted-foreground" title={motivo} aria-label={motivo}>
          –
        </span>
      ) : (
        valor
      )}
    </td>
  );
}
