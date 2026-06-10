import { useState } from "react";
import type { FormEvent } from "react";
import { Link, Navigate, useLocation, useNavigate } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { loginErrorMessage } from "@/lib/messages";
import type { LoginRequest, LoginResponse } from "@/lib/types";
import { AuthCard } from "@/components/auth/AuthCard";
import { MfaSetupPanel, MfaVerifyForm } from "@/components/auth/MfaFlow";
import { FormError } from "@/components/FormError";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

type Step =
  | { kind: "credentials" }
  | { kind: "mfa_verify"; mfaToken: string }
  | { kind: "mfa_setup"; mfaToken: string };

export function LoginPage() {
  const { status, signIn } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? "/visao-geral";

  const [step, setStep] = useState<Step>({ kind: "credentials" });
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);

  const loginMutation = useMutation({
    mutationFn: (body: LoginRequest) =>
      api<LoginResponse>("/auth/login", { method: "POST", body, auth: false }),
    onSuccess: (data) => {
      if (data.status === "ok") {
        finishSignIn(data.access_token);
      } else if (data.status === "mfa_required") {
        setStep({ kind: "mfa_verify", mfaToken: data.mfa_token });
      } else {
        setStep({ kind: "mfa_setup", mfaToken: data.mfa_token });
      }
    },
  });

  if (status === "authenticated") {
    return <Navigate to={from} replace />;
  }

  function finishSignIn(accessToken: string) {
    signIn(accessToken);
    navigate(from, { replace: true });
  }

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    const trimmedEmail = email.trim();
    if (trimmedEmail.length === 0 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(trimmedEmail)) {
      setValidationError("Informe um e-mail válido.");
      return;
    }
    if (password.length === 0) {
      setValidationError("Informe sua senha.");
      return;
    }
    setValidationError(null);
    loginMutation.mutate({ email: trimmedEmail, password });
  }

  if (step.kind === "mfa_verify") {
    return (
      <AuthCard
        title="Verificação em duas etapas"
        description="Informe o código de 6 dígitos gerado pelo seu aplicativo autenticador."
        footer={
          <button type="button" className="underline underline-offset-4" onClick={() => setStep({ kind: "credentials" })}>
            Voltar ao login
          </button>
        }
      >
        <MfaVerifyForm mfaToken={step.mfaToken} onSuccess={finishSignIn} submitLabel="Entrar" />
      </AuthCard>
    );
  }

  if (step.kind === "mfa_setup") {
    return (
      <AuthCard
        title="Configurar verificação em duas etapas"
        footer={
          <button type="button" className="underline underline-offset-4" onClick={() => setStep({ kind: "credentials" })}>
            Voltar ao login
          </button>
        }
      >
        <MfaSetupPanel mfaToken={step.mfaToken} onSuccess={finishSignIn} />
      </AuthCard>
    );
  }

  return (
    <AuthCard
      title="Entrar"
      description="Acesse o portal da sua organização."
      footer={
        <Link to="/recuperar-senha" className="underline underline-offset-4">
          Esqueci minha senha
        </Link>
      }
    >
      <form onSubmit={handleSubmit} className="space-y-4" noValidate>
        <div className="space-y-2">
          <Label htmlFor="email">E-mail</Label>
          <Input
            id="email"
            type="email"
            autoComplete="username"
            placeholder="voce@empresa.com.br"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoFocus
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="password">Senha</Label>
          <Input
            id="password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </div>
        <FormError
          message={validationError ?? (loginMutation.isError ? loginErrorMessage(loginMutation.error) : null)}
        />
        <Button type="submit" className="w-full" disabled={loginMutation.isPending}>
          {loginMutation.isPending ? "Entrando…" : "Entrar"}
        </Button>
      </form>
    </AuthCard>
  );
}
