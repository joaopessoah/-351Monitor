// =============================================================================
// "Composição por dia" — o bloco de colunas empilhadas da Visão Geral (F6,
// linha 3 do mockup aprovado).
//
// POR QUE ELE EXISTE: o donut da linha 2 responde "como o período se compôs",
// mas não "em que dia a coisa mudou". O gestor precisa das duas leituras — o
// total diz se há problema, a série por dia diz QUANDO ele começou. Esta é a
// única leitura da tela que localiza o problema no calendário.
//
// POR QUE NÃO HÁ CONSULTA NOVA: o array `days` já vem dentro de
// GET /dashboard/overview, que a tela carrega para os totais. O card recebe o
// MESMO UseQueryResult do bloco de composição: dois observadores do mesmo
// cache, zero requisição extra (o padrão de overviewData.ts).
//
// POR QUE O CALENDÁRIO É CONSTRUÍDO AQUI: o endpoint agrupa por summary_date, e
// dia sem nenhuma linha simplesmente NÃO VEM na resposta. Se o eixo fosse a
// resposta crua, um dia de máquina desligada desapareceria — e "não apareceu"
// leria como "não existiu". Aqui todo dia do intervalo tem lugar no eixo: com
// dado ele é barra, sem dado é lacuna declarada, com observação no rodapé.
//
// POR QUE NÃO HÁ ÍNDICE POR DIA: índice e cobertura vêm CALCULADOS do servidor
// (decisão 4 do spec) e o contrato de `days` não os traz. Recalcular aqui a
// partir dos segundos abriria a segunda fórmula que a decisão 4 existe para
// impedir — então este bloco fala só de horas, e o índice mora no anel.
//
// Ocioso entra na pilha com HACHURA, nunca com cor cheia de julgamento: é
// estado de máquina (sem teclado/mouse — reunião, chamada, leitura) e NUNCA
// conta como improdutivo.
// =============================================================================

import { useMemo, useState } from "react";
import type { EChartsOption } from "echarts";

import { formatDuration, localDateOf, parseHmToMinutes } from "@/lib/format";
import { eachDayInclusive } from "@/lib/period";
import type { BusinessHours, DashboardSummaryDay } from "@/lib/types";
import { EChart } from "@/components/charts/EChart";
import {
  BUCKET_LABELS,
  BlockCard,
  CHART_H,
  ChartSkeleton,
  EMPTY_GRAPHIC,
  HATCH_DECAL,
  HATCH_STYLE,
  IDLE_HINT,
  InlineError,
  LegendItem,
  LegendRow,
  RefetchAlert,
  VIZ,
  ViewToggle,
  hoursLabel,
} from "@/components/dashboard/overviewKit";
import type { useOverviewQuery } from "@/components/dashboard/overviewData";

/** Jornada diária declarada em segundos: business_hours da org, fallback 8 h. */
function jornadaSeconds(businessHours: BusinessHours | null | undefined): number {
  if (businessHours !== null && businessHours !== undefined) {
    const start = parseHmToMinutes(businessHours.start);
    const end = parseHmToMinutes(businessHours.end);
    if (start !== null && end !== null && end > start) return (end - start) * 60;
  }
  return 8 * 3600;
}

/** Uma coluna do gráfico: sempre um dia do calendário, com ou sem dado. */
interface DiaRow {
  date: string;
  /** Dia depois de hoje no fuso da org: sem barra, e não é zero. */
  futuro: boolean;
  /** Dia dentro do período que o agregado não devolveu: lacuna, não zero. */
  semDado: boolean;
  produtivo: number;
  neutro: number;
  improdutivo: number;
  semClassificacao: number;
  ocioso: number;
  ativo: number;
  ligado: number;
  incompleto: boolean;
}

/** "seg 09/06" quando cabe no eixo, "09/06" quando o período é longo. */
function diaLabel(date: string, curto: boolean): string {
  const [y, m, d] = date.split("-").map(Number);
  const dd = `${String(d).padStart(2, "0")}/${String(m).padStart(2, "0")}`;
  if (curto) return dd;
  const semana = new Intl.DateTimeFormat("pt-BR", { weekday: "short", timeZone: "UTC" })
    .format(new Date(Date.UTC(y, m - 1, d)))
    .replace(".", "");
  return `${semana} ${dd}`;
}

