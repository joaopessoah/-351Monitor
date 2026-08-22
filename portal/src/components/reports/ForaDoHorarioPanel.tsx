// =============================================================================
// Aba "Fora do horário" do relatório de Uso (/relatorios/uso?aba=fora-do-horario):
// tempo ATIVO somado fora do horário de trabalho declarado, por dispositivo,
// com os três baldes (antes do início, depois do fim e dias fora da escala).
//
// Reaproveita os filtros de período e de dispositivos da própria tela de Uso -
// o painel só recebe o recorte já resolvido e cuida da paginação.
//
// LINHA VERMELHA: indicador de EQUILÍBRIO, nunca de jornada. Nenhum texto desta
// tela pode falar em hora extra, banco de horas ou controle de ponto, e o
// disclaimer da Portaria 671/MTE acompanha o painel como no relatório de Jornada.
// =============================================================================

import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, Info } from "lucide-react";
import { api } from "@/lib/api";
import { formatDuration } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type { ForaDoHorarioResponse } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  businessHoursLabel,
  ForaDoHorarioDisclaimer,
  foraDoHorarioEmptyState,
  foraDoHorarioKey,
  foraDoHorarioPct,
  foraDoHorarioUrl,
} from "@/components/reports/ForaDoHorario";
import type { DateRange } from "@/components/reports/filters";

/** page_size máximo do contrato - o painel raramente passa de uma página. */
export const FORA_PAGE_SIZE = 100;

/** Tooltip pedagógico do indicador - sempre presente via title. */
const FORA_HINT =
  "Soma do tempo ativo registrado fora do horário de trabalho declarado pela organização. " +
  "É um indicador de equilíbrio da equipe, não um cálculo de jornada.";

