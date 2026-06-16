// =============================================================================
// Privacidade -> Dados do Titular (DSR completo, F4.5). A fatia mais sensível de
// LGPD: direitos do titular (art. 18) que a controladora responde em 15 dias
// (art. 19). MVP: EXPORT (pacote ZIP de TODOS os dados do titular, link 72h) e
// EXCLUSÃO (hard delete irreversível, confirmação dupla, motivo registrado,
// recibo com contagens).
//
// O TITULAR é um device_user (NÃO um usuário do portal): as rotas usam
// {deviceUserId}. Como não há endpoint de listagem dedicado, o portal encontra
// os titulares pela MESMA fonte das lanes/relatórios:
// GET /reports/usage?group_by=device_user (janela ampla de 92 dias). A
// lane-máquina (UUID zero) NÃO é titular e fica fora da busca.
//
// Gating de papel (espelho do backend):
//  - EXPORT: admin + owner (PolicyAdminPlus);
//  - EXCLUSÃO definitiva: SÓ owner (PolicyOwnerOnly);
//  - Viewer: NÃO vê ações de DSR (somente o aviso de finalidade).
//
// Regra de exclusão (decisão documentada p/ o silêncio da spec, linha 995):
// hard delete dos dados identificáveis (raw_events + activity_intervals do
// titular, onde vivem window_title/detalhe) e ANONIMIZAÇÃO da linha de
// device_users (nome -> marcador neutro), preservando os AGREGADOS DE EQUIPE já
// computados (daily_*). Defensável, mas o RESULTADO marca: validar com o
// jurídico antes de operar em produção.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  Download,
  Eye,
  Info,
  MonitorSmartphone,
  Search,
  ShieldAlert,
  Trash2,
  UserRound,
} from "lucide-react";
import { api, ApiError } from "@/lib/api";
import { addDays, localDateOf } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin, isOwner } from "@/lib/roles";
import type {
  DeviceItem,
  DsrDeleteResponse,
  DsrReceipt,
  DsrSubject,
  ExportCreateResponse,
  MeResponse,
  PagedResponse,
  UsageDeviceUserItem,
  UsageReportResponse,
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";

/** UUID zero = lane-máquina (sem usuário Windows): não é titular - fora da busca. */
const MACHINE_LANE = "00000000-0000-0000-0000-000000000000";

/** Janela ampla para DESCOBRIR titulares (o pacote em si é o acervo inteiro). */
const SUBJECT_LOOKBACK_DAYS = 92;
const SUBJECT_PAGE_SIZE = 100;

type Scope = "subjects" | "devices";

// -----------------------------------------------------------------------------
// Página
// -----------------------------------------------------------------------------

export function PrivacidadePage() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const me = meQuery.data;
  const canExport = isAdmin(me);
  const canDelete = isOwner(me);
  // Viewer não tem nenhuma ação de DSR; mostramos só o aviso de finalidade.
  const canActOnDsr = canExport || canDelete;

  const [scope, setScope] = useState<Scope>("subjects");

  return (
    <div className="space-y-6">
      <DisclaimerBanner />

      {meQuery.isPending ? (
        // Aguarda o papel antes de decidir o que mostrar (evita o flash do aviso
        // de viewer para um admin/owner durante um cache frio do GET /me).
        <Card>
          <SkeletonRows columns={3} />
        </Card>
      ) : !canActOnDsr ? (
        <ViewerNotice />
      ) : (
        <>
          <div
            role="tablist"
            aria-label="Escopo do DSR"
            className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
          >
            <ScopeTab active={scope === "subjects"} onClick={() => setScope("subjects")} icon={UserRound}>
              Por titular
            </ScopeTab>
            <ScopeTab active={scope === "devices"} onClick={() => setScope("devices")} icon={MonitorSmartphone}>
              Por dispositivo
            </ScopeTab>
          </div>

          {scope === "subjects" ? (
            <SubjectsPanel canExport={canExport} canDelete={canDelete} />
          ) : (
            <DevicesPanel canExport={canExport} canDelete={canDelete} />
          )}
        </>
      )}
    </div>
  );
}

