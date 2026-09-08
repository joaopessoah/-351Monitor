// =============================================================================
// Linha de SEIS KPIs compactos da Visão Geral (F6), na densidade da referência
// aprovada: rótulo, valor grande, variação vs período anterior e um minigráfico
// de 12 dias desenhado à MÃO em SVG.
//
// Por que SVG e não ECharts aqui: são seis gráficos numa linha só, sempre
// visíveis; seis instâncias de ECharts (canvas + ResizeObserver cada) custam
// bem mais que seis <svg> de vinte elementos. ECharts fica para os blocos
// grandes, onde tooltip e eixos valem o preço.
//
// A série de 12 dias vem de UMA consulta extra ao /dashboard/overview (janela
// de 12 dias terminando no fim do período) - não de doze consultas.
//
// REGRA INVIOLÁVEL: índice e cobertura são do SERVIDOR. O número grande do
// índice é `totals.productivity_index` formatado, nunca recalculado. A
// tendência do minigráfico é a única coisa derivada dos baldes do dia, e por
// isso NÃO exibe número nenhum: é forma, não medida (o servidor não publica
// índice por dia).
// =============================================================================

import { useMemo } from "react";
import type { ReactNode } from "react";
import { Info } from "lucide-react";
import {
  addDays,
  ddmm,
  formatDuration,
  weekdayShort,
} from "@/lib/format";
import { formatPct } from "@/lib/period";
import type { ResolvedPeriod } from "@/lib/period";
import { genericErrorMessage } from "@/lib/messages";
import type { DashboardSummaryDay } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { DeltaBadge } from "@/components/dashboard/comparison";
import {
  MINI_SERIES_DAYS,
  miniSeriesPeriodOf,
  previousPeriodOf,
  useForaDoHorarioQuery,
  useOverviewQuery,
} from "@/components/dashboard/overviewData";
import { IDLE_HINT, PointsBadge, VIZ, hoursLabel } from "@/components/dashboard/overviewKit";
import { foraDoHorarioEmptyState } from "@/components/reports/ForaDoHorario";

/** Geometria do minigráfico (viewBox esticado na largura do card). */
const MINI_W = 100;
const MINI_H = 34;

/** Um ponto da série de 12 dias - `missing` distingue "sem dado" de zero. */
interface MiniPoint {
  date: string;
  value: number | null;
  missing: boolean;
}

