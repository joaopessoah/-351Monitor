// =============================================================================
// Licenças da versão de teste (trial) — o medidor "N de 10 licenças em uso" e o
// aviso quando o teto é atingido.
//
// Decisão de 09/09/2026: todo trial nasce com 10 licenças (N24) e cabe ao
// cliente usar todas ou não. Quando a frota ocupa o teto, a máquina seguinte é
// recusada no enroll e o portal mostra a frase combinada, a MESMA que a API
// devolve ao agente (EnrollmentService.TrialLimitMessage no backend): quem
// instala vê no log do agente, quem gerencia vê aqui. Mudou uma, muda a outra.
//
// Só aparece em organização com plan === "trial" e limite definido. Nos planos
// pagos o teto é contrato e não é assunto de tela. O número em uso vem do
// health-summary (mesma queryKey e cadência da tela Dispositivos), que conta
// pela regra do enroll: pausado ocupa licença, arquivado e revogado liberam.
// =============================================================================

import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { KeyRound, TriangleAlert } from "lucide-react";

import { api } from "@/lib/api";
import type { DeviceHealthSummaryResponse, MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";

/** Plano com que toda organização nasce no backoffice (create-org). */
export const PLANO_TRIAL = "trial";

/** Frase exibida ao atingir o teto; espelha EnrollmentService.TrialLimitMessage. */
export function mensagemLimiteTrial(limite: number): string {
  const licencas = limite === 1 ? "1 licença" : `${limite} licenças`;
  return `Como é uma versão de teste, tem somente ${licencas}. Entre em contato com o time da +351 Monitor.`;
}

export function LicencasDeTeste({
  somenteQuandoCheio = false,
  className,
}: {
  /** true: só o aviso de teto atingido (telas densas, como a Visão Geral). */
  somenteQuandoCheio?: boolean;
  className?: string;
}) {
  // Mesma queryKey do AppShell: resolve do cache, sem requisição extra.
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const organization = meQuery.data?.organization;
  const limite =
    organization !== undefined && organization.plan === PLANO_TRIAL ? organization.device_limit : null;

  // Mesma key e cadência da tela Dispositivos: quando as duas estão montadas
  // resolve do cache, e continua atualizando enquanto o TI instala máquinas.
  const healthQuery = useQuery({
    queryKey: ["devices", "health-summary"],
    queryFn: () => api<DeviceHealthSummaryResponse>("/devices/health-summary"),
    enabled: limite !== null,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  });

  if (limite === null) return null;

  const emUso = healthQuery.data?.licensed_devices;
  const cheio = emUso !== undefined && emUso >= limite;

  if (cheio) {
    return (
      <div
        role="status"
        className={cn(
          "flex items-start gap-3 rounded-lg border border-viz-improdutivo/60 bg-viz-improdutivo/10 px-4 py-3 text-sm",
          className,
        )}
      >
        <TriangleAlert className="mt-0.5 h-5 w-5 shrink-0 text-viz-improdutivo" aria-hidden />
        <div className="space-y-1">
          <p className="font-medium text-foreground">{mensagemLimiteTrial(limite)}</p>
          <p className="text-muted-foreground">
            {emUso} de {limite} licenças em uso. Uma máquina nova só consegue se registrar depois
            que uma licença for liberada (arquivar ou revogar um dispositivo) ou que o plano for
            contratado.
          </p>
        </div>
      </div>
    );
  }

  if (somenteQuandoCheio) return null;

  return (
    <p className={cn("flex items-center gap-2 text-sm text-muted-foreground", className)}>
      <KeyRound className="h-4 w-4 shrink-0" aria-hidden />
      <span>
        Versão de teste:{" "}
        {emUso === undefined ? (
          <>até {limite} licenças.</>
        ) : (
          <>
            <span className="font-medium text-foreground">{emUso}</span> de {limite} licenças em uso.
          </>
        )}{" "}
        <Link to="/dispositivos" className="underline-offset-4 hover:underline">
          Ver dispositivos
        </Link>
      </span>
    </p>
  );
}
