import { useState } from "react";
import type { FormEvent } from "react";
import { Link } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { NETWORK_ERROR } from "@/lib/messages";
import type { ForgotPasswordRequest } from "@/lib/types";
import { AuthCard } from "@/components/auth/AuthCard";
import { FormError } from "@/components/FormError";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

/** Recuperação de senha — a resposta é SEMPRE genérica (não revela se o e-mail existe). */
export function RecuperarSenhaPage() {
  const [email, setEmail] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);
  const [sent, setSent] = useState(false);

  const forgotMutation = useMutation({
    mutationFn: (body: ForgotPasswordRequest) =>
      api<void>("/auth/forgot-password", { method: "POST", body, auth: false }),
    onSuccess: () => setSent(true),
    // Mesmo em erro 4xx o servidor responde genérico; só falha de rede é exibida.
    onError: () => undefined,
  });

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    const trimmed = email.trim();
    if (trimmed.length === 0 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(trimmed)) {
      setValidationError("Informe um e-mail válido.");
      return;
    }
    setValidationError(null);
    forgotMutation.mutate({ email: trimmed });
  }

  if (sent) {
    return (
      <AuthCard
        title="Verifique seu e-mail"
        footer={
          <Link to="/login" className="underline underline-offset-4">
            Voltar ao login
          </Link>
        }
      >
        <p className="text-sm text-muted-foreground">
          Se este e-mail existir em nossa base, enviamos instruções de recuperação. O link expira em
          60 minutos.
        </p>
      </AuthCard>
    );
  }

  return (
    <AuthCard
      title="Recuperar senha"
      description="Informe o e-mail da sua conta. Enviaremos instruções de recuperação."
      footer={
        <Link to="/login" className="underline underline-offset-4">
          Voltar ao login
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
        <FormError message={validationError ?? (forgotMutation.isError ? NETWORK_ERROR : null)} />
        <Button type="submit" className="w-full" disabled={forgotMutation.isPending}>
          {forgotMutation.isPending ? "Enviando…" : "Enviar instruções"}
        </Button>
      </form>
    </AuthCard>
  );
}