export function KpisRow({ period, tag }: { period: ResolvedPeriod | null; tag: string | null }) {
  const overviewQuery = useOverviewQuery(period, tag, true);
  const miniQuery = useOverviewQuery(miniSeriesPeriodOf(period), tag, false);
  const prevPeriod = previousPeriodOf(period);
  const foraQuery = useForaDoHorarioQuery(period, tag);
  const foraPrevQuery = useForaDoHorarioQuery(prevPeriod, tag);

  const data = overviewQuery.data;
  const totals = data?.totals;
  const previous = data?.previous ?? null;
  const goals = data?.goals;

  // Doze slots SEMPRE presentes: o endpoint não devolve dia sem linha, e um
  // buraco na série tem de aparecer como ausência, não como zero.
  const miniDays = useMemo<Array<{ date: string; day: DashboardSummaryDay | undefined }>>(() => {
    const end = period?.to ?? null;
    if (end === null) return [];
    const byDate = new Map((miniQuery.data?.days ?? []).map((d) => [d.date, d]));
    return Array.from({ length: MINI_SERIES_DAYS }, (_, i) => {
      const date = addDays(end, -(MINI_SERIES_DAYS - 1 - i));
      return { date, day: byDate.get(date) };
    });
  }, [miniQuery.data, period?.to]);

  function serie(pick: (day: DashboardSummaryDay) => number): MiniPoint[] {
    return miniDays.map(({ date, day }) => ({
      date,
      value: day === undefined ? null : pick(day),
      missing: day === undefined,
    }));
  }

  // Erro sem nenhum dado em cache: uma faixa de erro para a linha inteira.
  if (data === undefined) {
    if (overviewQuery.isError) {
      return (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          <span>{genericErrorMessage(overviewQuery.error)}</span>
          <Button variant="outline" size="sm" onClick={() => void overviewQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      );
    }
    // Skeleton com a geometria final dos seis cards.
    return (
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
        {Array.from({ length: 6 }, (_, i) => (
          <Skeleton key={i} className="h-[122px] rounded-lg" />
        ))}
      </div>
    );
  }

  const on = totals?.seconds_on ?? 0;
  const idle = totals?.seconds_idle ?? 0;
  const prevOn = previous?.seconds_on ?? 0;
  const prevIdle = previous?.seconds_idle ?? 0;

  // Ociosidade e aproveitamento NÃO são publicados pelo servidor (só índice e
  // cobertura são): são razões simples sobre os baldes, calculadas aqui.
  const idleShare = on > 0 ? idle / on : null;
  const prevIdleShare = prevOn > 0 ? prevIdle / prevOn : null;

  const metaPct = goals?.work_related_pct ?? null;
  const fora = foraQuery.data;
  const foraVazio = fora !== undefined ? foraDoHorarioEmptyState(fora) : null;

  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
      {/* 1. Índice de produtividade - valor SEMPRE do servidor. */}
      <KpiCard
        label="Índice de produtividade"
        tag={
          metaPct !== null
            ? { text: `meta ${metaPct}%`, title: `Meta da organização para tempo em aplicativos relacionados ao trabalho: ${metaPct}%.` }
            : undefined
        }
        value={formatPct(totals?.productivity_index ?? null)}
        delta={
          <PointsBadge
            current={totals?.productivity_index ?? null}
            previous={previous?.productivity_index ?? null}
            title={comparisonTitle(prevPeriod)}
          />
        }
        hint="Tempo produtivo ÷ tempo ativo classificado."
        mini={
          <MiniLine
            points={serie((d) => {
              const classified =
                d.seconds_work_related + d.seconds_neutral + d.seconds_not_work_related;
              return classified > 0 ? d.seconds_work_related / classified : Number.NaN;
            })}
            color={VIZ.produtivo}
            meta={metaPct !== null ? metaPct / 100 : null}
            fixedMax={1}
            label="Tendência do tempo produtivo sobre o tempo classificado nos últimos 12 dias, com a marca da meta"
          />
        }
      />

      {/* 2. Horas com a máquina ligada = ativo + ocioso + bloqueado. */}
      <KpiCard
        label="Horas com a máquina ligada"
        value={hoursLabel(on)}
        delta={
          <DeltaBadge
            current={on}
            previous={previous?.seconds_on ?? null}
            previousRange={prevPeriod}
            incomplete={totals?.data_incomplete === true}
            showLabel={false}
          />
        }
        hint="Ativo + ocioso + bloqueado."
        mini={
          <MiniBars
            points={serie((d) => d.seconds_on / 3600)}
            color={VIZ.neutro}
            label="Horas com a máquina ligada nos últimos 12 dias"
            unit="h"
          />
        }
      />

      {/* 3. Horas ativas = uso de teclado/mouse. */}
      <KpiCard
        label="Horas ativas"
        value={hoursLabel(totals?.seconds_active ?? 0)}
        delta={
          <DeltaBadge
            current={totals?.seconds_active ?? 0}
            previous={previous?.seconds_active ?? null}
            previousRange={prevPeriod}
            incomplete={totals?.data_incomplete === true}
            showLabel={false}
          />
        }
        hint="Uso de teclado e mouse."
        mini={
          <MiniBars
            points={serie((d) => d.seconds_active / 3600)}
            color={VIZ.produtivo}
            label="Horas ativas nos últimos 12 dias"
            unit="h"
          />
        }
      />

      {/* 4. Ociosidade - jamais somada ao improdutivo. */}
      <KpiCard
        label="Ociosidade"
        labelIcon={
          <Info role="img" aria-label={IDLE_HINT} className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        }
        labelTitle={IDLE_HINT}
        value={formatPct(idleShare)}
        delta={
          <PointsBadge
            current={idleShare}
            previous={prevIdleShare}
            title={comparisonTitle(prevPeriod)}
          />
        }
        hint={`${hoursLabel(idle)} do tempo ligado.`}
        mini={
          <MiniBars
            points={serie((d) => d.seconds_idle / 3600)}
            color={VIZ.ocioso}
            hatch
            label="Horas ociosas nos últimos 12 dias"
            unit="h"
          />
        }
      />

      {/* 5. Sem classificação - o buraco da curadoria, explícito. */}
      <KpiCard
        label="Sem classificação"
        value={hoursLabel(totals?.seconds_unclassified ?? 0)}
        delta={
          <DeltaBadge
            current={totals?.seconds_unclassified ?? 0}
            previous={previous?.seconds_unclassified ?? null}
            previousRange={prevPeriod}
            showLabel={false}
          />
        }
        hint={`Cobertura da classificação: ${formatPct(totals?.classification_coverage ?? null)}.`}
        mini={
          <MiniBars
            points={serie((d) => d.seconds_unclassified / 3600)}
            color={VIZ.semClassificacao}
            label="Horas sem classificação nos últimos 12 dias"
            unit="h"
          />
        }
      />

      {/* 6. Atividade fora do horário - indicador de EQUILÍBRIO, nunca de ponto. */}
      <KpiCard
        label="Atividade fora do horário"
        value={
          fora === undefined
            ? "–"
            : foraVazio !== null
              ? "–"
              : formatDuration(fora.totals?.seconds_outside ?? 0)
        }
        delta={
          fora !== undefined && foraVazio === null ? (
            <DeltaBadge
              current={fora.totals?.seconds_outside ?? 0}
              previous={foraPrevQuery.data?.totals?.seconds_outside ?? null}
              previousRange={prevPeriod}
              showLabel={false}
            />
          ) : undefined
        }
        hint={
          /* No grão MENSAL a consulta nem sai (o relatório tem teto de 92 dias),
             então dizer "Carregando…" seria uma espera que nunca termina. O
             indicador se declara indisponível e diz o que fazer para tê-lo. */
          period?.grain === "month"
            ? "Disponível em janelas de até 92 dias: escolha Hoje, Esta semana ou Este mês."
            : fora === undefined
              ? "Carregando…"
              : foraVazio !== null
                ? foraVazio.titulo
                : "Tempo ativo fora do horário de trabalho declarado."
        }
        /* Sem minigráfico: o endpoint de fora do horário não publica série por
           dia, e inventar uma seria mentir. O slot fica vazio para os seis
           cards manterem a mesma altura. */
        mini={
          <div
            className="h-[34px]"
            title="Este indicador não tem série diária: o relatório de fora do horário agrega o período inteiro."
          />
        }
      />
    </div>
  );
}

