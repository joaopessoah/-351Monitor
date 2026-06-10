import { useState } from "react";
import type { FormEvent } from "react";
import { Link, useParams } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { api, ApiError } from "@/lib/api";
import { GENERIC_ERROR, NETWORK_ERROR } from "@/lib/messages";
import type { ResetPasswordRequest } from "@/lib/types";
import { AuthCard } from "@/components/auth/AuthCard";
import { MIN_PASSWORD_LENGTH, PasswordStrength } from "@/components/auth/PasswordStrength";
import { FormError } from "@/components/FormError";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

/** Redefinição de senha com token do e-mail (60 min); invalida todas as sessões. */
export function RedefinirSenhaPage() {
  const { token } = useParams<{ token: string }>();
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);

  const resetMutation = useMutation({
    mutationFn: (body: ResetPasswordRequest) =>
      api<void>("/auth/reset-password", { method: "POST", body, auth: false }),
  });

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (password.length < MIN_PASSWORD_LENGTH) {
      setValidationError(`A senha deve ter no mínimo ${MIN_PASSWORD_LENGTH} caracteres.`);
      return;
    }
    if (password !== confirmPassword) {
      setValidationError("As senhas não coincidem.");
      return;
    }
    setValidationError(null);
    resetMutation.mutate({ token: token ?? "", password });
  }

  if (resetMutation.isSuccess) {
    return (
      <AuthCard title="Senha redefinida">
        <div className="space-y-4">
          <p className="text-sm text-muted-foreground">
            Sua senha foi redefinida e todas as sessões anteriores foram encerradas.
          </p>
          <Link
            to="/login"
            className="inline-flex h-10 w-full items-center justify-center rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90"
          >
            Ir para o login
          </Link>
        </div>
      </AuthCard>
    );
  }

  const requestError = resetMutation.isError
    ? resetMutation.error instanceof ApiError
      ? resetMutation.error.status === 400 || resetMutation.error.status === 404 || resetMutation.error.status === 422
        ? "Link inválido ou expirado. Solicite uma nova recuperação de senha."
        : GENERIC_ERROR
      : NETWORK_ERROR
    : null;

  return (
    <AuthCard
      title="Definir nova senha"
      footer={
        <Link to="/recuperar-senha" className="underline underline-offset-4">
          Solicitar nova recuperação
        </Link>
      }
    >
      <form onSubmit={handleSubmit} className="space-y-4" noValidate>
        <div className="space-y-2">
          <Label htmlFor="new-password">Nova senha</Label>
          <Input
            id="new-password"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoFocus
          />
          <PasswordStrength password={password} />
        </div>
        <div className="space-y-2">
          <Label htmlFor="confirm-password">Confirmar nova senha</Label>
          <Input
            id="confirm-password"
            type="password"
            autoComplete="new-password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
          />
        </div>
        <FormError message={validationError ?? requestError} />
        <Button type="submit" className="w-full" disabled={resetMutation.isPending}>
          {resetMutation.isPending ? "Salvando…" : "Redefinir senha"}
        </Button>
      </form>
    </AuthCard>
  );
}
