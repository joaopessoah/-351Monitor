// =============================================================================
// Configurações > Classificação (F3.3 + F6, Seção 8.7 e spec 07/09/2026): três
// abas internas (a ROTA continua /configuracoes/categorias - link salvo/aba
// renomeada em ConfiguracoesLayout.tsx não quebra).
// - "Categorias": vocabulário da organização (seletor Produtividade/Trabalho),
//   recálculo de histórico sob demanda e a tabela nome/classificação/cor/apps
//   mapeados com criar, editar (renomear/mudar classificação - reagrega 30
//   dias no backend) e excluir (confirmação textual: os apps mapeados viram
//   Não categorizado e os últimos 30 dias são reagregados).
// - "Mapeamento de apps": busca no catálogo do tenant (GET /app-catalog,
//   janela fixa de 30 dias, máx. 500 itens), contador de não categorizados,
//   select de categoria por linha e recategorização em LOTE (N PUTs
//   sequenciais com progresso simples).
// - Seletor de ESCOPO (F5, spec 2.4) nas abas "Mapeamento" e "Fila": a regra
//   pode ser da ORGANIZAÇÃO (padrão, o de sempre) ou de UMA EQUIPE, e a regra
//   da equipe vence a geral para as pessoas dela. Ausência de regra de equipe
//   é HERANÇA da geral (a linha mostra "herdado da organização"), nunca "sem
//   classificação" - a distinção é a diferença entre não decidir e decidir que
//   não conta. Sem nenhuma equipe cadastrada o seletor some e a tela é a de
//   antes, byte a byte.
// - "Fila de classificação" (F6, decisão 4): cobertura da classificação em %
//   + quanto falta em horas/apps para 95%, fila dos apps SEM categoria
//   ordenada por impacto (GET /app-catalog?sort=impacto), sugestão do
//   dicionário por linha e em lote com prévia obrigatória.
// Admin/owner editam; viewer é somente leitura. Rótulos de classificação vêm
// de lib/classification.ts (fixos OU no vocabulário da organização) - jamais
// os adjetivos vetados.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  CheckCircle2,
  History,
  Info,
  Lightbulb,
  Pencil,
  Plus,
  Tags,
  Trash2,
} from "lucide-react";
import { api, ApiError } from "@/lib/api";
import {
  CLASSIFICATION_FRAMING,
  classificationColor,
  classificationLabel,
  classificationVocabularyOf,
  UNCATEGORIZED_LABEL,
  VOCABULARY_OPTIONS,
} from "@/lib/classification";
import type { ClassificationVocabulary } from "@/lib/classification";
import { formatDuration } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { formatHours, formatPct } from "@/lib/period";
import { isAdmin } from "@/lib/roles";
import type {
  AppCatalogResponseF6,
  AppCatalogScopedResponse,
  CategoriesResponse,
  CategoryCreateRequest,
  CategoryItem,
  CategoryUpdateRequest,
  MeResponse,
  OrganizationVocabularyPatchRequest,
  ReaggregationRequest,
  ReaggregationResponse,
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
  useSetAppCategory,
} from "@/components/apps/CategoryInlineSelect";
import {
  appsToCloseGap,
  classificationCoverage,
  COVERAGE_TARGET,
  coverageGap,
} from "@/components/apps/classificationCoverage";
import { useApplyCategoryBatch } from "@/components/apps/useApplyCategoryBatch";
import {
  ClassificationScopeSelect,
  ORGANIZATION_SCOPE,
  ScopeBadge,
  ScopeNotice,
  scopeQuery,
  scopeTeamId,
  useTeamsQuery,
} from "@/components/apps/ClassificationScopeSelect";

// Classes do grupo segmentado (mesmo padrão da timeline/dashboard).
const segmentedButton = "rounded-[5px] px-3 text-xs font-medium transition-colors";
const segmentedOn = "bg-primary/10 text-primary";
const segmentedOff = "text-muted-foreground hover:bg-accent hover:text-accent-foreground";

