// =============================================================================
// Relatório de Jornada (/relatorios/jornada - F3.5, Seção 8.6 linha 947):
// uma linha por dispositivo × dia do range INTEIRO (dias sem dados TAMBÉM
// viram linha, com observação), agrupado por device com os totais do período
// (device_totals independem da página corrente).
// - Banner do disclaimer (Portaria 671/MTE) PERMANENTE e não-dispensável; o
//   MESMO texto verbatim vai como rodapé de todo CSV de jornada (backend).
// - Colunas "Primeiro evento"/"Último evento" - JAMAIS "Entrada"/"Saída";
//   nenhum cálculo de horas extras/adicional noturno/banco de horas/atrasos.
// - Números idênticos aos do rodapé da timeline para o mesmo device/dia
//   (consistência absoluta entre telas - DoD 11.3).
// - Exportar CSV (admin E viewer): POST /exports {kind:"jornada_csv"} -> 202;
//   acompanhamento em /relatorios/exportacoes. O backend audita view_report.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, Scale } from "lucide-react";
import { api } from "@/lib/api";
import { ddmm, formatDuration, formatHm, weekdayShort } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type { JornadaReportResponse, JornadaRow, MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  DeviceMultiSelect,
  PeriodPresetGroup,
  useFilterDevices,
  useReportRange,
} from "@/components/reports/filters";
import { ExportCsvBanner, ExportCsvButton, useCsvExport } from "@/components/reports/ExportCsv";

const PAGE_SIZE = 50;

function NoteCell({ note }: { note: JornadaRow["note"] }) {
  if (note === null) return null;
  if (note === "sem_comunicacao") {
    return (
      <span className="inline-flex items-center gap-1.5 text-amber-700">
        <AlertTriangle className="h-3.5 w-3.5 shrink-0" aria-hidden />
        Agente sem comunicação
      </span>
    );
  }
  if (note === "dados_incompletos") {
    return <span className="text-muted-foreground">Dados incompletos</span>;
  }
  return <span className="text-muted-foreground">Sem dados, máquina desligada</span>;
}

