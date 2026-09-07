// =============================================================================
// ALERTAS E PENDÊNCIAS na Visão Geral (F6, seção 4 do spec de 07/09/2026).
//
// Central ÚNICA com as duas naturezas que o gestor confunde hoje:
//  - GESTÃO (novo): as sete regras avaliadas no worker sobre os agregados
//    diários, lidas de `GET /alerts`. Vêm com título, contexto e link prontos
//    do servidor, porque o vocabulário ("variação e contexto, nunca
//    julgamento") tem de ter fonte única — a tela não reescreve a frase;
//  - ADMINISTRAÇÃO: as pendências que já existem no sino da barra superior
//    (apps sem categoria, dispositivos com alerta, exportações, convites).
//    REUSADAS, não reimplementadas: `usePendenciasAdmin()` é o mesmo hook do
//    sino, com as MESMAS queryKeys, então o TanStack resolve tudo do cache
//    compartilhado e este cartão não custa uma requisição a mais.
//
// Por que juntar: hoje a pendência só aparece se a pessoa clicar no sininho, e
// alerta de gestão não existia. Separá-los em dois cartões faria o gestor
// decidir qual olhar; juntar com abas deixa a decisão no lugar certo — o que
// exige DECISÃO de gestão fica na primeira aba.
//
// VOCABULÁRIO INVIOLÁVEL: nada de ranking, nada de julgamento sobre pessoas.
// Ocioso é estado de máquina e nunca aparece como improdutivo. Classificação é
// sobre aplicativos. `null` imprime "–", nunca 0%.
// =============================================================================

import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  AlertTriangle,
  ArrowRight,
  BellRing,
  CheckCircle2,
  ChevronRight,
  Info,
  Lock,
} from "lucide-react";

