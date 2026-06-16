// =============================================================================
// Auditoria de acesso (/configuracoes/auditoria — F4.7). Tabela de GET
// /audit-logs (PolicyAdminPlus = Owner + Admin) com filtros de período, ator e
// ação traduzida. Owner/Admin apenas: o Viewer NÃO vê a aba (ConfiguracoesLayout)
// nem esta tela (gate defensivo abaixo, espelhando a PrivacidadePage).
//
// Contrato do endpoint (programado em paralelo com o backend):
//   GET /audit-logs?from&to&actor&action&page&page_size
//   -> { items:[AuditLogItem], total, page, page_size }
//   - from/to no fuso do tenant (default últimos 30 dias se ausentes; range <= 92).
//   - actor = filtro opcional por user_id; action = filtro opcional exato.
//   - ordenado occurred_at desc; page_size default 50, máx 100.
//   - a própria leitura de /audit-logs NÃO é auditada (evita recursão).
//
// O período usa o mesmo preset de 7/14/30/92 dias das telas de relatório (todos
// dentro do teto de 92 dias do contrato). O filtro por ator vem de GET /users
// (mesma audiência PolicyAdminPlus). Vocabulário neutro, sem travessão.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, Eye, ScrollText } from "lucide-react";
import { api } from "@/lib/api";
import { auditActionLabel, auditDetailSummary, auditTargetSummary, AUDIT_ACTION_FILTER_OPTIONS } from "@/lib/audit";
import { ddmm, formatDateTime } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type { AuditLogItem, AuditLogsResponse, MeResponse, UsersResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { PeriodPresetGroup, useReportRange } from "@/components/reports/filters";

/** page_size default do contrato (máx 100). */
const PAGE_SIZE = 50;

/** Classe base dos selects nativos — mesmo visual h-9 dos demais controles de filtro. */
const selectClass = cn(
  "h-9 rounded-md border border-input bg-card px-3 text-sm text-foreground",
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
);

export function AuditoriaPage() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const me = meQuery.data;
  const timezone = me?.organization.timezone ?? null;
  const canAudit = isAdmin(me);

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Auditoria de acesso</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Registro de quem visualizou ou alterou dados pessoais e de quando isso aconteceu. A trilha
          é somente leitura e não pode ser editada ou apagada.
        </p>
      </div>

      {meQuery.isPending ? (
        // Aguarda o papel antes de decidir o que mostrar (evita o flash do aviso
        // de viewer para um admin/owner num cache frio do GET /me).
        <Card>
          <SkeletonRows />
        </Card>
      ) : !canAudit ? (
        <ViewerNotice />
      ) : (
        <AuditTable timezone={timezone} />
      )}
    </div>
  );
}

function ViewerNotice() {
  return (
    <Card>
      <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
        <span className="flex h-12 w-12 items-center justify-center rounded-full bg-muted">
          <Eye className="h-6 w-6 text-muted-foreground" aria-hidden />
        </span>
        <p className="text-base font-medium">Sem permissão para ver a auditoria</p>
        <p className="max-w-md text-sm text-muted-foreground">
          A trilha de auditoria fica disponível para Administradores e Proprietários da organização.
        </p>
      </div>
    </Card>
  );
}