const segmentedTab =
  "inline-flex items-center gap-1.5 rounded-[5px] px-3 text-xs font-medium transition-colors";

function ScopeTab({
  active,
  onClick,
  icon: Icon,
  children,
}: {
  active: boolean;
  onClick: () => void;
  icon: typeof UserRound;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={cn(
        segmentedTab,
        active ? "bg-primary/10 text-primary" : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
      )}
    >
      <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden />
      {children}
    </button>
  );
}

function DisclaimerBanner() {
  return (
    <div>
      <h1 className="text-2xl font-semibold tracking-tight">Privacidade</h1>
      <p className="mt-1 text-sm text-muted-foreground">
        Atendimento aos direitos do titular (LGPD): exportação e exclusão dos dados de uma pessoa
        identificada por um usuário do Windows em um dispositivo.
      </p>
      <div className="mt-4 flex gap-3 rounded-md border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-900">
        <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
        <div className="space-y-1">
          <p className="font-medium">Finalidade e responsabilidade</p>
          <p>
            Estas ações existem para apoiar a controladora a responder a pedidos do titular no prazo
            legal de 15 dias. Os dados exportados se limitam ao próprio titular; a exclusão é
            definitiva e registrada na trilha de auditoria. Os agregados de equipe já computados são
            preservados de forma anonimizada e não identificam mais a pessoa.
          </p>
        </div>
      </div>
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
        <p className="text-base font-medium">Sem permissão para ações de privacidade</p>
        <p className="max-w-md text-sm text-muted-foreground">
          A exportação e a exclusão de dados do titular ficam disponíveis para Administradores e
          Proprietários. Procure um Proprietário da organização para conduzir um pedido de DSR.
        </p>
      </div>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Busca compartilhada (input com debounce)
// -----------------------------------------------------------------------------

function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

function SearchBox({
  value,
  onChange,
  placeholder,
  label,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  label: string;
}) {
  return (
    <div className="relative w-full max-w-sm">
      <Search
        className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
        aria-hidden
      />
      <Input
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        aria-label={label}
        className="h-9 pl-9"
      />
    </div>
  );
}

// -----------------------------------------------------------------------------
// Painel: por titular
// -----------------------------------------------------------------------------

function SubjectsPanel({ canExport, canDelete }: { canExport: boolean; canDelete: boolean }) {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;

  const [search, setSearch] = useState("");
  const q = useDebounced(search.trim(), 300).toLocaleLowerCase("pt-BR");

  // Ação aberta (export ou exclusão) sobre um titular.
  const [action, setAction] = useState<{ kind: "export" | "delete"; subject: DsrSubject } | null>(null);

  const range = useMemo(() => {
    if (timezone === null) return null;
    const today = localDateOf(new Date(), timezone);
    return { from: addDays(today, -(SUBJECT_LOOKBACK_DAYS - 1)), to: today };
  }, [timezone]);

  const subjectsQuery = useQuery({
    queryKey: ["dsr", "subjects", { from: range?.from, to: range?.to }],
    queryFn: () =>
      api<UsageReportResponse<UsageDeviceUserItem>>(
        `/reports/usage?from=${range?.from ?? ""}&to=${range?.to ?? ""}&group_by=device_user&page=1&page_size=${SUBJECT_PAGE_SIZE}`,
      ),
    enabled: range !== null,
    staleTime: 60_000,
  });

  // Titulares = device_users reais (lane-máquina fora). Mesma chave dos relatórios.
  const subjects = useMemo<DsrSubject[]>(() => {
    const items = subjectsQuery.data?.items ?? [];
    return items
      .filter((i) => i.device_user_id !== MACHINE_LANE)
      .map((i) => ({
        device_user_id: i.device_user_id,
        device_id: i.device_id,
        device_name: i.device_name,
        windows_user: i.windows_user,
        display_name: i.display_name,
      }));
  }, [subjectsQuery.data]);

  const filtered = useMemo(() => {
    if (q.length === 0) return subjects;
    return subjects.filter((s) =>
      `${s.display_name} ${s.windows_user ?? ""} ${s.device_name}`.toLocaleLowerCase("pt-BR").includes(q),
    );
  }, [subjects, q]);

  const total = subjectsQuery.data?.total ?? 0;
  const capped = total > SUBJECT_PAGE_SIZE;

  return (
    <div className="space-y-3">
      <SearchBox
        value={search}
        onChange={setSearch}
        placeholder="Buscar por nome, usuário do Windows ou dispositivo"
        label="Buscar titular"
      />

      {/* Aviso honesto de alcance: a descoberta de titulares vem da atividade agregada dos
          últimos 92 dias (GET /reports/usage). Um titular sem atividade nesse período não
          aparece aqui, mesmo dentro da retenção — o caminho é o painel por dispositivo. */}
      <p className="text-xs text-muted-foreground">
        A lista cobre titulares com atividade nos últimos {SUBJECT_LOOKBACK_DAYS} dias.
        {capped
          ? ` Mostrando os ${SUBJECT_PAGE_SIZE} mais ativos; refine a busca para encontrar uma pessoa específica.`
          : ""}{" "}
        Um titular sem atividade nesse período não aparece — nesse caso, use o painel por
        dispositivo.
      </p>

      <Card>
        {subjectsQuery.isPending ? (
          <SkeletonRows columns={canDelete || canExport ? 3 : 2} />
        ) : subjectsQuery.isError ? (
          <ErrorState message={genericErrorMessage(subjectsQuery.error)} onRetry={() => void subjectsQuery.refetch()} />
        ) : filtered.length === 0 ? (
          <EmptyState
            icon={UserRound}
            title={q.length > 0 ? "Nenhum titular corresponde à busca" : "Nenhum titular com atividade recente"}
            description={
              q.length > 0
                ? "Ajuste os termos da busca. Os titulares aparecem quando há atividade nos últimos 92 dias."
                : "Os titulares aparecem aqui assim que houver atividade coletada com um usuário do Windows identificado."
            }
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Titular</th>
                  <th scope="col" className="px-3 py-2">Dispositivo</th>
                  {(canExport || canDelete) && (
                    <th scope="col" className="px-6 py-2 text-right">
                      <span className="sr-only">Ações</span>
                    </th>
                  )}
                </tr>
              </thead>
              <tbody>
                {filtered.map((s) => (
                  <tr key={`${s.device_id}:${s.device_user_id}`} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                    <td className="px-6 py-2">
                      <span className="block max-w-[20rem] truncate font-medium">{s.display_name}</span>
                      {s.windows_user !== null && (
                        <span className="block max-w-[20rem] truncate text-xs text-muted-foreground">
                          {s.windows_user}
                        </span>
                      )}
                    </td>
                    <td className="max-w-[14rem] truncate px-3 py-2 text-muted-foreground">{s.device_name}</td>
                    {(canExport || canDelete) && (
                      <td className="whitespace-nowrap px-6 py-2 text-right">
                        <div className="inline-flex gap-2">
                          {canExport && (
                            <Button variant="outline" size="sm" onClick={() => setAction({ kind: "export", subject: s })}>
                              <Download className="h-4 w-4" aria-hidden />
                              Exportar dados
                            </Button>
                          )}
                          {canDelete && (
                            <Button
                              variant="outline"
                              size="sm"
                              className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
                              onClick={() => setAction({ kind: "delete", subject: s })}
                            >
                              <Trash2 className="h-4 w-4" aria-hidden />
                              Excluir definitivamente
                            </Button>
                          )}
                        </div>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {action?.kind === "export" && (
        <SubjectExportDialog subject={action.subject} onClose={() => setAction(null)} />
      )}
      {action?.kind === "delete" && (
        <SubjectDeleteDialog subject={action.subject} onClose={() => setAction(null)} />
      )}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Painel: por dispositivo
// -----------------------------------------------------------------------------

function DevicesPanel({ canExport, canDelete }: { canExport: boolean; canDelete: boolean }) {
  const [search, setSearch] = useState("");
  const q = useDebounced(search.trim(), 300).toLocaleLowerCase("pt-BR");

  const [action, setAction] = useState<{ kind: "export" | "delete"; device: DeviceItem } | null>(null);

  const devicesQuery = useQuery({
    queryKey: ["devices", { page_size: 100 }],
    queryFn: () => api<PagedResponse<DeviceItem>>("/devices?page_size=100"),
    staleTime: 60_000,
  });

  const devices = useMemo(() => {
    const items = devicesQuery.data?.items ?? [];
    return [...items].sort((a, b) =>
      (a.display_name ?? a.hostname).localeCompare(b.display_name ?? b.hostname, "pt-BR"),
    );
  }, [devicesQuery.data]);

  const filtered = useMemo(() => {
    if (q.length === 0) return devices;
    return devices.filter((d) =>
      `${d.display_name ?? ""} ${d.hostname}`.toLocaleLowerCase("pt-BR").includes(q),
    );
  }, [devices, q]);

  return (
    <div className="space-y-3">
      <SearchBox
        value={search}
        onChange={setSearch}
        placeholder="Buscar por nome ou hostname"
        label="Buscar dispositivo"
      />
      <p className="text-xs text-muted-foreground">
        Por dispositivo, a ação abrange todos os usuários do Windows que utilizaram o aparelho e os
        eventos do próprio dispositivo.
      </p>

      <Card>
        {devicesQuery.isPending ? (
          <SkeletonRows columns={canDelete || canExport ? 3 : 2} />
        ) : devicesQuery.isError ? (
          <ErrorState message={genericErrorMessage(devicesQuery.error)} onRetry={() => void devicesQuery.refetch()} />
        ) : filtered.length === 0 ? (
          <EmptyState
            icon={MonitorSmartphone}
            title={q.length > 0 ? "Nenhum dispositivo corresponde à busca" : "Nenhum dispositivo"}
            description={
              q.length > 0
                ? "Ajuste os termos da busca."
                : "Cadastre uma chave de instalação e instale o agente para ver os dispositivos aqui."
            }
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Dispositivo</th>
                  <th scope="col" className="px-3 py-2">Hostname</th>
                  {(canExport || canDelete) && (
                    <th scope="col" className="px-6 py-2 text-right">
                      <span className="sr-only">Ações</span>
                    </th>
                  )}
                </tr>
              </thead>
              <tbody>
                {filtered.map((d) => (
                  <tr key={d.id} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                    <td className="max-w-[20rem] truncate px-6 py-2 font-medium">{d.display_name ?? d.hostname}</td>
                    <td className="max-w-[14rem] truncate px-3 py-2 text-muted-foreground">{d.hostname}</td>
                    {(canExport || canDelete) && (
                      <td className="whitespace-nowrap px-6 py-2 text-right">
                        <div className="inline-flex gap-2">
                          {canExport && (
                            <Button variant="outline" size="sm" onClick={() => setAction({ kind: "export", device: d })}>
                              <Download className="h-4 w-4" aria-hidden />
                              Exportar dados
                            </Button>
                          )}
                          {canDelete && (
                            <Button
                              variant="outline"
                              size="sm"
                              className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
                              onClick={() => setAction({ kind: "delete", device: d })}
                            >
                              <Trash2 className="h-4 w-4" aria-hidden />
                              Excluir definitivamente
                            </Button>
                          )}
                        </div>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {action?.kind === "export" && (
        <DeviceExportDialog device={action.device} onClose={() => setAction(null)} />
      )}
      {action?.kind === "delete" && (
        <DeviceDeleteDialog device={action.device} onClose={() => setAction(null)} />
      )}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Estados compartilhados de tabela
// -----------------------------------------------------------------------------

function SkeletonRows({ columns }: { columns: number }) {
  return (
    <div className="space-y-px p-4">
      {Array.from({ length: 5 }, (_, i) => (
        <div key={i} className="flex items-center gap-4 py-2">
          {Array.from({ length: columns }, (_, c) => (
            <Skeleton key={c} className={cn("h-5", c === columns - 1 ? "ml-auto w-48" : "w-40")} />
          ))}
        </div>
      ))}
    </div>
  );
}

function ErrorState({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
      <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
      <p className="text-sm text-muted-foreground">{message}</p>
      <Button variant="outline" onClick={onRetry}>
        Tentar novamente
      </Button>
    </div>
  );
}

function EmptyState({
  icon: Icon,
  title,
  description,
}: {
  icon: typeof UserRound;
  title: string;
  description: string;
}) {
  return (
    <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
      <span className="flex h-12 w-12 items-center justify-center rounded-full bg-muted">
        <Icon className="h-6 w-6 text-muted-foreground" aria-hidden />
      </span>
      <p className="text-base font-medium">{title}</p>
      <p className="max-w-md text-sm text-muted-foreground">{description}</p>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Export (POST /privacy/.../export) -> 202; banner de sucesso linka p/ Exportações
// -----------------------------------------------------------------------------

function useDsrExport(path: string) {
  return useMutation({
    mutationFn: () => api<ExportCreateResponse>(path, { method: "POST" }),
  });
}

function ExportDialog({
  title,
  targetLabel,
  scopeNote,
  path,
  onClose,
}: {
  title: string;
  targetLabel: string;
  scopeNote: string;
  path: string;
  onClose: () => void;
}) {
  const mutation = useDsrExport(path);

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{targetLabel}</DialogDescription>
        </DialogHeader>

        {mutation.isSuccess ? (
          <div className="space-y-3 text-sm">
            <div className="flex gap-3 rounded-md border border-emerald-200 bg-emerald-50 px-4 py-3 text-emerald-900">
              <Download className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
              <div className="space-y-1">
                <p className="font-medium">Pacote em geração</p>
                <p>
                  O arquivo será preparado em segundo plano. O link de download fica válido por 72
                  horas. Acompanhe e baixe em{" "}
                  <Link
                    to="/relatorios/exportacoes"
                    className="font-medium underline underline-offset-2 hover:text-emerald-700"
                  >
                    Exportações
                  </Link>
                  .
                </p>
              </div>
            </div>
          </div>
        ) : (
          <div className="space-y-2 text-sm text-muted-foreground">
            <p>{scopeNote}</p>
            <p>
              O pacote sai em formato ZIP com eventos, intervalos e agregados, mais um manifesto com
              período e contagens. O link de download fica válido por 72 horas.
            </p>
          </div>
        )}

        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {dsrErrorMessage(mutation.error)}
          </p>
        )}

        <DialogFooter>
          {mutation.isSuccess ? (
            <Button onClick={onClose}>Fechar</Button>
          ) : (
            <>
              <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
                Cancelar
              </Button>
              <Button onClick={() => mutation.mutate()} disabled={mutation.isPending}>
                {mutation.isPending ? "Enviando..." : "Gerar pacote"}
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function SubjectExportDialog({ subject, onClose }: { subject: DsrSubject; onClose: () => void }) {
  return (
    <ExportDialog
      title="Exportar dados do titular"
      targetLabel={`${subject.display_name}${subject.windows_user !== null ? ` (${subject.windows_user})` : ""} em ${subject.device_name}`}
      scopeNote="Gera um pacote com todos os dados pessoais deste titular: eventos de sessão e energia, intervalos de atividade e os agregados diários."
      path={`/privacy/subjects/${encodeURIComponent(subject.device_user_id)}/export`}
      onClose={onClose}
    />
  );
}

function DeviceExportDialog({ device, onClose }: { device: DeviceItem; onClose: () => void }) {
  return (
    <ExportDialog
      title="Exportar dados do dispositivo"
      targetLabel={device.display_name ?? device.hostname}
      scopeNote="Gera um pacote com os dados de todos os usuários do Windows que utilizaram este dispositivo e os eventos do próprio aparelho."
      path={`/privacy/devices/${encodeURIComponent(device.id)}/export`}
      onClose={onClose}
    />
  );
}

// -----------------------------------------------------------------------------
// Exclusão (DELETE /privacy/.../data) -> recibo. Confirmação DUPLA.
// -----------------------------------------------------------------------------

function useDsrDelete(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: { confirmation: string; reason: string }) =>
      api<DsrDeleteResponse>(path, { method: "DELETE", body }),
    onSuccess: () => {
      // O titular sai da busca (anonimizado) e os relatórios passam a exibir o
      // rótulo neutro - invalida as fontes afetadas.
      void queryClient.invalidateQueries({ queryKey: ["dsr"] });
      void queryClient.invalidateQueries({ queryKey: ["reports", "usage"] });
      void queryClient.invalidateQueries({ queryKey: ["devices"] });
    },
  });
}

/**
 * Dialog de exclusão com confirmação DUPLA: o operador precisa (1) digitar
 * EXATAMENTE o valor de segurança (confirmationValue) e (2) preencher um motivo
 * com pelo menos 10 caracteres. Ao concluir, mostra o RECIBO com as contagens.
 */
function DeleteDialog({
  title,
  targetLabel,
  confirmationValue,
  confirmationHint,
  scopeNote,
  path,
  onClose,
}: {
  title: string;
  targetLabel: string;
  confirmationValue: string;
  confirmationHint: string;
  scopeNote: string;
  path: string;
  onClose: () => void;
}) {
  const mutation = useDsrDelete(path);
  const [confirmation, setConfirmation] = useState("");
  const [reason, setReason] = useState("");

  // Espelha o DsrService.MinReasonLength do backend (8 chars).
  const MIN_REASON = 8;
  const confirmationOk = confirmation.trim() === confirmationValue;
  const reasonOk = reason.trim().length >= MIN_REASON;
  const canSubmit = confirmationOk && reasonOk && !mutation.isPending;

  const receipt = mutation.data?.receipt ?? null;

  function submit() {
    if (!canSubmit) return;
    mutation.mutate({ confirmation: confirmation.trim(), reason: reason.trim() });
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2 text-destructive">
            <ShieldAlert className="h-5 w-5 shrink-0" aria-hidden />
            {title}
          </DialogTitle>
          <DialogDescription>{targetLabel}</DialogDescription>
        </DialogHeader>

        {receipt !== null ? (
          <Receipt receipt={receipt} />
        ) : (
          <>
            <div className="space-y-2 rounded-md border border-destructive/30 bg-destructive/5 px-4 py-3 text-sm text-foreground">
              <p className="font-medium text-destructive">Esta ação é irreversível.</p>
              <p className="text-muted-foreground">{scopeNote}</p>
              <p className="text-muted-foreground">
                Os agregados de equipe já computados são preservados de forma anonimizada: as somas
                continuam, mas deixam de identificar a pessoa.
              </p>
            </div>

            <form
              className="space-y-4"
              onSubmit={(e) => {
                e.preventDefault();
                submit();
              }}
            >
              <div className="space-y-1.5">
                <Label htmlFor="dsr-confirmation">
                  Para confirmar, digite{" "}
                  <span className="font-mono font-semibold text-foreground">{confirmationValue}</span>
                </Label>
                <Input
                  id="dsr-confirmation"
                  value={confirmation}
                  onChange={(e) => setConfirmation(e.target.value)}
                  placeholder={confirmationHint}
                  autoComplete="off"
                  autoFocus
                  aria-invalid={confirmation.length > 0 && !confirmationOk}
                />
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="dsr-reason">Motivo (registrado na auditoria)</Label>
                <textarea
                  id="dsr-reason"
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  placeholder="Ex.: pedido de exclusão do titular recebido em 10/06/2026."
                  rows={3}
                  className={cn(
                    "flex w-full rounded-md border border-input bg-card px-3 py-2 text-sm",
                    "placeholder:text-muted-foreground",
                    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
                  )}
                />
                <p className="text-xs text-muted-foreground">
                  Mínimo de {MIN_REASON} caracteres. {reason.trim().length}/{MIN_REASON}
                </p>
              </div>
            </form>
          </>
        )}

        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {dsrDeleteErrorMessage(mutation.error)}
          </p>
        )}

        <DialogFooter>
          {receipt !== null ? (
            <Button onClick={onClose}>Fechar</Button>
          ) : (
            <>
              <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
                Cancelar
              </Button>
              <Button variant="destructive" onClick={submit} disabled={!canSubmit}>
                {mutation.isPending ? "Excluindo..." : "Excluir definitivamente"}
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function SubjectDeleteDialog({ subject, onClose }: { subject: DsrSubject; onClose: () => void }) {
  // Valor de segurança: o usuário do Windows (estável e visível); se ausente
  // (titular já anonimizado), recai sobre o nome de exibição.
  const confirmationValue = subject.windows_user ?? subject.display_name;
  return (
    <DeleteDialog
      title="Excluir dados do titular"
      targetLabel={`${subject.display_name} em ${subject.device_name}`}
      confirmationValue={confirmationValue}
      confirmationHint={confirmationValue}
      scopeNote="Os eventos, os intervalos de atividade e os detalhes (títulos de janela) deste titular serão apagados em definitivo."
      path={`/privacy/subjects/${encodeURIComponent(subject.device_user_id)}/data`}
      onClose={onClose}
    />
  );
}

function DeviceDeleteDialog({ device, onClose }: { device: DeviceItem; onClose: () => void }) {
  // Valor de segurança do device: o hostname (sempre presente, distinto).
  return (
    <DeleteDialog
      title="Excluir dados do dispositivo"
      targetLabel={device.display_name ?? device.hostname}
      confirmationValue={device.hostname}
      confirmationHint={device.hostname}
      scopeNote="Os eventos e os intervalos de atividade de todos os usuários do Windows deste dispositivo serão apagados em definitivo."
      path={`/privacy/devices/${encodeURIComponent(device.id)}/data`}
      onClose={onClose}
    />
  );
}

// -----------------------------------------------------------------------------
// Recibo de exclusão (LGPD art. 19): contagens do que foi feito.
// -----------------------------------------------------------------------------

const RECEIPT_ROWS: { key: keyof DsrReceipt; label: string }[] = [
  { key: "raw_events_deleted", label: "Eventos apagados" },
  { key: "intervals_deleted", label: "Intervalos de atividade apagados" },
  { key: "device_users_anonymized", label: "Titulares anonimizados" },
  { key: "daily_rows_kept", label: "Agregados de equipe preservados" },
];

function Receipt({ receipt }: { receipt: DsrReceipt }) {
  return (
    <div className="space-y-3 text-sm">
      <div className="flex gap-3 rounded-md border border-emerald-200 bg-emerald-50 px-4 py-3 text-emerald-900">
        <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
        <p>
          Exclusão concluída e registrada na trilha de auditoria. Guarde este recibo para a resposta
          ao titular.
        </p>
      </div>
      <dl className="divide-y rounded-md border">
        {RECEIPT_ROWS.map((row) => {
          const value = receipt[row.key];
          return (
            <div key={String(row.key)} className="flex items-center justify-between px-4 py-2">
              <dt className="text-muted-foreground">{row.label}</dt>
              <dd className="font-medium tabular-nums">
                {typeof value === "number" ? value.toLocaleString("pt-BR") : String(value ?? 0)}
              </dd>
            </div>
          );
        })}
      </dl>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Mensagens de erro específicas (genéricas - sem vazar detalhe técnico).
// -----------------------------------------------------------------------------

function dsrErrorMessage(err: unknown): string {
  if (err instanceof ApiError && err.status === 404) {
    return "Titular ou dispositivo não encontrado. A lista pode ter mudado; atualize a página.";
  }
  return genericErrorMessage(err);
}

function dsrDeleteErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 400) {
      return "Confirmação ou motivo inválidos. Confira o valor digitado e o motivo (mínimo de 8 caracteres).";
    }
    if (err.status === 404) {
      return "Titular ou dispositivo não encontrado. A lista pode ter mudado; atualize a página.";
    }
  }
  return genericErrorMessage(err);
}
