// =============================================================================
// Configurações > Categorias (F3.3, Seção 8.7): duas abas internas.
// - "Categorias": tabela nome/classificação/cor/apps mapeados com criar,
//   editar (renomear/mudar classificação - reagrega 30 dias no backend) e
//   excluir (confirmação textual: os apps mapeados viram Não categorizado e
//   os últimos 30 dias são reagregados).
// - "Mapeamento de apps": busca no catálogo do tenant (GET /app-catalog,
//   janela fixa de 30 dias, máx. 500 itens), contador de não categorizados,
//   select de categoria por linha e recategorização em LOTE (N PUTs
//   sequenciais com progresso simples).
// Admin/owner editam; viewer é somente leitura. Vocabulário FIXO da
// classificação (lib/classification.ts) - jamais os adjetivos vetados.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Info, Pencil, Plus, Tags, Trash2 } from "lucide-react";
import { api, ApiError } from "@/lib/api";
import {
  classificationColor,
  classificationLabel,
  UNCATEGORIZED_LABEL,
} from "@/lib/classification";
import { formatDuration } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type {
  AppCatalogResponse,
  CategoriesResponse,
  CategoryCreateRequest,
  CategoryItem,
  CategoryUpdateRequest,
  MeResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import {
  CategoryInlineSelect,
  invalidateAppCategoryData,
} from "@/components/apps/CategoryInlineSelect";

// Classes do grupo segmentado (mesmo padrão da timeline/dashboard).
const segmentedButton = "rounded-[5px] px-3 text-xs font-medium transition-colors";
const segmentedOn = "bg-primary/10 text-primary";
const segmentedOff = "text-muted-foreground hover:bg-accent hover:text-accent-foreground";

const selectClass = cn(
  "h-10 w-full rounded-md border border-input bg-card px-3 text-sm",
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
);

/** 409 = nome duplicado no tenant; o resto cai na mensagem genérica. */
function categoryErrorMessage(err: unknown): string {
  if (err instanceof ApiError && err.status === 409) {
    return "Já existe uma categoria com esse nome.";
  }
  return genericErrorMessage(err);
}

export function CategoriasPage() {
  const [tab, setTab] = useState<"categorias" | "mapeamento">("categorias");

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const admin = isAdmin(meQuery.data);

  return (
    <div className="space-y-4">
      <div
        role="group"
        aria-label="Seções de categorias"
        className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
      >
        <button
          type="button"
          aria-pressed={tab === "categorias"}
          onClick={() => setTab("categorias")}
          className={cn(segmentedButton, tab === "categorias" ? segmentedOn : segmentedOff)}
        >
          Categorias
        </button>
        <button
          type="button"
          aria-pressed={tab === "mapeamento"}
          onClick={() => setTab("mapeamento")}
          className={cn(segmentedButton, tab === "mapeamento" ? segmentedOn : segmentedOff)}
        >
          Mapeamento de apps
        </button>
      </div>

      {tab === "categorias" ? <CategoriasTab admin={admin} /> : <MapeamentoTab admin={admin} />}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Aba "Categorias" - CRUD
// -----------------------------------------------------------------------------

function CategoriasTab({ admin }: { admin: boolean }) {
  const categoriesQuery = useQuery({
    queryKey: ["categories"],
    queryFn: () => api<CategoriesResponse>("/categories"),
  });
  const data = categoriesQuery.data;

  // null = fechado; { category: null } = criar; { category } = editar.
  const [form, setForm] = useState<{ category: CategoryItem | null } | null>(null);
  const [deleting, setDeleting] = useState<CategoryItem | null>(null);

  return (
    <>
      <Card>
        <CardHeader className="pb-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="space-y-1.5">
              <CardTitle className="text-base">Categorias</CardTitle>
              <CardDescription>
                Cada categoria classifica apps como Relacionado ao trabalho, Neutro ou Não
                relacionado ao trabalho.
                {!admin && " Somente administradores e proprietários editam."}
              </CardDescription>
            </div>
            {admin && (
              <Button size="sm" className="h-9" onClick={() => setForm({ category: null })}>
                <Plus className="h-4 w-4" aria-hidden />
                Nova categoria
              </Button>
            )}
          </div>
        </CardHeader>
        <div className="pb-0">
          {categoriesQuery.isError && data === undefined ? (
            <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
              <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
              <p className="text-sm text-muted-foreground">
                {genericErrorMessage(categoriesQuery.error)}
              </p>
              <Button variant="outline" onClick={() => void categoriesQuery.refetch()}>
                Tentar novamente
              </Button>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                    <th scope="col" className="px-6 py-2">Nome</th>
                    <th scope="col" className="px-3 py-2">Classificação</th>
                    <th scope="col" className="px-3 py-2">Cor</th>
                    <th scope="col" className="px-3 py-2 text-right">Apps mapeados</th>
                    {admin && (
                      <th scope="col" className="px-6 py-2 text-right">Ações</th>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {data === undefined ? (
                    Array.from({ length: 4 }, (_, i) => (
                      <tr key={i} className="border-b last:border-b-0">
                        <td colSpan={admin ? 5 : 4} className="px-6 py-2">
                          <Skeleton className="h-8 w-full" />
                        </td>
                      </tr>
                    ))
                  ) : data.items.length === 0 ? (
                    <tr>
                      <td colSpan={admin ? 5 : 4} className="px-6 py-10 text-center text-sm text-muted-foreground">
                        <span className="inline-flex flex-col items-center gap-2">
                          <span>Nenhuma categoria ainda.</span>
                          {admin && (
                            <Button variant="outline" size="sm" onClick={() => setForm({ category: null })}>
                              Criar a primeira categoria
                            </Button>
                          )}
                        </span>
                      </td>
                    </tr>
                  ) : (
                    data.items.map((c) => (
                      <tr key={c.id} className="border-b last:border-b-0">
                        <td className="max-w-[16rem] truncate px-6 py-2 font-medium">{c.name}</td>
                        <td className="whitespace-nowrap px-3 py-2">
                          <span className="flex items-center gap-2">
                            <span
                              aria-hidden
                              className="h-2.5 w-2.5 shrink-0 rounded-sm"
                              style={{ backgroundColor: classificationColor(c.classification) }}
                            />
                            <span>{classificationLabel(c.classification)}</span>
                          </span>
                        </td>
                        <td className="whitespace-nowrap px-3 py-2">
                          {c.color !== null ? (
                            <span
                              role="img"
                              aria-label={`Cor ${c.color}`}
                              title={c.color}
                              className="inline-block h-3.5 w-3.5 rounded-full border border-border"
                              style={{ backgroundColor: c.color }}
                            />
                          ) : (
                            <span className="text-muted-foreground">-</span>
                          )}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                          {c.app_count}
                        </td>
                        {admin && (
                          <td className="whitespace-nowrap px-6 py-2 text-right">
                            <Button
                              variant="ghost"
                              size="sm"
                              className="h-8"
                              onClick={() => setForm({ category: c })}
                            >
                              <Pencil className="h-3.5 w-3.5" aria-hidden />
                              Editar
                            </Button>
                            <Button
                              variant="ghost"
                              size="sm"
                              className="h-8 text-destructive hover:text-destructive"
                              onClick={() => setDeleting(c)}
                            >
                              <Trash2 className="h-3.5 w-3.5" aria-hidden />
                              Excluir
                            </Button>
                          </td>
                        )}
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </Card>

      {form !== null && (
        <CategoryFormDialog category={form.category} onClose={() => setForm(null)} />
      )}
      {deleting !== null && (
        <DeleteCategoryDialog category={deleting} onClose={() => setDeleting(null)} />
      )}
    </>
  );
}

/** Criar/editar categoria - POST /categories ou PATCH /categories/{id}. */
function CategoryFormDialog({
  category,
  onClose,
}: {
  category: CategoryItem | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(category?.name ?? "");
  const [classification, setClassification] = useState(String(category?.classification ?? 0));
  const [color, setColor] = useState(
    category !== null && category.color !== null
      ? category.color
      : classificationColor(category?.classification ?? 0),
  );
  // Enquanto o admin não mexe na cor, ela acompanha a classificação escolhida.
  const [colorTouched, setColorTouched] = useState(category !== null && category.color !== null);

  const mutation = useMutation({
    mutationFn: () => {
      if (category === null) {
        const body: CategoryCreateRequest = {
          name: name.trim(),
          classification: Number(classification),
          color,
        };
        return api<CategoryItem>("/categories", { method: "POST", body });
      }
      const body: CategoryUpdateRequest = {
        name: name.trim(),
        classification: Number(classification),
        color,
      };
      return api<CategoryItem>(`/categories/${encodeURIComponent(category.id)}`, {
        method: "PATCH",
        body,
      });
    },
    // Mudar a classificação reagrega 30 dias no backend: invalida relatórios,
    // catálogo, dashboard e timeline junto com a lista de categorias.
    onSuccess: async () => {
      await invalidateAppCategoryData(queryClient);
      onClose();
    },
  });

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{category === null ? "Nova categoria" : "Editar categoria"}</DialogTitle>
          <DialogDescription>
            {category === null
              ? "A categoria classifica apps para toda a organização."
              : "Mudar a classificação reagrega os últimos 30 dias. Histórico anterior mantém a classificação antiga."}
          </DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault();
            if (name.trim().length > 0 && !mutation.isPending) mutation.mutate();
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="categoria-nome">Nome</Label>
            <Input
              id="categoria-nome"
              value={name}
              onChange={(e) => setName(e.target.value)}
              maxLength={80}
              autoFocus
              required
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="categoria-classificacao">Classificação</Label>
            <select
              id="categoria-classificacao"
              value={classification}
              onChange={(e) => {
                setClassification(e.target.value);
                if (!colorTouched) setColor(classificationColor(Number(e.target.value)));
              }}
              className={selectClass}
            >
              <option value="1">Relacionado ao trabalho</option>
              <option value="0">Neutro</option>
              <option value="-1">Não relacionado ao trabalho</option>
            </select>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="categoria-cor">Cor</Label>
            <div className="flex items-center gap-3">
              <input
                id="categoria-cor"
                type="color"
                value={color}
                onChange={(e) => {
                  setColor(e.target.value);
                  setColorTouched(true);
                }}
                className="h-10 w-14 cursor-pointer rounded-md border border-input bg-card p-1"
              />
              <span className="text-xs text-muted-foreground">
                Usada nos gráficos e nas listas de apps.
              </span>
            </div>
          </div>
          {mutation.isError && (
            <p role="alert" className="text-sm text-destructive">
              {categoryErrorMessage(mutation.error)}
            </p>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
              Cancelar
            </Button>
            <Button type="submit" disabled={mutation.isPending || name.trim().length === 0}>
              {mutation.isPending ? "Salvando…" : "Salvar"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Exclusão com confirmação TEXTUAL - DELETE /categories/{id}. A operação é
 * destrutiva em cascata (remove os mapeamentos e reagrega 30 dias): o botão
 * só habilita quando o admin digita o nome EXATO da categoria.
 */
function DeleteCategoryDialog({
  category,
  onClose,
}: {
  category: CategoryItem;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [confirmation, setConfirmation] = useState("");
  const confirmed = confirmation.trim() === category.name;

  const mutation = useMutation({
    mutationFn: () =>
      api<void>(`/categories/${encodeURIComponent(category.id)}`, { method: "DELETE" }),
    onSuccess: async () => {
      await invalidateAppCategoryData(queryClient);
      onClose();
    },
  });

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !mutation.isPending) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Excluir categoria</DialogTitle>
          <DialogDescription>Excluir a categoria "{category.name}"?</DialogDescription>
        </DialogHeader>
        <div className="space-y-2 text-sm text-muted-foreground">
          <p>
            {category.app_count === 0
              ? "Nenhum app está mapeado nesta categoria."
              : category.app_count === 1
                ? "O app mapeado nesta categoria passa a Não categorizado."
                : `Os ${category.app_count} apps mapeados nesta categoria passam a Não categorizado.`}
          </p>
          <p>
            Os últimos 30 dias serão reagregados. Histórico anterior mantém a classificação
            antiga.
          </p>
        </div>
        <form
          className="space-y-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            if (confirmed && !mutation.isPending) mutation.mutate();
          }}
        >
          <Label htmlFor="categoria-excluir-confirmacao">
            Digite o nome da categoria para confirmar
          </Label>
          <Input
            id="categoria-excluir-confirmacao"
            value={confirmation}
            onChange={(e) => setConfirmation(e.target.value)}
            placeholder={category.name}
            autoComplete="off"
            autoFocus
          />
        </form>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {genericErrorMessage(mutation.error)}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>
            Cancelar
          </Button>
          <Button
            variant="destructive"
            onClick={() => mutation.mutate()}
            disabled={!confirmed || mutation.isPending}
          >
            {mutation.isPending ? "Excluindo…" : "Excluir categoria"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// -----------------------------------------------------------------------------
// Aba "Mapeamento de apps" - GET /app-catalog + recategorização em lote
// -----------------------------------------------------------------------------

interface BulkProgress {
  done: number;
  total: number;
}

function MapeamentoTab({ admin }: { admin: boolean }) {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState("");
  const [q, setQ] = useState("");
  const [onlyUncategorized, setOnlyUncategorized] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulkCategory, setBulkCategory] = useState("");
  const [bulk, setBulk] = useState<BulkProgress | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Debounce de 300ms na busca - evita uma consulta por tecla.
  useEffect(() => {
    const id = window.setTimeout(() => setQ(search.trim()), 300);
    return () => window.clearTimeout(id);
  }, [search]);

  const categoriesQuery = useQuery({
    queryKey: ["categories"],
    queryFn: () => api<CategoriesResponse>("/categories"),
  });
  const categories = categoriesQuery.data?.items ?? [];

  const catalogParams = useMemo(() => {
    const params = new URLSearchParams();
    if (q.length > 0) params.set("q", q);
    if (onlyUncategorized) params.set("uncategorized", "true");
    const s = params.toString();
    return s.length > 0 ? `?${s}` : "";
  }, [q, onlyUncategorized]);

  const catalogQuery = useQuery({
    queryKey: ["app-catalog", { q, uncategorized: onlyUncategorized }],
    queryFn: () => api<AppCatalogResponse>(`/app-catalog${catalogParams}`),
    placeholderData: (prev) => prev,
  });
  const data = catalogQuery.data;
  const items = useMemo(() => data?.items ?? [], [data]);

  // A seleção só pode conter apps visíveis (a lista muda com busca/refetch).
  useEffect(() => {
    setSelected((prev) => {
      if (prev.size === 0) return prev;
      const visible = new Set(items.map((i) => i.app_id));
      const next = new Set([...prev].filter((id) => visible.has(id)));
      return next.size === prev.size ? prev : next;
    });
  }, [items]);

  const hasFilters = q.length > 0 || onlyUncategorized;

  function toggleSelected(appId: string): void {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(appId)) next.delete(appId);
      else next.add(appId);
      return next;
    });
  }

  // Lote = N PUTs SEQUENCIAIS (sem endpoint de lote no contrato) com progresso
  // simples; falhas individuais não interrompem o restante. O PUT é declarativo:
  // reenviar o custom_display_name atual de cada app preserva o nome custom.
  async function applyBulk(): Promise<void> {
    const targets = items.filter((i) => selected.has(i.app_id));
    if (targets.length === 0) return;
    setError(null);
    setBulk({ done: 0, total: targets.length });
    let failed = 0;
    for (const item of targets) {
      try {
        await api<unknown>(`/app-catalog/${encodeURIComponent(item.app_id)}/category`, {
          method: "PUT",
          body: {
            category_id: bulkCategory === "" ? null : bulkCategory,
            custom_display_name: item.custom_display_name,
          },
        });
      } catch {
        failed += 1;
      }
      setBulk((b) => (b === null ? b : { ...b, done: b.done + 1 }));
    }
    await invalidateAppCategoryData(queryClient);
    setBulk(null);
    setSelected(new Set());
    if (failed > 0) {
      setError(
        failed === 1
          ? "Não foi possível aplicar a categoria em 1 app. Tente novamente."
          : `Não foi possível aplicar a categoria em ${failed} apps. Tente novamente.`,
      );
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="space-y-1.5">
            <CardTitle className="text-base">Mapeamento de apps</CardTitle>
            <CardDescription>
              Apps usados na organização nos últimos 30 dias, ordenados por tempo ativo.
              {!admin && " Somente administradores e proprietários editam."}
            </CardDescription>
          </div>
          {data !== undefined &&
            (data.uncategorized_count > 0 ? (
              <span className="inline-flex items-center gap-1.5 rounded-full border border-viz-improdutivo/40 bg-viz-improdutivo/10 px-2.5 py-0.5 text-xs text-viz-improdutivo">
                <Tags className="h-3.5 w-3.5 shrink-0" aria-hidden />
                {data.uncategorized_count === 1
                  ? "1 app sem categoria"
                  : `${data.uncategorized_count} apps sem categoria`}
              </span>
            ) : (
              <span className="rounded-full bg-secondary px-2.5 py-0.5 text-xs text-secondary-foreground">
                Nenhum app sem categoria
              </span>
            ))}
        </div>
      </CardHeader>

      {/* Barra de busca e filtro (controles h-9). */}
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b px-6 pb-4">
        <Input
          type="search"
          aria-label="Buscar app"
          placeholder="Buscar por nome ou processo"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="h-9 w-64"
        />
        <label className="flex cursor-pointer items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={onlyUncategorized}
            onChange={(e) => setOnlyUncategorized(e.target.checked)}
            className="h-4 w-4 accent-primary"
          />
          Somente não categorizados
        </label>
      </div>

      {admin && (
        <div
          role="note"
          className="flex items-start gap-2 border-b bg-viz-neutro/10 px-6 py-2.5 text-sm text-viz-neutro"
        >
          <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <span>
            A categoria vale para toda a organização e reagrega os últimos 30 dias. Histórico
            anterior mantém a classificação antiga.
          </span>
        </div>
      )}

      {error !== null && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 border-b bg-destructive/10 px-6 py-2.5 text-sm text-destructive"
        >
          <span>{error}</span>
          <Button variant="outline" size="sm" onClick={() => setError(null)}>
            Fechar
          </Button>
        </div>
      )}

      {/* Barra de lote: aparece com seleção; progresso durante os PUTs. */}
      {admin && selected.size > 0 && (
        <div className="flex flex-wrap items-center gap-3 border-b bg-muted/50 px-6 py-2.5 text-sm">
          <span className="tabular-nums">
            {selected.size === 1 ? "1 app selecionado" : `${selected.size} apps selecionados`}
          </span>
          <select
            aria-label="Categoria a aplicar"
            value={bulkCategory}
            onChange={(e) => setBulkCategory(e.target.value)}
            disabled={bulk !== null}
            className={cn(
              "h-8 rounded-md border border-input bg-card px-2 text-sm",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            )}
          >
            {/* a seedada "Não categorizado" sai da lista: equivale à option "" */}
            <option value="">{UNCATEGORIZED_LABEL}</option>
            {categories
              .filter((c) => c.name !== UNCATEGORIZED_LABEL)
              .map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
          </select>
          <Button
            size="sm"
            className="h-8"
            disabled={bulk !== null}
            onClick={() => {
              void applyBulk();
            }}
          >
            Aplicar categoria
          </Button>
          {bulk !== null && (
            <span role="status" className="tabular-nums text-muted-foreground">
              Aplicando {Math.min(bulk.done + 1, bulk.total)} de {bulk.total}…
            </span>
          )}
          <Button
            variant="ghost"
            size="sm"
            className="h-8"
            disabled={bulk !== null}
            onClick={() => setSelected(new Set())}
          >
            Limpar seleção
          </Button>
        </div>
      )}

      <div className="pb-0">
        {catalogQuery.isError && data === undefined ? (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(catalogQuery.error)}</p>
            <Button variant="outline" onClick={() => void catalogQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : (
          <div className={cn("overflow-x-auto", catalogQuery.isPlaceholderData && "opacity-70 transition-opacity")}>
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  {admin && (
                    <th scope="col" className="w-10 px-4 py-2">
                      <input
                        type="checkbox"
                        aria-label="Selecionar todos os apps visíveis"
                        checked={items.length > 0 && selected.size === items.length}
                        disabled={bulk !== null || items.length === 0}
                        onChange={(e) =>
                          setSelected(
                            e.target.checked ? new Set(items.map((i) => i.app_id)) : new Set(),
                          )
                        }
                        className="h-4 w-4 accent-primary"
                      />
                    </th>
                  )}
                  <th scope="col" className={cn("py-2", admin ? "px-2" : "px-6")}>App</th>
                  <th scope="col" className="px-3 py-2">Categoria</th>
                  <th scope="col" className="px-3 py-2 text-right">Tempo ativo (30 dias)</th>
                  <th scope="col" className="px-6 py-2 text-right">Dispositivos</th>
                </tr>
              </thead>
              <tbody>
                {data === undefined ? (
                  Array.from({ length: 6 }, (_, i) => (
                    <tr key={i} className="border-b last:border-b-0">
                      <td colSpan={admin ? 5 : 4} className="px-6 py-2">
                        <Skeleton className="h-8 w-full" />
                      </td>
                    </tr>
                  ))
                ) : items.length === 0 ? (
                  <tr>
                    <td colSpan={admin ? 5 : 4} className="px-6 py-10 text-center text-sm text-muted-foreground">
                      {hasFilters ? (
                        <span className="inline-flex flex-col items-center gap-2">
                          <span>Nenhum resultado</span>
                          <Button
                            variant="outline"
                            size="sm"
                            onClick={() => {
                              setSearch("");
                              setOnlyUncategorized(false);
                            }}
                          >
                            Limpar filtros
                          </Button>
                        </span>
                      ) : (
                        "Nenhum app no catálogo ainda. Os apps aparecem aqui conforme os agentes enviam dados."
                      )}
                    </td>
                  </tr>
                ) : (
                  items.map((item) => {
                    const name = item.custom_display_name ?? item.display_name;
                    return (
                      <tr key={item.app_id} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                        {admin && (
                          <td className="w-10 px-4 py-2">
                            <input
                              type="checkbox"
                              aria-label={`Selecionar ${name}`}
                              checked={selected.has(item.app_id)}
                              disabled={bulk !== null}
                              onChange={() => toggleSelected(item.app_id)}
                              className="h-4 w-4 accent-primary"
                            />
                          </td>
                        )}
                        <td className={cn("py-2", admin ? "px-2" : "px-6")}>
                          <p className="max-w-[20rem] truncate font-medium">{name}</p>
                          <p className="max-w-[20rem] truncate text-xs text-muted-foreground">
                            {item.process_name}
                          </p>
                        </td>
                        <td className="px-3 py-2">
                          {admin ? (
                            <CategoryInlineSelect
                              appId={item.app_id}
                              categoryId={item.category?.id ?? null}
                              categoryName={item.category?.name ?? null}
                              customDisplayName={item.custom_display_name}
                              categories={categories}
                              disabled={bulk !== null}
                              onError={() =>
                                setError("Não foi possível salvar a categoria. Tente novamente.")
                              }
                            />
                          ) : (
                            <span className="flex items-center gap-2">
                              <span
                                aria-hidden
                                className="h-2.5 w-2.5 shrink-0 rounded-full"
                                style={{
                                  backgroundColor:
                                    item.category?.color ??
                                    classificationColor(item.category?.classification ?? null),
                                }}
                              />
                              <span className="max-w-[14rem] truncate">
                                {item.category?.name ?? "Não categorizado"}
                              </span>
                            </span>
                          )}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                          {formatDuration(item.seconds_active_30d)}
                        </td>
                        <td className="whitespace-nowrap px-6 py-2 text-right tabular-nums">
                          {item.device_count_30d}
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        )}

        {/* Cap de 500 itens do contrato - a busca alcança o restante. */}
        {items.length >= 500 && (
          <p className="border-t px-6 py-3 text-xs text-muted-foreground">
            Mostrando os 500 apps com mais tempo ativo. Use a busca para encontrar os demais.
          </p>
        )}
      </div>
    </Card>
  );
}
