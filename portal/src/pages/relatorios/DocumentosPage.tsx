// =============================================================================
// Relatório de Documentos (/relatorios/documentos): quais ARQUIVOS foram
// abertos no período, com o aplicativo, o tempo ativo e quantas vezes.
// - Fonte: GET /reports/documents (activity_intervals; não há agregado por
//   arquivo - ver DocumentsReportResponse no backend). Mesmos filtros de
//   período, dispositivos e equipe das demais telas de relatório.
// - DADO PESSOAL POR NATUREZA: "Rescisão Fulano.docx" diz muito mais que
//   "winword.exe". O backend audita view_report SEMPRE (não só quando há
//   filtro), e a tela avisa quem está olhando que a consulta fica registrada.
// - O produto coleta o NOME do arquivo, nunca a pasta e nunca o conteúdo; o
//   nome sai do título da janela JÁ mascarado pela política da organização.
// =============================================================================

import { useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { FileText, ShieldCheck } from "lucide-react";
import { api } from "@/lib/api";
import { ddmm, formatDuration } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type { DocumentsReportResponse, MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  DeviceMultiSelect,
  PeriodPresetGroup,
  useFilterDevices,
  useReportRange,
} from "@/components/reports/filters";
import { TeamTagSelect, useTeamTags } from "@/components/filters/TeamTagSelect";

const PAGE_SIZE = 50;

export function DocumentosPage() {
  const [deviceIds, setDeviceIds] = useState<string[]>([]);
  const [busca, setBusca] = useState("");
  const [tag, setTag] = useState<string | null>(null);
  const [page, setPage] = useState(1);

  const meQuery = useQuery({ queryKey: ["me"], queryFn: () => api<MeResponse>("/me") });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const { range, activePreset, applyPreset } = useReportRange(timezone);
  const { devices } = useFilterDevices();
  const { tags } = useTeamTags();

  const params = new URLSearchParams();
  if (range !== null) {
    params.set("from", range.from);
    params.set("to", range.to);
  }
  if (busca.trim().length > 0) params.set("q", busca.trim());
  // device_ids é CSV no contrato dos relatórios (um único parâmetro).
  if (deviceIds.length > 0) params.set("device_ids", deviceIds.join(","));
  if (tag !== null) params.set("tag", tag);
  params.set("page", String(page));
  params.set("page_size", String(PAGE_SIZE));

  const query = useQuery({
    queryKey: ["reports", "documents", params.toString()],
    queryFn: () => api<DocumentsReportResponse>(`/reports/documents?${params.toString()}`),
    enabled: range !== null,
    placeholderData: (previous) => previous,
  });

  const itens = query.data?.items ?? [];
  const total = query.data?.total ?? 0;
  const paginas = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight">
          <FileText className="h-6 w-6 text-muted-foreground" aria-hidden />
          Documentos
        </h1>
        <p className="text-sm text-muted-foreground">
          Arquivos abertos no período{range !== null && `, de ${ddmm(range.from)} a ${ddmm(range.to)}`}.
        </p>
      </div>

      <Card className="border-l-4 border-l-primary/60">
        <CardHeader className="gap-2 py-4">
          <CardTitle className="flex items-center gap-2 text-base">
            <ShieldCheck className="h-4 w-4 text-muted-foreground" aria-hidden />
            Nome do arquivo, nunca o conteúdo
          </CardTitle>
          <CardDescription>
            O agente coleta apenas o <strong>nome</strong> do arquivo em foco — nunca a pasta, nunca
            o caminho e nunca o conteúdo. O nome sai do título da janela já mascarado pela política
            de títulos da organização. Esta consulta é dado pessoal e fica registrada na{" "}
            <Link to="/configuracoes/auditoria" className="underline underline-offset-2">
              trilha de auditoria
            </Link>
            .
          </CardDescription>
        </CardHeader>
      </Card>

      <Card>
        <CardHeader className="gap-3 py-4">
          <div className="flex flex-wrap items-center gap-3">
            <PeriodPresetGroup active={activePreset} onSelect={applyPreset} />
            <DeviceMultiSelect
              devices={devices}
              selected={deviceIds}
              onToggle={(id) => {
                setPage(1);
                setDeviceIds((ids) => (ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id]));
              }}
              onClear={() => {
                setPage(1);
                setDeviceIds([]);
              }}
            />
            <TeamTagSelect
              tags={tags}
              value={tag}
              onChange={(next) => {
                setPage(1);
                setTag(next);
              }}
            />
            <Input
              value={busca}
              onChange={(e) => {
                setPage(1);
                setBusca(e.target.value);
              }}
              placeholder="Buscar arquivo"
              aria-label="Buscar arquivo pelo nome"
              className="h-9 w-full max-w-xs"
            />
          </div>
        </CardHeader>

        <div className={cn("overflow-x-auto", query.isPlaceholderData && "opacity-70 transition-opacity")}>
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="px-6 py-2">
                  Arquivo
                </th>
                <th scope="col" className="px-3 py-2">
                  Aplicativo
                </th>
                <th scope="col" className="px-3 py-2 text-right">
                  Tempo ativo
                </th>
                <th scope="col" className="px-3 py-2 text-right">
                  Aberturas
                </th>
                <th scope="col" className="px-3 py-2 text-right">
                  Dispositivos
                </th>
              </tr>
            </thead>
            <tbody>
              {query.isLoading || range === null ? (
                Array.from({ length: 8 }, (_, i) => (
                  <tr key={i} className="border-b last:border-b-0">
                    <td colSpan={5} className="px-6 py-2">
                      <Skeleton className="h-8 w-full" />
                    </td>
                  </tr>
                ))
              ) : query.isError ? (
                <tr>
                  <td colSpan={5} className="px-6 py-10 text-center text-sm">
                    <span className="block text-muted-foreground">{genericErrorMessage(query.error)}</span>
                    <Button variant="outline" size="sm" className="mt-3" onClick={() => void query.refetch()}>
                      Tentar novamente
                    </Button>
                  </td>
                </tr>
              ) : itens.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-6 py-10 text-center text-sm text-muted-foreground">
                    Nenhum arquivo no período.
                  </td>
                </tr>
              ) : (
                itens.map((item) => (
                  <tr
                    key={`${item.app_id ?? "sem-app"}|${item.document_name}`}
                    className="border-b transition-colors last:border-b-0 hover:bg-accent/50"
                  >
                    <td className="px-6 py-2">
                      <span className="block max-w-[26rem] truncate font-medium">{item.document_name}</span>
                      {item.extension !== null && (
                        <span className="text-xs uppercase text-muted-foreground">{item.extension}</span>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      <span className="block max-w-[14rem] truncate">{item.app_display_name ?? "—"}</span>
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {formatDuration(item.seconds_active)}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">{item.open_count}</td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">{item.device_count}</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        {total > PAGE_SIZE && (
          <div className="flex items-center justify-between gap-3 border-t px-6 py-3 text-sm">
            <span className="text-muted-foreground">
              Página {page} de {paginas} · {total} arquivo(s)
            </span>
            <span className="flex gap-2">
              <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
                Anterior
              </Button>
              <Button variant="outline" size="sm" disabled={page >= paginas} onClick={() => setPage((p) => p + 1)}>
                Próxima
              </Button>
            </span>
          </div>
        )}
      </Card>
    </div>
  );
}
