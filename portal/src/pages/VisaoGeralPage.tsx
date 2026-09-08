// =============================================================================
// Visão Geral (F6) — a tela macro que o gestor abre todo dia.
//
// O QUE MUDOU e POR QUÊ (spec de 07/09/2026, seções 1.1 e 3):
// a tela antiga respondia "quem está online agora" com cinco cards de contagem
// de dispositivos, e três dos cinco eram estado técnico de máquina. A pergunta
// do gestor é outra: "quão produtiva foi a equipe no período, e o que eu preciso
// fazer". Então a presença ao vivo virou uma FAIXA no topo (pedido explícito do
// dono: "Agora" é o primeiro cabeçalho da tela) e o corpo passou a ser
// indicador de produtividade.
//
// Blocos que saíram daqui e para onde foram:
//  - faixa de saúde da frota  → Administração › Dispositivos (é da frota inteira,
//    não de nenhuma equipe, e é problema de TI);
//  - medidor de uso do plano  → Cobrança (é do Proprietário);
//  - checklist de onboarding  → Configurações (é do Admin, e só até concluir);
//  - tabela "Equipe agora"    → Linha do Tempo, nível do dia (o link da faixa
//    "Agora" leva até lá).
// Nada foi apagado do produto: o que saiu tem dono em outra tela.
//
// PERÍODO E EQUIPE são globais e vivem na URL (?periodo=&de=&ate=&tag=), então o
// link reproduz exatamente o recorte visível. Os três presets ganharam o quarto
// botão do mockup - intervalo livre de até MAX_PERIOD_DAYS dias, validado no
// cliente para o gestor não descobrir o teto por um 400 -, e TODO bloco desta
// tela lê o mesmo período: nenhum card tem janela própria. O ÍNDICE e a
// COBERTURA vêm calculados do servidor (decisão 4 do spec) e esta tela apenas
// formata: fórmula única, sem chance de a tela e o relatório divergirem.
//
// "Resumo em PDF" e "Enviar por e-mail" aparecem DESABILITADOS: o mockup os
// promete, mas o PDF no servidor e o digest por e-mail são a fase seguinte, e
// um botão que responde 404 custa mais confiança do que um botão que avisa.
// =============================================================================

import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import type { EChartsOption } from "echarts";
import { ArrowRight, Info, Tags } from "lucide-react";

import { api } from "@/lib/api";
import { formatDuration } from "@/lib/format";
import type { ActivityByHourItem, MeResponse, OverviewResponse } from "@/lib/types";
import {
  PERIOD_CODEC,
  comparisonLabel,
  formatPct,
  resolvePeriod,
  type ResolvedPeriod,
} from "@/lib/period";
import { localDateOf } from "@/lib/format";
import { useUrlState } from "@/lib/useUrlState";
import { cn } from "@/lib/utils";
import { Card } from "@/components/ui/card";
import { EChart } from "@/components/charts/EChart";
import { TAG_CODEC, TeamTagSelect, useTeamTags } from "@/components/filters/TeamTagSelect";
import { AcoesDoCabecalho } from "@/components/dashboard/AcoesDoCabecalho";
import { AplicativosDoPeriodoCard } from "@/components/dashboard/AplicativosDoPeriodoCard";
import { ComposicaoPorDiaCard } from "@/components/dashboard/ComposicaoPorDiaCard";
import { PeriodoSelector } from "@/components/dashboard/PeriodoSelector";
import { AgoraFaixa } from "@/components/dashboard/AgoraFaixa";
import { KpisRow } from "@/components/dashboard/KpisRow";
import { AlertasCard } from "@/components/dashboard/AlertasCard";
import { EquipesLadoALado, useEquipesQuery } from "@/components/dashboard/EquipesLadoALado";
import { ResumoDoPeriodo } from "@/components/dashboard/ResumoDoPeriodo";
import { PorQueOIndiceMudou } from "@/components/dashboard/PorQueOIndiceMudou";
import {
  useActivityByHourQuery,
  useIndexExplainedQuery,
  useOverviewQuery,
  previousPeriodOf,
} from "@/components/dashboard/overviewData";
import {
  BUCKET_LABELS,
  BlockCard,
  CHART_H,
  ChartSkeleton,
  EMPTY_GRAPHIC,
  FRAMING,
  HATCH_DECAL,
  HATCH_STYLE,
  IDLE_HINT,
  InlineError,
  LegendItem,
  LegendRow,
  VIZ,
  ViewToggle,
  hoursLabel,
} from "@/components/dashboard/overviewKit";

