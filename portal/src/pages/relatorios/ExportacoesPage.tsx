// =============================================================================
// Exportações (/relatorios/exportacoes - F3.5, Seção 8.6 linha 949): histórico
// dos exports CSV dos últimos 30 dias do tenant (GET /exports) - trilha de
// auditoria visível: quem gerou, quando e com quais filtros.
// - Polling: 5 s enquanto houver job queued/running (o worker roda a cada
//   15 s), senão 60 s; pausa em aba oculta (padrão N18 do TanStack Query).
// - Baixar (done && !expired): fetch AUTENTICADO (apiDownload - a API só
//   aceita Bearer e navegação de browser não envia header) -> Blob -> <a
//   download> sintético; 409/410 viram mensagem inline (sem ejetar da SPA).
//   O arquivo expira 7 dias após a geração (o backend responde 410 depois).
// - truncated=true: o CSV parou no teto de 500.000 linhas - aviso visível
//   (jamais truncamento silencioso; o usuário estreita o filtro).
// =============================================================================

import { useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, Download, FileDown } from "lucide-react";
import { api, apiDownload, ApiError } from "@/lib/api";
import { ddmm, formatDayMonthTime, formatRelative } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type { ExportJobItem, ExportKind, ExportsResponse, MeResponse } from "@/lib/types";
import { isDsrExportKind } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

const KIND_LABELS: Record<ExportKind, string> = {
  jornada_csv: "Jornada (CSV)",
  usage_csv: "Uso de aplicativos (CSV)",
  // Pacotes DSR/offboarding (F4.5): ZIP, solicitados em Privacidade, prazo de 72h.
  dsr_subject: "Dados do titular (ZIP)",
  dsr_device: "Dados do dispositivo (ZIP)",
  tenant_full: "Acervo completo da organização (ZIP)",
};

/** Mesmos rótulos do seletor segmentado da tela de Uso. */
const GROUP_BY_LABELS: Record<string, string> = {
  app: "por aplicativo",
  category: "por categoria",
  device: "por dispositivo",
  device_user: "por pessoa",
};

/**
 * Resumo legível dos filtros do job. Para CSV de relatório: período, devices e
 * agrupamento. Para pacotes DSR (F4.5) o alvo é o titular/dispositivo e não há
 * filtro de período (o pacote é o acervo INTEIRO do titular) - mostramos um
 * rótulo neutro do escopo em vez de inventar uma janela de datas.
 */
function paramsSummary(item: ExportJobItem): string {
  const { kind, params } = item;
  if (kind === "dsr_subject") return "Todos os dados do titular";
  if (kind === "dsr_device") return "Todos os dados do dispositivo";
  if (kind === "tenant_full") return "Acervo completo da organização";

  const parts: string[] = [];
  if (params.from !== undefined && params.to !== undefined) {
    parts.push(`${ddmm(params.from)} a ${ddmm(params.to)}`);
  }
  const n = params.device_ids?.length ?? 0;
  parts.push(n === 0 ? "todos os dispositivos" : n === 1 ? "1 dispositivo" : `${n} dispositivos`);
  if (params.group_by !== undefined && params.group_by in GROUP_BY_LABELS) {
    parts.push(GROUP_BY_LABELS[params.group_by]);
  }
  return parts.join(" · ");
}

/**
 * Nome de arquivo de fallback quando o backend não manda Content-Disposition.
 * DSR/offboarding saem como .zip; CSV de relatório como .csv com o período.
 */
function downloadFallbackName(item: ExportJobItem): string {
  if (isDsrExportKind(item.kind)) {
    const target =
      item.kind === "dsr_subject"
        ? item.params.device_user_id
        : item.kind === "dsr_device"
          ? item.params.device_id
          : "organizacao";
    const prefix =
      item.kind === "dsr_subject" ? "dsr_titular" : item.kind === "dsr_device" ? "dsr_dispositivo" : "acervo_tenant";
    return `${prefix}_${target ?? item.id}.zip`;
  }
  return `${item.kind === "jornada_csv" ? "jornada" : "uso"}_${item.params.from}_${item.params.to}.csv`;
}

