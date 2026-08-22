// Renderizado em Configurações > Organização (a aba que todo papel enxerga).
// =============================================================================
// Preferências de e-mail da PESSOA logada (GET/PATCH /me/email-prefs). Cada
// toggle grava sozinho, com PATCH parcial (campos ausentes não mudam), então
// uma falha em um toggle não desfaz os outros.
//
// "Alertas de dispositivos" é exclusivo do plano Pro: fora do Pro o toggle
// aparece desabilitado com a nota, em vez de deixar a pessoa ligar algo que o
// backend recusaria.
//
// VOCABULÁRIO: aqui "alertas" são estes e-mails. As pendências do sino do topo
// do portal NUNCA se chamam alertas.
// =============================================================================

import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { genericErrorMessage } from "@/lib/messages";
import type { EmailPrefs, EmailPrefsPatchRequest, MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

/** Plano que habilita o e-mail de alertas de dispositivos. */
const PLANO_PRO = "pro";

export function PreferenciasEmailCard() {
  const queryClient = useQueryClient();

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const isPro = meQuery.data?.organization.plan === PLANO_PRO;

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

  // Qual campo está gravando agora: só ele fica desabilitado durante o PATCH.
  const savingKey = mutation.isPending
    ? (Object.keys(mutation.variables ?? {})[0] as keyof EmailPrefs | undefined)
    : undefined;

  const prefs = prefsQuery.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Preferências de e-mail</CardTitle>
        <CardDescription>
          Estes envios valem para a sua conta. Cada pessoa da organização escolhe os seus.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {prefsQuery.isPending ? (
          <div className="space-y-4">
            {Array.from({ length: 3 }, (_, i) => (
              <Skeleton key={i} className="h-14 w-full" />
            ))}
          </div>
        ) : prefsQuery.isError || prefs === undefined ? (
          <div className="flex flex-col items-center gap-3 py-8 text-center">
            <p className="text-sm text-muted-foreground">{genericErrorMessage(prefsQuery.error)}</p>
            <Button variant="outline" onClick={() => void prefsQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : (
          <>
            <PrefToggle
              id="pref-weekly-digest"
              label="Resumo semanal"
              description="Um e-mail por semana com as horas ativas da equipe e os aplicativos mais usados no período."
              checked={prefs.weekly_digest}
              saving={savingKey === "weekly_digest"}
              onChange={(next) => mutation.mutate({ weekly_digest: next })}
            />
            <PrefToggle
              id="pref-fleet-alerts"
              label="Alertas de dispositivos"
              description="Aviso quando dispositivos param de reportar ou entram em alerta de saúde, para agir antes de perder dados."
              checked={isPro && prefs.fleet_alerts}
              saving={savingKey === "fleet_alerts"}
              disabled={!isPro}
              note={
                !isPro
                  ? "Exclusivo do plano Pro. Fale com a gente para habilitar no seu plano."
                  : "Exclusivo do plano Pro."
              }
              onChange={(next) => mutation.mutate({ fleet_alerts: next })}
            />
            <PrefToggle
              id="pref-jornada-weekly"
              label="Relatório de jornada semanal"
              description="Planilha semanal com primeiro e último evento por dispositivo. Não é ponto eletrônico e não substitui registro de jornada."
              checked={prefs.jornada_weekly}
              saving={savingKey === "jornada_weekly"}
              onChange={(next) => mutation.mutate({ jornada_weekly: next })}
            />
            {mutation.isError && (
              <p role="alert" className="text-sm text-destructive">
                {genericErrorMessage(mutation.error)}
              </p>
            )}
          </>
        )}
      </CardContent>
    </Card>
  );
}

function PrefToggle({
  id,
  label,
  description,
  checked,
  saving,
  disabled = false,
  note,
  onChange,
}: {
  id: string;
  label: string;
  description: string;
  checked: boolean;
  saving: boolean;
  disabled?: boolean;
  note?: string;
  onChange: (next: boolean) => void;
}) {
  return (
    <div className={cn("flex items-start gap-3", disabled && "opacity-70")}>
      <input
        id={id}
        type="checkbox"
        checked={checked}
        disabled={disabled || saving}
        onChange={(e) => onChange(e.target.checked)}
        className="mt-0.5 h-4 w-4 shrink-0 accent-primary disabled:cursor-not-allowed"
      />
      <div className="min-w-0 space-y-0.5">
        <label
          htmlFor={id}
          className={cn(
            "block text-sm font-medium leading-none",
            disabled ? "cursor-not-allowed" : "cursor-pointer",
          )}
        >
          {label}
        </label>
        <p className="text-xs text-muted-foreground">{description}</p>
        {note !== undefined && <p className="text-xs text-viz-neutro">{note}</p>}
      </div>
    </div>
  );
}