function AuditTable({ timezone }: { timezone: string | null }) {
  const { range, activePreset, applyPreset } = useReportRange(timezone);
  const [actor, setActor] = useState("");
  const [action, setAction] = useState("");
  const [page, setPage] = useState(1);

  // Trocar período/ator/ação volta para a primeira página.
  useEffect(() => {
    setPage(1);
  }, [range?.from, range?.to, actor, action]);

  // Lista de atores para o filtro (GET /users, mesma audiência PolicyAdminPlus).
  const usersQuery = useQuery({
    queryKey: ["users"],
    queryFn: () => api<UsersResponse>("/users"),
    staleTime: 5 * 60 * 1000,
  });
  const actors = useMemo(() => {
    const items = usersQuery.data?.items ?? [];
    return [...items].sort((a, b) => a.display_name.localeCompare(b.display_name, "pt-BR"));
  }, [usersQuery.data]);

  const actorParam = actor.length > 0 ? `&actor=${encodeURIComponent(actor)}` : "";
  const actionParam = action.length > 0 ? `&action=${encodeURIComponent(action)}` : "";

  const logsQuery = useQuery({
    queryKey: ["audit-logs", { from: range?.from, to: range?.to, actor, action, page, page_size: PAGE_SIZE }],
    queryFn: () =>
      api<AuditLogsResponse>(
        `/audit-logs?from=${range?.from ?? ""}&to=${range?.to ?? ""}${actorParam}${actionParam}&page=${page}&page_size=${PAGE_SIZE}`,
      ),
    enabled: range !== null,
    placeholderData: (prev) => prev,
  });
  const data = logsQuery.data;
  const hasFilters = actor.length > 0 || action.length > 0;

  function clearFilters() {
    setActor("");
    setAction("");
  }

  return (
    <>
      {/* Barra de filtros (controles em h-9): período, ator, ação. */}
      <Card>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
          <PeriodPresetGroup active={activePreset} onSelect={applyPreset} disabled={range === null} />
          {range !== null && (
            <span className="text-xs tabular-nums text-muted-foreground">
              {ddmm(range.from)} a {ddmm(range.to)}
            </span>
          )}

          <select
            value={actor}
            onChange={(e) => setActor(e.target.value)}
            aria-label="Filtrar por ator"
            className={cn(selectClass, "max-w-[16rem]")}
          >
            <option value="">Todos os atores</option>
            {actors.map((u) => (
              <option key={u.id} value={u.id}>
                {u.display_name}
              </option>
            ))}
          </select>

          <select
            value={action}
            onChange={(e) => setAction(e.target.value)}
            aria-label="Filtrar por ação"
            className={cn(selectClass, "max-w-[18rem]")}
          >
            <option value="">Todas as ações</option>
            {AUDIT_ACTION_FILTER_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>

          {hasFilters && (
            <Button variant="ghost" size="sm" className="h-9" onClick={clearFilters}>
              Limpar filtros
            </Button>
          )}
        </div>
      </Card>

      {logsQuery.isError && data !== undefined && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar a trilha. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void logsQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      <Card>
        {logsQuery.isError && data === undefined ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(logsQuery.error)}</p>
            <Button variant="outline" onClick={() => void logsQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : (
          <div className={cn("overflow-x-auto", logsQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Quando</th>
                  <th scope="col" className="px-3 py-2">Ator</th>
                  <th scope="col" className="px-3 py-2">IP</th>
                  <th scope="col" className="px-3 py-2">Ação</th>
                  <th scope="col" className="px-3 py-2">Alvo</th>
                  <th scope="col" className="px-3 py-2">Detalhe</th>
                </tr>
              </thead>
              <tbody>
                {data === undefined ? (
                  Array.from({ length: 10 }, (_, i) => (
                    <tr key={i} className="border-b last:border-b-0">
                      <td colSpan={6} className="px-6 py-2">
                        <Skeleton className="h-8 w-full" />
                      </td>
                    </tr>
                  ))
                ) : data.items.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="px-6 py-10 text-center text-sm text-muted-foreground">
                      {hasFilters ? (
                        <span className="inline-flex flex-col items-center gap-2">
                          <span>Nenhum registro com os filtros atuais.</span>
                          <Button variant="outline" size="sm" onClick={clearFilters}>
                            Limpar filtros
                          </Button>
                        </span>
                      ) : (
                        <span className="inline-flex flex-col items-center gap-2">
                          <ScrollText className="h-6 w-6 text-muted-foreground" aria-hidden />
                          <span>Nenhum registro no período.</span>
                        </span>
                      )}
                    </td>
                  </tr>
                ) : (
                  data.items.map((item) => <AuditRow key={item.id} item={item} timezone={timezone} />)
                )}
              </tbody>
            </table>

            {data !== undefined && data.items.length > 0 && (
              <Pagination page={page} total={data.total} onPage={setPage} />
            )}
          </div>
        )}
      </Card>
    </>
  );
}

function AuditRow({ item, timezone }: { item: AuditLogItem; timezone: string | null }) {
  // Ações de sistema (sem ator resolvido) recebem o rótulo neutro "Sistema".
  const actorLabel = item.actor_name ?? "Sistema";
  const isSystem = item.actor_name === null;

  return (
    <tr className="border-b align-top transition-colors last:border-b-0 hover:bg-accent/50">
      <td className="whitespace-nowrap px-6 py-2 tabular-nums text-muted-foreground">
        {timezone !== null ? formatDateTime(item.occurred_at, timezone) : "—"}
      </td>
      <td className="px-3 py-2">
        <span className={cn("block max-w-[16rem] truncate", isSystem ? "italic text-muted-foreground" : "font-medium")}>
          {actorLabel}
        </span>
      </td>
      <td className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground">{item.actor_ip ?? "—"}</td>
      <td className="px-3 py-2">{auditActionLabel(item.action)}</td>
      <td className="max-w-[14rem] truncate px-3 py-2 text-muted-foreground">
        {auditTargetSummary(item.target_type, item.target_id)}
      </td>
      <td className="max-w-[18rem] truncate px-3 py-2 text-muted-foreground">{auditDetailSummary(item.detail)}</td>
    </tr>
  );
}

function Pagination({ page, total, onPage }: { page: number; total: number; onPage: (p: number) => void }) {
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE));
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 border-t px-6 py-3 text-sm">
      <span className="tabular-nums text-muted-foreground">
        {total > 0
          ? `${(page - 1) * PAGE_SIZE + 1} a ${Math.min(page * PAGE_SIZE, total)} de ${total} registros`
          : "Nenhum registro"}
      </span>
      {total > PAGE_SIZE && (
        <div className="flex gap-2">
          <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => onPage(Math.max(1, page - 1))}>
            Anterior
          </Button>
          <Button variant="outline" size="sm" disabled={page >= pageCount} onClick={() => onPage(page + 1)}>
            Próxima
          </Button>
        </div>
      )}
    </div>
  );
}

function SkeletonRows() {
  return (
    <div className="space-y-px p-4">
      {Array.from({ length: 6 }, (_, i) => (
        <div key={i} className="flex items-center gap-4 py-2">
          <Skeleton className="h-5 w-32" />
          <Skeleton className="h-5 w-40" />
          <Skeleton className="ml-auto h-5 w-48" />
        </div>
      ))}
    </div>
  );
}
