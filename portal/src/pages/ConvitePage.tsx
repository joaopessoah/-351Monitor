import { useState } from "react";
import type { FormEvent } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { api, ApiError } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { genericErrorMessage } from "@/lib/messages";
import type { InvitationAcceptRequest, InvitationInfo, LoginResponse } from "@/lib/types";
import { roleLabels } from "@/lib/format";
import { AuthCard } from "@/components/auth/AuthCard";
import { MfaSetupPanel } from "@/components/auth/MfaFlow";
import { MIN_PASSWORD_LENGTH, PasswordStrength } from "@/components/auth/PasswordStrength";
import { FormError } from "@/components/FormError";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";

/**
 * Aceite de convite (/convite/:token): define nome + senha (mínimo 12 — N23) e,
 * quando o papel exige (Owner/Admin), completa o setup de MFA TOTP na sequência.
 */
export function ConvitePage() {
  const { token } = useParams<{ token: string }>();
  const navigate = useNavigate();
  const { signIn } = useAuth();

  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);
  const [mfaToken, setMfaToken] = useState<string | null>(null);

  const invitationQuery = useQuery({
    queryKey: ["invitation", token],
    queryFn: () => api<InvitationInfo>(`/auth/invite/${token}`, { auth: false }),
    enabled: typeof token === "string" && token.length > 0,
    retry: false,
  });

  const acceptMutation = useMutation({
    mutationFn: (body: InvitationAcceptRequest) =>
      api<LoginResponse>("/auth/invite/accept", { method: "POST", body, auth: false }),
    onSuccess: (data) => {
      if (data.status === "ok") {
        finishSignIn(data.access_token);
      } else if (data.status === "mfa_setup_required" || data.status === "mfa_required") {
        setMfaToken(data.mfa_token);
      }
    },
  });

  function finishSignIn(accessToken: string) {
    signIn(accessToken);
    navigate("/visao-geral", { replace: true });
  }

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (displayName.trim().length < 2) {
      setValidationError("Informe seu nome completo.");
      return;
    }
    if (password.length < MIN_PASSWORD_LENGTH) {
      setValidationError(`A senha deve ter no mínimo ${MIN_PASSWORD_LENGTH} caracteres.`);
      return;
    }
    if (password !== confirmPassword) {
      setValidationError("As senhas não coincidem.");
      return;
    }
    setValidationError(null);
    acceptMutation.mutate({ token: token ?? "", display_name: displayName.trim(), password });
  }

  const inviteInvalid =
    invitationQuery.isError &&
    invitationQuery.error instanceof ApiError &&
    [400, 404, 410, 422].includes(invitationQuery.error.status);

  if (typeof token !== "string" || token.length === 0 || inviteInvalid) {
    return (
      <AuthCard
        title="Convite inválido"
        footer={
          <Link to="/login" className="underline underline-offset-4">
            Ir para o login
          </Link>
        }
      >
        <p className="text-sm text-muted-foreground">
          Este convite é inválido, já foi utilizado ou expirou. Peça um novo convite ao
          administrador da sua organização.
        </p>
      </AuthCard>
    );
  }

  if (invitationQuery.isPending) {
    return (
      <AuthCard title="Convite">
        <div className="space-y-3">
          <Skeleton className="h-4 w-3/4" />
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
        </div>
      </AuthCard>
    );
  }

  if (invitationQuery.isError) {
    return (
      <AuthCard title="Convite">
        <div className="space-y-4">
          <FormError message={genericErrorMessage(invitationQuery.error)} />
          <Button variant="outline" className="w-full" onClick={() => void invitationQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      </AuthCard>
    );
  }

  const invitation = invitationQuery.data;

  if (mfaToken !== null) {
    return (
      <AuthCard title="Configurar verificação em duas etapas">
        <MfaSetupPanel mfaToken={mfaToken} onSuccess={finishSignIn} />
      </AuthCard>
    );
  }

  return (
    <AuthCard
      title="Criar sua conta"
      description={`Você foi convidado(a) para ${invitation.organization_name} como ${roleLabels[invitation.role]}.`}
    >
      <form onSubmit={handleSubmit} className="space-y-4" noValidate>
        <div className="space-y-2">
          <Label htmlFor="invite-email">E-mail</Label>
          <Input id="invite-email" type="email" value={invitation.email} disabled />
        </div>
        <div className="space-y-2">
          <Label htmlFor="display-name">Nome</Label>
          <Input
            id="display-name"
            autoComplete="name"
            placeholder="Seu nome completo"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            autoFocus
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="new-password">Senha</Label>
          <Input
            id="new-password"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <PasswordStrength password={password} />
        </div>
        <div className="space-y-2">
          <Label htmlFor="confirm-password">Confirmar senha</Label>
          <Input
            id="confirm-password"
            type="password"
            autoComplete="new-password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
          />
        </div>
        {invitation.mfa_required && (
          <p className="text-xs text-muted-foreground">
            Seu papel ({roleLabels[invitation.role]}) exige verificação em duas etapas, e você fará a
            configuração na próxima etapa.
          </p>
        )}
        <FormError
          message={validationError ?? (acceptMutation.isError ? genericErrorMessage(acceptMutation.error) : null)}
        />
        <Button type="submit" className="w-full" disabled={acceptMutation.isPending}>
          {acceptMutation.isPending ? "Criando conta…" : "Criar conta e entrar"}
        </Button>
      </form>
    </AuthCard>
  );
}