export function VisaoGeralPage() {
  const [period, setPeriod] = useUrlState(PERIOD_CODEC);
  const [tag, setTag] = useUrlState(TAG_CODEC);
  const { tags } = useTeamTags();

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const organization = meQuery.data?.organization;
  const timezone = organization?.timezone ?? null;

  // O período SÓ resolve com o fuso da organização em mãos: sem isso o dia do
  // navegador vazaria para o recorte de quem viaja ou opera em outro fuso.
  const resolved = useMemo(() => resolvePeriod(period, timezone), [period, timezone]);

  // O "hoje" da ORGANIZAÇÃO é o teto dos campos de data do seletor: um gestor
  // em outro fuso não deve poder pedir um dia que a operação ainda não viveu.
  const hoje = useMemo(
    () => (timezone !== null ? localDateOf(new Date(), timezone) : null),
    [timezone],
  );

  const overview = useOverviewQuery(resolved, tag, true);
  const totals = overview.data?.totals;
  const previous = overview.data?.previous ?? null;

  // Decomposição do índice: consulta própria, porque varre daily_app_usage nos
  // DOIS períodos e o overview não. Mesmo recorte global, então invalida junto.
  const indiceExplicado = useIndexExplainedQuery(resolved, tag);

  // A comparação de equipes é UMA consulta (Promise.all por etiqueta dentro de
  // um useQuery só). Declarada aqui além de dentro do EquipesLadoALado porque o
  // Resumo do Período também precisa das linhas: mesma queryKey, mesmo cache,
  // dois observadores e ZERO requisição a mais.
  const equipes = useEquipesQuery(resolved, tags, organization?.business_hours);

  const header = (
    <div className="space-y-4">
      <AgoraFaixa tag={tag} />

      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold tracking-tight">Visão Geral</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            {resolved !== null ? (
              <>
                {resolved.label}
                {totals !== undefined && (
                  <>
                    {" · "}
                    {totals.person_count} {totals.person_count === 1 ? "colaborador" : "colaboradores"}
                  </>
                )}
                {" · "}
                {comparisonLabel(resolved.preset)}
              </>
            ) : (
              "Carregando o fuso da organização…"
            )}
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          {/* Presets + intervalo livre: um grupo segmentado só, estado na URL. */}
          <PeriodoSelector
            period={period}
            onChange={setPeriod}
            today={hoje}
            resolvedFrom={resolved?.from}
            resolvedTo={resolved?.to}
          />
          <TeamTagSelect tags={tags} value={tag} onChange={setTag} />
          {/* Exportações do mockup, desabilitadas com o motivo no title: PDF no
              servidor e digest por e-mail são a fase seguinte. */}
          <AcoesDoCabecalho />
        </div>
      </div>
    </div>
  );

  if (meQuery.isError && meQuery.data === undefined) {
    return (
      <div className="space-y-6">
        {header}
        <InlineError
          message="Não foi possível carregar a organização, e sem o fuso dela nenhum período é confiável."
          onRetry={() => void meQuery.refetch()}
        />
      </div>
    );
  }

  return (
    <div className="space-y-5">
      {header}

      {/* Linha 1 — os seis KPIs com minigráfico de 12 dias. */}
      <KpisRow period={resolved} tag={tag} />

      {/* Linha 2 — composição, atividade por hora, índice/meta/cobertura. */}
      <div className="grid gap-4 lg:grid-cols-12">
        <ComposicaoCard
          className="lg:col-span-4"
          query={overview}
          personDays={totals?.person_days ?? 0}
        />
        <AtividadePorHoraCard className="lg:col-span-5" period={resolved} tag={tag} />
        <IndiceMetaCard
          className="lg:col-span-3"
          data={overview.data}
          isPending={overview.isPending}
          onRetry={() => void overview.refetch()}
          hasError={overview.isError}
        />
      </div>

      {/* Linha 3 — composição por dia · aplicativos · resumo, na proporção do
          mockup aprovado (5/4/3).

          O que saiu daqui: o WeeklyChartsRow, que resolvia os dois primeiros
          com janela PRÓPRIA (semana atual / semana anterior, com seletor só
          dele). Numa tela cujo período é global e vive na URL, um bloco com
          recorte particular é uma mentira de leitura — o gestor escolhia "Este
          mês" no cabeçalho e os gráficos continuavam respondendo pela semana. O
          arquivo continua no repositório porque a comparação semana-a-semana
          que ele faz ainda serve a outra tela; aqui ele não é mais usado.

          A composição por dia NÃO abre consulta: recebe o mesmo UseQueryResult
          do overview (o array `days` já vem lá dentro), dois observadores do
          mesmo cache. O RESUMO também não: sai dos agregados que o overview e a
          comparação de equipes já trouxeram, e o cartão desaparece inteiro se
          nenhuma frase tiver número real para dizer. */}
      <div className="grid gap-4 lg:grid-cols-12">
        <ComposicaoPorDiaCard
          className="lg:col-span-5"
          query={overview}
          businessHours={organization?.business_hours}
          timezone={timezone}
        />
        <AplicativosDoPeriodoCard className="lg:col-span-4" period={resolved} tag={tag} />
        <ResumoDoPeriodo
          className="lg:col-span-3"
          data={overview.data}
          equipes={equipes.data ?? []}
        />
      </div>

      {/* Linha 3.5 — POR QUE O ÍNDICE MUDOU. Vem logo depois do Resumo do
          Período de propósito: o Resumo diz em UMA frase que o índice mudou e
          qual equipe puxou; este painel abre a mesma afirmação em números que
          somam. Um responde "o quê", o outro responde "de onde", e ler os dois
          na ordem é a diferença entre desconfiar do número e conseguir agir
          sobre ele. Largura cheia porque as barras divergentes precisam de
          trilho: espremido em terço de grade, a metade negativa some. */}
      <PorQueOIndiceMudou data={indiceExplicado.data} isPending={indiceExplicado.isPending} />

      {/* Linha 4 — ALERTAS de gestão (motor de regras no worker, GET /alerts)
          somados às pendências de administração que hoje só existem no sino, e
          o comparativo de EQUIPES lado a lado com o mínimo de grupo da
          decisão 3. Os dois lado a lado porque respondem à mesma pergunta em
          dois níveis: "o que exige decisão" e "onde a diferença está". */}
      <div className="grid gap-4 lg:grid-cols-12">
        <AlertasCard className="lg:col-span-5" />
        <EquipesLadoALado
          className="lg:col-span-7"
          period={resolved}
          tags={tags}
          businessHours={organization?.business_hours}
        />
      </div>

      <p className="text-xs text-muted-foreground">
        {FRAMING} Estados de máquina (ativo, ocioso, bloqueado) são fisiológicos e não recebem
        julgamento: <span title={IDLE_HINT}>ocioso não é improdutivo</span>.
      </p>

      {previous !== null && previous.seconds_active === 0 && totals !== undefined && totals.seconds_active > 0 && (
        <p className="text-xs text-muted-foreground">
          O período anterior não tem dado, então as variações aparecem como “sem base”.
        </p>
      )}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Composição das horas com a máquina ligada (donut com o total ao centro)
// -----------------------------------------------------------------------------

/**
 * Os SEIS baldes do tempo ligado: os quatro de classificação (que somam o tempo
 * ativo) mais ocioso e bloqueado. O total no miolo é a leitura que o gestor
 * procura primeiro, e a legenda dá horas e percentual de cada balde.
 *
 * Ocioso vai com hachura (decal) e bloqueado só com contorno: redundância
 * não-cromática, e o âmbar continua reservado para "improdutivo" — a colisão de
 * significado apontada na seção 1.2 do spec.
 */
function ComposicaoCard({
  className,
  query,
  personDays,
}: {
  className?: string;
  query: ReturnType<typeof useOverviewQuery>;
  personDays: number;
}) {
  const [view, setView] = useState<"chart" | "table">("chart");
  const totals = query.data?.totals;

  const buckets = useMemo(() => {
    if (totals === undefined) return [];
    return [
      { key: "produtivo", label: BUCKET_LABELS.produtivo, value: totals.seconds_work_related, color: VIZ.produtivo },
      { key: "neutro", label: BUCKET_LABELS.neutro, value: totals.seconds_neutral, color: VIZ.neutro },
      { key: "improdutivo", label: BUCKET_LABELS.improdutivo, value: totals.seconds_not_work_related, color: VIZ.improdutivo },
      { key: "semClassificacao", label: BUCKET_LABELS.semClassificacao, value: totals.seconds_unclassified, color: VIZ.semClassificacao },
      { key: "ocioso", label: BUCKET_LABELS.ocioso, value: totals.seconds_idle, color: VIZ.ocioso, hatch: true },
      { key: "bloqueado", label: BUCKET_LABELS.bloqueado, value: totals.seconds_locked, color: VIZ.bloqueado, outline: true },
    ];
  }, [totals]);

  const total = totals?.seconds_on ?? 0;

  const option = useMemo<EChartsOption>(() => {
    const vazio = total === 0;
    return {
      aria: { enabled: true, decal: { show: true } },
      animation: false,
      tooltip: {
        trigger: "item",
        formatter: (params: unknown) => {
          const p = params as { name: string; value: number };
          const pctDoLigado = total > 0 ? Math.round((p.value / total) * 100) : 0;
          return `<strong>${p.name}</strong><br/>${formatDuration(p.value)}<br/>${pctDoLigado}% do tempo ligado`;
        },
      },
      series: vazio
        ? []
        : [
            {
              type: "pie",
              radius: ["62%", "88%"],
              center: ["50%", "50%"],
              avoidLabelOverlap: false,
              itemStyle: { borderColor: "transparent", borderWidth: 2 },
              label: { show: false },
              labelLine: { show: false },
              data: buckets
                .filter((b) => b.value > 0)
                .map((b) => ({
                  name: b.label,
                  value: b.value,
                  itemStyle: {
                    color: b.outline === true ? "transparent" : b.color,
                    borderColor: b.outline === true ? VIZ.contorno : "transparent",
                    borderWidth: b.outline === true ? 2 : 2,
                    decal:
                      b.hatch === true
                        ? {
                            color: HATCH_DECAL.color,
                            dashArrayX: [...HATCH_DECAL.dashArrayX],
                            dashArrayY: [...HATCH_DECAL.dashArrayY],
                            rotation: HATCH_DECAL.rotation,
                          }
                        : undefined,
                  },
                })),
            },
          ],
      graphic: vazio
        ? EMPTY_GRAPHIC
        : [
            {
              type: "text",
              left: "center",
              top: "44%",
              silent: true,
              style: {
                text: hoursLabel(total),
                fill: VIZ.ink,
                font: "600 22px 'Space Grotesk', sans-serif",
                textAlign: "center",
              },
            },
            {
              type: "text",
              left: "center",
              top: "58%",
              silent: true,
              style: {
                text: "máquina ligada",
                fill: VIZ.axisText,
                font: "11px 'Open Sans', sans-serif",
                textAlign: "center",
              },
            },
          ],
    };
  }, [buckets, total]);

  const porPessoaDia = personDays > 0 ? formatDuration(total / personDays) : null;

  return (
    <BlockCard
      className={className}
      title="Composição das horas ligadas"
      hint={
        porPessoaDia !== null
          ? `${hoursLabel(total)} no período · ${porPessoaDia} por pessoa por dia`
          : "Ligada = ativa + ociosa + bloqueada"
      }
      tools={<ViewToggle view={view} onChange={setView} />}
    >
      {query.isPending && totals === undefined ? (
        <ChartSkeleton />
      ) : totals === undefined ? (
        <InlineError
          message="Não foi possível carregar a composição do período."
          onRetry={() => void query.refetch()}
        />
      ) : view === "chart" ? (
        <>
          <EChart option={option} height={CHART_H} />
          <ul className="mt-3 space-y-1 text-xs">
            {buckets.map((b) => (
              <li key={b.key} className="grid grid-cols-[12px_1fr_auto_auto] items-center gap-2">
                <span
                  aria-hidden
                  className={cn("h-3 w-3 rounded-sm", b.outline === true && "border-2")}
                  style={
                    b.hatch === true
                      ? HATCH_STYLE
                      : b.outline === true
                        ? { borderColor: VIZ.contorno }
                        : { background: b.color }
                  }
                />
                <span className="truncate text-muted-foreground">{b.label}</span>
                <span className="font-display tabular-nums">{formatDuration(b.value)}</span>
                <span className="w-10 text-right tabular-nums text-muted-foreground">
                  {total > 0 ? `${Math.round((b.value / total) * 100)}%` : "–"}
                </span>
              </li>
            ))}
          </ul>
        </>
      ) : (
        <div className="overflow-x-auto" style={{ minHeight: CHART_H }}>
          <table className="w-full text-xs" aria-label="Composição das horas ligadas, em tabela">
            <thead>
              <tr className="border-b text-left text-[10px] uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="py-1.5 pr-2">Balde</th>
                <th scope="col" className="py-1.5 pr-2 text-right">Horas</th>
                <th scope="col" className="py-1.5 text-right">Do tempo ligado</th>
              </tr>
            </thead>
            <tbody>
              {buckets.map((b) => (
                <tr key={b.key} className="border-b last:border-b-0">
                  <td className="py-1.5 pr-2">{b.label}</td>
                  <td className="py-1.5 pr-2 text-right tabular-nums">{formatDuration(b.value)}</td>
                  <td className="py-1.5 text-right tabular-nums">
                    {total > 0 ? `${Math.round((b.value / total) * 100)}%` : "–"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </BlockCard>
  );
}

// -----------------------------------------------------------------------------
// Atividade ao longo do dia
// -----------------------------------------------------------------------------

/** Array vazio compartilhado: mantém a identidade estável entre renders. */
const SEM_HORAS: readonly ActivityByHourItem[] = [];

/**
 * Pessoas ativas por hora, média do período, com a linha fina do período
 * anterior atrás. É a resposta visual para "a que horas minha operação
 * realmente acontece" — a pergunta que o mockup do site promete desde agosto e
 * que o produto não tinha, porque o agregado por hora não existia.
 *
 * O denominador da média são os DIAS COM DADO, não os dias do calendário: com
 * três dias de dado numa semana, dividir por sete faria a operação parecer
 * metade do que é.
 */
function AtividadePorHoraCard({
  className,
  period,
  tag,
}: {
  className?: string;
  period: ResolvedPeriod | null;
  tag: string | null;
}) {
  const [view, setView] = useState<"chart" | "table">("chart");
  const atual = useActivityByHourQuery(period, tag);
  const anterior = useActivityByHourQuery(previousPeriodOf(period), tag);

  // CAUSA RAIZ do cartão em branco (corrigida em 07/09/2026, ver o comentário
  // longo no topo de components/charts/EChart.tsx): este é o ÚNICO cartão da
  // Visão Geral feito só de séries `type: "line"`, e o registro modular do
  // ECharts no wrapper não incluía o LineChart. O ECharts descartava as duas
  // séries em silêncio (só um console.error), sobrava o eixo X sozinho, e o
  // estado de vazio não entrava porque, para o cartão, havia dado. O endpoint
  // sempre esteve certo. A correção é o `use([LineChart])` no wrapper.

  // Referência estável enquanto o período anterior ainda carrega: `?? []` novo a
  // cada render invalidaria o useMemo abaixo e refaria o setOption à toa.
  const horas = atual.data?.hours ?? SEM_HORAS;
  const horasAnteriores = anterior.data?.hours ?? SEM_HORAS;

  const option = useMemo<EChartsOption>(() => {
    const serie = horas.map((h) => h.avg_people_active ?? 0);
    const serieAnterior = horasAnteriores.map((h) => h.avg_people_active ?? 0);
    const vazio = serie.every((v) => v === 0);

    return {
      aria: { enabled: true },
      animation: false,
      grid: { left: 34, right: 12, top: 22, bottom: 24 },
      tooltip: {
        trigger: "axis",
        formatter: (params: unknown) => {
          const lista = params as Array<{ dataIndex: number }>;
          const i = lista[0]?.dataIndex ?? 0;
          const hora = horas[i];
          if (hora === undefined) return "";
          const linhas = [
            `<strong>${String(i).padStart(2, "0")}h</strong>`,
            `Pessoas ativas (média): ${(hora.avg_people_active ?? 0).toFixed(1)}`,
            `Ativo: ${formatDuration(hora.seconds_active)}`,
            `Ocioso: ${formatDuration(hora.seconds_idle)}`,
          ];
          const ant = horasAnteriores[i];
          if (ant !== undefined) {
            linhas.push(`Período anterior: ${(ant.avg_people_active ?? 0).toFixed(1)}`);
          }
          return linhas.join("<br/>");
        },
      },
      xAxis: {
        type: "category",
        data: horas.map((h) => `${String(h.hour).padStart(2, "0")}h`),
        axisTick: { show: false },
        axisLine: { lineStyle: { color: VIZ.grid } },
        axisLabel: {
          color: VIZ.axisText,
          fontSize: 10,
          interval: (index: number, _value: string) => index % 3 === 0,
        },
      },
      yAxis: {
        type: "value",
        minInterval: 1,
        axisLabel: { color: VIZ.axisText, fontSize: 10 },
        splitLine: { lineStyle: { color: VIZ.grid } },
      },
      series: vazio
        ? []
        : [
            {
              name: "Período anterior",
              type: "line",
              smooth: true,
              symbol: "none",
              silent: true,
              z: 1,
              data: serieAnterior,
              lineStyle: { color: VIZ.axisText, width: 1.2, opacity: 0.6 },
            },
            {
              name: "Pessoas ativas",
              type: "line",
              smooth: true,
              symbol: "none",
              z: 2,
              data: serie,
              lineStyle: { color: VIZ.produtivo, width: 2 },
              areaStyle: { color: VIZ.produtivo, opacity: 0.12 },
            },
          ],
      graphic: vazio ? EMPTY_GRAPHIC : undefined,
    };
  }, [horas, horasAnteriores]);

  return (
    <BlockCard
      className={className}
      title="Atividade ao longo do dia"
      hint={
        atual.data !== undefined
          ? `Pessoas ativas por hora, média de ${atual.data.days_with_data} ${atual.data.days_with_data === 1 ? "dia com dado" : "dias com dado"} · linha fina: período anterior`
          : "Pessoas ativas por hora, média do período"
      }
      tools={<ViewToggle view={view} onChange={setView} />}
    >
      {atual.isPending && atual.data === undefined ? (
        <ChartSkeleton />
      ) : atual.data === undefined ? (
        <InlineError
          message="Não foi possível carregar a atividade por hora."
          onRetry={() => void atual.refetch()}
        />
      ) : view === "chart" ? (
        <>
          <EChart option={option} height={CHART_H} />
          <LegendRow>
            <LegendItem color={VIZ.produtivo} label="Pessoas ativas (média)" />
            <LegendItem color={VIZ.axisText} label="Período anterior" />
          </LegendRow>
        </>
      ) : (
        <div className="max-h-[216px] overflow-y-auto" style={{ minHeight: CHART_H }}>
          <table className="w-full text-xs" aria-label="Atividade por hora, em tabela">
            <thead className="sticky top-0 bg-card">
              <tr className="border-b text-left text-[10px] uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="py-1.5 pr-2">Hora</th>
                <th scope="col" className="py-1.5 pr-2 text-right">Pessoas ativas</th>
                <th scope="col" className="py-1.5 pr-2 text-right">Ativo</th>
                <th scope="col" className="py-1.5 text-right">Ocioso</th>
              </tr>
            </thead>
            <tbody>
              {horas.map((h) => (
                <tr key={h.hour} className="border-b last:border-b-0">
                  <td className="py-1 pr-2 tabular-nums">{String(h.hour).padStart(2, "0")}h</td>
                  <td className="py-1 pr-2 text-right tabular-nums">
                    {h.avg_people_active === null ? "–" : h.avg_people_active.toFixed(1)}
                  </td>
                  <td className="py-1 pr-2 text-right tabular-nums">{formatDuration(h.seconds_active)}</td>
                  <td className="py-1 text-right tabular-nums">{formatDuration(h.seconds_idle)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </BlockCard>
  );
}

// -----------------------------------------------------------------------------
// Índice, meta e cobertura da classificação
// -----------------------------------------------------------------------------

/**
 * O anel do índice com a marca da meta, e logo abaixo a COBERTURA — os dois
 * nunca aparecem separados (decisão 4 do spec). Índice alto com cobertura baixa
 * não é operação produtiva: é curadoria incompleta, e a tela precisa dizer isso
 * na mesma olhada.
 *
 * Desenho em SVG, não em ECharts: é um anel estático de 132 px, e carregar um
 * gráfico inteiro para dois arcos custaria mais do que entrega.
 */
function IndiceMetaCard({
  className,
  data,
  isPending,
  hasError,
  onRetry,
}: {
  className?: string;
  data: OverviewResponse | undefined;
  isPending: boolean;
  hasError: boolean;
  onRetry: () => void;
}) {
  const totals = data?.totals;
  const indice = totals?.productivity_index ?? null;
  const cobertura = totals?.classification_coverage ?? null;
  const metaPct = data?.goals.work_related_pct ?? null;

  const R = 54;
  const CIRC = 2 * Math.PI * R;
  const preenchido = indice !== null ? Math.min(1, Math.max(0, indice)) * CIRC : 0;
  const metaAngulo = metaPct !== null ? -Math.PI / 2 + (metaPct / 100) * 2 * Math.PI : null;

  const semClassificacao = totals !== undefined ? totals.seconds_unclassified : 0;

  return (
    <BlockCard
      className={className}
      title="Índice, meta e cobertura"
      hint="A saúde da medição, não só o número"
    >
      {isPending && data === undefined ? (
        <ChartSkeleton height={180} />
      ) : data === undefined ? (
        <InlineError
          message={
            hasError
              ? "Não foi possível carregar o índice do período."
              : "Sem dado no período para calcular o índice."
          }
          onRetry={onRetry}
        />
      ) : (
        <div className="space-y-3">
          <div className="flex items-center gap-4">
            <svg
              viewBox="0 0 132 132"
              className="h-[124px] w-[124px] shrink-0"
              role="img"
              aria-label={
                indice === null
                  ? "Índice de produtividade sem dado no período"
                  : `Índice de produtividade em ${formatPct(indice)}${metaPct !== null ? `, meta de ${metaPct}%` : ""}`
              }
            >
              <circle cx="66" cy="66" r={R} fill="none" stroke={VIZ.grid} strokeWidth="12" />
              {indice !== null && (
                <circle
                  cx="66"
                  cy="66"
                  r={R}
                  fill="none"
                  stroke={VIZ.produtivo}
                  strokeWidth="12"
                  strokeLinecap="round"
                  strokeDasharray={`${preenchido} ${CIRC - preenchido}`}
                  transform="rotate(-90 66 66)"
                />
              )}
              {metaAngulo !== null && (
                <line
                  x1={66 + (R - 12) * Math.cos(metaAngulo)}
                  y1={66 + (R - 12) * Math.sin(metaAngulo)}
                  x2={66 + (R + 12) * Math.cos(metaAngulo)}
                  y2={66 + (R + 12) * Math.sin(metaAngulo)}
                  stroke={VIZ.ink}
                  strokeWidth="2"
                />
              )}
              <text
                x="66"
                y="70"
                textAnchor="middle"
                fill={VIZ.ink}
                style={{ font: "600 24px 'Space Grotesk', sans-serif", letterSpacing: "-0.02em" }}
              >
                {formatPct(indice)}
              </text>
              <text
                x="66"
                y="86"
                textAnchor="middle"
                fill={VIZ.axisText}
                style={{ font: "10px 'Open Sans', sans-serif" }}
              >
                índice
              </text>
            </svg>

            <div className="min-w-0 space-y-1.5 text-xs">
              <p className="text-muted-foreground">
                Meta da equipe{" "}
                <span className="font-display font-semibold text-foreground">
                  {metaPct !== null ? `${metaPct}%` : "não definida"}
                </span>
              </p>
              <div>
                <p className="text-muted-foreground">
                  Cobertura da classificação{" "}
                  <span className="font-display font-semibold text-foreground">
                    {formatPct(cobertura)}
                  </span>
                </p>
                <div className="mt-1 h-1.5 w-full overflow-hidden rounded-full bg-muted">
                  <div
                    className="h-full rounded-full"
                    style={{
                      width: cobertura !== null ? `${Math.round(cobertura * 100)}%` : "0%",
                      background: VIZ.neutro,
                    }}
                  />
                </div>
                <p className="mt-1 text-muted-foreground">
                  {semClassificacao > 0
                    ? `${formatDuration(semClassificacao)} sem classificar`
                    : "Tudo classificado no período"}
                </p>
              </div>
            </div>
          </div>

          <p className="border-l-2 border-border pl-2 text-[11px] leading-snug text-muted-foreground">
            Índice = tempo em apps <span className="text-foreground">produtivos</span> ÷ tempo ativo{" "}
            <span className="text-foreground">classificado</span>. Cobertura = tempo ativo com
            categoria ÷ tempo ativo. {FRAMING}
          </p>

          {semClassificacao > 0 && (
            <Link
              to="/configuracoes/categorias"
              className={cn(
                "inline-flex h-9 w-full items-center justify-center gap-2 rounded-md",
                "bg-primary px-3 text-xs font-semibold text-primary-foreground transition-colors",
                "hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2",
                "focus-visible:ring-ring focus-visible:ring-offset-2",
              )}
            >
              <Tags className="h-4 w-4" aria-hidden />
              Classificar aplicativos
              <ArrowRight className="h-4 w-4" aria-hidden />
            </Link>
          )}

          {indice === null && (
            <Card className="flex items-start gap-2 p-3 text-xs text-muted-foreground">
              <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
              <span>
                Sem tempo ativo classificado no período, o índice não existe. Ele aparece assim que
                houver aplicativo classificado com uso registrado.
              </span>
            </Card>
          )}
        </div>
      )}
    </BlockCard>
  );
}