/** Status de UI: "Expirado" vence "Pronto" quando o prazo de retenção passou. */
function uiStatus(item: ExportJobItem): "queued" | "running" | "done" | "failed" | "expired" {
  if (item.status === "done" && item.expired) return "expired";
  return item.status;
}

const STATUS_BADGE: Record<ReturnType<typeof uiStatus>, { label: string; className: string }> = {
  queued: { label: "Na fila", className: "border-slate-200 bg-slate-50 text-slate-700" },
  running: { label: "Gerando", className: "border-blue-200 bg-blue-50 text-blue-800" },
  done: { label: "Pronto", className: "border-emerald-200 bg-emerald-50 text-emerald-800" },
  failed: { label: "Falhou", className: "border-destructive/30 bg-destructive/10 text-destructive" },
  expired: { label: "Expirado", className: "border-slate-200 bg-muted text-muted-foreground" },
};

function StatusBadge({ item }: { item: ExportJobItem }) {
  const status = uiStatus(item);
  const badge = STATUS_BADGE[status];
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium",
        badge.className,
      )}
    >
      {status === "running" && (
        <span aria-hidden className="h-1.5 w-1.5 animate-pulse rounded-full bg-blue-600" />
      )}
      {badge.label}
    </span>
  );
}

export function ExportacoesPage() {
  // id do job baixando (botão desabilitado) + erro inline do último download.
  const [downloadingId, setDownloadingId] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;

  const exportsQuery = useQuery({
    queryKey: ["exports"],
    queryFn: () => api<ExportsResponse>("/exports"),
    // 5 s enquanto houver job na fila ou gerando (feedback rápido pós-POST),
    // senão 60 s - só para acompanhar a expiração dos links.
    refetchInterval: (query) => {
      const items = query.state.data?.items ?? [];
      return items.some((i) => i.status === "queued" || i.status === "running") ? 5_000 : 60_000;
    },
    placeholderData: (prev) => prev,
  });
  const data = exportsQuery.data;
  const nowIso = new Date().toISOString();

  /**
   * Download via fetch autenticado (apiDownload): a API só aceita Bearer e o
   * token vive em memória - navegação direta (location.href) tomaria 401.
   * Blob -> <a download> sintético; 409/410 viram mensagem amigável e a lista
   * é atualizada (o badge vira "Expirado" em vez de um JSON de erro full-page).
   */
  async function handleDownload(item: ExportJobItem): Promise<void> {
    setDownloadingId(item.id);
    setDownloadError(null);
    try {
      const { blob, filename } = await apiDownload(
        `/exports/${encodeURIComponent(item.id)}/download`,
      );
      // Nome sugerido: o backend manda no Content-Disposition; o fallback respeita
      // a extensão real - .zip para os pacotes DSR/offboarding, .csv para relatório.
      const fallbackName = downloadFallbackName(item);
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = filename ?? fallbackName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    } catch (error) {
      if (error instanceof ApiError && error.status === 410) {
        setDownloadError(
          isDsrExportKind(item.kind)
            ? "Este pacote expirou. Gere um novo em Configurações, Privacidade."
            : "Este arquivo expirou. Gere uma nova exportação no relatório.",
        );
        void exportsQuery.refetch();
      } else if (error instanceof ApiError && error.status === 409) {
        setDownloadError("A exportação ainda não foi concluída. Aguarde o status Pronto.");
        void exportsQuery.refetch();
      } else {
        setDownloadError(genericErrorMessage(error));
      }
    } finally {
      setDownloadingId(null);
    }
  }

  const header = (
    <div>
      <h1 className="text-2xl font-semibold tracking-tight">Exportações</h1>
      <p className="mt-1 text-sm text-muted-foreground">
        Exportações dos últimos 30 dias da organização: quem gerou, quando e com quais filtros.
        Os CSV de relatório ficam disponíveis por 7 dias; os pacotes de dados do titular (DSR),
        por 72 horas após a geração.
      </p>
    </div>
  );

  return (
    <div className="space-y-4">
      {header}

      {exportsQuery.isError && data !== undefined && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>Não foi possível atualizar os dados. Mostrando a última leitura.</span>
          <Button variant="outline" size="sm" onClick={() => void exportsQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      )}

      {downloadError !== null && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>{downloadError}</span>
          <Button variant="outline" size="sm" onClick={() => setDownloadError(null)}>
            Fechar
          </Button>
        </div>
      )}

      <Card>
        {exportsQuery.isError && data === undefined ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(exportsQuery.error)}</p>
            <Button variant="outline" onClick={() => void exportsQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : data !== undefined && data.items.length === 0 ? (
          // Estado vazio desenhado (Seção 8.9): CTA para os relatórios.
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <span className="flex h-12 w-12 items-center justify-center rounded-full bg-muted">
              <FileDown className="h-6 w-6 text-muted-foreground" aria-hidden />
            </span>
            <p className="text-base font-medium">Nenhuma exportação nos últimos 30 dias</p>
            <p className="max-w-md text-sm text-muted-foreground">
              Gere um CSV pelo botão Exportar CSV nos relatórios de Jornada ou de Uso de
              aplicativos - o arquivo aparece aqui quando estiver pronto.
            </p>
            <div className="flex flex-wrap justify-center gap-2">
              <Link
                to="/relatorios/jornada"
                className="inline-flex h-9 items-center justify-center gap-2 whitespace-nowrap rounded-md border border-input bg-card px-3 text-sm font-medium transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
              >
                Relatório de Jornada
              </Link>
              <Link
                to="/relatorios/uso"
                className="inline-flex h-9 items-center justify-center gap-2 whitespace-nowrap rounded-md border border-input bg-card px-3 text-sm font-medium transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
              >
                Uso de aplicativos
              </Link>
            </div>
          </div>
        ) : (
          <div className={cn("overflow-x-auto", exportsQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-6 py-2">Tipo</th>
                  <th scope="col" className="px-3 py-2">Solicitado por</th>
                  <th scope="col" className="px-3 py-2">Quando</th>
                  <th scope="col" className="px-3 py-2">Filtros</th>
                  <th scope="col" className="px-3 py-2">Status</th>
                  <th scope="col" className="px-6 py-2 text-right">
                    <span className="sr-only">Ações</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {data === undefined
                  ? Array.from({ length: 5 }, (_, i) => (
                      <tr key={i} className="border-b last:border-b-0">
                        <td colSpan={6} className="px-6 py-2">
                          <Skeleton className="h-8 w-full" />
                        </td>
                      </tr>
                    ))
                  : data.items.map((item) => (
                      <tr key={item.id} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                        <td className="whitespace-nowrap px-6 py-2 font-medium">
                          {KIND_LABELS[item.kind] ?? item.kind}
                        </td>
                        <td className="max-w-[12rem] truncate px-3 py-2" title={item.requested_by_name}>
                          {item.requested_by_name}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 tabular-nums">
                          <span className="block">{formatRelative(item.created_at, nowIso)}</span>
                          {timezone !== null && (
                            <span className="block text-xs text-muted-foreground">
                              {formatDayMonthTime(item.created_at, timezone)}
                            </span>
                          )}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-xs text-muted-foreground">
                          {paramsSummary(item)}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2">
                          <StatusBadge item={item} />
                          {item.status === "done" && item.row_count !== null && (
                            <span className="ml-2 text-xs tabular-nums text-muted-foreground">
                              {item.row_count.toLocaleString("pt-BR")} linhas
                            </span>
                          )}
                          {item.truncated && (
                            <span
                              className="ml-2 inline-flex items-center gap-1 text-xs font-medium text-amber-700"
                              title="O arquivo parou no teto de 500.000 linhas. Estreite o período ou os dispositivos e exporte novamente para obter o restante."
                            >
                              <AlertTriangle className="h-3 w-3" aria-hidden />
                              CSV parcial (teto de 500.000 linhas)
                            </span>
                          )}
                        </td>
                        <td className="whitespace-nowrap px-6 py-2 text-right">
                          {item.status === "done" && !item.expired && (
                            <Button
                              variant="outline"
                              size="sm"
                              disabled={downloadingId === item.id}
                              onClick={() => void handleDownload(item)}
                            >
                              <Download className="h-4 w-4" aria-hidden />
                              {downloadingId === item.id ? "Baixando…" : "Baixar"}
                            </Button>
                          )}
                        </td>
                      </tr>
                    ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}
