// =============================================================================
// Sino de PENDÊNCIAS da topbar: consolida o que está esperando uma ação humana
// e que hoje só aparece se a pessoa entrar na tela certa.
//
// VOCABULÁRIO: isto são "pendências", nunca "alertas". Alertas são a feature de
// e-mail (fleet_alerts das preferências); confundir os dois faria a pessoa
// procurar configuração de e-mail aqui.
//
// Zero requisição nova: cada pendência reusa a MESMA queryKey e a MESMA URL de
// uma query que o portal já faz, então o TanStack Query compartilha o cache com
// as telas de origem (Aplicativos, Dispositivos, Exportações, Auditoria).
// Polling de 60 s pausado em aba oculta, no padrão da presença.
//
// GET /users é PolicyAdminPlus: para Viewer a query nem é habilitada (em vez de
// tomar 403), e a pendência de convites simplesmente não existe para esse papel.
// =============================================================================

import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Bell, ChevronRight } from "lucide-react";
import { api } from "@/lib/api";
import { isAdmin } from "@/lib/roles";
import type {
  AppCatalogResponse,
  DeviceHealthSummaryResponse,
  ExportsResponse,
  MeResponse,
  UsersResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

/** Convite sem resposta por mais que isto vira pendência. */
const CONVITE_PARADO_MS = 5 * 24 * 60 * 60 * 1000;

/** Prefixo da chave de "lidas" no localStorage (uma por usuário). */
const LIDAS_KEY_PREFIX = "m351.pendencias.lidas.";

interface Pendencia {
  key: string;
  count: number;
  label: string;
  hint: string;
  to: string;
}

/**
 * Milissegundos do timestamp embutido num UUIDv7 (48 bits big-endian iniciais),
 * ou null quando o id não é v7. Os ids de usuário do backend são UUIDv7 e o
 * GET /users não devolve data de convite, é a mesma leitura que o backend faz
 * para derivar o enrolled_at do relatório de cobrança. Id sem timestamp
 * legível NÃO conta como convite parado: melhor omitir do que alarmar errado.
 */
function uuidV7Millis(id: string): number | null {
  const hex = id.replace(/-/g, "");
  if (hex.length !== 32 || hex[12] !== "7") return null;
  const ms = Number.parseInt(hex.slice(0, 12), 16);
  return Number.isFinite(ms) ? ms : null;
}

/** Lê a assinatura marcada como lida (todo acesso a localStorage em try/catch). */
function readLidas(userId: string | undefined): string | null {
  if (userId === undefined) return null;
  try {
    return window.localStorage.getItem(LIDAS_KEY_PREFIX + userId);
  } catch {
    return null;
  }
}

function writeLidas(userId: string | undefined, signature: string): void {
  if (userId === undefined) return;
  try {
    window.localStorage.setItem(LIDAS_KEY_PREFIX + userId, signature);
  } catch {
    // Modo privado/cota cheia: perder a marcação é aceitável, quebrar não.
  }
}

export function PendenciasBell() {
  const navigate = useNavigate();

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const userId = meQuery.data?.user.id;
  const admin = isAdmin(meQuery.data);

  // Apps sem categoria, mesma key/URL do badge da tela Aplicativos.
  const catalogQuery = useQuery({
    queryKey: ["app-catalog", { uncategorized: true, q: "" }],
    queryFn: () => api<AppCatalogResponse>("/app-catalog?uncategorized=true"),
    staleTime: 60_000,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  });

  // Dispositivos com alerta, mesma key da Visão Geral e de Dispositivos.
  const healthQuery = useQuery({
    queryKey: ["devices", "health-summary"],
    queryFn: () => api<DeviceHealthSummaryResponse>("/devices/health-summary"),
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  });

  // Exportações prontas, mesma key da tela Exportações.
  const exportsQuery = useQuery({
    queryKey: ["exports"],
    queryFn: () => api<ExportsResponse>("/exports"),
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  });

  // Convites parados, mesma key do filtro por ator da Auditoria (Admin+).
  const usersQuery = useQuery({
    queryKey: ["users"],
    queryFn: () => api<UsersResponse>("/users"),
    enabled: admin,
    staleTime: 5 * 60 * 1000,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  });

  const pendencias = useMemo<Pendencia[]>(() => {
    const list: Pendencia[] = [];

    const semCategoria = catalogQuery.data?.uncategorized_count ?? 0;
    if (semCategoria > 0) {
      list.push({
        key: "apps",
        count: semCategoria,
        label:
          semCategoria === 1
            ? "1 aplicativo sem categoria"
            : `${semCategoria} aplicativos sem categoria`,
        hint: "Categorize para os relatórios refletirem o trabalho da equipe.",
        to: "/apps",
      });
    }

    const comAlerta = healthQuery.data?.with_alert ?? 0;
    if (comAlerta > 0) {
      list.push({
        key: "devices",
        count: comAlerta,
        label:
          comAlerta === 1
            ? "1 dispositivo precisa de atenção"
            : `${comAlerta} dispositivos precisam de atenção`,
        hint: "Sem comunicação, relógio, versão do agente ou ciência pendente.",
        to: "/dispositivos?filtro=alerta",
      });
    }

    // "Pronto para download": concluído e ainda dentro do prazo de validade.
    const prontos = (exportsQuery.data?.items ?? []).filter(
      (item) => item.status === "done" && !item.expired,
    ).length;
    if (prontos > 0) {
      list.push({
        key: "exports",
        count: prontos,
        label:
          prontos === 1
            ? "1 exportação pronta para download"
            : `${prontos} exportações prontas para download`,
        hint: "Os arquivos têm prazo de validade: baixe antes de expirar.",
        to: "/relatorios/exportacoes",
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
        count: paradas,
        label:
          paradas === 1
            ? "1 convite sem resposta há mais de 5 dias"
            : `${paradas} convites sem resposta há mais de 5 dias`,
        hint: "Reenvie o convite ou remova quem não vai mais entrar.",
        to: "/configuracoes/usuarios",
      });
    }

    return list;
  }, [catalogQuery.data, healthQuery.data, exportsQuery.data, usersQuery.data]);

  const total = pendencias.reduce((sum, p) => sum + p.count, 0);

  // Assinatura do estado atual: marcar como lidas esconde o badge só até algo
  // mudar (contagem nova ou pendência nova traz o badge de volta).
  const signature = pendencias.map((p) => `${p.key}:${p.count}`).join("|");
  // Relê o localStorage quando o /me resolve (userId chega depois do primeiro
  // render) e depois de cada marcação, daí o contador de releitura.
  const [releituras, setReleituras] = useState(0);
  const lidas = useMemo(() => readLidas(userId), [userId, releituras]);
  const badgeVisible = total > 0 && lidas !== signature;

  function marcarComoLidas(): void {
    writeLidas(userId, signature);
    setReleituras((n) => n + 1);
  }

  const algumaFalhou =
    catalogQuery.isError ||
    healthQuery.isError ||
    exportsQuery.isError ||
    (admin && usersQuery.isError);

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          className="relative h-9 w-9"
          aria-label={
            total === 0
              ? "Pendências: nenhuma"
              : `Pendências: ${total} ${total === 1 ? "item" : "itens"}`
          }
        >
          <Bell className="h-4 w-4" aria-hidden="true" />
          {badgeVisible && (
            <span
              aria-hidden="true"
              className={cn(
                "absolute -right-0.5 -top-0.5 flex h-4 min-w-[1rem] items-center justify-center",
                "rounded-full bg-brand-red px-1 text-[10px] font-semibold tabular-nums text-background",
              )}
            >
              {total > 9 ? "9+" : total}
            </span>
          )}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-80">
        <DropdownMenuLabel className="flex items-baseline justify-between gap-2">
          <span>Pendências</span>
          {total > 0 && (
            <span className="text-xs font-normal tabular-nums text-muted-foreground">
              {total} {total === 1 ? "item" : "itens"}
            </span>
          )}
        </DropdownMenuLabel>
        <DropdownMenuSeparator />

        {pendencias.length === 0 ? (
          <p className="px-2 py-3 text-sm text-muted-foreground">
            Nada pendente por aqui. Tudo em ordem.
          </p>
        ) : (
          pendencias.map((p) => (
            <DropdownMenuItem
              key={p.key}
              onSelect={() => navigate(p.to)}
              className="flex items-start gap-2 py-2"
            >
              <span className="min-w-0 flex-1 space-y-0.5">
                <span className="block text-sm font-medium tabular-nums">{p.label}</span>
                <span className="block text-xs text-muted-foreground">{p.hint}</span>
              </span>
              <ChevronRight className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            </DropdownMenuItem>
          ))
        )}

        {algumaFalhou && (
          <p className="px-2 pb-1 pt-2 text-xs text-muted-foreground">
            Algumas pendências não puderam ser carregadas agora.
          </p>
        )}

        {total > 0 && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem
              // Não fecha o menu: a pessoa vê o badge sair e segue lendo a lista.
              onSelect={(e) => {
                e.preventDefault();
                marcarComoLidas();
              }}
              disabled={!badgeVisible}
              className="justify-center text-xs font-medium"
            >
              {badgeVisible ? "Marcar como lidas" : "Marcadas como lidas"}
            </DropdownMenuItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
