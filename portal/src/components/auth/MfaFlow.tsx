import { useState } from "react";
import type { FormEvent } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { QRCodeSVG } from "qrcode.react";
import { Copy } from "lucide-react";
import { api } from "@/lib/api";
import { mfaErrorMessage, genericErrorMessage } from "@/lib/messages";
import type { MfaSetupResponse, MfaVerifyRequest, MfaVerifyResponse, RecoveryCodesResponse } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { FormError } from "@/components/FormError";

interface MfaVerifyFormProps {
  mfaToken: string;
  /** Recebe o access token após verificação bem-sucedida. */
  onSuccess: (accessToken: string) => void;
  submitLabel?: string;
  /**
   * No LOGIN (MFA já habilitada) o backend também aceita um código de recuperação
   * (formato XXXXX-XXXXX) no lugar do TOTP; no setup, apenas o TOTP conclui.
   */
  allowRecoveryCode?: boolean;
}

/** Formulário de código TOTP (6 dígitos) — POST /auth/mfa/verify. */
export function MfaVerifyForm({
  mfaToken,
  onSuccess,
  submitLabel = "Verificar",
  allowRecoveryCode = false,
}: MfaVerifyFormProps) {
  const [code, setCode] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);

  // O mfa_token temporário autentica a chamada via Authorization: Bearer (policy MfaToken).
  const verifyMutation = useMutation({
    mutationFn: (body: MfaVerifyRequest) =>
      api<MfaVerifyResponse>("/auth/mfa/verify", {
        method: "POST",
        body,
        auth: false,
        bearerToken: mfaToken,
      }),
    onSuccess: (data) => onSuccess(data.access_token),
  });

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    const trimmed = code.trim();
    const isTotp = /^\d{6}$/.test(trimmed);
    const isRecovery = allowRecoveryCode && /^[A-Za-z0-9]{5}-?[A-Za-z0-9]{5}$/.test(trimmed);
    if (!isTotp && !isRecovery) {
      setValidationError(
        allowRecoveryCode
          ? "Informe o código de 6 dígitos do aplicativo autenticador ou um código de recuperação."
          : "Informe o código de 6 dígitos do seu aplicativo autenticador.",
      );
      return;
    }
    setValidationError(null);
    verifyMutation.mutate({ code: trimmed });
  }

  return (
    <form onSubmit={handleSubmit} className="space-y-4" noValidate>
      <div className="space-y-2">
        <Label htmlFor="mfa-code">Código de verificação</Label>
        <Input
          id="mfa-code"
          inputMode={allowRecoveryCode ? "text" : "numeric"}
          autoComplete="one-time-code"
          placeholder={allowRecoveryCode ? "000000 ou XXXXX-XXXXX" : "000000"}
          maxLength={allowRecoveryCode ? 11 : 6}
          value={code}
          onChange={(e) =>
            setCode(allowRecoveryCode ? e.target.value.toUpperCase() : e.target.value.replace(/\D/g, ""))
          }
          autoFocus
          className="text-center text-lg tracking-[0.35em] tabular-nums"
        />
        {allowRecoveryCode ? (
          <p className="text-xs text-muted-foreground">
            Sem acesso ao aplicativo? Use um dos seus códigos de recuperação, cada um vale uma
            única vez.
          </p>
        ) : null}
      </div>
      <FormError message={validationError ?? (verifyMutation.isError ? mfaErrorMessage(verifyMutation.error) : null)} />
      <Button type="submit" className="w-full" disabled={verifyMutation.isPending}>
        {verifyMutation.isPending ? "Verificando…" : submitLabel}
      </Button>
    </form>
  );
}

interface MfaSetupPanelProps {
  mfaToken: string;
  onSuccess: (accessToken: string) => void;
}

/**
 * Provisionamento de TOTP (POST /auth/mfa/setup) + confirmação do primeiro código.
 * Obrigatório para Proprietário/Administrador antes de qualquer outra navegação.
 * Após ativar, gera e exibe os códigos de recuperação UMA única vez antes de entrar.
 */
export function MfaSetupPanel({ mfaToken, onSuccess }: MfaSetupPanelProps) {
  const [recovery, setRecovery] = useState<{ accessToken: string; codes: string[] } | null>(null);

  // Gera os 10 códigos de recuperação logo após o setup: é a única chance de exibi-los.
  // Falha aqui não bloqueia o login (os códigos podem ser gerados depois em outro fluxo).
  const recoveryMutation = useMutation({
    mutationFn: (accessToken: string) =>
      api<RecoveryCodesResponse>("/auth/mfa/recovery-codes", {
        method: "POST",
        auth: false,
        bearerToken: accessToken,
      }),
  });

  async function handleVerified(accessToken: string) {
    try {
      const response = await recoveryMutation.mutateAsync(accessToken);
      setRecovery({ accessToken, codes: response.codes });
    } catch {
      onSuccess(accessToken);
    }
  }

  if (recovery) {
    return <RecoveryCodesPanel codes={recovery.codes} onContinue={() => onSuccess(recovery.accessToken)} />;
  }

  return <MfaSetupInner mfaToken={mfaToken} onSuccess={handleVerified} pending={recoveryMutation.isPending} />;
}