import { api } from "@/lib/api";
import { isAdmin } from "@/lib/roles";
import type {
  AlertItem,
  AlertsResponse,
  AppCatalogResponse,
  DeviceHealthSummaryResponse,
  ExportsResponse,
  MeResponse,
  UsersResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { BlockCard, ChartSkeleton, InlineError } from "@/components/dashboard/overviewKit";

/** Convite sem resposta por mais que isto vira pendência (igual ao sino). */
const CONVITE_PARADO_MS = 5 * 24 * 60 * 60 * 1000;

interface Pendencia {
  key: string;
  label: string;
  hint: string;
  to: string;
  action: string;
}

/**
 * Milissegundos do timestamp de um UUIDv7, ou null quando o id não é v7 —
 * mesma leitura do sino de pendências (o GET /users não devolve data de
 * convite). Id sem timestamp legível NÃO conta como convite parado: melhor
 * omitir do que alarmar errado.
 */
function uuidV7Millis(id: string): number | null {
  const hex = id.replace(/-/g, "");
  if (hex.length !== 32 || hex[12] !== "7") return null;
  const ms = Number.parseInt(hex.slice(0, 12), 16);
  return Number.isFinite(ms) ? ms : null;
}

/**
 * As pendências de ADMINISTRAÇÃO, nas mesmas queryKeys e URLs do
 * PendenciasBell. Um hook exportado em vez de código copiado: se a régua de
 * "convite parado" mudar, muda nos dois lugares de uma vez.
 */
export function usePendenciasAdmin(): { items: Pendencia[]; algumaFalhou: boolean } {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const admin = isAdmin(meQuery.data);

  const catalogQuery = useQuery({
    queryKey: ["app-catalog", { uncategorized: true, q: "" }],
    queryFn: () => api<AppCatalogResponse>("/app-catalog?uncategorized=true"),
    staleTime: 60_000,
  });

  const healthQuery = useQuery({
    queryKey: ["devices", "health-summary"],
    queryFn: () => api<DeviceHealthSummaryResponse>("/devices/health-summary"),
    staleTime: 60_000,
  });

  const exportsQuery = useQuery({
    queryKey: ["exports"],
    queryFn: () => api<ExportsResponse>("/exports"),
    staleTime: 60_000,
  });

  // GET /users é AdminPlus: para Viewer a query nem é habilitada (em vez de
  // tomar 403), e a pendência de convites simplesmente não existe para o papel.
  const usersQuery = useQuery({
    queryKey: ["users"],
    queryFn: () => api<UsersResponse>("/users"),
    enabled: admin,
    staleTime: 5 * 60 * 1000,
  });

  const items = useMemo<Pendencia[]>(() => {
    const list: Pendencia[] = [];

    const semCategoria = catalogQuery.data?.uncategorized_count ?? 0;
    if (semCategoria > 0) {
      list.push({
        key: "apps",
        label:
          semCategoria === 1
            ? "1 aplicativo sem categoria"
            : `${semCategoria} aplicativos sem categoria`,
        hint: "Tempo sem classificação não entra no índice: classificar muda o número.",
        to: "/configuracoes/categorias",
        action: "Classificar",
      });
    }

    const comAlerta = healthQuery.data?.with_alert ?? 0;
    if (comAlerta > 0) {
      list.push({
        key: "devices",
        label:
          comAlerta === 1
            ? "1 dispositivo precisa de atenção"
            : `${comAlerta} dispositivos precisam de atenção`,
        hint: "Sem comunicação, relógio, versão do agente ou ciência pendente.",
        to: "/dispositivos?filtro=alerta",
        action: "Ver dispositivos",
      });
    }

    const prontos = (exportsQuery.data?.items ?? []).filter(
      (item) => item.status === "done" && !item.expired,
    ).length;
    if (prontos > 0) {
      list.push({
        key: "exports",
        label:
          prontos === 1
            ? "1 exportação pronta para download"
            : `${prontos} exportações prontas para download`,
        hint: "Os arquivos têm prazo de validade: baixe antes de expirar.",
        to: "/relatorios/exportacoes",
        action: "Baixar",
      });
    }

    const agora = Date.now();
    const paradas = (usersQuery.data?.items ?? []).filter((user) => {
      if (user.status !== "invited") return false;
      const criadoEm = uuidV7Millis(user.id);
      return criadoEm !== null && agora - criadoEm > CONVITE_PARADO_MS;
    }).length;
    if (paradas > 0) {
      list.push({
        key: "invites",
        label:
          paradas === 1
            ? "1 convite sem resposta há mais de 5 dias"
            : `${paradas} convites sem resposta há mais de 5 dias`,
        hint: "Reenvie o convite ou remova quem não vai mais entrar.",
        to: "/configuracoes/usuarios",
        action: "Ver usuários",
      });
    }

    return list;
  }, [catalogQuery.data, healthQuery.data, exportsQuery.data, usersQuery.data]);

  const algumaFalhou =
    catalogQuery.isError ||
    healthQuery.isError ||
    exportsQuery.isError ||
    (admin && usersQuery.isError);

  return { items, algumaFalhou };
}

/** `GET /alerts` — os alertas de gestão vivos, já com frase e link do servidor. */
function useAlertsQuery() {
  return useQuery({
    queryKey: ["alerts"],
    queryFn: () => api<AlertsResponse>("/alerts"),
    // o motor reavalia a cada 15 min no worker; 5 min de staleTime evita
    // refetch a cada navegação sem deixar a tela velha
    staleTime: 5 * 60 * 1000,
  });
}

/**
 * Faixa de severidade: só intensidade de destaque, nunca nota. "atencao" é o
 * que pede decisão hoje; "informativo" é contexto que vale saber.
 */
const FAIXA: Record<AlertItem["severity"], { barra: string; icone: string; rotulo: string }> = {
  atencao: {
    barra: "bg-brand-red",
    icone: "text-brand-red",
    rotulo: "Pede decisão",
  },
  informativo: {
    barra: "bg-muted-foreground/40",
    icone: "text-muted-foreground",
    rotulo: "Contexto",
  },
};

/** Escopo em uma palavra, para a etiqueta discreta do cartão. */
const ESCOPO_LABEL: Record<AlertItem["scope_type"], string> = {
  organization: "Organização",
  team: "Equipe",
  person: "Pessoa",
  device: "Dispositivo",
};

export function AlertasCard({ className }: { className?: string }) {
  const [aba, setAba] = useState<"gestao" | "admin">("gestao");
  const alerts = useAlertsQuery();
  const { items: pendencias, algumaFalhou } = usePendenciasAdmin();

  const gestao = alerts.data?.items ?? [];
  const total = gestao.length + pendencias.length;

  // a aba de gestão é a primeira porque é a que exige DECISÃO; se ela está
  // vazia e há pendência de administração, a tela abre já na segunda
  const abaEfetiva = aba === "gestao" && gestao.length === 0 && pendencias.length > 0 ? "admin" : aba;

  return (
    <BlockCard
      className={className}
      title="Alertas e pendências"
      hint={
        total === 0
          ? "Nada esperando decisão no período"
          : `${total} ${total === 1 ? "item" : "itens"} · gestão e administração no mesmo lugar`
      }
      tools={
        <div
          role="group"
          aria-label="Natureza do alerta"
          className="inline-flex h-7 items-stretch rounded-md border border-input bg-card p-0.5"
        >
          {(
            [
              ["gestao", "Gestão", gestao.length],
              ["admin", "Administração", pendencias.length],
            ] as const
          ).map(([key, label, count]) => (
            <button
              key={key}
              type="button"
              aria-pressed={abaEfetiva === key}
              onClick={() => setAba(key)}
              className={cn(
                "rounded-[4px] px-2 text-[11px] font-medium tabular-nums transition-colors",
                abaEfetiva === key
                  ? "bg-primary/10 text-primary"
                  : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
              )}
            >
              {label}
              {count > 0 && <span className="ml-1 opacity-70">{count}</span>}
            </button>
          ))}
        </div>
      }
    >
      {abaEfetiva === "gestao" ? (
        alerts.isPending && alerts.data === undefined ? (
          <ChartSkeleton height={180} />
        ) : alerts.data === undefined ? (
          <InlineError
            message="Não foi possível carregar os alertas de gestão."
            onRetry={() => void alerts.refetch()}
            height={180}
          />
        ) : (
          <div className="space-y-2.5">
            {gestao.length === 0 ? (
              <VazioGestao data={alerts.data} />
            ) : (
              <ul className="m-0 list-none space-y-2 p-0">
                {gestao.map((alerta) => (
                  <AlertaLinha key={`${alerta.kind}:${alerta.scope_key}`} alerta={alerta} />
                ))}
              </ul>
            )}

            <RodapeGestao data={alerts.data} />
          </div>
        )
      ) : (
        <div className="space-y-2.5">
          {pendencias.length === 0 ? (
            <p className="flex items-start gap-2 rounded-md border border-border bg-muted/30 p-3 text-xs text-muted-foreground">
              <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
              <span>Nada pendente de administração. Catálogo, frota e convites em ordem.</span>
            </p>
          ) : (
            <ul className="m-0 list-none space-y-2 p-0">
              {pendencias.map((p) => (
                <li
                  key={p.key}
                  className="flex items-start gap-2.5 rounded-md border border-border p-2.5"
                >
                  <span aria-hidden className="mt-0.5 w-1 self-stretch rounded-full bg-muted-foreground/40" />
                  <span className="min-w-0 flex-1 space-y-0.5">
                    <span className="block text-xs font-semibold leading-snug">{p.label}</span>
                    <span className="block text-[11px] leading-snug text-muted-foreground">
                      {p.hint}
                    </span>
                  </span>
                  <Link to={p.to} className="inline-flex h-7 shrink-0 items-center gap-0.5 rounded-md px-2 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                    {p.action}
                    <ChevronRight className="h-3.5 w-3.5" aria-hidden />
                  </Link>
                </li>
              ))}
            </ul>
          )}

          {algumaFalhou && (
            <p className="text-[11px] text-muted-foreground">
              Algumas pendências não puderam ser carregadas agora.
            </p>
          )}
        </div>
      )}
    </BlockCard>
  );
}

/** Um alerta: faixa de severidade, título com o número, contexto e ação. */
function AlertaLinha({ alerta }: { alerta: AlertItem }) {
  const faixa = FAIXA[alerta.severity] ?? FAIXA.informativo;
  const Icone = alerta.severity === "atencao" ? AlertTriangle : Info;

  return (
    <li className="flex items-start gap-2.5 rounded-md border border-border p-2.5">
      {/* faixa de severidade: a cor é redundante ao ícone e ao rótulo do title,
          nunca a única pista (Seção 8.5) */}
      <span aria-hidden className={cn("mt-0.5 w-1 self-stretch rounded-full", faixa.barra)} />

      <span className="min-w-0 flex-1 space-y-1">
        <span className="flex flex-wrap items-baseline gap-x-1.5 gap-y-0.5">
          <Icone className={cn("h-3.5 w-3.5 shrink-0 translate-y-0.5", faixa.icone)} aria-hidden />
          <span className="text-xs font-semibold leading-snug" title={faixa.rotulo}>
            {alerta.title}
          </span>
          <span className="rounded border border-border px-1 text-[10px] uppercase tracking-wide text-muted-foreground">
            {ESCOPO_LABEL[alerta.scope_type] ?? alerta.scope_type}
          </span>
        </span>
        <span className="block text-[11px] leading-snug text-muted-foreground">
          {alerta.context}
        </span>
      </span>

      <Link to={alerta.link} className="inline-flex h-7 shrink-0 items-center gap-0.5 rounded-md px-2 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
        {alerta.action}
        <ChevronRight className="h-3.5 w-3.5" aria-hidden />
      </Link>
    </li>
  );
}

/**
 * Vazio da aba de gestão. Separa deliberadamente os dois "zeros": plano sem a
 * feature não é a mesma coisa que operação sem alerta, e um elogio no lugar
 * errado esconderia um upgrade.
 */
function VazioGestao({ data }: { data: AlertsResponse }) {
  if (!data.plan_includes_alerts) {
    return (
      <p className="flex items-start gap-2 rounded-md border border-border bg-muted/30 p-3 text-xs text-muted-foreground">
        <Lock className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
        <span>
          Os alertas de gestão fazem parte do plano Pro. As pendências de administração
          continuam disponíveis na outra aba.
        </span>
      </p>
    );
  }

  return (
    <p className="flex items-start gap-2 rounded-md border border-border bg-muted/30 p-3 text-xs text-muted-foreground">
      <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
      <span>
        Nenhuma variação fora da régua nesta semana. As regras seguem sendo reavaliadas a
        cada 15 minutos sobre os dados já agregados.
      </span>
    </p>
  );
}

/**
 * Rodapé da aba de gestão: o estado do OPT-IN de pessoa (decisão 5) e quando o
 * motor rodou. Dizer que as regras de pessoa estão desligadas é obrigatório —
 * sem isso o gestor conclui que ninguém trabalha fora do horário, quando na
 * verdade ninguém está medindo.
 */
function RodapeGestao({ data }: { data: AlertsResponse }) {
  const avaliado =
    data.evaluated_at !== null
      ? new Date(data.evaluated_at).toLocaleString("pt-BR", {
          day: "2-digit",
          month: "2-digit",
          hour: "2-digit",
          minute: "2-digit",
        })
      : null;

  return (
    <p className="flex flex-wrap items-center gap-x-2 gap-y-1 border-t border-border pt-2 text-[11px] leading-snug text-muted-foreground">
      <BellRing className="h-3 w-3 shrink-0" aria-hidden />
      {data.person_alerts_enabled ? (
        <span>
          Alertas por pessoa <span className="text-foreground">ligados</span> pela organização —
          sinais de equilíbrio (dias longos, fora do horário), nunca de desempenho.
        </span>
      ) : (
        <span
          title="Alertas de escopo pessoa são opt-in da organização (decisão 5 do produto). Enquanto desligados, as duas regras nem são avaliadas — a ausência de alerta não significa ausência do fato."
        >
          Alertas por pessoa <span className="text-foreground">desligados</span>: as regras de
          dias longos e de atividade fora do horário não estão sendo avaliadas.
        </span>
      )}
      {avaliado !== null && <span className="tabular-nums">· avaliado em {avaliado}</span>}
      {/* TODO(F6/Configurações › Alertas): o toggle de person_alerts_enabled entra
          na tela Configurações › Alertas, junto com o PATCH /organization — a
          coluna e a ação de auditoria (update_alert_prefs) já existem no backend. */}
      <Link
        to="/configuracoes"
        className="inline-flex items-center gap-0.5 text-foreground underline-offset-2 hover:underline"
      >
        Configurar alertas
        <ArrowRight className="h-3 w-3" aria-hidden />
      </Link>
    </p>
  );
}
