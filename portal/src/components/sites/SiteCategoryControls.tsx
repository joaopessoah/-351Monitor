// =============================================================================
// Curadoria de SITES: select inline de categoria + lote de sugestões.
// Espelho de components/apps/CategoryInlineSelect + useApplyCategoryBatch,
// apontando para /site-catalog. É o MESMO contrato e o MESMO vocabulário (a
// tabela `categories` é uma só para app e site) - o que muda é a chave do
// recurso (site_id em vez de app_id) e o conjunto de queries a invalidar.
// =============================================================================

import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { QueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { UNCATEGORIZED_LABEL } from "@/lib/classification";
import { genericErrorMessage } from "@/lib/messages";
import { SITE_CATEGORY_BATCH_MAX } from "@/lib/types";
import type {
  CategoryItem,
  SiteCategoryBatchRequest,
  SiteCategoryBatchResponse,
  SiteCategoryPutRequest,
} from "@/lib/types";
import { cn } from "@/lib/utils";

/**
 * Invalida tudo que muda quando um site é (des)mapeado: o catálogo de sites
 * (cobertura e contadores), os relatórios de uso (a classificação de SITE vence
 * a de app nos baldes), o dashboard e a timeline.
 */
export function invalidateSiteCategoryData(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ["site-catalog"] }),
    queryClient.invalidateQueries({ queryKey: ["categories"] }),
    queryClient.invalidateQueries({ queryKey: ["reports"] }),
    queryClient.invalidateQueries({ queryKey: ["dashboard"] }),
    queryClient.invalidateQueries({ queryKey: ["timeline"] }),
  ]).then(() => undefined);
}

interface SetSiteCategoryVars {
  siteId: string;
  categoryId: string | null;
  /** Nome custom ATUAL - o PUT é declarativo, reenviar preserva ao trocar a categoria. */
  customDisplayName: string | null;
}

export function useSetSiteCategory(onError?: (err: unknown) => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ siteId, categoryId, customDisplayName }: SetSiteCategoryVars) => {
      const body: SiteCategoryPutRequest = {
        category_id: categoryId,
        custom_display_name: customDisplayName,
      };
      return api<unknown>(`/site-catalog/${encodeURIComponent(siteId)}/category`, {
        method: "PUT",
        body,
      });
    },
    onSuccess: () => invalidateSiteCategoryData(queryClient),
    onError,
  });
}

export function SiteCategoryInlineSelect({
  siteId,
  categoryId,
  categoryName = null,
  customDisplayName = null,
  categories,
  disabled = false,
  onError,
}: {
  siteId: string;
  categoryId: string | null;
  categoryName?: string | null;
  customDisplayName?: string | null;
  categories: CategoryItem[];
  disabled?: boolean;
  onError?: (err: unknown) => void;
}) {
  const mutation = useSetSiteCategory(onError);
  // Valor otimista enquanto salva, limpo no onSettled (depois do refetch): sem
  // flicker de volta ao valor antigo.
  const [pendingValue, setPendingValue] = useState<string | null>(null);
  const saving = mutation.isPending || pendingValue !== null;

  const options = categories.filter((c) => c.name !== UNCATEGORIZED_LABEL);
  const effectiveId = categoryName === UNCATEGORIZED_LABEL ? null : categoryId;

  return (
    <select
      aria-label="Categoria do site"
      value={pendingValue ?? effectiveId ?? ""}
      disabled={disabled || saving}
      onChange={(e) => {
        const value = e.target.value;
        setPendingValue(value);
        mutation.mutate(
          { siteId, categoryId: value === "" ? null : value, customDisplayName },
          { onSettled: () => setPendingValue(null) },
        );
      }}
      className={cn(
        "h-8 w-full min-w-[11rem] rounded-md border border-input bg-card px-2 text-sm",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        "disabled:cursor-not-allowed",
        saving && "opacity-50",
      )}
    >
      <option value="">{UNCATEGORIZED_LABEL}</option>
      {effectiveId !== null && !options.some((c) => c.id === effectiveId) && (
        <option value={effectiveId}>{categoryName ?? "Categoria atual"}</option>
      )}
      {options.map((c) => (
        <option key={c.id} value={c.id}>
          {c.name}
        </option>
      ))}
    </select>
  );
}

export interface SiteBatchApplyItem {
  siteId: string;
  categoryId: string;
}

/** Aplica um lote {site_id, category_id}[] em páginas, com progresso — igual ao de apps. */
export function useApplySiteCategoryBatch() {
  const queryClient = useQueryClient();
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function apply(items: SiteBatchApplyItem[]): Promise<number> {
    if (items.length === 0) return 0;
    setError(null);
    setProgress({ done: 0, total: items.length });

    let applied = 0;
    try {
      for (let start = 0; start < items.length; start += SITE_CATEGORY_BATCH_MAX) {
        const page = items.slice(start, start + SITE_CATEGORY_BATCH_MAX);
        const body: SiteCategoryBatchRequest = {
          items: page.map((i) => ({ site_id: i.siteId, category_id: i.categoryId })),
        };
        const result = await api<SiteCategoryBatchResponse>("/site-catalog/categories/batch", {
          method: "PUT",
          body,
        });
        applied += result.applied;
        setProgress((p) => (p === null ? p : { ...p, done: p.done + page.length }));
      }
    } catch (err) {
      // páginas anteriores já commitaram: invalida para a tela refletir o real
      await invalidateSiteCategoryData(queryClient);
      setProgress(null);
      setError(
        applied > 0
          ? `${applied === 1 ? "1 site foi categorizado" : `${applied} sites foram categorizados`} antes da falha. ${genericErrorMessage(err)}`
          : genericErrorMessage(err),
      );
      throw err;
    }

    await invalidateSiteCategoryData(queryClient);
    setProgress(null);
    return applied;
  }

  return { apply, progress, error, setError };
}
