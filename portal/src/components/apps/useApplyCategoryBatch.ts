// =============================================================================
// F6 - lote de categorização com progresso (Fila de classificação). MESMO
// contrato/paginação já usado pela AppsPage (PUT /app-catalog/categories/
// batch, páginas de APP_CATEGORY_BATCH_MAX, invalida tudo no fim) - extraído
// aqui para a Fila REUSAR o fluxo em vez de reescrevê-lo; a tela de prévia
// (o diálogo com a lista de apps/categorias) é quem decide QUANDO chamar
// `apply`, sempre depois de o admin confirmar a prévia obrigatória.
// =============================================================================

import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { genericErrorMessage } from "@/lib/messages";
import { APP_CATEGORY_BATCH_MAX } from "@/lib/types";
import type { AppCategoryBatchRequest, AppCategoryBatchResponse } from "@/lib/types";
import { invalidateAppCategoryData } from "./CategoryInlineSelect";

export interface BatchApplyItem {
  appId: string;
  categoryId: string;
}

export interface BatchApplyProgress {
  done: number;
  total: number;
}

/** Estado e função de aplicar um lote {app_id, category_id}[] em páginas, com progresso. */
export function useApplyCategoryBatch() {
  const queryClient = useQueryClient();
  const [progress, setProgress] = useState<BatchApplyProgress | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function apply(items: BatchApplyItem[]): Promise<number> {
    if (items.length === 0) return 0;
    setError(null);
    setProgress({ done: 0, total: items.length });

    let applied = 0;
    try {
      for (let start = 0; start < items.length; start += APP_CATEGORY_BATCH_MAX) {
        const page = items.slice(start, start + APP_CATEGORY_BATCH_MAX);
        const body: AppCategoryBatchRequest = {
          items: page.map((i) => ({ app_id: i.appId, category_id: i.categoryId })),
        };
        const result = await api<AppCategoryBatchResponse>("/app-catalog/categories/batch", {
          method: "PUT",
          body,
        });
        applied += result.applied;
        setProgress((p) => (p === null ? p : { ...p, done: p.done + page.length }));
      }
    } catch (err) {
      // páginas anteriores já commitaram: invalida para a tela refletir o estado real
      await invalidateAppCategoryData(queryClient);
      setProgress(null);
      const detail =
        applied > 0
          ? `${applied === 1 ? "1 app foi categorizado" : `${applied} apps foram categorizados`} antes da falha. ${genericErrorMessage(err)}`
          : genericErrorMessage(err);
      setError(detail);
      throw err;
    }

    await invalidateAppCategoryData(queryClient);
    setProgress(null);
    return applied;
  }

  return { apply, progress, error, setError };
}
