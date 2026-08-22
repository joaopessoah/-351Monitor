// =============================================================================
// Assinatura do relatório de jornada semanal por e-mail (F5). Toda segunda 07h
// (fuso da organização) o worker enfileira o export da semana anterior no MESMO
// pipeline assíncrono do botão "Exportar CSV" e manda o LINK do download quando
// o arquivo fica pronto. Nunca anexo: dado pessoal não circula por e-mail.
//
// Exclusivo do plano Pro: fora dele o controle aparece desabilitado com a nota,
// em vez de deixar a pessoa ligar algo que o backend recusa com 403.
//
// A preferência mora em GET/PATCH /me/email-prefs, a MESMA query key do card de
// Configurações > Organização - ligar aqui reflete lá sem refetch.
// =============================================================================

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Mail } from "lucide-react";
import { api } from "@/lib/api";
import { problemErrorMessage } from "@/lib/messages";
import type { EmailPrefs, EmailPrefsPatchRequest, MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";

/** Plano que habilita os relatórios agendados por e-mail. */
export const PLANO_PRO = "pro";

/**
 * Preferências de e-mail da pessoa logada + o plano da org, com a mutation de
 * PATCH parcial. Compartilhado pelo card de Configurações e pelo toggle desta
 * tela para não haver duas cópias da mesma regra.
 */
export function useEmailPrefs() {
  const queryClient = useQueryClient();

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });

  // Key própria (não ["me","email-prefs"]): invalidar ["me"] em outro lugar não
  // deve arrastar estas preferências junto pela regra de prefixo.
  const prefsQuery = useQuery({
    queryKey: ["email-prefs"],
    queryFn: () => api<EmailPrefs>("/me/email-prefs"),
    staleTime: 60_000,
  });

  const mutation = useMutation({
    mutationFn: (body: EmailPrefsPatchRequest) =>
      api<EmailPrefs>("/me/email-prefs", { method: "PATCH", body }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["email-prefs"], updated);
    },
  });

  return {
    prefs: prefsQuery.data,
    prefsQuery,
    mutation,
    isPro: meQuery.data?.organization.plan === PLANO_PRO,
    // Qual campo está gravando agora: só ele fica desabilitado durante o PATCH.
    savingKey: mutation.isPending
      ? (Object.keys(mutation.variables ?? {})[0] as keyof EmailPrefs | undefined)
      : undefined,
  };
}

/**
 * Linha de assinatura da tela do Relatório de Jornada. Enquanto as preferências
 * não carregam (ou se o GET falhar) o controle simplesmente não aparece: é um
 * extra da tela, e um erro aqui não pode competir com o relatório em si.
 */
export function AssinaturaJornadaToggle() {
  const { prefs, mutation, isPro, savingKey } = useEmailPrefs();
  if (prefs === undefined) return null;

  const saving = savingKey === "jornada_weekly";
  const checked = isPro && prefs.jornada_weekly;

  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 rounded-md border bg-muted/30 px-4 py-2.5 text-sm">
      <Mail className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
      <input
        id="assinatura-jornada-semanal"
        type="checkbox"
        checked={checked}
        disabled={!isPro || saving}
        onChange={(e) => mutation.mutate({ jornada_weekly: e.target.checked })}
        className="h-4 w-4 shrink-0 accent-primary disabled:cursor-not-allowed"
      />
      <label
        htmlFor="assinatura-jornada-semanal"
        className={cn("font-medium", isPro ? "cursor-pointer" : "cursor-not-allowed opacity-70")}
      >
        Receber a jornada semanal por e-mail
      </label>
      <span className="text-xs text-muted-foreground">
        Toda segunda, com a semana anterior e o link para baixar a planilha no portal.
      </span>
      {!isPro && (
        <span className="text-xs text-viz-neutro">
          Exclusivo do plano Pro. Fale com a gente para habilitar no seu plano.
        </span>
      )}
      {mutation.isError && (
        <span role="alert" className="text-xs text-destructive">
          {problemErrorMessage(mutation.error)}
        </span>
      )}
    </div>
  );
}