export function ForaDoHorarioPanel({
  range,
  deviceIdsKey,
  onClearDevices,
}: {
  range: DateRange | null;
  deviceIdsKey: string;
  onClearDevices: () => void;
}) {
  const [page, setPage] = useState(1);

  // Trocar período ou dispositivos volta para a primeira página.
  useEffect(() => {
    setPage(1);
  }, [range?.from, range?.to, deviceIdsKey]);

  const params = {
    from: range?.from ?? "",
    to: range?.to ?? "",
    deviceIdsKey,
    page,
    includeDevices: true,
    pageSize: FORA_PAGE_SIZE,
  };

  const query = useQuery({
    queryKey: foraDoHorarioKey(params),
    queryFn: () => api<ForaDoHorarioResponse>(foraDoHorarioUrl(params)),
    enabled: range !== null,
    placeholderData: (prev) => prev,
  });
  const data = query.data;

  if (query.isError && data === undefined) {
    return (
      <Card>
        <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
          <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
          <p className="text-sm text-muted-foreground">{genericErrorMessage(query.error)}</p>
          <Button variant="outline" onClick={() => void query.refetch()}>
            Tentar novamente
          </Button>
        </div>
      </Card>
    );
  }

  if (data === undefined) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-[92px] w-full rounded-lg" />
        <Skeleton className="h-[280px] w-full rounded-lg" />
      </div>
    );
  }

  // Estados em que NÃO há número honesto a mostrar (ver ForaDoHorario.tsx).
  const vazio = foraDoHorarioEmptyState(data);
  if (vazio !== null) {
    return (
      <div className="space-y-4">
        <Card>
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <span className="flex h-12 w-12 items-center justify-center rounded-full bg-muted">
              <Info className="h-6 w-6 text-muted-foreground" aria-hidden />
            </span>
            <div className="max-w-xl space-y-1">
              <p className="text-base font-medium">{vazio.titulo}</p>
              <p className="text-sm text-muted-foreground">{vazio.explicacao}</p>
            </div>
            {vazio.acao !== null && (
              <Link
                to={vazio.acao.to}
                className="text-sm font-medium text-primary underline-offset-2 hover:underline"
              >
                {vazio.acao.label}
              </Link>
            )}
          </div>
        </Card>
        <ForaDoHorarioDisclaimer />
      </div>
    );
  }

  const totals = data.totals;
  const pct = totals !== null ? foraDoHorarioPct(totals.seconds_outside, totals.seconds_active) : null;
  const pages = Math.max(1, Math.ceil(data.total / FORA_PAGE_SIZE));

  return (
    <div className="space-y-4">
      {query.isError && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void query.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      {/* Resumo do período: total fora do horário + os três baldes. */}
      <Card>
        <div className="grid gap-4 px-6 py-4 sm:grid-cols-2 lg:grid-cols-4">
          <div title={FORA_HINT}>
            <p className="flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground">
              Fora do horário
              <Info role="img" aria-label={FORA_HINT} className="h-3.5 w-3.5 shrink-0" />
            </p>
            <p className="mt-1 text-2xl font-semibold tabular-nums">
              {totals !== null ? formatDuration(totals.seconds_outside) : "-"}
            </p>
            {pct !== null && (
              <p className="text-xs tabular-nums text-muted-foreground">
                {pct}% do tempo ativo do período
              </p>
            )}
          </div>
          <BaldeResumo label="Antes do horário" seconds={totals?.seconds_before ?? 0} />
          <BaldeResumo label="Depois do horário" seconds={totals?.seconds_after ?? 0} />
          <BaldeResumo label="Em dias fora da escala" seconds={totals?.seconds_non_business_day ?? 0} />
        </div>
        {data.business_hours !== null && (
          <p className="border-t px-6 py-2.5 text-xs text-muted-foreground">
            Horário de trabalho declarado: {businessHoursLabel(data.business_hours)} (
            {data.timezone}).{" "}
            <Link
              to="/configuracoes/organizacao"
              className="underline underline-offset-2 hover:text-primary"
            >
              Alterar
            </Link>
          </p>
        )}
      </Card>

      <Card>
        <div className={query.isPlaceholderData ? "overflow-x-auto opacity-70 transition-opacity" : "overflow-x-auto"}>
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="px-6 py-2">Dispositivo</th>
                <th scope="col" className="px-3 py-2 text-right">Fora do horário</th>
                <th scope="col" className="px-3 py-2 text-right">Antes</th>
                <th scope="col" className="px-3 py-2 text-right">Depois</th>
                <th scope="col" className="px-3 py-2 text-right">Dias fora da escala</th>
                <th scope="col" className="px-3 py-2 text-right">Dias com atividade fora</th>
                <th scope="col" className="px-6 py-2 text-right">Tempo ativo no período</th>
              </tr>
            </thead>
            <tbody>
              {data.items.length === 0 ? (
                <tr>
                  <td colSpan={7} className="px-6 py-10 text-center text-sm text-muted-foreground">
                    {deviceIdsKey.length > 0 ? (
                      <span className="inline-flex flex-col items-center gap-2">
                        <span>Nenhuma atividade fora do horário de trabalho neste recorte.</span>
                        <Button variant="outline" size="sm" onClick={onClearDevices}>
                          Limpar filtros
                        </Button>
                      </span>
                    ) : (
                      "Nenhuma atividade fora do horário de trabalho no período."
                    )}
                  </td>
                </tr>
              ) : (
                data.items.map((item) => (
                  <tr
                    key={item.device_id}
                    className="border-b transition-colors last:border-b-0 hover:bg-accent/50"
                  >
                    <td className="max-w-[16rem] truncate px-6 py-2 font-medium">{item.device_name}</td>
                    <td className="whitespace-nowrap px-3 py-2 text-right font-medium tabular-nums">
                      {formatDuration(item.seconds_outside)}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {formatDuration(item.seconds_before)}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {formatDuration(item.seconds_after)}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {formatDuration(item.seconds_non_business_day)}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {item.days_with_activity_outside}
                    </td>
                    <td className="whitespace-nowrap px-6 py-2 text-right tabular-nums text-muted-foreground">
                      {formatDuration(item.seconds_active)}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>

          <div className="flex flex-wrap items-center justify-between gap-2 border-t px-6 py-3 text-sm">
            <span className="tabular-nums text-muted-foreground">
              {data.total === 0
                ? "Nenhum dispositivo com atividade fora do horário no período."
                : data.total === 1
                  ? "1 dispositivo com atividade fora do horário no período."
                  : `${data.total} dispositivos com atividade fora do horário no período.`}
            </span>
            {pages > 1 && (
              <div className="flex gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={page <= 1}
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                >
                  Anterior
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={page >= pages}
                  onClick={() => setPage((p) => p + 1)}
                >
                  Próxima
                </Button>
              </div>
            )}
          </div>
        </div>
      </Card>

      <ForaDoHorarioDisclaimer />
    </div>
  );
}

/** Célula de um dos três baldes do resumo. */
function BaldeResumo({ label, seconds }: { label: string; seconds: number }) {
  return (
    <div>
      <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
      <p className="mt-1 text-2xl font-semibold tabular-nums">{formatDuration(seconds)}</p>
    </div>
  );
}