function MfaSetupInner({
  mfaToken,
  onSuccess,
  pending,
}: MfaSetupPanelProps & { pending: boolean }) {
  // POST sem body — o mfa_token temporário vai no Authorization: Bearer (policy MfaToken).
  const setupQuery = useQuery({
    queryKey: ["mfa-setup", mfaToken],
    queryFn: () =>
      api<MfaSetupResponse>("/auth/mfa/setup", {
        method: "POST",
        auth: false,
        bearerToken: mfaToken,
      }),
    retry: false,
    staleTime: Infinity,
  });

  if (setupQuery.isPending) {
    return (
      <div className="space-y-3">
        <Skeleton className="h-4 w-3/4" />
        <Skeleton className="h-20 w-full" />
        <Skeleton className="h-10 w-full" />
      </div>
    );
  }

  if (setupQuery.isError) {
    return (
      <div className="space-y-4">
        <FormError message={genericErrorMessage(setupQuery.error)} />
        <Button variant="outline" className="w-full" onClick={() => void setupQuery.refetch()}>
          Tentar novamente
        </Button>
      </div>
    );
  }

  const { otpauth_uri, secret } = setupQuery.data;

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        Seu papel exige verificação em duas etapas (MFA). Abra um aplicativo autenticador no
        celular (Google Authenticator, Microsoft Authenticator, Authy…), toque em adicionar
        conta e <strong>escaneie o QR code abaixo</strong>. Depois, confirme com o código de
        6 dígitos que o aplicativo mostrar.
      </p>
      <div className="flex justify-center rounded-lg border bg-white p-4">
        <QRCodeSVG value={otpauth_uri} size={192} marginSize={1} aria-label="QR code para o aplicativo autenticador" />
      </div>
      <details className="space-y-2 text-sm">
        <summary className="cursor-pointer text-muted-foreground">
          Não consegue escanear? Adicione manualmente
        </summary>
        <div className="space-y-2 pt-2">
          <Label htmlFor="otp-secret">Chave para digitação manual</Label>
          <div className="flex gap-2">
            <Input id="otp-secret" readOnly value={secret} className="font-mono tracking-widest" />
            <CopyButton value={secret} label="Copiar chave" />
          </div>
          <Label htmlFor="otpauth-uri">Endereço completo (otpauth)</Label>
          <div className="flex gap-2">
            <Input id="otpauth-uri" readOnly value={otpauth_uri} className="font-mono text-xs" />
            <CopyButton value={otpauth_uri} label="Copiar endereço" />
          </div>
        </div>
      </details>
      <MfaVerifyForm
        mfaToken={mfaToken}
        onSuccess={onSuccess}
        submitLabel={pending ? "Gerando códigos…" : "Ativar e continuar"}
      />
    </div>
  );
}

/**
 * Códigos de recuperação exibidos UMA única vez após ativar a MFA (Seção 7.5).
 * Cada código vale um login no lugar do TOTP; regenerar invalida todos.
 */
function RecoveryCodesPanel({ codes, onContinue }: { codes: string[]; onContinue: () => void }) {
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        Verificação ativada. Guarde os <strong>códigos de recuperação</strong> abaixo em um
        lugar seguro: se você perder o acesso ao aplicativo autenticador, cada código permite
        entrar uma única vez. <strong>Eles não serão exibidos novamente.</strong>
      </p>
      <div className="rounded-lg border bg-muted/30 p-4">
        <ul className="grid grid-cols-2 gap-x-6 gap-y-1 font-mono text-sm tabular-nums" aria-label="Códigos de recuperação">
          {codes.map((code) => (
            <li key={code}>{code}</li>
          ))}
        </ul>
      </div>
      <div className="flex gap-2">
        <CopyButton value={codes.join("\n")} label="Copiar todos os códigos" />
        <Button type="button" className="w-full" onClick={onContinue}>
          Guardei meus códigos, entrar
        </Button>
      </div>
    </div>
  );
}

function CopyButton({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard indisponível (ex.: contexto não seguro) — o campo permite seleção manual.
    }
  }

  return (
    <Button type="button" variant="outline" size="icon" onClick={() => void copy()} aria-label={label} title={copied ? "Copiado!" : label}>
      <Copy className="h-4 w-4" />
    </Button>
  );
}