/** Horas com duas casas para o ECharts; "-" é o vazio dele, e não é zero. */
function barValue(row: DiaRow, seconds: number): number | string {
  if (row.futuro || row.semDado) return "-";
  return Math.round((seconds / 3600) * 100) / 100;
}

export function ComposicaoPorDiaCard({
  className,
  query,
  businessHours,
  timezone,
}: {
  className?: string;
  query: ReturnType<typeof useOverviewQuery>;
  businessHours: BusinessHours | null | undefined;
  timezone: string | null;
}) {
  const [view, setView] = useState<"chart" | "table">("chart");
  const data = query.data;

  // "Hoje" SEMPRE no fuso da organização: com o dia do navegador, quem opera em
  // outro fuso veria marcado como futuro um dia que já fechou.
  const hoje = timezone !== null ? localDateOf(new Date(), timezone) : null;

  const rows = useMemo<DiaRow[]>(() => {
    if (data === undefined) return [];
    // O intervalo é o RESOLVIDO PELO SERVIDOR (data.period), não o do cliente:
    // foi ele que gerou os números, e é dele que o eixo tem de falar.
    const porData = new Map<string, DashboardSummaryDay>(data.days.map((d) => [d.date, d]));
    return eachDayInclusive(data.period.from, data.period.to).map((date) => {
      const dia = porData.get(date);
      if (dia === undefined) {
        const futuro = hoje !== null && date > hoje;
        return {
          date,
          futuro,
          semDado: !futuro,
          produtivo: 0,
          neutro: 0,
          improdutivo: 0,
          semClassificacao: 0,
          ocioso: 0,
          ativo: 0,
          ligado: 0,
          incompleto: false,
        };
      }
      return {
        date,
        futuro: false,
        semDado: false,
        produtivo: dia.seconds_work_related,
        neutro: dia.seconds_neutral,
        improdutivo: dia.seconds_not_work_related,
        semClassificacao: dia.seconds_unclassified,
        ocioso: dia.seconds_idle,
        ativo: dia.seconds_active,
        ligado: dia.seconds_on,
        incompleto: dia.data_incomplete,
      };
    });
  }, [data, hoje]);

  const personCount = data?.totals.person_count ?? 0;
  // Referência = jornada declarada × pessoas do período. Com zero pessoa no
  // recorte a linha não significaria nada, então ela não é desenhada.
  const referenciaSec = personCount > 0 ? jornadaSeconds(businessHours) * personCount : 0;

  const diasSemDado = rows.filter((r) => r.semDado).length;
  const diasIncompletos = rows.filter((r) => r.incompleto).length;
  const curto = rows.length > 14;

  const option = useMemo<EChartsOption>(() => {
    const vazio = rows.every((r) => r.futuro || r.semDado || r.ligado === 0);
    const pilhaMaxH = rows.reduce((mx, r) => Math.max(mx, (r.ativo + r.ocioso) / 3600), 0);
    const refH = referenciaSec / 3600;
    // markLine não entra no cálculo automático do eixo: max explícito garante a
    // linha de referência sempre visível, com 5% de folga acima dela.
    const yMax = Math.max(Math.ceil(pilhaMaxH), refH > 0 ? Math.ceil(refH * 1.05) : 0, 1);
    // Rótulo em todo dia só cabe em período curto; acima disso, um a cada N.
    const passo = Math.max(1, Math.ceil(rows.length / 14));

    const baldes = [
      { name: BUCKET_LABELS.produtivo, color: VIZ.produtivo, hatch: false, get: (r: DiaRow) => r.produtivo },
      { name: BUCKET_LABELS.neutro, color: VIZ.neutro, hatch: false, get: (r: DiaRow) => r.neutro },
      { name: BUCKET_LABELS.improdutivo, color: VIZ.improdutivo, hatch: false, get: (r: DiaRow) => r.improdutivo },
      { name: BUCKET_LABELS.semClassificacao, color: VIZ.semClassificacao, hatch: false, get: (r: DiaRow) => r.semClassificacao },
      { name: BUCKET_LABELS.ocioso, color: VIZ.ocioso, hatch: true, get: (r: DiaRow) => r.ocioso },
    ];

    return {
      aria: { enabled: true, decal: { show: true } },
      animation: false,
      grid: { left: 42, right: 14, top: 20, bottom: 26 },
      tooltip: {
        trigger: "axis",
        formatter: (params: unknown): string => {
          const lista = params as Array<{ dataIndex: number }>;
          const i = lista[0]?.dataIndex;
          const row = i !== undefined ? rows[i] : undefined;
          if (row === undefined) return "";
          const titulo = `<strong>${diaLabel(row.date, false)}</strong>`;
          if (row.futuro) return [titulo, "Dia futuro — ainda não há o que medir"].join("<br/>");
          if (row.semDado) {
            return [titulo, "Sem dado (máquina desligada, folga ou fim de semana)"].join("<br/>");
          }
          const linhas = [
            titulo,
            `${BUCKET_LABELS.produtivo}: ${formatDuration(row.produtivo)}`,
            `${BUCKET_LABELS.neutro}: ${formatDuration(row.neutro)}`,
            `${BUCKET_LABELS.improdutivo}: ${formatDuration(row.improdutivo)}`,
            `${BUCKET_LABELS.semClassificacao}: ${formatDuration(row.semClassificacao)}`,
            `${BUCKET_LABELS.ocioso}: ${formatDuration(row.ocioso)}`,
            `Ativa: ${formatDuration(row.ativo)} · Ligada: ${formatDuration(row.ligado)}`,
          ];
          if (row.incompleto) linhas.push("Dados incompletos neste dia");
          return linhas.join("<br/>");
        },
      },
      xAxis: {
        type: "category",
        data: rows.map((r) => diaLabel(r.date, curto)),
        axisTick: { show: false },
        axisLine: { lineStyle: { color: VIZ.grid } },
        axisLabel: {
          color: VIZ.axisText,
          fontSize: 10,
          interval: (index: number) => index % passo === 0,
        },
      },
      yAxis: {
        type: "value",
        max: yMax,
        minInterval: 1,
        axisLabel: { formatter: "{value}h", color: VIZ.axisText, fontSize: 10 },
        splitLine: { lineStyle: { color: VIZ.grid } },
      },
      series: vazio
        ? []
        : baldes.map((b, indice) => ({
            name: b.name,
            type: "bar" as const,
            stack: "dia",
            barMaxWidth: 26,
            data: rows.map((r) => barValue(r, b.get(r))),
            itemStyle: {
              color: b.color,
              decal: b.hatch
                ? {
                    color: HATCH_DECAL.color,
                    dashArrayX: [...HATCH_DECAL.dashArrayX],
                    dashArrayY: [...HATCH_DECAL.dashArrayY],
                    rotation: HATCH_DECAL.rotation,
                  }
                : undefined,
            },
            // A linha de referência vive numa série só (a primeira): repetida
            // nas cinco, o ECharts desenharia cinco rótulos sobrepostos.
            markLine:
              indice === 0 && referenciaSec > 0
                ? {
                    silent: true,
                    symbol: "none",
                    lineStyle: { type: "dashed" as const, color: VIZ.axisText, width: 1 },
                    label: {
                      formatter: `jornada × ${personCount} ${personCount === 1 ? "pessoa" : "pessoas"} · ${hoursLabel(referenciaSec)}`,
                      position: "insideEndTop" as const,
                      color: VIZ.axisText,
                      fontSize: 10,
                    },
                    data: [{ yAxis: referenciaSec / 3600 }],
                  }
                : undefined,
          })),
      graphic: vazio ? EMPTY_GRAPHIC : undefined,
    };
  }, [rows, referenciaSec, personCount, curto]);

  const semDadoNota =
    diasSemDado > 0
      ? `${diasSemDado} ${diasSemDado === 1 ? "dia do período sem dado" : "dias do período sem dado"} (máquina desligada, folga ou fim de semana): a coluna fica vazia, não zerada.`
      : null;

  return (
    <BlockCard
      className={className}
      title="Composição por dia"
      hint="Horas ativas por classificação e ociosidade · tracejado: jornada declarada × pessoas"
      tools={<ViewToggle view={view} onChange={setView} />}
    >
      {query.isError && data !== undefined && <RefetchAlert onRetry={() => void query.refetch()} />}

      {query.isPending && data === undefined ? (
        <ChartSkeleton />
      ) : data === undefined ? (
        <InlineError
          message="Não foi possível carregar a composição por dia."
          onRetry={() => void query.refetch()}
        />
      ) : view === "chart" ? (
        <>
          <EChart option={option} height={CHART_H} />
          <LegendRow>
            <LegendItem color={VIZ.produtivo} label={BUCKET_LABELS.produtivo} />
            <LegendItem color={VIZ.neutro} label={BUCKET_LABELS.neutro} />
            <LegendItem color={VIZ.improdutivo} label={BUCKET_LABELS.improdutivo} />
            <LegendItem color={VIZ.semClassificacao} label={BUCKET_LABELS.semClassificacao} />
            <li className="inline-flex items-center gap-1.5" title={IDLE_HINT}>
              <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={HATCH_STYLE} />
              <span>{BUCKET_LABELS.ocioso}</span>
            </li>
            <li className="inline-flex items-center gap-1.5">
              <span
                aria-hidden
                className="h-0 w-3.5 shrink-0"
                style={{ borderTop: `1px dashed ${VIZ.axisText}` }}
              />
              <span>Jornada × pessoas</span>
            </li>
          </LegendRow>
          {semDadoNota !== null && (
            <p className="mt-2 text-[11px] leading-snug text-muted-foreground">{semDadoNota}</p>
          )}
        </>
      ) : (
        <div className="max-h-[268px] overflow-auto" style={{ minHeight: CHART_H }}>
          <table className="w-full text-xs" aria-label="Composição por dia, em tabela">
            <thead className="sticky top-0 bg-card">
              <tr className="border-b text-left text-[10px] uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="py-1.5 pr-2">Dia</th>
                <th scope="col" className="py-1.5 pr-2 text-right">{BUCKET_LABELS.produtivo}</th>
                <th scope="col" className="py-1.5 pr-2 text-right">{BUCKET_LABELS.neutro}</th>
                <th scope="col" className="py-1.5 pr-2 text-right">{BUCKET_LABELS.improdutivo}</th>
                <th scope="col" className="py-1.5 pr-2 text-right">Sem classif.</th>
                <th scope="col" className="py-1.5 pr-2 text-right">{BUCKET_LABELS.ocioso}</th>
                <th scope="col" className="py-1.5 pr-2 text-right">Ativa</th>
                <th scope="col" className="py-1.5 text-right">Ligada</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => {
                // Dia futuro e dia sem dado imprimem "–" em TODA coluna: zero
                // seria uma afirmação ("ninguém trabalhou") que o dado não faz.
                const vazio = r.futuro || r.semDado;
                const cel = (seconds: number): string => (vazio ? "–" : formatDuration(seconds));
                return (
                  <tr key={r.date} className="border-b last:border-b-0">
                    <th scope="row" className="py-1 pr-2 text-left font-normal">
                      {diaLabel(r.date, false)}
                      {r.incompleto && (
                        <span className="text-muted-foreground" title="Dados incompletos neste dia">
                          {" *"}
                        </span>
                      )}
                      {r.futuro && <span className="text-muted-foreground"> (futuro)</span>}
                      {r.semDado && <span className="text-muted-foreground"> (sem dado)</span>}
                    </th>
                    <td className="py-1 pr-2 text-right tabular-nums">{cel(r.produtivo)}</td>
                    <td className="py-1 pr-2 text-right tabular-nums">{cel(r.neutro)}</td>
                    <td className="py-1 pr-2 text-right tabular-nums">{cel(r.improdutivo)}</td>
                    <td className="py-1 pr-2 text-right tabular-nums">{cel(r.semClassificacao)}</td>
                    <td className="py-1 pr-2 text-right tabular-nums">{cel(r.ocioso)}</td>
                    <td className="py-1 pr-2 text-right tabular-nums">{cel(r.ativo)}</td>
                    <td className="py-1 text-right tabular-nums">{cel(r.ligado)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          {diasIncompletos > 0 && (
            <p className="mt-2 text-[11px] text-muted-foreground">
              * dia com coleta incompleta (agente offline parte do tempo).
            </p>
          )}
        </div>
      )}
    </BlockCard>
  );
}