/** Title padrão dos selos de variação em pontos. */
function comparisonTitle(prev: ResolvedPeriod | null): string {
  return prev !== null
    ? `Comparação com o período imediatamente anterior, de ${ddmm(prev.from)} a ${ddmm(prev.to)}.`
    : "Comparação com o período imediatamente anterior, de mesma duração.";
}

// -----------------------------------------------------------------------------
// Cartão de KPI
// -----------------------------------------------------------------------------

function KpiCard({
  label,
  labelIcon,
  labelTitle,
  tag,
  value,
  delta,
  hint,
  mini,
}: {
  label: string;
  labelIcon?: ReactNode;
  labelTitle?: string;
  tag?: { text: string; title: string };
  value: string;
  delta?: ReactNode;
  hint: string;
  mini: ReactNode;
}) {
  return (
    <Card className="flex min-w-0 flex-col px-3.5 pb-2.5 pt-3">
      <div className="flex items-center justify-between gap-2">
        <p
          className="flex min-w-0 items-center gap-1 text-xs text-muted-foreground"
          title={labelTitle}
        >
          <span className="truncate">{label}</span>
          {labelIcon}
        </p>
        {tag !== undefined && (
          <span
            title={tag.title}
            className="shrink-0 rounded bg-brand/10 px-1.5 font-display text-[10px] leading-4 text-brand"
          >
            {tag.text}
          </span>
        )}
      </div>
      <p className="mt-1.5 font-display text-2xl font-semibold leading-none tracking-tight tabular-nums">
        {value}
      </p>
      <div className="mt-1 flex min-h-[16px] flex-wrap items-center gap-x-2 text-[11px] text-muted-foreground">
        {delta}
      </div>
      <p className="mt-0.5 truncate text-[11px] text-muted-foreground" title={hint}>
        {hint}
      </p>
      <div className="mt-2">{mini}</div>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Minigráficos SVG (12 dias)
// -----------------------------------------------------------------------------

/** Rótulo do dia para o title de cada barra: "seg 09/06". */
function pointTitle(point: MiniPoint, unit: string): string {
  const dia = `${weekdayShort(point.date)} ${ddmm(point.date)}`;
  if (point.missing || point.value === null || Number.isNaN(point.value)) {
    return `${dia}: sem dados`;
  }
  return `${dia}: ${point.value.toLocaleString("pt-BR", { maximumFractionDigits: 1 })} ${unit}`;
}

/**
 * Barras de 12 dias. Dia SEM dado não vira barra de altura zero: vira um traço
 * na linha de base, para "não temos dado" não ser lido como "ninguém trabalhou".
 */
function MiniBars({
  points,
  color,
  hatch = false,
  label,
  unit,
}: {
  points: MiniPoint[];
  color: string;
  hatch?: boolean;
  label: string;
  unit: string;
}) {
  const values = points.map((p) => (p.value === null || Number.isNaN(p.value) ? 0 : p.value));
  const max = Math.max(...values, 0);
  const slot = MINI_W / Math.max(1, points.length);
  const barW = slot * 0.62;
  const hatchId = useMemo(() => `mini-hatch-${Math.random().toString(36).slice(2, 9)}`, []);

  return (
    <svg
      viewBox={`0 0 ${MINI_W} ${MINI_H}`}
      preserveAspectRatio="none"
      role="img"
      aria-label={label}
      className="block h-[34px] w-full overflow-visible"
    >
      {hatch && (
        <defs>
          <pattern id={hatchId} width="4" height="4" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <rect width="4" height="4" fill={VIZ.ocioso} />
            <line x1="0" y1="0" x2="0" y2="4" stroke={VIZ.contorno} strokeWidth="2" />
          </pattern>
        </defs>
      )}
      {points.map((point, i) => {
        const x = i * slot + (slot - barW) / 2;
        const value = values[i];
        const h = max > 0 ? Math.max(1, (value / max) * (MINI_H - 2)) : 0;
        const empty = point.missing || point.value === null || Number.isNaN(point.value);
        return (
          <rect
            key={point.date}
            x={x}
            y={empty ? MINI_H - 1 : MINI_H - h}
            width={barW}
            height={empty ? 1 : h}
            rx={0.8}
            fill={empty ? VIZ.contorno : hatch ? `url(#${hatchId})` : color}
            opacity={empty ? 0.9 : 1}
          >
            <title>{pointTitle(point, unit)}</title>
          </rect>
        );
      })}
    </svg>
  );
}

/**
 * Linha de 12 dias com área e marca de meta. Usada só no índice: os pontos são
 * frações 0..1 e o eixo é fixo em 0..100% para a meta ficar no lugar certo.
 * Dias sem tempo classificado quebram a linha (NaN), em vez de virar zero.
 */
function MiniLine({
  points,
  color,
  meta,
  fixedMax,
  label,
}: {
  points: MiniPoint[];
  color: string;
  meta: number | null;
  fixedMax: number;
  label: string;
}) {
  const slot = MINI_W / Math.max(1, points.length);
  const x = (i: number): number => i * slot + slot / 2;
  const y = (v: number): number => MINI_H - 1 - (Math.min(v, fixedMax) / fixedMax) * (MINI_H - 3);

  // Trechos contínuos: cada sequência de dias COM dado é uma polyline própria.
  const segments: Array<Array<{ x: number; y: number }>> = [];
  let current: Array<{ x: number; y: number }> = [];
  points.forEach((point, i) => {
    const v = point.value;
    if (v === null || Number.isNaN(v)) {
      if (current.length > 0) segments.push(current);
      current = [];
      return;
    }
    current.push({ x: x(i), y: y(v) });
  });
  if (current.length > 0) segments.push(current);

  return (
    <svg
      viewBox={`0 0 ${MINI_W} ${MINI_H}`}
      preserveAspectRatio="none"
      role="img"
      aria-label={label}
      className="block h-[34px] w-full overflow-visible"
    >
      {meta !== null && (
        <line
          x1={0}
          x2={MINI_W}
          y1={y(meta)}
          y2={y(meta)}
          stroke={VIZ.axisText}
          strokeWidth={1}
          strokeDasharray="3 3"
          vectorEffect="non-scaling-stroke"
        />
      )}
      {segments.map((segment, index) => (
        <g key={index}>
          {segment.length > 1 && (
            <polygon
              points={[
                `${segment[0].x},${MINI_H}`,
                ...segment.map((p) => `${p.x},${p.y}`),
                `${segment[segment.length - 1].x},${MINI_H}`,
              ].join(" ")}
              fill={color}
              opacity={0.14}
            />
          )}
          <polyline
            points={segment.map((p) => `${p.x},${p.y}`).join(" ")}
            fill="none"
            stroke={color}
            strokeWidth={1.6}
            strokeLinejoin="round"
            strokeLinecap="round"
            vectorEffect="non-scaling-stroke"
          />
        </g>
      ))}
      {/* Pontos invisíveis só para o title de cada dia (leitura no hover). */}
      {points.map((point, i) => (
        <rect key={point.date} x={i * slot} y={0} width={slot} height={MINI_H} fill="transparent">
          <title>{pointTitle(point, "do tempo classificado")}</title>
        </rect>
      ))}
      {segments.length === 0 && (
        <line
          x1={0}
          x2={MINI_W}
          y1={MINI_H - 1}
          y2={MINI_H - 1}
          stroke={VIZ.contorno}
          strokeWidth={1}
          vectorEffect="non-scaling-stroke"
        />
      )}
    </svg>
  );
}

/** Classe compartilhada dos minigráficos (mantém a altura no slot vazio). */
export const MINI_CLASS = cn("block h-[34px] w-full");
