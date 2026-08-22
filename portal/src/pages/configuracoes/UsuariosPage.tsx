// =============================================================================
// Configurações -> Usuários do portal (Seção 7.4/8.8). Tabela de GET /users
// (PolicyAdminPlus - o viewer recebe o aviso amigável, espelhando a
// AuditoriaPage) com convite por e-mail (POST /users/invitations, válido por
// 7 dias), troca de papel (PATCH /users/{id}), desativação (DELETE - vira
// status disabled e revoga as sessões), reenvio de convite (POST
// /users/{id}/invitations/resend, só para convidados) e recuperação assistida
// de MFA (POST /users/{id}/mfa/reset). Gates de papel: mexer em Owner exige
// ator Owner (a UI nem oferece a ação); o backend garante sempre >= 1 Owner
// ativo e a UI exibe a mensagem do ProblemDetails (lib/messages).
// =============================================================================

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  CheckCircle2,
  Ellipsis,
  Eye,
  MailPlus,
  ShieldCheck,
  UserCog,
  UserPlus,
  UserX,
} from "lucide-react";
import { api } from "@/lib/api";
import { formatDateTime, roleLabels } from "@/lib/format";
import { genericErrorMessage, problemErrorMessage } from "@/lib/messages";
import { isAdmin, isOwner } from "@/lib/roles";
import type {
  InviteUserRequest,
  InviteUserResponse,
  MeResponse,
  Role,
  UserListItem,
  UserRolePatchRequest,
  UserStatus,
  UsersResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
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

const statusLabels: Record<UserStatus, string> = {
  invited: "Convidado",
  active: "Ativo",
  disabled: "Desativado",
};

const statusClasses: Record<UserStatus, string> = {
  invited: "bg-viz-neutro/15 text-viz-neutro",
  active: "bg-viz-produtivo/15 text-viz-produtivo",
  disabled: "bg-muted text-muted-foreground",
};

const selectClass = cn(
  "h-10 w-full rounded-md border border-input bg-card px-3 text-sm",
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
);

export function UsuariosPage() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const me = meQuery.data;

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold tracking-tight">Usuários do portal</h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Convites por e-mail, papéis e acesso ao portal. A organização precisa de pelo menos um
          Proprietário ativo.
        </p>
      </div>

      {meQuery.isPending ? (
        // Aguarda o papel antes de decidir o que mostrar (evita o flash do aviso
        // de viewer para um admin/owner num cache frio do GET /me).
        <Card>
          <div className="space-y-2 p-6">
            {Array.from({ length: 5 }, (_, i) => (
              <Skeleton key={i} className="h-9 w-full" />
            ))}
          </div>
        </Card>
      ) : !isAdmin(me) ? (
        <ViewerNotice />
      ) : me !== undefined ? (
        <UsersCard me={me} />
      ) : null}
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
        <p className="text-base font-medium">Sem permissão para gerenciar usuários</p>
        <p className="max-w-md text-sm text-muted-foreground">
          A administração de usuários fica disponível para Administradores e Proprietários da
          organização.
        </p>
      </div>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Tabela + ações (admin/owner)
// -----------------------------------------------------------------------------

type UserActionKind = "role" | "deactivate" | "mfa";

interface UserAction {
  kind: UserActionKind;
  user: UserListItem;
}

function UsersCard({ me }: { me: MeResponse }) {
  const owner = isOwner(me);
  const timezone = me.organization.timezone;

  const [inviting, setInviting] = useState(false);
  const [action, setAction] = useState<UserAction | null>(null);
  const [feedback, setFeedback] = useState<string | null>(null);
  const [rowError, setRowError] = useState<string | null>(null);

  const usersQuery = useQuery({
    queryKey: ["users"],
    queryFn: () => api<UsersResponse>("/users"),
    staleTime: 5 * 60 * 1000,
  });
  const data = usersQuery.data;

  // Reenvio de convite direto do menu (sem dialog - não é destrutivo). 409 do
  // backend (usuário já ativo) e afins aparecem no banner com o ProblemDetails.
  const resendMutation = useMutation({
    mutationFn: (user: UserListItem) =>
      api<unknown>(`/users/${encodeURIComponent(user.id)}/invitations/resend`, { method: "POST" }),
    onSuccess: (_data, user) => {
      setRowError(null);
      setFeedback(`Convite reenviado para ${user.email}, válido por 7 dias.`);
    },
    onError: (err) => {
      setFeedback(null);
      setRowError(problemErrorMessage(err));
    },
  });

  return (
    <>
      <Card>
        <CardHeader className="pb-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="space-y-1.5">
              <CardTitle className="text-base">Usuários</CardTitle>
              <CardDescription>
                Papéis: Proprietário administra tudo, Administrador configura a organização e
                Visualizador é somente leitura.
              </CardDescription>
            </div>
            <Button size="sm" className="h-9" onClick={() => setInviting(true)}>
              <UserPlus className="h-4 w-4" aria-hidden />
              Convidar usuário
            </Button>
          </div>
        </CardHeader>

        {feedback !== null && (
          <div
            role="status"
            className="flex flex-wrap items-center justify-between gap-2 border-b bg-viz-produtivo/10 px-6 py-2.5 text-sm text-viz-produtivo"
          >
            <span className="flex items-center gap-2">
              <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden />
              {feedback}
            </span>
            <Button variant="ghost" size="sm" className="h-8" onClick={() => setFeedback(null)}>
              Fechar
            </Button>
          </div>
        )}

        {rowError !== null && (
          <div
            role="alert"
            className="flex flex-wrap items-center justify-between gap-2 border-b bg-destructive/10 px-6 py-2.5 text-sm text-destructive"
          >
            <span>{rowError}</span>
            <Button variant="outline" size="sm" className="h-8" onClick={() => setRowError(null)}>
              Fechar
            </Button>
          </div>
        )}

        {usersQuery.isError && data === undefined ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(usersQuery.error)}</p>
            <Button variant="outline" onClick={() => void usersQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Nome</th>
                  <th scope="col" className="px-3 py-2">E-mail</th>
                  <th scope="col" className="px-3 py-2">Papel</th>
                  <th scope="col" className="px-3 py-2">Status</th>
                  <th scope="col" className="px-3 py-2">MFA</th>
                  <th scope="col" className="px-3 py-2">Último acesso</th>
                  <th scope="col" className="px-6 py-2 text-right">Ações</th>
                </tr>
              </thead>
              <tbody>
                {data === undefined ? (
                  Array.from({ length: 4 }, (_, i) => (
                    <tr key={i} className="border-b last:border-b-0">
                      <td colSpan={7} className="px-6 py-2">
                        <Skeleton className="h-8 w-full" />
                      </td>
                    </tr>
                  ))
                ) : (
                  data.items.map((user) => {
                    // Mexer em Owner (papel, desativação, MFA) exige ator Owner:
                    // a UI nem oferece o menu - espelho dos gates do backend.
                    const canAct = owner || user.role !== "owner";
                    const disabled = user.status === "disabled";
                    return (
                      <tr key={user.id} className="border-b last:border-b-0">
                        <td
                          className={cn(
                            "max-w-[14rem] truncate px-6 py-2 font-medium",
                            disabled && "text-muted-foreground",
                          )}
                        >
                          {user.display_name}
                        </td>
                        <td className="max-w-[16rem] truncate px-3 py-2 text-muted-foreground">
                          {user.email}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2">{roleLabels[user.role]}</td>
                        <td className="whitespace-nowrap px-3 py-2">
                          <span
                            className={cn(
                              "rounded-full px-2 py-0.5 text-xs font-medium",
                              statusClasses[user.status],
                            )}
                          >
                            {statusLabels[user.status]}
                          </span>
                        </td>
                        <td className="whitespace-nowrap px-3 py-2">
                          {user.mfa_enabled ? (
                            <span className="inline-flex items-center gap-1">
                              <ShieldCheck
                                className="h-3.5 w-3.5 shrink-0 text-viz-produtivo"
                                aria-hidden
                              />
                              Sim
                            </span>
                          ) : (
                            <span className="text-muted-foreground">Não</span>
                          )}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground">
                          {user.last_login_at !== null
                            ? formatDateTime(user.last_login_at, timezone)
                            : "nunca"}
                        </td>
                        <td className="whitespace-nowrap px-6 py-2 text-right">
                          {canAct ? (
                            <UserRowActions
                              user={user}
                              resendPending={resendMutation.isPending}
                              onAction={setAction}
                              onResend={(u) => resendMutation.mutate(u)}
                            />
                          ) : (
                            <span
                              className="text-muted-foreground"
                              title="Apenas um Proprietário altera outro Proprietário"
                            >
                              -
                            </span>
                          )}
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {inviting && (
        <InviteUserDialog
          owner={owner}
          onClose={() => setInviting(false)}
          onInvited={(email) => {
            setInviting(false);
            setRowError(null);
            setFeedback(`Convite enviado para ${email}, válido por 7 dias.`);
          }}
        />
      )}
      {action !== null && action.kind === "role" && (
        <ChangeRoleDialog user={action.user} owner={owner} onClose={() => setAction(null)} />
      )}
      {action !== null && action.kind === "deactivate" && (
        <DeactivateUserDialog user={action.user} onClose={() => setAction(null)} />
      )}
      {action !== null && action.kind === "mfa" && (
        <ResetMfaDialog
          user={action.user}
          onClose={() => setAction(null)}
          onDone={(message) => {
            setAction(null);
            setRowError(null);
            setFeedback(message);
          }}
        />
      )}
    </>
  );
}

/** Menu de reticências da linha - só ações válidas para o status/MFA do usuário. */
function UserRowActions({
  user,
  resendPending,
  onAction,
  onResend,
}: {
  user: UserListItem;
  resendPending: boolean;
  onAction: (action: UserAction) => void;
  onResend: (user: UserListItem) => void;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="sm"
          className="h-8 w-8 p-0"
          aria-label={`Ações de ${user.display_name}`}
        >
          <Ellipsis className="h-4 w-4" aria-hidden />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={() => onAction({ kind: "role", user })}>
          <UserCog className="h-3.5 w-3.5" aria-hidden />
          Alterar papel
        </DropdownMenuItem>
        {user.status === "invited" && (
          <DropdownMenuItem disabled={resendPending} onSelect={() => onResend(user)}>
            <MailPlus className="h-3.5 w-3.5" aria-hidden />
            Reenviar convite
          </DropdownMenuItem>
        )}
        {user.mfa_enabled && (
          <DropdownMenuItem onSelect={() => onAction({ kind: "mfa", user })}>
            <ShieldCheck className="h-3.5 w-3.5" aria-hidden />
            Redefinir MFA
          </DropdownMenuItem>
        )}
        {user.status !== "disabled" && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem
              className="text-destructive focus:text-destructive"
              onSelect={() => onAction({ kind: "deactivate", user })}
            >
              <UserX className="h-3.5 w-3.5" aria-hidden />
              Desativar
            </DropdownMenuItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

// -----------------------------------------------------------------------------
// Dialogs
// -----------------------------------------------------------------------------

/** Convite por e-mail - POST /users/invitations (o link do convite vale 7 dias). */
function InviteUserDialog({
  owner,
  onClose,
  onInvited,
}: {
  owner: boolean;
  onClose: () => void;
  onInvited: (email: string) => void;
}) {
  const queryClient = useQueryClient();
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<Role>("viewer");
  const [displayName, setDisplayName] = useState("");

  const emailValid = email.trim().includes("@");

  const mutation = useMutation({
    mutationFn: () => {
      const body: InviteUserRequest = { email: email.trim(), role };
      const name = displayName.trim();
      if (name.length > 0) body.display_name = name;
      return api<InviteUserResponse>("/users/invitations", { method: "POST", body });
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["users"] });
      onInvited(email.trim());
    },
  });

  function submit(): void {
    if (mutation.isPending || !emailValid) return;
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
          <DialogTitle>Convidar usuário</DialogTitle>
          <DialogDescription>
            A pessoa recebe um e-mail com o link para criar a senha, válido por 7 dias.
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
            <Label htmlFor="invite-email">E-mail</Label>
            <Input
              id="invite-email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="pessoa@empresa.com.br"
              autoFocus
              required
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="invite-role">Papel</Label>
            <select
              id="invite-role"
              value={role}
              onChange={(e) => setRole(e.target.value as Role)}
              className={selectClass}
            >
              <option value="viewer">{roleLabels.viewer}</option>
              <option value="admin">{roleLabels.admin}</option>
              {/* Convidar Owner exige ator Owner (gate do backend espelhado). */}
              {owner && <option value="owner">{roleLabels.owner}</option>}
            </select>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="invite-name">Nome (opcional)</Label>
            <Input
              id="invite-name"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              maxLength={120}
              placeholder="Como a pessoa aparece no portal"
            />
          </div>
          {mutation.isError && (
            <p role="alert" className="text-sm text-destructive">
              {problemErrorMessage(mutation.error)}
            </p>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
              Cancelar
            </Button>
            <Button type="submit" disabled={mutation.isPending || !emailValid}>
              {mutation.isPending ? "Enviando…" : "Enviar convite"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Troca de papel - PATCH /users/{id}. O 409 do último Owner aparece no dialog. */
function ChangeRoleDialog({
  user,
  owner,
  onClose,
}: {
  user: UserListItem;
  owner: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [role, setRole] = useState<Role>(user.role);

  const mutation = useMutation({
    mutationFn: () => {
      const body: UserRolePatchRequest = { role };
      return api<UserListItem>(`/users/${encodeURIComponent(user.id)}`, { method: "PATCH", body });
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
  });

  function submit(): void {
    if (mutation.isPending || role === user.role) return;
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
          <DialogTitle>Alterar papel</DialogTitle>
          <DialogDescription>
            Papel de {user.display_name} ({user.email}) no portal.
          </DialogDescription>
        </DialogHeader>
        <form
          className="space-y-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <Label htmlFor="user-role">Papel</Label>
          <select
            id="user-role"
            value={role}
            onChange={(e) => setRole(e.target.value as Role)}
            className={selectClass}
            autoFocus
          >
            <option value="viewer">{roleLabels.viewer}</option>
            <option value="admin">{roleLabels.admin}</option>
            {/* Promover a Owner exige ator Owner; a option só existe nesse caso
                (ou quando o usuário JÁ é Owner, para o valor atual aparecer). */}
            {(owner || user.role === "owner") && (
              <option value="owner">{roleLabels.owner}</option>
            )}
          </select>
        </form>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {problemErrorMessage(mutation.error)}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancelar
          </Button>
          <Button onClick={submit} disabled={mutation.isPending || role === user.role}>
            {mutation.isPending ? "Salvando…" : "Salvar"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Desativação - DELETE /users/{id} (status disabled + sessões revogadas). */
function DeactivateUserDialog({ user, onClose }: { user: UserListItem; onClose: () => void }) {
  const queryClient = useQueryClient();
  const mutation = useMutation({
    mutationFn: () => api<void>(`/users/${encodeURIComponent(user.id)}`, { method: "DELETE" }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
  });

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Desativar usuário</DialogTitle>
          <DialogDescription>
            Desativar {user.display_name} ({user.email})?
          </DialogDescription>
        </DialogHeader>
        <p className="text-sm text-muted-foreground">
          O usuário perde o acesso ao portal e as sessões ativas são encerradas. O histórico e a
          trilha de auditoria são preservados.
        </p>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {problemErrorMessage(mutation.error)}
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
            {mutation.isPending ? "Desativando…" : "Desativar"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Recuperação assistida de MFA - POST /users/{id}/mfa/reset (204). */
function ResetMfaDialog({
  user,
  onClose,
  onDone,
}: {
  user: UserListItem;
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  const queryClient = useQueryClient();
  const mutation = useMutation({
    mutationFn: () =>
      api<void>(`/users/${encodeURIComponent(user.id)}/mfa/reset`, { method: "POST" }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["users"] });
      onDone(`Verificação em duas etapas redefinida para ${user.email}.`);
    },
  });

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Redefinir verificação em duas etapas</DialogTitle>
          <DialogDescription>
            Redefinir a MFA de {user.display_name} ({user.email})?
          </DialogDescription>
        </DialogHeader>
        <p className="text-sm text-muted-foreground">
          A configuração atual do aplicativo autenticador será removida. No próximo login, o
          usuário fará um novo setup da verificação em duas etapas antes de entrar.
        </p>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {problemErrorMessage(mutation.error)}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancelar
          </Button>
          <Button onClick={() => mutation.mutate()} disabled={mutation.isPending}>
            {mutation.isPending ? "Redefinindo…" : "Redefinir MFA"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
