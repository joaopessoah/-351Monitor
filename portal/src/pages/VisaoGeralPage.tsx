import { useEffect, useMemo, useState } from "react";
import type { CSSProperties, ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, Info, KeyRound, MonitorSmartphone } from "lucide-react";
import { api } from "@/lib/api";
import { formatDuration, formatRelative, stateLabels } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type { PresenceItem, PresenceResponse, PresenceState } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

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
        <GraficosF3Card />
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
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-full bg-emerald-500" />}
          />
        </Card>
        <Card title={IDLE_HINT}>
          <CountCardBody
            count={counts.idle}
            label="Ociosos"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-full bg-amber-500" />}
            labelIcon={<Info role="img" aria-label={IDLE_HINT} className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />}
          />
        </Card>
        <Card>
          <CountCardBody
            count={counts.lockedNoSession}
            label="Bloqueados / sem usuário"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-full bg-slate-500" />}
          />
        </Card>
        {/* off_clean é estado esperado: cinza claro, apenas contorno, sem alerta. */}
        <Card>
          <CountCardBody
            count={counts.offClean}
            label="Desligadas"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-full border-2 border-slate-300" />}
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
            noDataFilter && "border-red-500 ring-2 ring-red-500/30",
          )}
        >
          <CountCardBody
            count={counts.noData}
            label="Sem comunicação"
            swatch={<span aria-hidden className="h-3.5 w-3.5 shrink-0 rounded-sm" style={noDataHatch} />}
            labelIcon={<AlertTriangle className="h-3.5 w-3.5 shrink-0 text-red-600" aria-hidden />}
          />
        </button>
      </div>

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

      <GraficosF3Card />
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
        isNoData ? "bg-red-50 hover:bg-red-100" : "hover:bg-accent",
      )}
    >
      <td className="px-6 py-2.5">
        <span className="flex items-center gap-2 whitespace-nowrap">
          <StateDot state={item.presence_state} />
          <span>{stateLabels[item.presence_state]}</span>
          {isNoData && <AlertTriangle className="h-3.5 w-3.5 shrink-0 text-red-600" aria-hidden />}
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
    return <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-full border-2 border-slate-300" />;
  }
  const solid: Record<"active" | "idle" | "locked" | "no_session", string> = {
    active: "bg-emerald-500",
    idle: "bg-amber-500",
    locked: "bg-slate-500",
    no_session: "bg-slate-400",
  };
  return <span aria-hidden className={cn("h-2.5 w-2.5 shrink-0 rounded-full", solid[state])} />;
}

/** Rodapé discreto: a Linha 3 (gráficos da semana) chega na F3 - não construir agora. */
function GraficosF3Card() {
  return (
    <Card className="border-dashed shadow-none">
      <div className="px-6 py-4 text-center text-sm text-muted-foreground">
        Gráficos de atividade chegam na F3.
      </div>
    </Card>
  );
}
