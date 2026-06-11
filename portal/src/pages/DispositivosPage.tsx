import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Archive,
  ArchiveRestore,
  ChevronLeft,
  ChevronRight,
  Ellipsis,
  MonitorSmartphone,
  Pencil,
  Search,
  Tags,
  TriangleAlert,
} from "lucide-react";
import { api } from "@/lib/api";
import { formatRelative, stateLabels } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type {
  DeviceItem,
  DevicePatchRequest,
  MeResponse,
  PagedResponse,
  PresenceItem,
  PresenceResponse,
  PresenceState,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";

const PAGE_SIZE = 50;
const CLOCK_SKEW_LIMIT_MS = 120_000; // Seção 8.7: |offset| > 2min = relógio dessincronizado.

type DeviceStatus = DeviceItem["status"];
type StatusFilter = DeviceStatus | "";

const deviceStatusLabels: Record<DeviceStatus, string> = {
  active: "Ativo",
  paused: "Pausado",
  archived: "Arquivado",
  revoked: "Revogado",
};

// paused em cinza-azulado (slate): âmbar ficava idêntico ao dot de "Inativo"
// e ao badge "não suportado" - pausado não é alerta, é um estado neutro.
const deviceStatusClasses: Record<DeviceStatus, string> = {
  active: "bg-emerald-100 text-emerald-800",
  paused: "bg-slate-200 text-slate-700",
  archived: "bg-gray-100 text-gray-600",
  revoked: "bg-red-100 text-red-700",
};

/**
 * Dots dos estados de presença (Seção 8.5). Redundância NÃO-cromática: o rótulo
 * textual acompanha sempre o dot; off_clean é só contorno; no_data tem hachura
 * diagonal + ícone de alerta (tratado à parte em PresenceStateCell).
 */
const presenceDotClasses: Record<Exclude<PresenceState, "no_data">, string> = {
  active: "bg-green-500",
  idle: "bg-amber-500",
  locked: "bg-slate-500",
  no_session: "bg-slate-400",
  off_clean: "border-2 border-gray-400 bg-transparent",
};

/** Offset atual (em minutos) de um fuso IANA, ex.: "America/Sao_Paulo" → -180. */
function timezoneOffsetMinutes(timezone: string): number | null {
  try {
    const parts = new Intl.DateTimeFormat("en-US", {
      timeZone: timezone,
      timeZoneName: "longOffset",
    }).formatToParts(new Date());
    const name = parts.find((p) => p.type === "timeZoneName")?.value ?? "";
    if (name === "GMT") return 0;
    const m = /^GMT([+-])(\d{2}):(\d{2})$/.exec(name);
    if (m === null) return null;
    const sign = m[1] === "-" ? -1 : 1;
    return sign * (Number(m[2]) * 60 + Number(m[3]));
  } catch {
    return null;
  }
}

/** Rótulo do badge de fuso divergente, ex.: -180 → "GMT-3"; -210 → "GMT-3:30". */
function gmtLabel(offsetMin: number): string {
  const sign = offsetMin < 0 ? "-" : "+";
  const abs = Math.abs(offsetMin);
  const h = Math.floor(abs / 60);
  const m = abs % 60;
  return m === 0 ? `GMT${sign}${h}` : `GMT${sign}${h}:${m.toString().padStart(2, "0")}`;
}

/** Coluna "Estado agora": dot + rótulo canônico; sem entrada na presença = esmaecido. */
function PresenceStateCell({ presence }: { presence: PresenceItem | undefined }) {
  if (presence === undefined) {
    return <span className="text-muted-foreground/60">sem dados ainda</span>;
  }
  const state = presence.presence_state;
  if (state === "no_data") {
    return (
      <span className="inline-flex items-center gap-1.5 whitespace-nowrap font-medium text-red-700">
        <span
          aria-hidden="true"
          className="h-2.5 w-2.5 shrink-0 rounded-full"
          style={{
            backgroundImage:
              "repeating-linear-gradient(45deg, #dc2626 0px, #dc2626 2px, #fca5a5 2px, #fca5a5 4px)",
          }}
        />
        <TriangleAlert className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        {stateLabels[state]}
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
      <span
        aria-hidden="true"
        className={cn("h-2.5 w-2.5 shrink-0 rounded-full", presenceDotClasses[state])}
      />
      {stateLabels[state]}
    </span>
  );
}

const headerCell = "px-3 py-2 text-left font-medium";
const bodyCell = "px-3 py-1.5 align-middle";

/** showActions: coluna de ações por linha - só admin/owner (viewer nem vê a coluna). */
function TableHead({ showActions }: { showActions: boolean }) {
  return (
    <thead>
      <tr className="border-b text-xs uppercase tracking-wide text-muted-foreground">
        <th className={headerCell}>Nome</th>
        <th className={headerCell}>Hostname</th>
        <th className={headerCell}>Estado agora</th>
        <th className={headerCell}>Usuário</th>
        <th className={headerCell}>Último contato</th>
        <th className={headerCell}>Versão</th>
        <th className={headerCell}>Fuso</th>
        <th className={headerCell}>Relógio</th>
        <th className={headerCell}>Status</th>
        <th className={headerCell}>Tags</th>
        {showActions && <th className={cn(headerCell, "w-12 text-right")}>Ações</th>}
      </tr>
    </thead>
  );
}

const skeletonWidths = ["w-32", "w-24", "w-20", "w-20", "w-16", "w-12", "w-12", "w-24", "w-14", "w-20"];

/**
 * Lista de dispositivos com saúde dos agentes (F2, Seção 8.7) + ações de
 * curadoria da F3.7 (admin/owner): renomear, etiquetas e arquivar/reativar via
 * PATCH /devices/{id}. Toggle "Incluir arquivados" (OFF por default) controla
 * o include_archived da listagem; o filtro de status "Arquivado" já inclui os
 * arquivados por definição e ignora o toggle.
 */
export function DispositivosPage() {
  const navigate = useNavigate();

  // Valores digitados (imediatos) e valores aplicados (após debounce de 400ms).
  const [qInput, setQInput] = useState("");
  const [tagInput, setTagInput] = useState("");
  const [q, setQ] = useState("");
  const [tag, setTag] = useState("");
  const [status, setStatus] = useState<StatusFilter>("");
  const [includeArchived, setIncludeArchived] = useState(false);
  const [page, setPage] = useState(1);

  // Ação aberta no momento (dialog) - as PRIMEIRAS mutações desta tela (F3.7).
  const [action, setAction] = useState<DeviceAction | null>(null);

  useEffect(() => {
    const handle = window.setTimeout(() => {
      const next = qInput.trim();
      if (next !== q) {
        setQ(next);
        setPage(1);
      }
    }, 400);
    return () => window.clearTimeout(handle);
  }, [qInput, q]);

  useEffect(() => {
    const handle = window.setTimeout(() => {
      const next = tagInput.trim();
      if (next !== tag) {
        setTag(next);
        setPage(1);
      }
    }, 400);
    return () => window.clearTimeout(handle);
  }, [tagInput, tag]);

  // include_archived é DEFAULT true na API (comportamento antigo preservado);
  // o portal nasce com o toggle OFF e manda =false explícito. Com o filtro de
  // status "Arquivado" o toggle é ignorado: o ?status= já inclui por definição.
  const effectiveIncludeArchived = status === "archived" || includeArchived;

  const devicesQuery = useQuery({
    queryKey: ["devices", { page, q, status, tag, include_archived: effectiveIncludeArchived }],
    queryFn: () => {
      const params = new URLSearchParams();
      params.set("page", String(page));
      params.set("page_size", String(PAGE_SIZE));
      if (q.length > 0) params.set("q", q);
      if (status.length > 0) params.set("status", status);
      if (tag.length > 0) params.set("tag", tag);
      if (!effectiveIncludeArchived) params.set("include_archived", "false");
      return api<PagedResponse<DeviceItem>>(`/devices?${params.toString()}`);
    },
    placeholderData: (prev) => prev,
  });

  const presenceQuery = useQuery({
    queryKey: ["dashboard", "presence"],
    queryFn: () => api<PresenceResponse>("/dashboard/presence"),
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    placeholderData: (prev) => prev,
  });

  // Mesma queryKey do AppShell - resolve do cache, sem requisição extra.
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });

  const presenceByDevice = useMemo(() => {
    const map = new Map<string, PresenceItem>();
    for (const item of presenceQuery.data?.items ?? []) {
      map.set(item.device_id, item);
    }
    return map;
  }, [presenceQuery.data]);

  const admin = isAdmin(meQuery.data);

  const orgTimezone = meQuery.data?.organization.timezone;
  const orgOffsetMin = useMemo(
    () => (orgTimezone !== undefined ? timezoneOffsetMinutes(orgTimezone) : null),
    [orgTimezone],
  );

  // Relógio de referência para "há Xmin": server_time da presença (nunca o relógio local,
  // exceto como fallback enquanto a presença não carregou).
  const referenceTime = presenceQuery.data?.server_time ?? new Date().toISOString();

  const hasActiveFilters = q.length > 0 || tag.length > 0 || status.length > 0 || includeArchived;

  function clearFilters() {
    setQInput("");
    setTagInput("");
    setQ("");
    setTag("");
    setStatus("");
    setIncludeArchived(false);
    setPage(1);
  }

  function goToTimeline(deviceId: string) {
    navigate(`/linha-do-tempo?device=${encodeURIComponent(deviceId)}`);
  }

  const devices = devicesQuery.data;
  const total = devices?.total ?? 0;
  const pageSize = devices?.page_size ?? PAGE_SIZE;
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, total);
  const hasNext = page * pageSize < total;

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">Dispositivos</h1>
        <p className="text-sm text-muted-foreground">
          Inventário e saúde dos agentes: estado agora, último contato, versão, fuso e relógio.
        </p>
      </div>

      {/* Filtros - qualquer mudança volta para a página 1 */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative w-full max-w-xs">
          <Search
            className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            value={qInput}
            onChange={(e) => setQInput(e.target.value)}
            placeholder="Buscar por nome ou hostname"
            aria-label="Buscar dispositivo"
            className="h-9 pl-9"
          />
        </div>
        <select
          value={status}
          onChange={(e) => {
            setStatus(e.target.value as StatusFilter);
            setPage(1);
          }}
          aria-label="Filtrar por status"
          className={cn(
            "h-9 rounded-md border border-input bg-card px-3 text-sm",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
          )}
        >
          <option value="">Todos</option>
          <option value="active">Ativo</option>
          <option value="paused">Pausado</option>
          <option value="archived">Arquivado</option>
          <option value="revoked">Revogado</option>
        </select>
        <Input
          value={tagInput}
          onChange={(e) => setTagInput(e.target.value)}
          placeholder="Filtrar por tag"
          aria-label="Filtrar por tag"
          className="h-9 w-full max-w-[12rem]"
        />
        <label
          className={cn(
            "flex h-9 items-center gap-2 text-sm",
            status === "archived" ? "cursor-not-allowed text-muted-foreground" : "cursor-pointer",
          )}
          title={
            status === "archived"
              ? "O filtro de status Arquivado já inclui os dispositivos arquivados"
              : undefined
          }
        >
          <input
            type="checkbox"
            // Com status "Arquivado" o toggle não tem efeito: aparece marcado e
            // desabilitado, sem perder a escolha do usuário ao trocar o filtro.
            checked={status === "archived" ? true : includeArchived}
            disabled={status === "archived"}
            onChange={(e) => {
              setIncludeArchived(e.target.checked);
              setPage(1);
            }}
            className="h-4 w-4 accent-primary disabled:cursor-not-allowed"
          />
          Incluir arquivados
        </label>
      </div>

      {presenceQuery.isError && presenceQuery.data === undefined && (
        <div className="flex items-center justify-between gap-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
          <span className="flex items-center gap-2">
            <TriangleAlert className="h-4 w-4 shrink-0" aria-hidden="true" />
            Não foi possível carregar o estado de presença dos dispositivos.
          </span>
          <Button variant="outline" size="sm" onClick={() => void presenceQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      <Card className="overflow-hidden">
        {devicesQuery.isPending ? (
          // Skeleton com a geometria final da tabela (nunca spinner de página inteira).
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <TableHead showActions={admin} />
              <tbody>
                {Array.from({ length: 8 }).map((_, row) => (
                  <tr key={row} className="h-9 border-b last:border-0">
                    {(admin ? [...skeletonWidths, "w-8"] : skeletonWidths).map((width, col) => (
                      <td key={col} className={bodyCell}>
                        <Skeleton className={cn("h-4", width)} />
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : devicesQuery.isError ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <p className="text-sm text-muted-foreground">{genericErrorMessage(devicesQuery.error)}</p>
            <Button variant="outline" onClick={() => void devicesQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : total === 0 && !hasActiveFilters ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <MonitorSmartphone className="h-8 w-8 text-muted-foreground" aria-hidden="true" />
            <p className="max-w-md text-sm text-muted-foreground">
              Nenhum dispositivo ainda. Crie uma chave em{" "}
              <Link
                to="/configuracoes/chaves"
                className="font-medium text-primary underline-offset-4 hover:underline"
              >
                Configurações → Chaves
              </Link>{" "}
              e instale o agente.
            </p>
          </div>
        ) : total === 0 ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <p className="text-sm text-muted-foreground">Nenhum dispositivo corresponde aos filtros.</p>
            <Button variant="outline" onClick={clearFilters}>
              Limpar filtros
            </Button>
          </div>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table
                className={cn(
                  "w-full text-sm transition-opacity",
                  devicesQuery.isFetching && "opacity-70",
                )}
              >
                <TableHead showActions={admin} />
                <tbody>
                  {(devices?.items ?? []).map((d) => {
                    const p = presenceByDevice.get(d.id);
                    const noData = p?.presence_state === "no_data";
                    const clockSkewed = Math.abs(d.clock_offset_ms) > CLOCK_SKEW_LIMIT_MS;
                    const tzDiverges =
                      d.tz_offset_min !== null &&
                      orgOffsetMin !== null &&
                      d.tz_offset_min !== orgOffsetMin;
                    return (
                      <tr
                        key={d.id}
                        tabIndex={0}
                        onClick={() => goToTimeline(d.id)}
                        onKeyDown={(e) => {
                          if (e.key === "Enter") goToTimeline(d.id);
                        }}
                        className={cn(
                          "h-9 cursor-pointer border-b transition-colors last:border-0 focus-visible:outline-none",
                          noData
                            ? "bg-red-50 hover:bg-red-100/70 focus-visible:bg-red-100/70"
                            : "hover:bg-accent focus-visible:bg-accent",
                        )}
                      >
                        <td className={bodyCell}>
                          <span className="flex items-center gap-2 whitespace-nowrap">
                            <span className="font-semibold">{d.display_name ?? d.hostname}</span>
                            {d.os_type === "server" && (
                              <span
                                className="rounded-full border border-amber-300 bg-amber-50 px-2 py-0.5 text-xs text-amber-800"
                                title="Sistemas operacionais de servidor não são suportados"
                              >
                                não suportado
                              </span>
                            )}
                          </span>
                        </td>
                        <td className={cn(bodyCell, "whitespace-nowrap text-muted-foreground")}>
                          {d.hostname}
                        </td>
                        <td className={bodyCell}>
                          <PresenceStateCell presence={p} />
                        </td>
                        <td className={cn(bodyCell, "whitespace-nowrap")}>
                          {p?.windows_username ?? <span className="text-muted-foreground">-</span>}
                        </td>
                        <td className={cn(bodyCell, "whitespace-nowrap tabular-nums text-muted-foreground")}>
                          {d.last_seen_at !== null ? formatRelative(d.last_seen_at, referenceTime) : "nunca"}
                        </td>
                        <td className={cn(bodyCell, "whitespace-nowrap tabular-nums text-muted-foreground")}>
                          {d.agent_version ?? "-"}
                        </td>
                        <td className={bodyCell}>
                          {tzDiverges && d.tz_offset_min !== null ? (
                            <span
                              className="whitespace-nowrap rounded-full bg-secondary px-2 py-0.5 text-xs tabular-nums text-secondary-foreground"
                              title="Fuso da máquina difere do fuso da organização"
                            >
                              {gmtLabel(d.tz_offset_min)}
                            </span>
                          ) : (
                            <span className="text-muted-foreground">-</span>
                          )}
                        </td>
                        <td className={bodyCell}>
                          {clockSkewed ? (
                            <span
                              className="inline-flex items-center gap-1 whitespace-nowrap rounded-full bg-red-100 px-2 py-0.5 text-xs font-medium text-red-700"
                              title={`Diferença de ${Math.round(Math.abs(d.clock_offset_ms) / 1000)}s em relação ao servidor`}
                            >
                              <TriangleAlert className="h-3 w-3 shrink-0" aria-hidden="true" />
                              relógio dessincronizado
                            </span>
                          ) : (
                            <span className="text-muted-foreground">-</span>
                          )}
                        </td>
                        <td className={bodyCell}>
                          <span
                            className={cn(
                              "whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium",
                              deviceStatusClasses[d.status],
                            )}
                          >
                            {deviceStatusLabels[d.status]}
                          </span>
                        </td>
                        <td className={bodyCell}>
                          {d.tags && d.tags.length > 0 ? (
                            <span className="flex flex-wrap gap-1">
                              {d.tags.map((t) => (
                                <span
                                  key={t}
                                  className="whitespace-nowrap rounded-full bg-secondary px-2 py-0.5 text-xs text-secondary-foreground"
                                >
                                  {t}
                                </span>
                              ))}
                            </span>
                          ) : (
                            <span className="text-muted-foreground">-</span>
                          )}
                        </td>
                        {admin && (
                          // stopPropagation: a linha inteira navega para a
                          // timeline; o menu de ações não pode disparar isso.
                          <td
                            className={cn(bodyCell, "w-12 text-right")}
                            onClick={(e) => e.stopPropagation()}
                            onKeyDown={(e) => e.stopPropagation()}
                          >
                            {d.status === "revoked" ? (
                              // revoked é terminal (só o re-enroll revive): o
                              // backend responde 400 a qualquer PATCH.
                              <span className="text-muted-foreground">-</span>
                            ) : (
                              <DeviceRowActions device={d} onAction={setAction} />
                            )}
                          </td>
                        )}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <div className="flex items-center justify-between border-t px-3 py-2 text-sm">
              <span className="tabular-nums text-muted-foreground">
                {from}–{to} de {total}
              </span>
              <div className="flex items-center gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={page <= 1}
                >
                  <ChevronLeft className="h-4 w-4" aria-hidden="true" />
                  Anterior
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setPage((p) => p + 1)}
                  disabled={!hasNext}
                >
                  Próxima
                  <ChevronRight className="h-4 w-4" aria-hidden="true" />
                </Button>
              </div>
            </div>
          </>
        )}
      </Card>

      {action !== null && action.kind === "rename" && (
        <RenameDeviceDialog device={action.device} onClose={() => setAction(null)} />
      )}
      {action !== null && action.kind === "tags" && (
        <DeviceTagsDialog device={action.device} onClose={() => setAction(null)} />
      )}
      {action !== null && (action.kind === "archive" || action.kind === "reactivate") && (
        <DeviceStatusDialog
          device={action.device}
          kind={action.kind}
          onClose={() => setAction(null)}
        />
      )}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Ações por dispositivo (F3.7) - PATCH /devices/{id}, admin/owner
// -----------------------------------------------------------------------------

type DeviceActionKind = "rename" | "tags" | "archive" | "reactivate";

interface DeviceAction {
  kind: DeviceActionKind;
  device: DeviceItem;
}

/**
 * PATCH /devices/{id} + invalidação - mesmo padrão de mutation da F3.3
 * (CategoryInlineSelect): mutationFn fina sobre api() e invalidação de TODAS
 * as queries afetadas no onSuccess, aguardando o refetch antes de fechar o
 * dialog (a tabela nunca "volta" ao valor antigo). Invalida ["devices"]
 * (esta tela e o useFilterDevices dos relatórios) e a presença - arquivar
 * tira o device dos dashboards.
 */
function useDevicePatch(onDone: () => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ deviceId, body }: { deviceId: string; body: DevicePatchRequest }) =>
      api<DeviceItem>(`/devices/${encodeURIComponent(deviceId)}`, { method: "PATCH", body }),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["devices"] }),
        queryClient.invalidateQueries({ queryKey: ["dashboard", "presence"] }),
      ]);
      onDone();
    },
  });
}

/** "a, b,,a " vira ["a","b"]: separa por vírgula, apara, descarta vazios e repetidos. */
function parseTags(input: string): string[] {
  const out: string[] = [];
  for (const raw of input.split(",")) {
    const t = raw.trim();
    if (t.length > 0 && !out.includes(t)) out.push(t);
  }
  return out;
}

/** Menu de reticências da linha - não renderizado para device revogado. */
function DeviceRowActions({
  device,
  onAction,
}: {
  device: DeviceItem;
  onAction: (action: DeviceAction) => void;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="sm"
          className="h-8 w-8 p-0"
          aria-label={`Ações de ${device.display_name ?? device.hostname}`}
        >
          <Ellipsis className="h-4 w-4" aria-hidden="true" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={() => onAction({ kind: "rename", device })}>
          <Pencil className="h-3.5 w-3.5" aria-hidden="true" />
          Renomear
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => onAction({ kind: "tags", device })}>
          <Tags className="h-3.5 w-3.5" aria-hidden="true" />
          Editar etiquetas
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        {device.status === "archived" ? (
          <DropdownMenuItem onSelect={() => onAction({ kind: "reactivate", device })}>
            <ArchiveRestore className="h-3.5 w-3.5" aria-hidden="true" />
            Reativar
          </DropdownMenuItem>
        ) : (
          <DropdownMenuItem onSelect={() => onAction({ kind: "archive", device })}>
            <Archive className="h-3.5 w-3.5" aria-hidden="true" />
            Arquivar
          </DropdownMenuItem>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** Renomear - display_name; vazio limpa o apelido (volta a exibir o hostname). */
function RenameDeviceDialog({ device, onClose }: { device: DeviceItem; onClose: () => void }) {
  const [name, setName] = useState(device.display_name ?? "");
  const mutation = useDevicePatch(onClose);

  function submit() {
    if (mutation.isPending) return;
    const trimmed = name.trim();
    mutation.mutate({
      deviceId: device.id,
      body: { display_name: trimmed.length > 0 ? trimmed : null },
    });
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Renomear dispositivo</DialogTitle>
          <DialogDescription>
            Nome de exibição de "{device.hostname}". Deixe em branco para voltar ao hostname.
          </DialogDescription>
        </DialogHeader>
        <form
          className="space-y-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <Label htmlFor="device-display-name">Nome de exibição</Label>
          <Input
            id="device-display-name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={device.hostname}
            maxLength={120}
            autoFocus
          />
        </form>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {genericErrorMessage(mutation.error)}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancelar
          </Button>
          <Button onClick={submit} disabled={mutation.isPending}>
            {mutation.isPending ? "Salvando…" : "Salvar"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Editar etiquetas - input separado por vírgula que vira o array tags do PATCH. */
function DeviceTagsDialog({ device, onClose }: { device: DeviceItem; onClose: () => void }) {
  const [value, setValue] = useState((device.tags ?? []).join(", "));
  const mutation = useDevicePatch(onClose);

  function submit() {
    if (mutation.isPending) return;
    mutation.mutate({ deviceId: device.id, body: { tags: parseTags(value) } });
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Editar etiquetas</DialogTitle>
          <DialogDescription>
            Etiquetas de "{device.display_name ?? device.hostname}", separadas por vírgula. Elas
            aparecem na tabela e no filtro por tag.
          </DialogDescription>
        </DialogHeader>
        <form
          className="space-y-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <Label htmlFor="device-tags">Etiquetas</Label>
          <Input
            id="device-tags"
            value={value}
            onChange={(e) => setValue(e.target.value)}
            placeholder="financeiro, matriz"
            autoComplete="off"
            autoFocus
          />
        </form>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {genericErrorMessage(mutation.error)}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancelar
          </Button>
          <Button onClick={submit} disabled={mutation.isPending}>
            {mutation.isPending ? "Salvando…" : "Salvar"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Arquivar (status archived) ou Reativar (status active) com confirmação. */
function DeviceStatusDialog({
  device,
  kind,
  onClose,
}: {
  device: DeviceItem;
  kind: "archive" | "reactivate";
  onClose: () => void;
}) {
  const mutation = useDevicePatch(onClose);
  const archiving = kind === "archive";
  const name = device.display_name ?? device.hostname;

  function submit() {
    if (mutation.isPending) return;
    mutation.mutate({
      deviceId: device.id,
      body: { status: archiving ? "archived" : "active" },
    });
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{archiving ? "Arquivar dispositivo" : "Reativar dispositivo"}</DialogTitle>
          <DialogDescription>
            {archiving ? `Arquivar "${name}"?` : `Reativar "${name}"?`}
          </DialogDescription>
        </DialogHeader>
        {archiving ? (
          <div className="space-y-2 text-sm text-muted-foreground">
            <p>
              O dispositivo sai dos dashboards e do relatório de cobrança. O histórico fica
              preservado e pode ser consultado marcando "Incluir arquivados" nos filtros.
            </p>
            <p>
              O agente instalado continua coletando normalmente; arquivar não interrompe a coleta.
            </p>
          </div>
        ) : (
          <p className="text-sm text-muted-foreground">
            O dispositivo volta a aparecer nos dashboards e a contar no relatório de cobrança.
          </p>
        )}
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {genericErrorMessage(mutation.error)}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancelar
          </Button>
          <Button onClick={submit} disabled={mutation.isPending}>
            {mutation.isPending
              ? archiving
                ? "Arquivando…"
                : "Reativando…"
              : archiving
                ? "Arquivar"
                : "Reativar"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
