// =============================================================================
// Edição inline da categoria de um app (F3.3) - o PRIMEIRO useMutation
// autenticado do portal e o padrão a seguir nas próximas fatias:
// - mutationFn fina sobre api() (PUT /app-catalog/{appId}/category;
//   category_id null desmapeia = app volta a Não categorizado);
// - invalidação de TODAS as queries afetadas no onSuccess, retornando a
//   Promise para o react-query segurar o onSettled até o refetch terminar
//   (o select nunca "volta" para o valor antigo);
// - estado de salvando POR LINHA (cada select tem sua própria instância de
//   mutation): disabled + opacity enquanto persiste.
// A categoria vale para o TENANT INTEIRO e o backend reagrega os últimos 30
// dias - quem renderiza este componente exibe o aviso fixo correspondente.
// =============================================================================

import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { QueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { UNCATEGORIZED_LABEL } from "@/lib/classification";
import type { AppCategoryPutRequest, CategoryItem } from "@/lib/types";
import { cn } from "@/lib/utils";

/**
 * Invalida tudo que muda quando um app é (des)mapeado ou uma categoria é
 * alterada/excluída: listas de categorias (app_count), catálogo
 * (uncategorized_count), relatórios de uso, top apps do dashboard e a
 * timeline (tooltip de apps traz o nome da categoria).
 */
export function invalidateAppCategoryData(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ["categories"] }),
    queryClient.invalidateQueries({ queryKey: ["app-catalog"] }),
    queryClient.invalidateQueries({ queryKey: ["reports"] }),
    queryClient.invalidateQueries({ queryKey: ["dashboard", "top-apps"] }),
    queryClient.invalidateQueries({ queryKey: ["timeline"] }),
  ]).then(() => undefined);
}

interface SetAppCategoryVars {
  appId: string;
  categoryId: string | null;
  /**
   * Nome custom ATUAL do app. O PUT é declarativo (custom_display_name ausente
   * ou null = sem nome custom): reenviar o valor vigente preserva o nome ao
   * trocar só a categoria. Desmapear (categoryId null) remove a linha inteira
   * no backend, nome junto - contrato documentado.
   */
  customDisplayName: string | null;
}

/** PUT /app-catalog/{appId}/category + invalidação - padrão de mutation do portal. */
export function useSetAppCategory(onError?: (err: unknown) => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ appId, categoryId, customDisplayName }: SetAppCategoryVars) => {
      const body: AppCategoryPutRequest = {
        category_id: categoryId,
        custom_display_name: customDisplayName,
      };
      return api<unknown>(`/app-catalog/${encodeURIComponent(appId)}/category`, {
        method: "PUT",
        body,
      });
    },
    onSuccess: () => invalidateAppCategoryData(queryClient),
    onError,
  });
}

export function CategoryInlineSelect({
  appId,
  categoryId,
  categoryName = null,
  customDisplayName = null,
  categories,
  disabled = false,
  onError,
}: {
  appId: string;
  /** Categoria atual do app segundo o servidor - null = Não categorizado. */
  categoryId: string | null;
  /** Nome da categoria atual - mantém o valor exibido enquanto GET /categories carrega. */
  categoryName?: string | null;
  /** Nome custom atual do app - reenviado no PUT para não ser apagado. */
  customDisplayName?: string | null;
  categories: CategoryItem[];
  /** Trava externa (ex.: recategorização em lote em andamento). */
  disabled?: boolean;
  onError?: (err: unknown) => void;
}) {
  const mutation = useSetAppCategory(onError);
  // Valor otimista exibido enquanto salva; limpo só no onSettled, DEPOIS da
  // invalidação refazer as queries - sem flicker do valor antigo.
  const [pendingValue, setPendingValue] = useState<string | null>(null);
  const saving = mutation.isPending || pendingValue !== null;

  // A categoria seedada "Não categorizado" é, para o usuário, o mesmo conceito
  // da option "" (desmapear): sai da lista e o app mapeado nela aparece como "".
  const options = categories.filter((c) => c.name !== UNCATEGORIZED_LABEL);
  const effectiveId = categoryName === UNCATEGORIZED_LABEL ? null : categoryId;

  return (
    <select
      aria-label="Categoria do app"
      value={pendingValue ?? effectiveId ?? ""}
      disabled={disabled || saving}
      onChange={(e) => {
        const value = e.target.value;
        setPendingValue(value);
        mutation.mutate(
          { appId, categoryId: value === "" ? null : value, customDisplayName },
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
        // Lista de categorias ainda carregando: preserva o valor atual do app.
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
