// Rota /cobranca (App.tsx), com o link "Cobrança" na navegação do AppShell
// visível apenas para o Proprietário.
// =============================================================================
// Extrato mensal de cobrança (GET /billing/billable-devices?month=, papel
// OWNER). Responde "quantos dispositivos contam neste mês e por quê", com
// evidência por dispositivo, para o Proprietário anexar à conversa de fatura.
//
// SEM PREÇO: a tela mostra contagem e evidências e NUNCA valor em reais —
// precificação é decisão comercial que vive fora do sistema.
//
// Gate de papel: apenas Owner (espelho do PolicyOwnerOnly do backend). Os
// outros papéis veem o aviso de permissão e a API nem é chamada (enabled do
// TanStack Query), em vez de tomar um 403 e mostrar erro genérico.
//
// Impressão: reusa o par no-print/print-plain de src/index.css (mesmo padrão da
// página pública de transparência). O extrato impresso identifica organização,
// mês e data de emissão em texto normal — nada de conteúdo só-impressão.
// =============================================================================

import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, Clock, Printer, ShieldAlert } from "lucide-react";
import { api } from "@/lib/api";
import { formatDateTime, localDateOf } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isOwner } from "@/lib/roles";
import type { BillableDeviceItem, BillableDevicesResponse, MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

/** Quantos meses o seletor oferece: mês corrente + os 11 anteriores. */
const MESES_NO_SELETOR = 12;

/** Evidência traduzida para o vocabulário do extrato (Record aberto: regra nova cai no default). */
const evidenceLabels: Record<string, string> = {
  events: "eventos recebidos",
  enrolled: "registrado no mês",
  keep_alive: "último contato no mês",
};

function evidenceLabel(evidence: string): string {
  return evidenceLabels[evidence] ?? "sinal de uso no mês";
}

/** Status do device no vocabulário da tela de dispositivos. */
const statusLabels: Record<string, string> = {
  active: "Ativo",
  paused: "Pausado",
  archived: "Arquivado",
  revoked: "Revogado",
};

function statusLabel(status: string): string {
  return statusLabels[status] ?? status;
}

/** "2026-06" -> "junho de 2026" (mês por extenso em pt-BR). */
function monthLabel(month: string): string {
  const [y, m] = month.split("-").map(Number);
  if (Number.isNaN(y) || Number.isNaN(m)) return month;
  return new Intl.DateTimeFormat("pt-BR", {
    month: "long",
    year: "numeric",
    timeZone: "UTC",
  }).format(new Date(Date.UTC(y, m - 1, 1)));
}

/** Soma `delta` meses a um "YYYY-MM" (delta negativo volta no tempo). */
function addMonths(month: string, delta: number): string {
  const [y, m] = month.split("-").map(Number);
  const dt = new Date(Date.UTC(y, m - 1 + delta, 1));
  return `${dt.getUTCFullYear()}-${String(dt.getUTCMonth() + 1).padStart(2, "0")}`;
}

export function CobrancaPage() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const owner = isOwner(meQuery.data);
  const timezone = meQuery.data?.organization.timezone ?? null;
  const orgName = meQuery.data?.organization.name ?? null;

  // Data de emissão fixada na montagem: reimprimir a mesma tela não deve
  // mudar o carimbo de emissão a cada re-render.
  const [emitidoEm] = useState(() => new Date().toISOString());

  // Mês corrente no FUSO DA ORGANIZAÇÃO (não do navegador): no dia 1, o fuso
  // errado colocaria o extrato no mês vizinho.
  const currentMonth = useMemo(() => {
    if (timezone === null) return null;
    return localDateOf(new Date(), timezone).slice(0, 7);
  }, [timezone]);

  const months = useMemo(() => {
    if (currentMonth === null) return [];
    return Array.from({ length: MESES_NO_SELETOR }, (_, i) => addMonths(currentMonth, -i));
  }, [currentMonth]);

  // Default: mês ANTERIOR (o fechado, que é o que se anexa a uma fatura).
  const [selected, setSelected] = useState<string | null>(null);
  const month = selected ?? (currentMonth !== null ? addMonths(currentMonth, -1) : null);

  const billingQuery = useQuery({
    queryKey: ["billing", "billable-devices", month],
    queryFn: () =>
      api<BillableDevicesResponse>(
        `/billing/billable-devices?month=${encodeURIComponent(month ?? "")}`,
      ),
    // Papel sem permissão nem chega a chamar a API (o aviso é local).
    enabled: owner && month !== null,
    placeholderData: (prev) => prev,
  });

  const header = (
    <div>
      <h1 className="text-2xl font-semibold tracking-tight">Extrato de cobrança</h1>
      <p className="mt-1 text-sm text-muted-foreground">
        Dispositivos que contam no mês e a evidência de uso de cada um. Este extrato mostra
        contagem, não valores: a precificação é tratada comercialmente, fora do portal.
      </p>
    </div>
  );

  // Sem /me não há papel nem fuso: erro inline com retry (nunca skeleton eterno).
  if (meQuery.isError) {
    return (
      <div className="space-y-4">
        {header}
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <p className="text-sm text-muted-foreground">{genericErrorMessage(meQuery.error)}</p>
            <Button variant="outline" onClick={() => void meQuery.refetch()}>
              Tentar novamente
            </Button>
          </CardContent>
        </Card>
      </div>
    );
  }

  // Papel sem permissão: aviso claro, sem chamada à API.
  if (meQuery.isSuccess && !owner) {
    return (
      <div className="space-y-4">
        {header}
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <ShieldAlert className="h-8 w-8 text-muted-foreground" aria-hidden="true" />
            <p className="max-w-md text-sm text-muted-foreground">
              O extrato de cobrança fica disponível apenas para o Proprietário da organização. Peça
              a ele o extrato do mês que você precisa.
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  const data = billingQuery.data;

  return (
    <div className="space-y-4">
      {header}

      {/* Controles: fora do papel impresso (no-print de src/index.css). */}
      <div className="no-print flex flex-wrap items-end justify-between gap-3">
        <div className="space-y-1.5">
          <label
            htmlFor="cobranca-mes"
            className="text-xs font-medium uppercase tracking-wide text-muted-foreground"
          >
            Mês de referência
          </label>
          <select
            id="cobranca-mes"
            value={month ?? ""}
            disabled={months.length === 0}
            onChange={(e) => setSelected(e.target.value)}
            className={cn(
              "block h-9 rounded-md border border-input bg-card px-3 text-sm",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
              "disabled:cursor-not-allowed disabled:opacity-50",
            )}
          >
            {months.map((m) => (
              <option key={m} value={m}>
                {monthLabel(m)}
              </option>
            ))}
          </select>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => window.print()}
          disabled={data === undefined}
        >
          <Printer className="h-4 w-4" aria-hidden="true" />
          Imprimir extrato
        </Button>
      </div>

      {/* Identificação do extrato: vale em tela E no papel. */}
      <div className="space-y-1 text-sm">
        <p>
          <span className="text-muted-foreground">Organização: </span>
          <span className="font-medium">{orgName ?? "-"}</span>
        </p>
        <p className="tabular-nums">
          <span className="text-muted-foreground">Mês de referência: </span>
          <span className="font-medium">{month !== null ? monthLabel(month) : "-"}</span>
          <span className="text-muted-foreground">
            {" · Emitido em "}
            {timezone !== null ? formatDateTime(emitidoEm, timezone) : "-"}
          </span>
        </p>
      </div>

      {billingQuery.isError && data !== undefined && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar o extrato. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void billingQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      {data === undefined ? (
        billingQuery.isError ? (
          <Card>
            <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
              <p className="text-sm text-muted-foreground">
                {genericErrorMessage(billingQuery.error)}
              </p>
              <Button variant="outline" onClick={() => void billingQuery.refetch()}>
                Tentar novamente
              </Button>
            </CardContent>
          </Card>
        ) : (
          <ExtratoSkeleton />
        )
      ) : (
        <Extrato data={data} timezone={timezone} loading={billingQuery.isPlaceholderData} />
      )}
    </div>
  );
}

/** Skeleton com a geometria final do extrato (selo, total e tabela). */
function ExtratoSkeleton() {
  return (
    <div className="space-y-4">
      <Skeleton className="h-9 w-72 rounded-full" />
      <Card>
        <CardHeader className="pb-3">
          <Skeleton className="h-8 w-56" />
          <Skeleton className="h-4 w-72" />
        </CardHeader>
        <CardContent className="space-y-2 pb-6">
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={i} className="h-8 w-full" />
          ))}
        </CardContent>
      </Card>
    </div>
  );
}

