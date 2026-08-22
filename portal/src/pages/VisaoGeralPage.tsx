import { useEffect, useMemo, useState } from "react";
import type { CSSProperties, ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import {
  AlertTriangle,
  CheckCircle2,
  ChevronRight,
  Info,
  KeyRound,
  MonitorSmartphone,
  Moon,
} from "lucide-react";
import { api } from "@/lib/api";
import {
  addDays,
  ddmm,
  formatDuration,
  formatRelative,
  localDateOf,
  mondayOf,
  stateLabels,
} from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type {
  DashboardSummaryResponse,
  DeviceHealthSummaryResponse,
  ForaDoHorarioResponse,
  MeResponse,
  PresenceItem,
  PresenceResponse,
  PresenceState,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { OnboardingChecklist } from "@/components/dashboard/OnboardingChecklist";
import { WeeklyChartsRow } from "@/components/dashboard/WeeklyChartsRow";
import {
  businessHoursLabel,
  foraDoHorarioEmptyState,
  foraDoHorarioKey,
  foraDoHorarioPct,
  foraDoHorarioUrl,
} from "@/components/reports/ForaDoHorario";

/** Tooltip pedagógico do estado Ocioso (Seção 8.4) - sempre presente via title. */
const IDLE_HINT =
  "Ocioso significa sem uso de teclado/mouse. Reuniões, chamadas e leitura podem aparecer como ociosidade.";

/** Ordem default da tabela "Equipe agora": problemas primeiro (Seção 8.4). */
const stateOrder: Record<PresenceState, number> = {
  no_data: 0,
  active: 1,
  idle: 2,
  locked: 3,
  no_session: 4,
  off_clean: 5,
};

/** Hachura diagonal vermelha do no_data - redundância NÃO-cromática (Seção 8.5). */
const noDataHatch: CSSProperties = {
  backgroundImage:
    "repeating-linear-gradient(45deg, #dc2626 0px, #dc2626 2px, #fecaca 2px, #fecaca 4px)",
};

/** Dashboard de presença "agora" (F2, Seção 8.4): cards de contagem + tabela Equipe agora. */
export function VisaoGeralPage() {
  const navigate = useNavigate();
  const [noDataFilter, setNoDataFilter] = useState(false);
  const [nowMs, setNowMs] = useState(() => Date.now());

  const presenceQuery = useQuery({
    queryKey: ["dashboard", "presence"],
    queryFn: () => api<PresenceResponse>("/dashboard/presence"),
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    placeholderData: (prev) => prev,
  });

  // Saúde da FROTA INTEIRA (não da página de /dispositivos): mesmo polling de
  // 60 s da presença, pausado em aba oculta. A key sob ["devices"] faz o PATCH
  // de dispositivo (arquivar/reativar) invalidar estes contadores também.
  const healthQuery = useQuery({
    queryKey: ["devices", "health-summary"],
    queryFn: () => api<DeviceHealthSummaryResponse>("/devices/health-summary"),
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    placeholderData: (prev) => prev,
  });

  // Mesma queryKey do AppShell: resolve do cache, sem requisição extra.
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const organization = meQuery.data?.organization;
  const goalHours = organization?.goal_weekly_active_hours ?? null;
  const goalWorkPct = organization?.goal_work_related_pct ?? null;

  // Semana corrente (segunda a domingo) no FUSO DA ORGANIZAÇÃO — a mesma janela
  // dos gráficos da semana logo abaixo.
  const timezone = organization?.timezone ?? null;
  const weekFrom = timezone !== null ? mondayOf(localDateOf(new Date(), timezone)) : null;
  const weekTo = weekFrom !== null ? addDays(weekFrom, 6) : null;

  /**
   * MESMA queryKey e MESMA URL do WeeklyChartsRow para a semana atual: o
   * TanStack Query compartilha o cache entre os dois observadores, então a
   * barra de meta não gera requisição extra (e o card de gráficos segue dono
   * do seu próprio arquivo, sem acoplamento de props).
   */
  const weekSummaryQuery = useQuery({
    queryKey: ["dashboard", "summary", weekFrom, weekTo],
    queryFn: () =>
      api<DashboardSummaryResponse>(
        `/dashboard/summary?from=${weekFrom ?? ""}&to=${weekTo ?? ""}`,
      ),
    enabled: weekFrom !== null && goalHours !== null,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    placeholderData: (prev) => prev,
  });

  /**
   * Atividade fora do horário de trabalho na semana corrente. SEM
   * include_devices: o card é um agregado de EQUIPE, e por isso a leitura não
   * gera view_report (o recorte pessoal só existe na aba do relatório de Uso).
   * Sem polling: o indicador é semanal, não muda a cada minuto.
   */
  const foraParams = {
    from: weekFrom ?? "",
    to: weekTo ?? "",
    deviceIdsKey: "",
    page: 1,
    includeDevices: false,
    pageSize: 1,
  };
  const foraQuery = useQuery({
    queryKey: foraDoHorarioKey(foraParams),
    queryFn: () => api<ForaDoHorarioResponse>(foraDoHorarioUrl(foraParams)),
    enabled: weekFrom !== null,
    staleTime: 5 * 60 * 1000,
  });

  // Tick de 1s só para o badge "Atualizado há Xs" (relógio local vs server_time).
  useEffect(() => {
    const id = window.setInterval(() => setNowMs(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, []);

  const data = presenceQuery.data;

  const counts = useMemo(() => {
    const c = { active: 0, idle: 0, lockedNoSession: 0, offClean: 0, noData: 0 };
    for (const item of data?.items ?? []) {
      switch (item.presence_state) {
        case "active":
          c.active += 1;
          break;
        case "idle":
          c.idle += 1;
          break;
        case "locked":
        case "no_session":
          c.lockedNoSession += 1;
          break;
        case "off_clean":
          c.offClean += 1;
          break;
        case "no_data":
          c.noData += 1;
          break;
      }
    }
    return c;
  }, [data]);

  const rows = useMemo(() => {
    const items = data?.items ?? [];
    const visible = noDataFilter ? items.filter((i) => i.presence_state === "no_data") : items;
    return [...visible].sort((a, b) => {
      const byState = stateOrder[a.presence_state] - stateOrder[b.presence_state];
      if (byState !== 0) return byState;
      return a.device_name.localeCompare(b.device_name, "pt-BR");
    });
  }, [data, noDataFilter]);

  function openTimeline(deviceId: string) {
    navigate(`/linha-do-tempo?device=${encodeURIComponent(deviceId)}`);
  }

  const header = (
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Visão Geral</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Presença da equipe agora, atualizada a cada minuto.
        </p>
      </div>
      {data !== undefined && (
        <span className="rounded-full bg-secondary px-2.5 py-0.5 text-xs tabular-nums text-secondary-foreground">
          Atualizado {formatRelative(data.server_time, new Date(nowMs).toISOString())}
        </span>
      )}
    </div>
  );

  // Skeleton inicial com a geometria final (nunca spinner de página inteira).
  if (presenceQuery.isPending) {
    return (
      <div className="space-y-6">
        {header}
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
          {Array.from({ length: 5 }, (_, i) => (
            <Skeleton key={i} className="h-[74px] rounded-lg" />
          ))}
        </div>
        {/* Mesma geometria da faixa de saúde da frota que carrega em seguida. */}
        <Skeleton className="h-[76px] w-full rounded-lg" />
        <Card>
          <CardHeader className="pb-3">
            <Skeleton className="h-5 w-32" />
            <Skeleton className="h-4 w-72" />
          </CardHeader>
          <CardContent className="space-y-2 pb-4">
            {Array.from({ length: 6 }, (_, i) => (
              <Skeleton key={i} className="h-9 w-full" />
            ))}
          </CardContent>
        </Card>
      </div>
    );
  }

  // Erro sem nenhum dado em cache: estado inline com retry (nunca tela quebrada).
  if (data === undefined) {
    return (
      <div className="space-y-6">
        {header}
        <Card>
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(presenceQuery.error)}</p>
            <Button variant="outline" onClick={() => void presenceQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        </Card>
      </div>
    );
  }

  // Org sem nenhum device: estado vazio apontando para a chave de enrollment.
  if (data.items.length === 0) {
    return (
      <div className="space-y-6">
        {header}
        <Card>
          <div className="flex flex-col items-center gap-4 px-6 py-14 text-center">
            <span className="flex h-14 w-14 items-center justify-center rounded-full bg-muted">
              <MonitorSmartphone className="h-7 w-7 text-muted-foreground" aria-hidden />
            </span>
            <div className="space-y-1">
              <p className="text-base font-medium">Nenhum dispositivo ainda.</p>
              <p className="text-sm text-muted-foreground">
                Crie uma chave em Configurações → Chaves e instale o agente.
              </p>
            </div>
            <Link
              to="/configuracoes/chaves"
              className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
            >
              <KeyRound className="h-4 w-4" aria-hidden />
              Criar chave de instalação
            </Link>
          </div>
        </Card>
        <OnboardingChecklist />
        <WeeklyChartsRow />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {header}

      {presenceQuery.isError && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void presenceQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      {/* Linha 1 - cards de contagem por estado de presença (Seção 8.5). */}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
        <Card>
          <CountCardBody
            count={counts.active}
            label="Ativos"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-full bg-viz-produtivo" />}
          />
        </Card>
        <Card title={IDLE_HINT}>
          <CountCardBody
            count={counts.idle}
            label="Ociosos"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-full bg-viz-improdutivo" />}
            labelIcon={<Info role="img" aria-label={IDLE_HINT} className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />}
          />
        </Card>
        <Card>
          <CountCardBody
            count={counts.lockedNoSession}
            label="Bloqueados / sem usuário"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-full bg-brand-slate" />}
          />
        </Card>
        {/* off_clean é estado esperado: cinza claro, apenas contorno, sem alerta. */}
        <Card>
          <CountCardBody
            count={counts.offClean}
            label="Desligadas"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-full border-2 border-border" />}
          />
        </Card>
        {/* no_data é clicável: filtra a tabela abaixo; clicar de novo desfaz. */}
        <button
          type="button"
          onClick={() => setNoDataFilter((f) => !f)}
          aria-pressed={noDataFilter}
          title={noDataFilter ? "Clique para remover o filtro da tabela" : "Clique para filtrar a tabela"}
          className={cn(
            "rounded-lg border bg-card text-left text-card-foreground shadow-sm transition-colors hover:bg-accent",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
            noDataFilter && "border-brand-red ring-2 ring-brand-red/30",
          )}
        >
          <CountCardBody
            count={counts.noData}
            label="Sem comunicação"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-sm" style={noDataHatch} />}
            labelIcon={<AlertTriangle className="h-3.5 w-3.5 shrink-0 text-brand-red" aria-hidden />}
          />
        </button>
      </div>

      {/* Linha 1b - saúde da frota INTEIRA (F4.4 + health-summary). */}
      <FleetHealthWidget query={healthQuery} />

      {/* Linha 1c - meta da semana (só quando a organização definiu meta). */}
      <MetaSemanaWidget
        goalHours={goalHours}
        goalWorkPct={goalWorkPct}
        weekFrom={weekFrom}
        weekTo={weekTo}
        query={weekSummaryQuery}
      />

      {/* Linha 1c-bis - atividade fora do horário de trabalho na semana. */}
      <ForaDoHorarioWidget query={foraQuery} weekFrom={weekFrom} weekTo={weekTo} />

      {/* Linha 1d - uso do plano (só quando o plano tem teto de dispositivos). */}
      <PlanoMedidor
        deviceLimit={organization?.device_limit ?? null}
        activeDevices={healthQuery.data?.active_devices ?? data.items.length}
      />

      {/* Linha 2 - tabela "Equipe agora" (Seção 8.4). */}
      <Card>
        <CardHeader className="pb-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="space-y-1.5">
              <CardTitle className="text-base">Equipe agora</CardTitle>
              <CardDescription>
                Clique em um dispositivo para abrir a linha do tempo do dia.
              </CardDescription>
            </div>
            {noDataFilter && (
              <Button variant="outline" size="sm" onClick={() => setNoDataFilter(false)}>
                Limpar filtro: Sem comunicação
              </Button>
            )}
          </div>
        </CardHeader>
        <CardContent className="px-0 pb-0">
          <div className="overflow-x-auto rounded-b-lg">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Status</th>
                  <th scope="col" className="px-3 py-2">Device / Usuário</th>
                  <th scope="col" className="px-3 py-2">App em foco</th>
                  <th scope="col" className="px-3 py-2 text-right">Neste app</th>
                  <th scope="col" className="px-6 py-2 text-right">Último contato</th>
                </tr>
              </thead>
              <tbody>
                {rows.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="px-6 py-8 text-center text-sm text-muted-foreground">
                      Nenhum dispositivo sem comunicação agora.
                    </td>
                  </tr>
                ) : (
                  rows.map((item) => (
                    <PresenceRow
                      key={item.device_id}
                      item={item}
                      serverTime={data.server_time}
                      onOpen={openTimeline}
                    />
                  ))
                )}
              </tbody>
            </table>
          </div>
        </CardContent>
      </Card>

      {/* Checklist de primeiros passos (funil de ativação) - admin/owner,
          some após o dismiss. Acima dos gráficos, abaixo da presença. */}
      <OnboardingChecklist />

      {/* Linha 3 - gráficos da semana (F3.2, Seção 8.4). */}
      <WeeklyChartsRow />
    </div>
  );
}

// -----------------------------------------------------------------------------
// Saúde da frota inteira (GET /devices/health-summary)
// -----------------------------------------------------------------------------

/**
 * Dimensões de saúde com contagem > 0, no vocabulário neutro dos badges de
 * /dispositivos. Ex.: ["3 sem comunicação", "1 com ciência pendente"]. Nunca
 * soma as dimensões: um mesmo device pode acionar várias, e with_alert é a
 * contagem DISTINTA de dispositivos.
 */
function healthAlertParts(summary: DeviceHealthSummaryResponse): string[] {
  const parts: string[] = [];
  if (summary.offline > 0) parts.push(`${summary.offline} sem comunicação`);
  if (summary.clock_skewed > 0) parts.push(`${summary.clock_skewed} com relógio dessincronizado`);
  if (summary.outdated > 0) parts.push(`${summary.outdated} com versão desatualizada`);
  if (summary.tampered > 0) parts.push(`${summary.tampered} com adulteração`);
  if (summary.notice_pending > 0) parts.push(`${summary.notice_pending} com ciência pendente`);
  return parts;
}

/**
 * Card "Dispositivos precisam de atenção" com o with_alert da FROTA INTEIRA,
 * clicável para /dispositivos?filtro=alerta (que já abre a listagem filtrada
 * pelo mesmo predicado no servidor). Frota inteira respondendo: uma linha
 * discreta, jamais um card de erro. Skeleton com a geometria final e erro
 * inline com retry, no padrão dos vizinhos desta tela.
 */
function FleetHealthWidget({ query }: { query: UseQueryResult<DeviceHealthSummaryResponse> }) {
  const summary = query.data;

  if (summary === undefined) {
    if (query.isError) {
      return (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          <span>Não foi possível carregar a saúde da frota.</span>
          <Button variant="outline" size="sm" onClick={() => void query.refetch()}>
            Tentar novamente
          </Button>
        </div>
      );
    }
    return <Skeleton className="h-[76px] w-full rounded-lg" />;
  }

  if (summary.with_alert === 0) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <CheckCircle2 className="h-4 w-4 shrink-0 text-viz-produtivo" aria-hidden />
        Toda a frota respondendo.
        <span className="tabular-nums">
          {summary.active_devices === 1
            ? "1 dispositivo ativo"
            : `${summary.active_devices} dispositivos ativos`}
          .
        </span>
      </p>
    );
  }

  const parts = healthAlertParts(summary);
  const severeHint =
    summary.offline_severe > 0
      ? `${summary.offline_severe} sem comunicação há mais de 30 minutos em horário de trabalho`
      : undefined;

  return (
    <Link
      to="/dispositivos?filtro=alerta"
      title={severeHint}
      className={cn(
        "flex items-center gap-4 rounded-lg border px-4 py-3 text-card-foreground shadow-sm transition-colors",
        "border-brand-red/30 bg-brand-red/10 hover:bg-brand-red/15",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
      )}
    >
      <AlertTriangle className="h-5 w-5 shrink-0 text-brand-red" aria-hidden />
      <span className="min-w-0 flex-1">
        <span className="flex items-baseline gap-2">
          <span className="text-2xl font-semibold leading-none tabular-nums">
            {summary.with_alert}
          </span>
          <span className="text-sm font-medium">
            {summary.with_alert === 1
              ? "dispositivo precisa de atenção"
              : "dispositivos precisam de atenção"}
          </span>
        </span>
        <span className="mt-1 block truncate text-xs tabular-nums text-muted-foreground">
          {parts.join(", ")}
          {severeHint !== undefined && (
            <span className="text-brand-red"> · {summary.offline_severe} há mais de 30 minutos</span>
          )}
        </span>
      </span>
      <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
    </Link>
  );
}

// -----------------------------------------------------------------------------
// Meta da semana (goal_weekly_active_hours / goal_work_related_pct de GET /me)
// -----------------------------------------------------------------------------

/** Barra fina de progresso, com o mesmo desenho do medidor de plano. */
function ProgressBar({
  pct,
  tone,
  label,
}: {
  pct: number;
  tone: "primary" | "atencao" | "ok";
  label: string;
}) {
  const width = Math.min(100, Math.max(0, pct));
  const fill =
    tone === "ok" ? "bg-viz-produtivo" : tone === "atencao" ? "bg-viz-improdutivo" : "bg-primary";
  return (
    <div
      role="progressbar"
      aria-valuenow={pct}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-label={label}
      className="h-1.5 w-full overflow-hidden rounded-full bg-muted"
    >
      <div className={cn("h-full rounded-full", fill)} style={{ width: `${width}%` }} />
    </div>
  );
}

/**
 * Progresso da meta SEMANAL da organização. Vocabulário sempre de equipe:
 * "horas ativas da equipe" e "tempo em aplicativos relacionados ao trabalho".
 * Nunca meta individual, nunca ranking de pessoas. Organização sem meta
 * definida não renderiza nada.
 */
function MetaSemanaWidget({
  goalHours,
  goalWorkPct,
  weekFrom,
  weekTo,
  query,
}: {
  goalHours: number | null;
  goalWorkPct: number | null;
  weekFrom: string | null;
  weekTo: string | null;
  query: UseQueryResult<DashboardSummaryResponse>;
}) {
  if (goalHours === null || goalHours <= 0) return null;

  const periodo =
    weekFrom !== null && weekTo !== null ? `semana de ${ddmm(weekFrom)} a ${ddmm(weekTo)}` : "semana";
  const data = query.data;

  if (data === undefined) {
    if (query.isError) {
      return (
        <Card className="flex flex-wrap items-center justify-between gap-2 px-4 py-3">
          <p className="text-sm text-muted-foreground">
            Não foi possível carregar o progresso da meta da semana.
          </p>
          <Button variant="outline" size="sm" onClick={() => void query.refetch()}>
            Tentar novamente
          </Button>
        </Card>
      );
    }
    return <Skeleton className="h-[86px] w-full rounded-lg" />;
  }

  const activeSeconds = data.totals.seconds_active;
  const goalSeconds = goalHours * 3600;
  const pct = Math.round((activeSeconds / goalSeconds) * 100);
  const atingida = pct >= 100;

  // % do tempo ativo em apps relacionados ao trabalho (só com tempo ativo > 0).
  const workPct =
    activeSeconds > 0 ? Math.round((data.totals.seconds_work_related / activeSeconds) * 100) : null;

  return (
    <Card className="space-y-2 px-4 py-3">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <p className="text-sm font-medium tabular-nums">Meta da semana: {pct}% atingida</p>
        <p className="text-xs tabular-nums text-muted-foreground">
          {formatDuration(activeSeconds)} de {goalHours}h de horas ativas da equipe
        </p>
      </div>
      <ProgressBar
        pct={pct}
        tone={atingida ? "ok" : "primary"}
        label={`Meta da semana de horas ativas da equipe: ${pct}% atingida`}
      />
      <p className="text-xs text-muted-foreground">
        Horas ativas da equipe na {periodo}, no fuso da organização.
      </p>

      {goalWorkPct !== null && goalWorkPct > 0 && (
        <div className="space-y-2 border-t pt-2">
          <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
            <p className="text-sm font-medium tabular-nums">
              Tempo em aplicativos relacionados ao trabalho: {workPct ?? 0}%
            </p>
            <p className="text-xs tabular-nums text-muted-foreground">meta de {goalWorkPct}%</p>
          </div>
          <ProgressBar
            pct={workPct !== null ? Math.round((workPct / goalWorkPct) * 100) : 0}
            tone={workPct !== null && workPct >= goalWorkPct ? "ok" : "primary"}
            label={`Meta de tempo em aplicativos relacionados ao trabalho: ${workPct ?? 0}% de ${goalWorkPct}%`}
          />
          <p className="text-xs text-muted-foreground">
            Percentual do tempo ativo da equipe classificado como relacionado ao trabalho.
          </p>
        </div>
      )}
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Atividade fora do horário de trabalho (GET /reports/fora-do-horario)
// -----------------------------------------------------------------------------

/**
 * Card de EQUILÍBRIO da semana: quanto tempo ativo a equipe registrou fora do
 * horário de trabalho declarado. Vocabulário fixo "atividade fora do horário de
 * trabalho" - o produto não calcula, e a tela não sugere, hora extra, jornada
 * extraordinária ou banco de horas.
 *
 * Os dois estados sem número (horário não configurado e coleta restrita ao
 * horário) viram uma linha discreta EXPLICATIVA: zero seria uma resposta falsa,
 * e o gestor precisa saber por que não há indicador em vez de ler "tudo certo".
 */
function ForaDoHorarioWidget({
  query,
  weekFrom,
  weekTo,
}: {
  query: UseQueryResult<ForaDoHorarioResponse>;
  weekFrom: string | null;
  weekTo: string | null;
}) {
  const data = query.data;

  if (data === undefined) {
    // Erro aqui não vira card de erro: é um indicador secundário da tela.
    return query.isError ? null : <Skeleton className="h-[52px] w-full rounded-lg" />;
  }

  const periodo =
    weekFrom !== null && weekTo !== null ? `semana de ${ddmm(weekFrom)} a ${ddmm(weekTo)}` : "semana";

  const vazio = foraDoHorarioEmptyState(data);
  if (vazio !== null) {
    return (
      <p className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
        <Info className="h-4 w-4 shrink-0" aria-hidden />
        <span>
          Atividade fora do horário de trabalho: {vazio.titulo.toLocaleLowerCase("pt-BR")}.
        </span>
        {vazio.acao !== null && (
          <Link
            to={vazio.acao.to}
            className="font-medium text-primary underline-offset-2 hover:underline"
          >
            {vazio.acao.label}
          </Link>
        )}
      </p>
    );
  }

  const totals = data.totals;
  if (totals === null || totals.seconds_outside === 0) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <CheckCircle2 className="h-4 w-4 shrink-0 text-viz-produtivo" aria-hidden />
        Nenhuma atividade fora do horário de trabalho na {periodo}.
      </p>
    );
  }

  const pct = foraDoHorarioPct(totals.seconds_outside, totals.seconds_active);
  const dispositivos =
    totals.devices_with_activity_outside === 1
      ? "1 dispositivo"
      : `${totals.devices_with_activity_outside} dispositivos`;

  return (
    <Link
      to="/relatorios/uso?aba=fora-do-horario"
      className={cn(
        "flex items-center gap-4 rounded-lg border px-4 py-3 text-card-foreground shadow-sm transition-colors",
        "hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
      )}
    >
      <Moon className="h-5 w-5 shrink-0 text-muted-foreground" aria-hidden />
      <span className="min-w-0 flex-1">
        <span className="flex flex-wrap items-baseline gap-2">
          <span className="text-2xl font-semibold leading-none tabular-nums">
            {formatDuration(totals.seconds_outside)}
          </span>
          <span className="text-sm font-medium">de atividade fora do horário de trabalho</span>
        </span>
        <span className="mt-1 block truncate text-xs tabular-nums text-muted-foreground">
          {dispositivos} na {periodo}
          {pct !== null && ` · ${pct}% do tempo ativo da equipe`}
          {data.business_hours !== null && ` · horário declarado: ${businessHoursLabel(data.business_hours)}`}
        </span>
      </span>
      <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
    </Link>
  );
}

// -----------------------------------------------------------------------------
// Uso do plano (device_limit de GET /me)
// -----------------------------------------------------------------------------

/** Contato comercial para ampliação de plano (sem preço em tela: Seção 7.4). */
const SUPORTE_EMAIL = "bruna@mais351monitor.com.br";

/** A partir de 80% do teto o medidor troca de tom e convida a falar com a gente. */
const PLANO_ATENCAO_PCT = 80;

/**
 * Medidor discreto "X de Y dispositivos do plano". Sem device_limit (plano sem
 * teto) NÃO renderiza nada — o produto não inventa limite onde não existe.
 * Mostra apenas contagem, jamais valor em reais: preço é decisão comercial
 * fora do sistema.
 */
function PlanoMedidor({
  deviceLimit,
  activeDevices,
}: {
  deviceLimit: number | null;
  activeDevices: number;
}) {
  if (deviceLimit === null || deviceLimit <= 0) return null;

  const pct = Math.min(100, Math.round((activeDevices / deviceLimit) * 100));
  const atencao = pct >= PLANO_ATENCAO_PCT;

  return (
    <div className="space-y-1.5">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1 text-sm">
        <span className={cn("tabular-nums", atencao ? "font-medium text-viz-improdutivo" : "text-muted-foreground")}>
          {activeDevices} de {deviceLimit} dispositivos do plano
        </span>
        {atencao && (
          <span className="text-xs text-muted-foreground">
            <a
              href={`mailto:${SUPORTE_EMAIL}?subject=${encodeURIComponent("Ampliação de plano, +351 Monitor")}`}
              className="font-medium text-primary underline-offset-4 hover:underline"
            >
              Fale com a gente para ampliar o plano
            </a>
          </span>
        )}
      </div>
      <div
        role="progressbar"
        aria-valuenow={activeDevices}
        aria-valuemin={0}
        aria-valuemax={deviceLimit}
        aria-label={`Uso do plano: ${activeDevices} de ${deviceLimit} dispositivos`}
        className="h-1.5 w-full overflow-hidden rounded-full bg-muted"
      >
        <div
          className={cn("h-full rounded-full", atencao ? "bg-viz-improdutivo" : "bg-primary")}
          style={{ width: `${pct}%` }}
        />
      </div>
    </div>
  );
}

/** Conteúdo interno dos cards de contagem (densidade alta, contagem tabular). */
function CountCardBody({
  count,
  label,
  swatch,
  labelIcon,
}: {
  count: number;
  label: string;
  swatch: ReactNode;
  labelIcon?: ReactNode;
}) {
  return (
    <div className="flex items-center gap-3 p-4">
      {swatch}
      <div className="min-w-0">
        <p className="text-2xl font-semibold leading-none tabular-nums">{count}</p>
        <p className="mt-1 flex items-center gap-1 text-xs text-muted-foreground">
          <span className="truncate">{label}</span>
          {labelIcon}
        </p>
      </div>
    </div>
  );
}

/** Linha clicável da tabela Equipe agora - Enter/Espaço também navegam. */
function PresenceRow({
  item,
  serverTime,
  onOpen,
}: {
  item: PresenceItem;
  serverTime: string;
  onOpen: (deviceId: string) => void;
}) {
  const isNoData = item.presence_state === "no_data";
  const appSinceSeconds =
    item.app_since !== null
      ? (new Date(serverTime).getTime() - new Date(item.app_since).getTime()) / 1000
      : null;

  return (
    <tr
      tabIndex={0}
      onClick={() => onOpen(item.device_id)}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onOpen(item.device_id);
        }
      }}
      aria-label={`Abrir linha do tempo de ${item.device_name}`}
      className={cn(
        "cursor-pointer border-b transition-colors last:border-b-0",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
        isNoData ? "bg-brand-red/10 hover:bg-brand-red/15" : "hover:bg-accent",
      )}
    >
      <td className="px-6 py-2.5">
        <span className="flex items-center gap-2 whitespace-nowrap">
          <StateDot state={item.presence_state} />
          <span>{stateLabels[item.presence_state]}</span>
          {isNoData && <AlertTriangle className="h-3.5 w-3.5 shrink-0 text-brand-red" aria-hidden />}
        </span>
      </td>
      <td className="px-3 py-2.5">
        <p className="max-w-[16rem] truncate font-medium">{item.device_name}</p>
        <p className="max-w-[16rem] truncate text-xs text-muted-foreground">
          {item.windows_username ?? "-"}
        </p>
      </td>
      <td className="px-3 py-2.5">
        {item.foreground_process !== null ? (
          <p className="max-w-[24rem] truncate">
            <span className="font-medium">{item.foreground_process}</span>
            {item.foreground_title !== null && (
              <span className="text-muted-foreground"> · {item.foreground_title}</span>
            )}
          </p>
        ) : (
          <span className="text-muted-foreground">-</span>
        )}
      </td>
      <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums">
        {appSinceSeconds !== null ? formatDuration(appSinceSeconds) : <span className="text-muted-foreground">-</span>}
      </td>
      <td className="whitespace-nowrap px-6 py-2.5 text-right tabular-nums text-muted-foreground">
        {formatRelative(item.last_contact_at, serverTime)}
      </td>
    </tr>
  );
}

/** Dot de status com redundância não-cromática (Seção 8.5). */
function StateDot({ state }: { state: PresenceState }) {
  if (state === "no_data") {
    return <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={noDataHatch} />;
  }
  if (state === "off_clean") {
    return <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-full border-2 border-border" />;
  }
  const solid: Record<"active" | "idle" | "locked" | "no_session", string> = {
    active: "bg-viz-produtivo",
    idle: "bg-viz-improdutivo",
    locked: "bg-brand-slate",
    no_session: "bg-brand-slate",
  };
  return <span aria-hidden className={cn("h-2.5 w-2.5 shrink-0 rounded-full", solid[state])} />;
}
