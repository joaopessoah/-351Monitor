// =============================================================================
// Faixa "Agora" - PRIMEIRO CABEÇALHO da Visão Geral (pedido explícito do dono,
// spec de 07/09/2026, seção 3). É a única parte da tela que se atualiza sozinha:
// polling de 60 s pausado em aba oculta, como a presença sempre fez.
//
// VOCABULÁRIO: estados de MÁQUINA, sempre neutros - Ativo, Ocioso, Bloqueado,
// Desligada, Sem comunicação. Ocioso NUNCA conta como improdutivo, e por isso
// carrega o tooltip pedagógico (texto verbatim da tela anterior). "Bloqueado"
// junta locked e no_session: para quem lê, as duas coisas são "máquina ligada
// sem ninguém usando".
//
// A tabela "Equipe agora" saiu daqui e vive na Linha do Tempo (aba Hoje): o
// link à direita é a ponte, para o dado não desaparecer do produto.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, Info } from "lucide-react";
import { api } from "@/lib/api";
import { formatRelative } from "@/lib/format";
import type { PresenceResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { IDLE_HINT, NO_DATA_HATCH, VIZ } from "@/components/dashboard/overviewKit";

/** "1 ativo" / "3 ativos" - plural só do rótulo, o número é sempre tabular. */
function plural(count: number, singular: string, plural_: string): string {
  return count === 1 ? singular : plural_;
}

export function AgoraFaixa({ tag }: { tag: string | null }) {
  const [nowMs, setNowMs] = useState(() => Date.now());

  const presenceQuery = useQuery({
    queryKey: ["dashboard", "presence", tag],
    queryFn: () => api<PresenceResponse>(`/dashboard/presence?tag=${encodeURIComponent(tag ?? "")}`),
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    placeholderData: (prev) => prev,
  });

  // Tick de 1 s só para o "atualizado há Xs" (relógio local vs server_time).
  useEffect(() => {
    const id = window.setInterval(() => setNowMs(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, []);

  const data = presenceQuery.data;

  const counts = useMemo(() => {
    const c = { active: 0, idle: 0, locked: 0, offClean: 0, noData: 0, total: 0 };
    for (const item of data?.items ?? []) {
      c.total += 1;
      switch (item.presence_state) {
        case "active":
          c.active += 1;
          break;
        case "idle":
          c.idle += 1;
          break;
        case "locked":
        case "no_session":
          c.locked += 1;
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

  return (
    <section
      aria-label="Agora"
      className={cn(
        "-mx-4 flex flex-wrap items-center gap-x-5 gap-y-2 border-b px-4 py-3 sm:-mx-6 sm:px-6",
        "bg-gradient-to-r from-brand/10 via-viz-neutro/5 to-transparent",
      )}
    >
      <Pulso />
      <span className="font-display text-sm font-semibold">Agora</span>

      {data === undefined ? (
        presenceQuery.isError ? (
          <span className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            <AlertTriangle className="h-3.5 w-3.5 shrink-0 text-destructive" aria-hidden />
            Não foi possível carregar a presença agora.
            <Button
              variant="outline"
              size="sm"
              className="h-6 px-2 text-xs"
              onClick={() => void presenceQuery.refetch()}
            >
              Tentar novamente
            </Button>
          </span>
        ) : (
          // Skeleton com a geometria final da faixa (nunca spinner).
          <Skeleton className="h-6 w-[26rem] max-w-full rounded-md" />
        )
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-x-5 gap-y-1.5">
            <Contagem
              count={counts.active}
              label={plural(counts.active, "ativo", "ativos")}
              swatch={<Dot color={VIZ.produtivo} />}
            />
            <Contagem
              count={counts.idle}
              label={plural(counts.idle, "ocioso", "ociosos")}
              swatch={<Dot color="#4E5C78" />}
              title={IDLE_HINT}
              icon={
                <Info
                  role="img"
                  aria-label={IDLE_HINT}
                  className="h-3 w-3 shrink-0 text-muted-foreground"
                />
              }
            />
            <Contagem
              count={counts.locked}
              label={plural(counts.locked, "bloqueado", "bloqueados")}
              title="Máquina ligada e bloqueada, ou sem usuário logado."
              swatch={
                <span
                  aria-hidden
                  className="h-2.5 w-2.5 shrink-0 rounded-full"
                  style={{ backgroundColor: VIZ.ocioso, outline: `1px solid ${VIZ.contorno}` }}
                />
              }
            />
            {/* Desligada é estado ESPERADO: só contorno, nunca alerta. */}
            <Contagem
              count={counts.offClean}
              label={plural(counts.offClean, "desligada", "desligadas")}
              swatch={
                <span
                  aria-hidden
                  className="h-2.5 w-2.5 shrink-0 rounded-full border-2 border-border"
                />
              }
            />
            <Contagem
              count={counts.noData}
              label="sem comunicação"
              swatch={<span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={NO_DATA_HATCH} />}
              icon={
                counts.noData > 0 ? (
                  <AlertTriangle className="h-3 w-3 shrink-0 text-brand-red" aria-hidden />
                ) : undefined
              }
            />
          </div>

          <span className="ml-auto flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
            <span className="tabular-nums">
              {counts.total} {plural(counts.total, "monitorado", "monitorados")}
            </span>
            <span className="tabular-nums">
              atualizado {formatRelative(data.server_time, new Date(nowMs).toISOString())}
            </span>
            {/* A tabela "Equipe agora" foi demovida para a Linha do Tempo (aba Hoje). */}
            <Link
              to="/linha-do-tempo"
              title="A tabela Equipe agora vive na Linha do Tempo, na visão de hoje."
              className="font-medium text-primary underline-offset-2 hover:underline"
            >
              Ver equipe agora →
            </Link>
          </span>
        </>
      )}
    </section>
  );
}

/**
 * Pulso verde do "ao vivo". A animação só existe para quem não pediu menos
 * movimento (`motion-safe:`): com prefers-reduced-motion o ponto fica estático,
 * e a informação não depende do movimento em nenhum dos dois casos.
 */
function Pulso() {
  return (
    <span className="relative flex h-2.5 w-2.5 shrink-0" aria-hidden>
      <span className="absolute inline-flex h-full w-full rounded-full bg-brand opacity-50 motion-safe:animate-ping" />
      <span className="relative inline-flex h-2.5 w-2.5 rounded-full bg-brand shadow-[0_0_0_4px_rgba(182,255,60,0.18)]" />
    </span>
  );
}

function Dot({ color }: { color: string }) {
  return (
    <span
      aria-hidden
      className="h-2.5 w-2.5 shrink-0 rounded-full"
      style={{ backgroundColor: color }}
    />
  );
}

/** Uma contagem da faixa: swatch + número grande + rótulo neutro. */
function Contagem({
  count,
  label,
  swatch,
  icon,
  title,
}: {
  count: number;
  label: string;
  swatch: ReactNode;
  icon?: ReactNode;
  title?: string;
}) {
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground" title={title}>
      {swatch}
      <span className="font-display text-lg font-semibold leading-none tracking-tight text-foreground tabular-nums">
        {count}
      </span>
      <span>{label}</span>
      {icon}
    </span>
  );
}