export function JornadaPage() {
  const [deviceIds, setDeviceIds] = useState<string[]>([]);
  const [page, setPage] = useState(1);

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const { todayStr, range, activePreset, applyPreset } = useReportRange(timezone);

  const { devices } = useFilterDevices();
  const deviceIdsKey = useMemo(() => [...deviceIds].sort().join(","), [deviceIds]);
  const deviceParam = deviceIdsKey.length > 0 ? `&device_ids=${deviceIdsKey}` : "";

  // Trocar período/devices volta para a primeira página.
  useEffect(() => {
    setPage(1);
  }, [range?.from, range?.to, deviceIdsKey]);

  const jornadaQuery = useQuery({
    queryKey: ["reports", "jornada", { from: range?.from, to: range?.to, devices: deviceIdsKey, page }],
    queryFn: () =>
      api<JornadaReportResponse>(
        `/reports/jornada?from=${range?.from ?? ""}&to=${range?.to ?? ""}${deviceParam}&page=${page}&page_size=${PAGE_SIZE}`,
      ),
    enabled: range !== null,
    placeholderData: (prev) => prev,
  });
  const data = jornadaQuery.data;

  // Linhas da página agrupadas por device (items vêm ordenados por
  // device_name, date) + totais do RANGE INTEIRO por device_id.
  const groups = useMemo(() => {
    const out: { device_id: string; device_name: string; rows: JornadaRow[] }[] = [];
    for (const row of data?.items ?? []) {
      const last = out[out.length - 1];
      if (last !== undefined && last.device_id === row.device_id) last.rows.push(row);
      else out.push({ device_id: row.device_id, device_name: row.device_name, rows: [row] });
    }
    return out;
  }, [data]);
  const totalsById = useMemo(
    () => new Map((data?.device_totals ?? []).map((t) => [t.device_id, t])),
    [data],
  );

  const exportMutation = useCsvExport();
  const exportRequest =
    range !== null
      ? {
          kind: "jornada_csv" as const,
          params: {
            from: range.from,
            to: range.to,
            ...(deviceIds.length > 0 ? { device_ids: deviceIds } : {}),
          },
        }
      : null;

  const header = (
    <div>
      <h1 className="text-2xl font-semibold tracking-tight">Relatório de Jornada</h1>
      <p className="mt-1 text-sm text-muted-foreground">
        Uma linha por dispositivo e dia: primeiro e último evento, tempo ligada, ativo, ocioso e
        bloqueado.
      </p>
    </div>
  );

  // Banner FIXO e não-dispensável (DoD 11.3) - texto verbatim, sem botão de fechar.
  const disclaimer = (
    <div
      role="note"
      className="flex items-start gap-2 rounded-md border bg-muted/50 px-4 py-3 text-xs text-muted-foreground"
    >
      <Scale className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
      <span>
        Relatório gerencial de uso da estação de trabalho. Não constitui registro eletrônico de
        ponto (Portaria 671/MTE) e não substitui o controle de jornada do art. 74 da CLT.
      </span>
    </div>
  );

  // GET /me falhou: sem fuso não há período - erro com retry (padrão da AppsPage).
  if (meQuery.isError && meQuery.data === undefined) {
    return (
      <div className="space-y-4">
        {header}
        {disclaimer}
        <Card>
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(meQuery.error)}</p>
            <Button variant="outline" onClick={() => void meQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        </Card>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {header}
      {disclaimer}

      {/* Barra de filtros (todos os controles em h-9, padrão da timeline). */}
      <Card>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
          <PeriodPresetGroup active={activePreset} onSelect={applyPreset} disabled={todayStr === null} />
          {range !== null && (
            <span className="text-xs tabular-nums text-muted-foreground">
              {ddmm(range.from)} a {ddmm(range.to)}
            </span>
          )}
          <DeviceMultiSelect
            devices={devices}
            selected={deviceIds}
            onToggle={(id) =>
              setDeviceIds((prev) => (prev.includes(id) ? prev.filter((d) => d !== id) : [...prev, id]))
            }
            onClear={() => setDeviceIds([])}
          />
          <div className="ml-auto">
            <ExportCsvButton mutation={exportMutation} request={exportRequest} />
          </div>
        </div>
      </Card>

      <ExportCsvBanner mutation={exportMutation} />

      {jornadaQuery.isError && data !== undefined && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void jornadaQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      <Card>
        {jornadaQuery.isError && data === undefined ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(jornadaQuery.error)}</p>
            <Button variant="outline" onClick={() => void jornadaQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : (
          <div className={cn("overflow-x-auto", jornadaQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Data</th>
                  <th scope="col" className="px-3 py-2">Dia</th>
                  <th scope="col" className="px-3 py-2">Usuários</th>
                  <th scope="col" className="px-3 py-2 text-right">Primeiro evento</th>
                  <th scope="col" className="px-3 py-2 text-right">Último evento</th>
                  <th scope="col" className="px-3 py-2 text-right">Tempo ligada</th>
                  <th scope="col" className="px-3 py-2 text-right">Tempo ativo</th>
                  <th scope="col" className="px-3 py-2 text-right">Tempo ocioso</th>
                  <th scope="col" className="px-3 py-2 text-right">Tempo bloqueado</th>
                  <th scope="col" className="px-6 py-2">Observação</th>
                </tr>
              </thead>
              {data === undefined ? (
                <tbody>
                  {Array.from({ length: 8 }, (_, i) => (
                    <tr key={i} className="border-b last:border-b-0">
                      <td colSpan={10} className="px-6 py-2">
                        <Skeleton className="h-8 w-full" />
                      </td>
                    </tr>
                  ))}
                </tbody>
              ) : data.items.length === 0 ? (
                <tbody>
                  <tr>
                    <td colSpan={10} className="px-6 py-10 text-center text-sm text-muted-foreground">
                      {deviceIds.length > 0 ? (
                        <span className="inline-flex flex-col items-center gap-2">
                          <span>Nenhum resultado</span>
                          <Button variant="outline" size="sm" onClick={() => setDeviceIds([])}>
                            Limpar filtros
                          </Button>
                        </span>
                      ) : (
                        "Nenhum dado no período."
                      )}
                    </td>
                  </tr>
                </tbody>
              ) : (
                groups.map((group) => {
                  const totals = totalsById.get(group.device_id);
                  return (
                    <tbody key={group.device_id}>
                      {/* Cabeçalho do grupo: device + totais do RANGE INTEIRO. */}
                      <tr className="border-b bg-muted/50">
                        <th scope="rowgroup" colSpan={5} className="px-6 py-2 text-left">
                          <span className="font-medium">{group.device_name}</span>
                          {totals !== undefined && (
                            <span className="ml-2 text-xs font-normal text-muted-foreground">
                              {totals.days_with_data === 1
                                ? "1 dia com dados no período"
                                : `${totals.days_with_data} dias com dados no período`}
                            </span>
                          )}
                        </th>
                        <td className="whitespace-nowrap px-3 py-2 text-right font-medium tabular-nums">
                          {totals !== undefined ? formatDuration(totals.seconds_on) : ""}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right font-medium tabular-nums">
                          {totals !== undefined ? formatDuration(totals.seconds_active) : ""}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right font-medium tabular-nums">
                          {totals !== undefined ? formatDuration(totals.seconds_idle) : ""}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right font-medium tabular-nums">
                          {totals !== undefined ? formatDuration(totals.seconds_locked) : ""}
                        </td>
                        <td className="px-6 py-2" />
                      </tr>
                      {group.rows.map((row) => {
                        // Dia sem nenhum dado: linha esmaeçida, horários e durações como "-".
                        const noData = row.first_event_at === null && row.seconds_on === 0;
                        return (
                          <tr
                            key={row.date}
                            className={cn(
                              "border-b transition-colors hover:bg-accent/50",
                              noData && "text-muted-foreground",
                            )}
                          >
                            <td className="whitespace-nowrap px-6 py-2 tabular-nums">{ddmm(row.date)}</td>
                            <td className="whitespace-nowrap px-3 py-2">{weekdayShort(row.date)}</td>
                            <td className="max-w-[16rem] truncate px-3 py-2" title={row.users ?? undefined}>
                              {row.users ?? "-"}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {row.first_event_at !== null && timezone !== null
                                ? formatHm(row.first_event_at, timezone)
                                : "-"}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {row.last_event_at !== null && timezone !== null
                                ? formatHm(row.last_event_at, timezone)
                                : "-"}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {noData ? "-" : formatDuration(row.seconds_on)}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {noData ? "-" : formatDuration(row.seconds_active)}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {noData ? "-" : formatDuration(row.seconds_idle)}
                            </td>
                            <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                              {noData ? "-" : formatDuration(row.seconds_locked)}
                            </td>
                            <td className="whitespace-nowrap px-6 py-2 text-xs">
                              <NoteCell note={row.note} />
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  );
                })
              )}
            </table>

            {/* Paginação no padrão da AppsPage (uma linha por device × dia). */}
            {data !== undefined && data.total > PAGE_SIZE && (
              <div className="flex flex-wrap items-center justify-between gap-2 border-t px-6 py-3 text-sm">
                <span className="tabular-nums text-muted-foreground">
                  {`${(page - 1) * PAGE_SIZE + 1} a ${Math.min(page * PAGE_SIZE, data.total)} de ${data.total} linhas`}
                </span>
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
                    disabled={page >= Math.ceil(data.total / PAGE_SIZE)}
                    onClick={() => setPage((p) => p + 1)}
                  >
                    Próxima
                  </Button>
                </div>
              </div>
            )}
          </div>
        )}
      </Card>
    </div>
  );
}