function Extrato({
  data,
  timezone,
  loading,
}: {
  data: BillableDevicesResponse;
  timezone: string | null;
  loading: boolean;
}) {
  return (
    <div className={cn("space-y-4 transition-opacity", loading && "opacity-70")}>
      {/* Selo de estado: congelado (fechado) x mês corrente em andamento. */}
      {data.frozen ? (
        <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-viz-produtivo/40 bg-viz-produtivo/10 px-3 py-1.5 text-sm font-medium text-viz-produtivo">
          <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span className="tabular-nums">
            {data.frozen_at !== null && timezone !== null
              ? `Mês fechado e congelado em ${formatDateTime(data.frozen_at, timezone)}`
              : "Mês fechado e congelado"}
          </span>
        </div>
      ) : (
        <div className="space-y-1.5">
          <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-viz-improdutivo/40 bg-viz-improdutivo/10 px-3 py-1.5 text-sm font-medium text-viz-improdutivo">
            <Clock className="h-4 w-4 shrink-0" aria-hidden="true" />
            Mês corrente, ainda em andamento
          </div>
          <p className="text-sm text-muted-foreground">
            A contagem pode mudar até o fechamento do mês, conforme os dispositivos continuarem
            reportando.
          </p>
        </div>
      )}

      <Card className="print-plain">
        <CardHeader className="pb-3">
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <CardTitle className="text-3xl tabular-nums">{data.device_count}</CardTitle>
            <span className="text-sm font-medium">
              {data.device_count === 1
                ? "dispositivo cobrável em"
                : "dispositivos cobráveis em"}{" "}
              {monthLabel(data.month)}
            </span>
          </div>
          <CardDescription>
            Um dispositivo conta uma única vez no mês, qualquer que seja a evidência.
          </CardDescription>
        </CardHeader>
        <CardContent className="px-0 pb-0">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Dispositivo</th>
                  <th scope="col" className="px-3 py-2">Hostname</th>
                  <th scope="col" className="px-3 py-2">Status</th>
                  <th scope="col" className="px-3 py-2">Registrado em</th>
                  <th scope="col" className="px-3 py-2">Último contato</th>
                  <th scope="col" className="px-6 py-2">Evidência</th>
                </tr>
              </thead>
              <tbody>
                {data.items.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="px-6 py-10 text-center text-sm text-muted-foreground">
                      Nenhum dispositivo cobrável neste mês.
                    </td>
                  </tr>
                ) : (
                  data.items.map((item) => (
                    <ExtratoRow key={item.device_id} item={item} timezone={timezone} />
                  ))
                )}
              </tbody>
            </table>
          </div>
          <p className="border-t px-6 py-3 text-xs text-muted-foreground">{data.criteria}</p>
        </CardContent>
      </Card>
    </div>
  );
}

function ExtratoRow({
  item,
  timezone,
}: {
  item: BillableDeviceItem;
  timezone: string | null;
}) {
  return (
    <tr className="border-b last:border-b-0">
      <td className="px-6 py-2 font-medium">{item.display_name ?? item.hostname}</td>
      <td className="px-3 py-2 text-muted-foreground">{item.hostname}</td>
      <td className="whitespace-nowrap px-3 py-2">{statusLabel(item.status)}</td>
      <td className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground">
        {timezone !== null ? formatDateTime(item.enrolled_at, timezone) : "-"}
      </td>
      <td className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground">
        {item.last_seen_at !== null && timezone !== null
          ? formatDateTime(item.last_seen_at, timezone)
          : "nunca"}
      </td>
      <td className="px-6 py-2">{evidenceLabel(item.evidence)}</td>
    </tr>
  );
}
