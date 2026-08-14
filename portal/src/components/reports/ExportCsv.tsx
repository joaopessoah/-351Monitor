// =============================================================================
// Exportação CSV assíncrona (F3.5): botão "Exportar CSV" + banner inline de
// resultado, compartilhados por /relatorios/jornada e /relatorios/uso.
// POST /exports responde 202 e o worker gera o arquivo (UTF-8 com BOM,
// separador ';'); o acompanhamento/download fica em /relatorios/exportacoes.
// Admin E viewer podem exportar (a trilha de auditoria registra quem pediu).
// =============================================================================

import { Link } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import type { UseMutationResult } from "@tanstack/react-query";
import { Download } from "lucide-react";
import { api } from "@/lib/api";
import { genericErrorMessage } from "@/lib/messages";
import type { ExportCreateRequest, ExportCreateResponse } from "@/lib/types";
import { Button } from "@/components/ui/button";

export type CsvExportMutation = UseMutationResult<ExportCreateResponse, unknown, ExportCreateRequest>;

/** Mutation fina sobre POST /exports - uma instância por tela. */
export function useCsvExport(): CsvExportMutation {
  return useMutation({
    mutationFn: (req: ExportCreateRequest) =>
      api<ExportCreateResponse>("/exports", { method: "POST", body: req }),
  });
}

export function ExportCsvButton({
  mutation,
  request,
  disabled = false,
}: {
  mutation: CsvExportMutation;
  /** null enquanto os filtros ainda não estão prontos (fuso carregando). */
  request: ExportCreateRequest | null;
  disabled?: boolean;
}) {
  return (
    <Button
      variant="outline"
      size="sm"
      className="h-9"
      disabled={disabled || request === null || mutation.isPending}
      onClick={() => {
        if (request !== null) mutation.mutate(request);
      }}
    >
      <Download className="h-4 w-4" aria-hidden />
      {mutation.isPending ? "Enviando…" : "Exportar CSV"}
    </Button>
  );
}

/** Banner inline com o resultado do POST - sucesso linka para o histórico. */
export function ExportCsvBanner({ mutation }: { mutation: CsvExportMutation }) {
  if (mutation.isSuccess) {
    return (
      <div
        role="status"
        className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-viz-produtivo/30 bg-viz-produtivo/10 px-3 py-2 text-sm text-brand-soft"
      >
        <span>
          Exportação adicionada à fila.{" "}
          <Link
            to="/relatorios/exportacoes"
            className="font-medium underline underline-offset-2 hover:text-viz-produtivo"
          >
            Acompanhar em Exportações
          </Link>
        </span>
        <Button variant="ghost" size="sm" onClick={() => mutation.reset()}>
          Fechar
        </Button>
      </div>
    );
  }
  if (mutation.isError) {
    return (
      <div
        role="alert"
        className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
      >
        <span>{genericErrorMessage(mutation.error)}</span>
        <Button variant="outline" size="sm" onClick={() => mutation.reset()}>
          Fechar
        </Button>
      </div>
    );
  }
  return null;
}
