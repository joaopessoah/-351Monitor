// =============================================================================
// Configurações -> Chaves de instalação (Seção 8.3). Lista as enrollment keys
// do tenant (GET /enrollment-keys), cria novas (POST - o segredo completo sai
// UMA única vez, na resposta 201) e revoga (DELETE /enrollment-keys/{id}:
// devices já registrados continuam; novas instalações são recusadas). Após
// criar, o painel destacado mostra a chave, o comando msiexec pronto (passo 2
// da 8.3) e o bloco "Aguardando a primeira máquina" (passo 3): GET /devices a
// cada 10s até surgir um device que não existia antes da criação. Admin/owner
// criam e revogam; o viewer vê a lista sem ações (aviso amigável no 403).
// =============================================================================

import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  Ban,
  Check,
  CheckCircle2,
  Copy,
  KeyRound,
  Loader2,
  Plus,
} from "lucide-react";
import { api, ApiError } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type {
  DeviceItem,
  EnrollmentKeyCreateRequest,
  EnrollmentKeyCreateResponse,
  EnrollmentKeyItem,
  EnrollmentKeysResponse,
  MeResponse,
  PagedResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
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

/** Mesma queryKey do useFilterDevices (components/reports/filters) - cache compartilhado. */
const DEVICES_QUERY_KEY = ["devices", { page_size: 100 }] as const;
const fetchDevicesPage = () => api<PagedResponse<DeviceItem>>("/devices?page_size=100");

const deviceStatusLabels: Record<DeviceItem["status"], string> = {
  active: "Ativo",
  paused: "Pausado",
  archived: "Arquivado",
  revoked: "Revogado",
};

/** Estado derivado da chave - só "revogada" vem do backend; o resto é leitura local. */
type KeyState = "ativa" | "revogada" | "expirada" | "esgotada";

function keyState(key: EnrollmentKeyItem, nowMs: number): KeyState {
  if (key.revoked_at !== null) return "revogada";
  if (key.expires_at !== null && new Date(key.expires_at).getTime() <= nowMs) return "expirada";
  if (key.max_uses !== null && key.use_count >= key.max_uses) return "esgotada";
  return "ativa";
}

const keyStateLabels: Record<KeyState, string> = {
  ativa: "Ativa",
  revogada: "Revogada",
  expirada: "Expirada",
  esgotada: "Esgotada",
};

const keyStateClasses: Record<KeyState, string> = {
  ativa: "bg-viz-produtivo/15 text-viz-produtivo",
  revogada: "bg-brand-red/15 text-brand-red",
  expirada: "bg-muted text-muted-foreground",
  esgotada: "bg-muted text-muted-foreground",
};

/** Painel de espera pela primeira máquina: baseline no momento da criação da chave. */
interface EnrollWatch {
  baseline: ReadonlySet<string>;
  found: DeviceItem | null;
}

export function ChavesPage() {
  const queryClient = useQueryClient();

  // Mesma queryKey do AppShell - resolve do cache, sem requisição extra.
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const admin = isAdmin(meQuery.data);
  const timezone = meQuery.data?.organization.timezone;

  const keysQuery = useQuery({
    queryKey: ["enrollment-keys"],
    queryFn: () => api<EnrollmentKeysResponse>("/enrollment-keys"),
  });

  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<EnrollmentKeyCreateResponse | null>(null);
  const [revoking, setRevoking] = useState<EnrollmentKeyItem | null>(null);
  const [watch, setWatch] = useState<EnrollWatch | null>(null);

  // Polling de 10s no GET /devices enquanto o painel aguarda a primeira máquina.
  const watchQuery = useQuery({
    queryKey: DEVICES_QUERY_KEY,
    queryFn: fetchDevicesPage,
    enabled: watch !== null && watch.found === null,
    refetchInterval: 10_000,
    refetchIntervalInBackground: false,
  });

  useEffect(() => {
    if (watch === null || watch.found !== null) return;
    const novel = (watchQuery.data?.items ?? []).find((d) => !watch.baseline.has(d.id));
    if (novel !== undefined) {
      setWatch({ baseline: watch.baseline, found: novel });
    }
  }, [watchQuery.data, watch]);

  async function startWatch(): Promise<void> {
    try {
      const current = await queryClient.fetchQuery({
        queryKey: DEVICES_QUERY_KEY,
        queryFn: fetchDevicesPage,
      });
      setWatch({ baseline: new Set(current.items.map((d) => d.id)), found: null });
    } catch {
      // Sem baseline (falha pontual de rede): segue aguardando com baseline
      // vazia - no pior caso o card celebra um device pré-existente, sem
      // nenhum efeito colateral além do link para a lista de dispositivos.
      setWatch({ baseline: new Set<string>(), found: null });
    }
  }

  function handleCreated(key: EnrollmentKeyCreateResponse): void {
    setCreating(false);
    setCreated(key);
    setWatch(null);
    void startWatch();
  }

  const data = keysQuery.data;
  const forbidden =
    keysQuery.isError &&
    data === undefined &&
    keysQuery.error instanceof ApiError &&
    keysQuery.error.status === 403;
  const nowMs = Date.now();

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold tracking-tight">Chaves de instalação</h2>
          <p className="mt-1 text-sm text-muted-foreground">
            Chaves de registro (enrollment keys) para instalar o agente nas máquinas. O segredo
            completo aparece uma única vez, na criação.
          </p>
        </div>
        {admin && (
          <Button size="sm" className="h-9 shrink-0" onClick={() => setCreating(true)}>
            <Plus className="h-4 w-4" aria-hidden />
            Nova chave
          </Button>
        )}
      </div>

      {created !== null && (
        <CreatedKeyPanel
          created={created}
          watch={watch}
          onClose={() => {
            setCreated(null);
            setWatch(null);
          }}
        />
      )}

      <Card>
        {forbidden ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <span className="flex h-12 w-12 items-center justify-center rounded-full bg-muted">
              <KeyRound className="h-6 w-6 text-muted-foreground" aria-hidden />
            </span>
            <p className="text-base font-medium">Sem permissão para ver as chaves</p>
            <p className="max-w-md text-sm text-muted-foreground">
              As chaves de instalação ficam disponíveis para Administradores e Proprietários da
              organização.
            </p>
          </div>
        ) : keysQuery.isError && data === undefined ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(keysQuery.error)}</p>
            <Button variant="outline" onClick={() => void keysQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Prefixo</th>
                  <th scope="col" className="px-3 py-2">Rótulo</th>
                  <th scope="col" className="px-3 py-2 text-right">Usos</th>
                  <th scope="col" className="px-3 py-2">Expiração</th>
                  <th scope="col" className="px-3 py-2">Status</th>
                  {admin && <th scope="col" className="px-6 py-2 text-right">Ações</th>}
                </tr>
              </thead>
              <tbody>
                {data === undefined ? (
                  Array.from({ length: 4 }, (_, i) => (
                    <tr key={i} className="border-b last:border-b-0">
                      <td colSpan={admin ? 6 : 5} className="px-6 py-2">
                        <Skeleton className="h-8 w-full" />
                      </td>
                    </tr>
                  ))
                ) : data.items.length === 0 ? (
                  <tr>
                    <td
                      colSpan={admin ? 6 : 5}
                      className="px-6 py-10 text-center text-sm text-muted-foreground"
                    >
                      <span className="inline-flex flex-col items-center gap-2">
                        <span>Nenhuma chave de instalação ainda.</span>
                        {admin && (
                          <Button variant="outline" size="sm" onClick={() => setCreating(true)}>
                            Criar a primeira chave
                          </Button>
                        )}
                      </span>
                    </td>
                  </tr>
                ) : (
                  data.items.map((key) => {
                    const state = keyState(key, nowMs);
                    return (
                      <tr key={key.id} className="border-b last:border-b-0">
                        <td className="whitespace-nowrap px-6 py-2 font-mono">{key.key_prefix}…</td>
                        <td className="max-w-[16rem] truncate px-3 py-2">
                          {key.label ?? <span className="text-muted-foreground">-</span>}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                          {key.max_uses !== null
                            ? `${key.use_count} de ${key.max_uses}`
                            : key.use_count}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground">
                          {key.expires_at === null ? (
                            "sem expiração"
                          ) : timezone !== undefined ? (
                            formatDateTime(key.expires_at, timezone)
                          ) : (
                            "-"
                          )}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2">
                          <span
                            className={cn(
                              "rounded-full px-2 py-0.5 text-xs font-medium",
                              keyStateClasses[state],
                            )}
                          >
                            {keyStateLabels[state]}
                          </span>
                        </td>
                        {admin && (
                          <td className="whitespace-nowrap px-6 py-2 text-right">
                            {key.revoked_at === null ? (
                              <Button
                                variant="ghost"
                                size="sm"
                                className="h-8 text-destructive hover:text-destructive"
                                onClick={() => setRevoking(key)}
                              >
                                <Ban className="h-3.5 w-3.5" aria-hidden />
                                Revogar
                              </Button>
                            ) : (
                              <span className="text-muted-foreground">-</span>
                            )}
                          </td>
                        )}
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {creating && <CreateKeyDialog onClose={() => setCreating(false)} onCreated={handleCreated} />}
      {revoking !== null && (
        <RevokeKeyDialog enrollmentKey={revoking} onClose={() => setRevoking(null)} />
      )}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Painel da chave recém-criada (o segredo aparece UMA única vez) + espera
// pela primeira máquina
// -----------------------------------------------------------------------------

function CreatedKeyPanel({
  created,
  watch,
  onClose,
}: {
  created: EnrollmentKeyCreateResponse;
  watch: EnrollWatch | null;
  onClose: () => void;
}) {
  // SERVERURL é a própria origem do portal (mesmo domínio da API, Seção 8.3).
  const installCommand = `msiexec /i MonitorAgent.msi /qn ENROLLKEY=${created.key} SERVERURL=${window.location.origin}`;

  return (
    <Card className="border-primary/50">
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="space-y-1.5">
            <CardTitle className="text-base">
              {created.label !== null ? `Chave criada: ${created.label}` : "Chave criada"}
            </CardTitle>
            <CardDescription className="font-medium text-viz-improdutivo">
              Guarde a chave agora, ela não será exibida novamente.
            </CardDescription>
          </div>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Fechar
          </Button>
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="space-y-1.5">
          <Label htmlFor="created-key">Chave completa</Label>
          <div className="flex gap-2">
            <Input id="created-key" readOnly value={created.key} className="font-mono" />
            <CopyButton value={created.key} label="Copiar chave" />
          </div>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="install-command">Comando de instalação silenciosa</Label>
          <div className="flex items-start gap-2">
            <pre
              id="install-command"
              className="min-w-0 flex-1 overflow-x-auto rounded-md border border-input bg-muted px-3 py-2.5 font-mono text-xs"
            >
              {installCommand}
            </pre>
            <CopyButton value={installCommand} label="Copiar comando" />
          </div>
          <p className="text-xs text-muted-foreground">
            Para instalar em várias máquinas, distribua este comando por GPO, Intune ou RMM
            apontando para o kit de instalação (MonitorAgent.msi).
          </p>
        </div>

        <div className="rounded-md border bg-muted/40 px-4 py-3">
          {watch !== null && watch.found !== null ? (
            <div className="flex items-start gap-3">
              <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0 text-viz-produtivo" aria-hidden />
              <div className="space-y-1">
                <p className="text-sm font-medium">Primeira máquina conectada!</p>
                <p className="text-sm text-muted-foreground">
                  {watch.found.display_name ?? watch.found.hostname} ·{" "}
                  {deviceStatusLabels[watch.found.status]}
                </p>
                <Link
                  to="/dispositivos"
                  className="inline-block text-sm font-medium text-primary underline-offset-4 hover:underline"
                >
                  Ver dispositivos
                </Link>
              </div>
            </div>
          ) : (
            <div className="flex items-start gap-3">
              <Loader2 className="mt-0.5 h-4 w-4 shrink-0 animate-spin text-muted-foreground" aria-hidden />
              <div className="space-y-1">
                <p className="text-sm font-medium">Aguardando a primeira máquina…</p>
                <p className="text-sm text-muted-foreground">
                  Instale o agente com o comando acima. Assim que o dispositivo se registrar, ele
                  aparece aqui (verificação a cada 10 segundos).
                </p>
                <details className="text-sm text-muted-foreground">
                  <summary className="cursor-pointer underline underline-offset-4 hover:text-foreground">
                    Está demorando?
                  </summary>
                  <p className="pt-1">
                    Verifique o firewall e o proxy da rede: o agente precisa de saída HTTPS (porta
                    443) para este domínio.
                  </p>
                </details>
              </div>
            </div>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

/** Botão de copiar (mesmo idioma do MfaFlow) - troca o ícone por 2s ao copiar. */
function CopyButton({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false);

  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard indisponível (ex.: contexto não seguro) - o campo permite seleção manual.
    }
  }

  return (
    <Button
      type="button"
      variant="outline"
      size="icon"
      className="shrink-0"
      onClick={() => void copy()}
      aria-label={label}
      title={copied ? "Copiado!" : label}
    >
      {copied ? (
        <Check className="h-4 w-4 text-viz-produtivo" aria-hidden />
      ) : (
        <Copy className="h-4 w-4" aria-hidden />
      )}
    </Button>
  );
}

// -----------------------------------------------------------------------------
// Dialogs de criação e revogação (admin/owner)
// -----------------------------------------------------------------------------

function CreateKeyDialog({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (created: EnrollmentKeyCreateResponse) => void;
}) {
  const queryClient = useQueryClient();
  const [label, setLabel] = useState("");
  const [maxUses, setMaxUses] = useState("");
  const [expiresAt, setExpiresAt] = useState("");

  // Gates do cliente espelham as validações do backend (400): inteiro > 0 e
  // expiração no futuro - evita a ida ao servidor para erro conhecido.
  const maxUsesInvalid =
    maxUses.trim().length > 0 && (!/^\d+$/.test(maxUses.trim()) || Number(maxUses.trim()) < 1);
  const expiresInvalid = expiresAt.length > 0 && !(new Date(expiresAt).getTime() > Date.now());

  const mutation = useMutation({
    mutationFn: () => {
      const body: EnrollmentKeyCreateRequest = {};
      const trimmedLabel = label.trim();
      if (trimmedLabel.length > 0) body.label = trimmedLabel;
      const trimmedMax = maxUses.trim();
      if (trimmedMax.length > 0) body.max_uses = Number(trimmedMax);
      if (expiresAt.length > 0) body.expires_at = new Date(expiresAt).toISOString();
      return api<EnrollmentKeyCreateResponse>("/enrollment-keys", { method: "POST", body });
    },
    onSuccess: async (createdKey) => {
      await queryClient.invalidateQueries({ queryKey: ["enrollment-keys"] });
      onCreated(createdKey);
    },
  });

  function submit(): void {
    if (mutation.isPending || maxUsesInvalid || expiresInvalid) return;
    mutation.mutate();
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
          <DialogTitle>Nova chave de instalação</DialogTitle>
          <DialogDescription>
            Todos os campos são opcionais. A chave completa aparece uma única vez, logo após a
            criação.
          </DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="key-label">Rótulo</Label>
            <Input
              id="key-label"
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              placeholder="Ex.: matriz, filial sul"
              maxLength={120}
              autoFocus
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="key-max-uses">Limite de usos</Label>
            <Input
              id="key-max-uses"
              value={maxUses}
              onChange={(e) => setMaxUses(e.target.value)}
              inputMode="numeric"
              placeholder="Sem limite"
              className="max-w-[10rem]"
            />
            {maxUsesInvalid && (
              <p role="alert" className="text-sm text-destructive">
                Informe um número inteiro maior que zero.
              </p>
            )}
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="key-expires">Expiração</Label>
            <Input
              id="key-expires"
              type="datetime-local"
              value={expiresAt}
              onChange={(e) => setExpiresAt(e.target.value)}
              className="max-w-xs"
            />
            {expiresInvalid && (
              <p role="alert" className="text-sm text-destructive">
                A expiração precisa estar no futuro.
              </p>
            )}
          </div>
          {mutation.isError && (
            <p role="alert" className="text-sm text-destructive">
              {genericErrorMessage(mutation.error)}
            </p>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
              Cancelar
            </Button>
            <Button type="submit" disabled={mutation.isPending || maxUsesInvalid || expiresInvalid}>
              {mutation.isPending ? "Criando…" : "Criar chave"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function RevokeKeyDialog({
  enrollmentKey,
  onClose,
}: {
  enrollmentKey: EnrollmentKeyItem;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const mutation = useMutation({
    mutationFn: () =>
      api<void>(`/enrollment-keys/${encodeURIComponent(enrollmentKey.id)}`, { method: "DELETE" }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["enrollment-keys"] });
      onClose();
    },
  });

  const name = enrollmentKey.label ?? `${enrollmentKey.key_prefix}…`;

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Revogar chave</DialogTitle>
          <DialogDescription>Revogar a chave "{name}"?</DialogDescription>
        </DialogHeader>
        <p className="text-sm text-muted-foreground">
          Novas instalações com esta chave serão recusadas. Os dispositivos já registrados
          continuam funcionando normalmente.
        </p>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {genericErrorMessage(mutation.error)}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancelar
          </Button>
          <Button
            variant="destructive"
            onClick={() => mutation.mutate()}
            disabled={mutation.isPending}
          >
            {mutation.isPending ? "Revogando…" : "Revogar chave"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