const selectClass = cn(
  "h-10 w-full rounded-md border border-input bg-card px-3 text-sm",
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
);

/** Select compacto (largura automática, h-9) - Vocabulário e Recalcular histórico. */
const compactSelectClass = cn(
  "h-9 rounded-md border border-input bg-card px-3 text-sm",
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
);

/** 409 = nome duplicado no tenant; o resto cai na mensagem genérica. */
function categoryErrorMessage(err: unknown): string {
  if (err instanceof ApiError && err.status === 409) {
    return "Já existe uma categoria com esse nome.";
  }
  return genericErrorMessage(err);
}

type CategoriasTabId = "categorias" | "mapeamento" | "fila";

/** ?tab= inválido ou ausente cai em "categorias" (o default de sempre, sem query na URL). */
function parseTab(raw: string | null): CategoriasTabId {
  return raw === "mapeamento" || raw === "fila" ? raw : "categorias";
}

export function CategoriasPage() {
  // Tab na URL (não só em estado local): a AppsPage linka direto para
  // ?tab=fila, e a rota base /configuracoes/categorias continua abrindo em
  // "Categorias" - link salvo/compartilhado de antes da F6 não muda de aba.
  const [searchParams, setSearchParams] = useSearchParams();
  const tab = parseTab(searchParams.get("tab"));

  function setTab(next: CategoriasTabId): void {
    setSearchParams(
      (prev) => {
        const params = new URLSearchParams(prev);
        if (next === "categorias") params.delete("tab");
        else params.set("tab", next);
        return params;
      },
      { replace: true },
    );
  }

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
        aria-label="Seções de classificação"
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
        <button
          type="button"
          aria-pressed={tab === "fila"}
          onClick={() => setTab("fila")}
          className={cn(segmentedButton, tab === "fila" ? segmentedOn : segmentedOff)}
        >
          Fila de classificação
        </button>
      </div>

      {tab === "categorias" ? (
        <CategoriasTab admin={admin} me={meQuery.data} />
      ) : tab === "mapeamento" ? (
        <MapeamentoTab admin={admin} />
      ) : (
        <FilaTab admin={admin} />
      )}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Aba "Categorias" - CRUD
// -----------------------------------------------------------------------------

function CategoriasTab({ admin, me }: { admin: boolean; me: MeResponse | undefined }) {
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
      {/* F6 (decisões 1 e 7): vocabulário da organização e recálculo de
          histórico são configuração GLOBAL da classificação - vivem acima do
          CRUD de categorias em vez de dentro dele. */}
      <VocabularyCard me={me} admin={admin} />
      <ReaggregationCard admin={admin} />

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
  // F5 - escopo da regra; "" = organização (padrão de sempre)
  const [teamId, setTeamId] = useState<string>(ORGANIZATION_SCOPE);
  const teamsQuery = useTeamsQuery();
  const teamName = teamsQuery.data?.items.find((t) => t.id === teamId)?.name;

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
    // o escopo entra pela mesma função das outras telas (scopeQuery) para o
    // parâmetro nunca ser montado de dois jeitos diferentes
    return `?${params.toString()}${scopeQuery(teamId)}`;
  }, [q, onlyUncategorized, teamId]);

  const catalogQuery = useQuery({
    queryKey: ["app-catalog", { q, uncategorized: onlyUncategorized, teamId }],
    queryFn: () => api<AppCatalogScopedResponse>(`/app-catalog${catalogParams}`),
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
            // F5 - o lote respeita o escopo selecionado na barra acima
            team_id: scopeTeamId(teamId),
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
        {/* F5 - escopo da regra (some quando não há equipe cadastrada) */}
        <ClassificationScopeSelect value={teamId} onChange={setTeamId} disabled={bulk !== null} />
      </div>

      {admin && (
        <div
          role="note"
          className="flex items-start gap-2 border-b bg-viz-neutro/10 px-6 py-2.5 text-sm text-viz-neutro"
        >
          <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <span>
            <ScopeNotice teamId={teamId} teamName={teamName} /> Histórico anterior aos 30 dias
            mantém a classificação antiga.
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
                            <span className="flex flex-wrap items-center gap-2">
                              <CategoryInlineSelect
                                appId={item.app_id}
                                categoryId={item.category?.id ?? null}
                                categoryName={item.category?.name ?? null}
                                customDisplayName={item.custom_display_name}
                                categories={categories}
                                disabled={bulk !== null}
                                teamId={scopeTeamId(teamId)}
                                onError={() =>
                                  setError("Não foi possível salvar a categoria. Tente novamente.")
                                }
                              />
                              {/* diz quando a categoria exibida veio da regra GERAL */}
                              <ScopeBadge scope={item.category_scope} teamId={teamId} />
                            </span>
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

// -----------------------------------------------------------------------------
// Vocabulário da organização (F6, decisão 1) - PATCH /organization
// -----------------------------------------------------------------------------

/**
 * Seletor Produtividade/Trabalho: os DADOS por trás são os mesmos nos dois
 * conjuntos (classification +1/0/-1 e o balde sem classificação) - só o
 * RÓTULO muda, e o backend não reagrega nada nesta troca (é rotulagem pura).
 * Lê o vigente do /me (classificationVocabularyOf) em vez de um GET /organization
 * à parte: o /me já é a query de sessão do portal, toda tela que mostra
 * classificação depende dela.
 */
function VocabularyCard({ me, admin }: { me: MeResponse | undefined; admin: boolean }) {
  const queryClient = useQueryClient();
  const current = classificationVocabularyOf(me);
  const [pending, setPending] = useState<ClassificationVocabulary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [justSaved, setJustSaved] = useState(false);

  const mutation = useMutation({
    mutationFn: (vocabulary: ClassificationVocabulary) => {
      const body: OrganizationVocabularyPatchRequest = { classification_vocabulary: vocabulary };
      return api<unknown>("/organization", { method: "PATCH", body });
    },
    onSuccess: async () => {
      // /me carrega o rótulo em toda tela que mostra classificação; /organization
      // é lido pela aba Organização se o admin passar por lá na mesma sessão.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["me"] }),
        queryClient.invalidateQueries({ queryKey: ["organization"] }),
      ]);
      setPending(null);
      setJustSaved(true);
    },
    onError: (err) => {
      setPending(null);
      setError(genericErrorMessage(err));
    },
  });

  function choose(vocabulary: ClassificationVocabulary): void {
    if (!admin || vocabulary === current || mutation.isPending) return;
    setError(null);
    setJustSaved(false);
    setPending(vocabulary);
    mutation.mutate(vocabulary);
  }

  const displayed = pending ?? current;

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-base">Vocabulário de classificação</CardTitle>
        <CardDescription>
          Como os rótulos aparecem em toda a interface. Os dados por trás são os mesmos nos dois
          conjuntos - trocar não reagrega nada, só o texto muda.
          {!admin && " Somente administradores e proprietários editam."}
        </CardDescription>
      </CardHeader>
      <div className="space-y-3 px-6 pb-6">
        <div className="grid gap-3 sm:grid-cols-2">
          {VOCABULARY_OPTIONS.map((opt) => {
            const selected = displayed === opt.value;
            return (
              <button
                key={opt.value}
                type="button"
                aria-pressed={selected}
                disabled={!admin || mutation.isPending}
                onClick={() => choose(opt.value)}
                className={cn(
                  "rounded-md border px-4 py-3 text-left text-sm transition-colors",
                  selected ? "border-primary bg-primary/5" : "border-input bg-card",
                  admin && "hover:border-primary/60",
                  "disabled:cursor-not-allowed",
                  mutation.isPending && pending === opt.value && "opacity-70",
                  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
                )}
              >
                <span className="flex items-center gap-2 font-medium">
                  <span
                    aria-hidden
                    className={cn(
                      "h-3.5 w-3.5 shrink-0 rounded-full border-2",
                      selected ? "border-primary bg-primary" : "border-input",
                    )}
                  />
                  {opt.label}
                </span>
                <span className="mt-1 block text-xs text-muted-foreground">{opt.sample}</span>
              </button>
            );
          })}
        </div>
        <p className="flex items-start gap-2 text-xs text-muted-foreground">
          <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
          <span>Enquadramento fixo ao lado dos rótulos, em toda tela: "{CLASSIFICATION_FRAMING}".</span>
        </p>
        {error !== null && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        {justSaved && error === null && (
          <p role="status" className="text-sm text-viz-produtivo">
            Vocabulário atualizado.
          </p>
        )}
      </div>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Recalcular histórico (F6, decisão 7) - POST /reaggregation
// -----------------------------------------------------------------------------

/** Janelas oferecidas pela tela - todas dentro do teto de 366 dias do backend. */
const REAGGREGATION_OPTIONS = [30, 90, 180, 365] as const;

function reaggregationLabel(days: number): string {
  return days === 365 ? "365 dias (12 meses)" : `${days} dias`;
}

/**
 * Ação de custo alto (spec Seção 2.4/decisão 7): reaplica a classificação
 * VIGENTE sobre um período passado, além dos 30 dias que a curadoria do dia a
 * dia já reagrega sozinha. Só Admin+ - a tela nem renderiza o card para Viewer,
 * não há nada para o Viewer observar aqui (é ação, não configuração salva).
 */
function ReaggregationCard({ admin }: { admin: boolean }) {
  const [days, setDays] = useState<(typeof REAGGREGATION_OPTIONS)[number]>(30);
  const [result, setResult] = useState<ReaggregationResponse | null>(null);

  const mutation = useMutation({
    mutationFn: () => {
      const body: ReaggregationRequest = { days };
      return api<ReaggregationResponse>("/reaggregation", { method: "POST", body });
    },
    onSuccess: (response) => setResult(response),
  });

  if (!admin) return null;

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-base">Recalcular histórico</CardTitle>
        <CardDescription>
          Reaplica a classificação vigente (categorias e mapeamentos de hoje) sobre um período
          passado. Útil depois de reorganizar categorias: o dia a dia só reagrega automaticamente
          os últimos 30 dias.
        </CardDescription>
      </CardHeader>
      <div className="space-y-3 px-6 pb-6">
        <div className="flex flex-wrap items-center gap-3">
          <Label htmlFor="reaggregation-days" className="text-sm">
            Janela
          </Label>
          <select
            id="reaggregation-days"
            value={days}
            onChange={(e) => {
              setDays(Number(e.target.value) as (typeof REAGGREGATION_OPTIONS)[number]);
              setResult(null);
            }}
            disabled={mutation.isPending}
            className={cn(compactSelectClass, "w-auto")}
          >
            {REAGGREGATION_OPTIONS.map((d) => (
              <option key={d} value={d}>
                {reaggregationLabel(d)}
              </option>
            ))}
          </select>
          <Button
            size="sm"
            className="h-9"
            disabled={mutation.isPending}
            onClick={() => {
              setResult(null);
              mutation.mutate();
            }}
          >
            <History className="h-4 w-4" aria-hidden />
            {mutation.isPending ? "Enfileirando…" : "Recalcular histórico"}
          </Button>
        </div>
        <div
          role="note"
          className="flex items-start gap-2 rounded-md border border-viz-neutro/30 bg-viz-neutro/10 px-3 py-2 text-xs text-viz-neutro"
        >
          <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
          <span>
            Operação assíncrona: o servidor drena a fila em ciclos de 15 minutos, e numa frota
            grande pode levar horas até terminar. O portal continua utilizável enquanto isso.
          </span>
        </div>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {genericErrorMessage(mutation.error)}
          </p>
        )}
        {result !== null && (
          <p role="status" className="text-sm text-viz-produtivo">
            {result.enqueued === 0
              ? `Nenhum par (dispositivo, dia) pendente nos últimos ${result.days} dias - o histórico já reflete a classificação vigente.`
              : `${result.enqueued} pares (dispositivo, dia) enfileirados para reagregação nos últimos ${result.days} dias.`}
          </p>
        )}
      </div>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Aba "Fila de classificação" (F6, decisão 4) - GET /app-catalog?sort=impacto
// -----------------------------------------------------------------------------

function FilaTab({ admin }: { admin: boolean }) {
  const categoriesQuery = useQuery({
    queryKey: ["categories"],
    queryFn: () => api<CategoriesResponse>("/categories"),
  });
  const categories = categoriesQuery.data?.items ?? [];

  // F5 - a fila também tem escopo: com uma equipe selecionada ela lista o que
  // AQUELA equipe ainda não tem coberto (por regra própria OU herdada), e a
  // cobertura do cabeçalho segue a mesma régua efetiva.
  const [teamId, setTeamId] = useState<string>(ORGANIZATION_SCOPE);
  const teamsQuery = useTeamsQuery();
  const teamName = teamsQuery.data?.items.find((t) => t.id === teamId)?.name;

  // Mesma janela de 30 dias da Mapeamento de apps, mas ordenada por IMPACTO
  // (sem categoria primeiro, por tempo ativo desc) - é a fila em si.
  const catalogQuery = useQuery({
    queryKey: ["app-catalog", { uncategorized: true, sort: "impacto", q: "", teamId }],
    queryFn: () =>
      api<AppCatalogResponseF6>(`/app-catalog?uncategorized=true&sort=impacto${scopeQuery(teamId)}`),
    staleTime: 60_000,
  });
  const data = catalogQuery.data;
  const items = useMemo(() => data?.items ?? [], [data]);

  const coverage = useMemo(
    () => (data === undefined ? null : classificationCoverage(data.total_seconds_active, data.uncategorized_seconds_active)),
    [data],
  );
  const gap = useMemo(
    () => (data === undefined ? null : coverageGap(data.total_seconds_active, data.uncategorized_seconds_active)),
    [data],
  );
  // appsToCloseGap assume a fila JÁ ordenada por impacto (garantido pelo sort=impacto acima).
  const appsGap = useMemo(() => (gap === null ? null : appsToCloseGap(items, gap.secondsToGo)), [items, gap]);

  // Sugestão do dicionário por app - mesmo casamento nome->id da AppsPage
  // (default_category é um NOME canônico; o lote e o PUT individual precisam de id).
  const categoryIdByName = useMemo(() => {
    const byName = new Map<string, string>();
    for (const c of categories) if (c.name !== UNCATEGORIZED_LABEL) byName.set(c.name, c.id);
    return byName;
  }, [categories]);

  const suggestionByApp = useMemo(() => {
    const map = new Map<string, { categoryId: string; categoryName: string }>();
    for (const item of items) {
      if (item.default_category === null) continue;
      const categoryId = categoryIdByName.get(item.default_category);
      if (categoryId === undefined) continue;
      map.set(item.app_id, { categoryId, categoryName: item.default_category });
    }
    return map;
  }, [items, categoryIdByName]);

  const [rowError, setRowError] = useState<string | null>(null);
  const [previewOpen, setPreviewOpen] = useState(false);
  const [appliedCount, setAppliedCount] = useState<number | null>(null);
  // "Ver mesmo assim" no estado vazio (cobertura já alta, mas ainda há sobras de baixo impacto).
  const [forceShow, setForceShow] = useState(false);

  const applyOne = useSetAppCategory(() =>
    setRowError("Não foi possível aplicar a categoria. Tente novamente."),
  );
  const batch = useApplyCategoryBatch();

  // Candidatos ao lote: todo item da fila com sugestão casada (mesma trava da
  // AppsPage - só o que a organização ainda NÃO categorizou entra, e aqui TODOS
  // os itens já são "sem categoria" por definição do filtro uncategorized=true).
  const previewItems = useMemo(() => items.filter((i) => suggestionByApp.has(i.app_id)), [items, suggestionByApp]);

  const suggestionGroups = useMemo(() => {
    const counts = new Map<string, number>();
    for (const i of previewItems) {
      const s = suggestionByApp.get(i.app_id)!;
      counts.set(s.categoryName, (counts.get(s.categoryName) ?? 0) + 1);
    }
    return [...counts.entries()]
      .map(([name, count]) => ({ name, count }))
      .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name, "pt-BR"));
  }, [previewItems, suggestionByApp]);

  async function handleApplyBatch(): Promise<void> {
    try {
      const applied = await batch.apply(
        previewItems.map((i) => ({ appId: i.app_id, categoryId: suggestionByApp.get(i.app_id)!.categoryId })),
        scopeTeamId(teamId),
      );
      setPreviewOpen(false);
      setAppliedCount(applied);
    } catch {
      // erro já fica em batch.error - o diálogo continua aberto para o admin ver e tentar de novo.
    }
  }

  const failed = catalogQuery.isError && data === undefined;
  const targetPct = Math.round(COVERAGE_TARGET * 100);

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader className="pb-3">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="space-y-1.5">
              <CardTitle className="text-base">Cobertura da classificação</CardTitle>
              <CardDescription>
                Últimos 30 dias · quanto do tempo ativo está em apps com categoria
                {teamId === ORGANIZATION_SCOPE ? "." : ` para ${teamName ?? "a equipe"}.`}
              </CardDescription>
            </div>
            {/* F5 - a fila herda o mesmo seletor de escopo do Mapeamento */}
            <ClassificationScopeSelect
              value={teamId}
              onChange={setTeamId}
              disabled={batch.progress !== null}
            />
          </div>
        </CardHeader>
        <div className="px-6 pb-6">
          {failed ? (
            <div className="flex flex-col items-center gap-3 py-6 text-center">
              <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
              <p className="text-sm text-muted-foreground">{genericErrorMessage(catalogQuery.error)}</p>
              <Button variant="outline" onClick={() => void catalogQuery.refetch()}>
                Tentar novamente
              </Button>
            </div>
          ) : data === undefined ? (
            <div className="space-y-3">
              <Skeleton className="h-8 w-32" />
              <Skeleton className="h-2 w-full max-w-md" />
              <Skeleton className="h-4 w-64" />
            </div>
          ) : (
            <div className="space-y-3">
              <div className="flex items-baseline gap-3">
                <span className="text-3xl font-semibold tabular-nums">{formatPct(coverage)}</span>
                <span className="text-sm text-muted-foreground">cobertos nos últimos 30 dias</span>
              </div>
              <span aria-hidden className="block h-2 w-full max-w-md overflow-hidden rounded-full bg-secondary">
                <span
                  className="block h-full rounded-full bg-viz-produtivo"
                  style={{ width: coverage !== null ? `${Math.round(coverage * 100)}%` : "0%" }}
                />
              </span>
              {gap !== null && !gap.metTarget && appsGap !== null && (
                <p className="text-sm text-muted-foreground">
                  Faltam classificar{" "}
                  <strong className="font-semibold text-foreground">
                    {appsGap.exact ? "" : "pelo menos "}
                    {appsGap.count === 1 ? "1 app" : `${appsGap.count} apps`}
                  </strong>{" "}
                  (~{formatHours(gap.secondsToGo)} h) para chegar a {targetPct}% de cobertura.
                </p>
              )}
            </div>
          )}
        </div>
      </Card>

      {rowError !== null && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          <span>{rowError}</span>
          <Button variant="outline" size="sm" onClick={() => setRowError(null)}>
            Fechar
          </Button>
        </div>
      )}

      {appliedCount !== null && (
        <div
          role="status"
          className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-viz-produtivo/30 bg-viz-produtivo/10 px-3 py-2 text-sm text-viz-produtivo"
        >
          <span>{appliedCount === 1 ? "1 app categorizado." : `${appliedCount} apps categorizados.`}</span>
          <Button variant="outline" size="sm" onClick={() => setAppliedCount(null)}>
            Fechar
          </Button>
        </div>
      )}

      {data !== undefined && gap !== null && gap.metTarget && (
        // Estado vazio (Seção 8.9): cobertura já alta - desenhado, não só "lista vazia".
        <Card>
          <div className="flex flex-col items-center gap-3 px-6 py-10 text-center">
            <span className="flex h-12 w-12 items-center justify-center rounded-full bg-viz-produtivo/10">
              <CheckCircle2 className="h-6 w-6 text-viz-produtivo" aria-hidden />
            </span>
            <p className="text-base font-medium">Cobertura acima da meta</p>
            <p className="max-w-md text-sm text-muted-foreground">
              {formatPct(coverage)} do tempo ativo dos últimos 30 dias já está em apps com
              categoria - acima da meta de {targetPct}%.
            </p>
            {items.length > 0 && !forceShow && (
              <Button variant="outline" size="sm" onClick={() => setForceShow(true)}>
                {items.length === 1
                  ? "Ver mesmo assim o 1 app sem categoria"
                  : `Ver mesmo assim os ${items.length} apps sem categoria`}
              </Button>
            )}
          </div>
        </Card>
      )}

      {data !== undefined && (gap === null || !gap.metTarget || forceShow) && (
        <>
          {admin && previewItems.length > 0 && (
            <div className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-viz-neutro/30 bg-viz-neutro/10 px-3 py-2 text-sm">
              <span className="flex items-start gap-2 text-viz-neutro">
                <Lightbulb className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
                <span>
                  O dicionário sugere categoria para{" "}
                  <strong className="font-semibold tabular-nums">
                    {previewItems.length === 1 ? "1 app" : `${previewItems.length} apps`}
                  </strong>{" "}
                  desta fila. Nada é aplicado sem a sua confirmação.
                </span>
              </span>
              <Button variant="outline" size="sm" className="h-8" onClick={() => setPreviewOpen(true)}>
                Ver sugestões
              </Button>
            </div>
          )}

          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-base">Apps sem categoria</CardTitle>
              <CardDescription>Ordenado por horas de impacto nos últimos 30 dias.</CardDescription>
            </CardHeader>
            <div className="pb-0">
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                      <th scope="col" className={cn("py-2", admin ? "px-2" : "px-6")}>
                        App
                      </th>
                      {admin && (
                        <th scope="col" className="px-3 py-2">
                          Categoria
                        </th>
                      )}
                      <th scope="col" className="px-3 py-2 text-right">
                        Impacto (30 dias)
                      </th>
                      <th scope="col" className="px-6 py-2 text-right">
                        Dispositivos
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {items.length === 0 ? (
                      <tr>
                        <td colSpan={admin ? 4 : 3} className="px-6 py-10 text-center text-sm text-muted-foreground">
                          Nenhum app sem categoria.
                        </td>
                      </tr>
                    ) : (
                      items.map((item) => {
                        const name = item.custom_display_name ?? item.display_name;
                        const suggestion = suggestionByApp.get(item.app_id) ?? null;
                        return (
                          <tr key={item.app_id} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                            <td className={cn("py-2", admin ? "px-2" : "px-6")}>
                              <p className="max-w-[20rem] truncate font-medium">{name}</p>
                              <p className="max-w-[20rem] truncate text-xs text-muted-foreground">
                                {item.process_name}
                              </p>
                            </td>
                            {admin && (
                              <td className="px-3 py-2">
                                <div className="space-y-1">
                                  <CategoryInlineSelect
                                    appId={item.app_id}
                                    categoryId={null}
                                    categoryName={null}
                                    customDisplayName={item.custom_display_name}
                                    categories={categories}
                                    disabled={batch.progress !== null}
                                    teamId={scopeTeamId(teamId)}
                                    onError={() =>
                                      setRowError("Não foi possível salvar a categoria. Tente novamente.")
                                    }
                                  />
                                  {suggestion !== null && (
                                    <button
                                      type="button"
                                      disabled={batch.progress !== null || applyOne.isPending}
                                      title={`Aplicar a categoria ${suggestion.categoryName} a este app`}
                                      onClick={() =>
                                        applyOne.mutate({
                                          appId: item.app_id,
                                          categoryId: suggestion.categoryId,
                                          customDisplayName: item.custom_display_name,
                                          teamId: scopeTeamId(teamId),
                                        })
                                      }
                                      className={cn(
                                        "inline-flex max-w-full items-center gap-1 rounded-sm text-left text-xs text-muted-foreground",
                                        "transition-colors hover:text-viz-neutro focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                                        "disabled:cursor-not-allowed disabled:opacity-50",
                                      )}
                                    >
                                      <Lightbulb className="h-3 w-3 shrink-0" aria-hidden />
                                      <span className="truncate">Sugestão: {suggestion.categoryName} · aplicar</span>
                                    </button>
                                  )}
                                </div>
                              </td>
                            )}
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
              {/* Cap de 500 itens do contrato (mesmo teto da Mapeamento de apps). */}
              {items.length >= 500 && (
                <p className="border-t px-6 py-3 text-xs text-muted-foreground">
                  Mostrando os 500 apps sem categoria com mais impacto.
                </p>
              )}
            </div>
          </Card>
        </>
      )}

      {/* Prévia obrigatória do lote - mesmo padrão da AppsPage (nada aplica sem confirmação). */}
      <Dialog
        open={previewOpen}
        onOpenChange={(open) => {
          if (!open && batch.progress === null) setPreviewOpen(false);
        }}
      >
        <DialogContent className="max-w-xl">
          <DialogHeader>
            <DialogTitle className="pr-8">Sugestões do dicionário de apps</DialogTitle>
            <DialogDescription>
              {previewItems.length === 1
                ? "1 app sem categoria receberá a categoria abaixo."
                : `${previewItems.length} apps sem categoria receberão as categorias abaixo.`}{" "}
              A categoria vale para toda a organização e reagrega os últimos 30 dias.
            </DialogDescription>
          </DialogHeader>

          <div className="flex flex-wrap gap-1.5">
            {suggestionGroups.map((g) => (
              <span
                key={g.name}
                className="inline-flex items-center gap-1.5 rounded-full bg-secondary px-2.5 py-0.5 text-xs text-secondary-foreground"
              >
                {g.name}
                <span className="tabular-nums text-muted-foreground">{g.count}</span>
              </span>
            ))}
          </div>

          <div className="max-h-64 overflow-y-auto rounded-md border">
            <table className="w-full text-sm">
              <thead className="sticky top-0 bg-card">
                <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="px-3 py-2">
                    App
                  </th>
                  <th scope="col" className="px-3 py-2">
                    Categoria sugerida
                  </th>
                </tr>
              </thead>
              <tbody>
                {previewItems.map((i) => {
                  const s = suggestionByApp.get(i.app_id)!;
                  return (
                    <tr key={i.app_id} className="border-b last:border-b-0">
                      <td className="px-3 py-1.5">
                        <span className="block max-w-[16rem] truncate">
                          {i.custom_display_name ?? i.display_name}
                        </span>
                        <span className="block max-w-[16rem] truncate text-xs text-muted-foreground">
                          {i.process_name}
                        </span>
                      </td>
                      <td className="px-3 py-1.5">{s.categoryName}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {batch.error !== null && (
            <p role="alert" className="text-sm text-destructive">
              {batch.error}
            </p>
          )}

          <DialogFooter>
            {batch.progress !== null && (
              <span className="mr-auto self-center text-xs tabular-nums text-muted-foreground">
                Aplicando {batch.progress.done} de {batch.progress.total}…
              </span>
            )}
            <Button variant="outline" disabled={batch.progress !== null} onClick={() => setPreviewOpen(false)}>
              Cancelar
            </Button>
            <Button
              disabled={batch.progress !== null || previewItems.length === 0}
              onClick={() => {
                void handleApplyBatch();
              }}
            >
              {batch.progress !== null
                ? "Aplicando…"
                : previewItems.length === 1
                  ? "Aplicar sugestão em 1 app"
                  : `Aplicar sugestões em ${previewItems.length} apps`}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
