// =============================================================================
// Busca Global da topbar (Ctrl+K/⌘K) - abre um diálogo com resultados
// agrupados em Pessoas, Aplicativos, Dispositivos e Telas. As três buscas
// remotas só disparam a partir de 2 caracteres e com debounce de 300 ms; a
// seção Telas é local e estática (searchScreens.ts), então continua
// respondendo mesmo se a rede falhar - é o "menu de atalhos" que aparece
// assim que o diálogo abre, antes de qualquer digitação.
//
// Segue o mesmo padrão do drawer mobile do AppShell: DialogPrimitive cru (não
// o wrapper genérico de components/ui/dialog.tsx, que é o modal centralizado
// de formulário) - aqui o conteúdo é ancorado perto do topo, como uma paleta
// de comandos.
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent as ReactKeyboardEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import {
  AppWindow,
  ArrowDown,
  ArrowUp,
  BarChart3,
  CalendarClock,
  CornerDownLeft,
  LayoutDashboard,
  Loader2,
  MonitorSmartphone,
  Search,
  Settings,
  ShieldCheck,
  Users,
  X,
  type LucideIcon,
} from "lucide-react";
import { api } from "@/lib/api";
import type { MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Skeleton } from "@/components/ui/skeleton";
import { filterScreens } from "./searchScreens";
import { MIN_QUERY_LENGTH, useGlobalSearchResults } from "./useGlobalSearchResults";

/** Mesmo ícone da navegação lateral (AppShell), pra a tela ficar reconhecível na busca. */
const SCREEN_ICONS: Record<string, LucideIcon> = {
  "visao-geral": LayoutDashboard,
  "linha-do-tempo": CalendarClock,
  colaboradores: Users,
  relatorios: BarChart3,
  dispositivos: MonitorSmartphone,
  configuracoes: Settings,
  transparencia: ShieldCheck,
};

const DEBOUNCE_MS = 300;

type SearchGroup = "Pessoas" | "Aplicativos" | "Dispositivos" | "Telas";

const GROUP_ORDER: SearchGroup[] = ["Pessoas", "Aplicativos", "Dispositivos", "Telas"];

interface FlatItem {
  id: string;
  group: SearchGroup;
  label: string;
  sublabel?: string;
  to: string;
  icon: LucideIcon;
}

/** Debounce de 300 ms: só propaga o valor depois da pessoa parar de digitar,
    no mesmo espírito da busca com filtro no servidor já usada noutras telas. */
function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(timer);
  }, [value, delayMs]);
  return debounced;
}

export function GlobalSearch() {
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [term, setTerm] = useState("");
  const [activeIndex, setActiveIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  const debouncedTerm = useDebouncedValue(term, DEBOUNCE_MS);

  // Mesma queryKey/queryFn do AppShell: resolve do cache do TanStack Query,
  // sem disparar uma segunda requisição a /me.
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const transparenciaSlug = meQuery.data?.organization.slug ?? "";

  const searchQuery = useGlobalSearchResults(debouncedTerm, timezone);
  const showRemote = debouncedTerm.trim().length >= MIN_QUERY_LENGTH;
  const loading = showRemote && searchQuery.isLoading;

  const screens = useMemo(() => filterScreens(term, transparenciaSlug), [term, transparenciaSlug]);

  const flatItems = useMemo<FlatItem[]>(() => {
    const items: FlatItem[] = [];
    const data = showRemote ? searchQuery.data : undefined;

    for (const p of data?.people ?? []) {
      items.push({
        id: `people-${p.sid}`,
        group: "Pessoas",
        label: p.name,
        sublabel: p.teams.length > 0 ? p.teams.join(", ") : undefined,
        to: `/colaboradores?q=${encodeURIComponent(p.name)}`,
        icon: Users,
      });
    }
    for (const a of data?.apps ?? []) {
      items.push({
        id: `app-${a.id}`,
        group: "Aplicativos",
        label: a.name,
        to: `/apps?q=${encodeURIComponent(a.name)}`,
        icon: AppWindow,
      });
    }
    for (const d of data?.devices ?? []) {
      items.push({
        id: `device-${d.id}`,
        group: "Dispositivos",
        label: d.name,
        to: `/dispositivos?q=${encodeURIComponent(d.name)}`,
        icon: MonitorSmartphone,
      });
    }
    for (const s of screens) {
      items.push({
        id: `screen-${s.key}`,
        group: "Telas",
        label: s.label,
        to: s.to,
        icon: SCREEN_ICONS[s.key] ?? LayoutDashboard,
      });
    }
    return items;
  }, [showRemote, searchQuery.data, screens]);

  const grouped = useMemo(() => {
    const map = new Map<SearchGroup, FlatItem[]>();
    for (const item of flatItems) {
      const list = map.get(item.group) ?? [];
      list.push(item);
      map.set(item.group, list);
    }
    return map;
  }, [flatItems]);

  // Reabrir o diálogo ou trocar de termo sempre volta o destaque pro topo.
  useEffect(() => {
    setActiveIndex(0);
  }, [debouncedTerm, open]);

  const clampedIndex = flatItems.length === 0 ? -1 : Math.min(activeIndex, flatItems.length - 1);
  const activeItem = clampedIndex >= 0 ? flatItems[clampedIndex] : undefined;

  // Mantém o item em destaque visível ao navegar pelas setas.
  useEffect(() => {
    if (activeItem === undefined) return;
    const el = listRef.current?.querySelector(`[data-key="${CSS.escape(activeItem.id)}"]`);
    el?.scrollIntoView({ block: "nearest" });
  }, [activeItem]);

  // Atalho global Ctrl+K / ⌘K - ignora quando o foco já está num campo de
  // formulário, pra não roubar o atalho de um input que esteja usando.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent): void {
      if (e.key.toLowerCase() !== "k" || !(e.ctrlKey || e.metaKey)) return;
      const target = e.target as HTMLElement | null;
      const tag = target?.tagName;
      const isFormField =
        tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || target?.isContentEditable === true;
      if (isFormField) return;
      e.preventDefault();
      setOpen(true);
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, []);

  function handleOpenChange(next: boolean): void {
    setOpen(next);
    if (!next) setTerm("");
  }

  function activate(item: FlatItem | undefined): void {
    if (item === undefined) return;
    setOpen(false);
    setTerm("");
    navigate(item.to);
  }

  function handleInputKeyDown(e: ReactKeyboardEvent<HTMLInputElement>): void {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActiveIndex((i) => Math.min(i + 1, flatItems.length - 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActiveIndex((i) => Math.max(i - 1, 0));
    } else if (e.key === "Enter") {
      e.preventDefault();
      activate(activeItem);
    }
  }

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label="Buscar pessoa, equipe ou app"
        className={cn(
          "ml-1 flex min-w-0 items-center gap-2 rounded-md border border-input bg-card px-2.5 py-1.5",
          "text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground sm:text-sm",
        )}
      >
        <Search className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        <span className="hidden truncate sm:inline">Buscar pessoa, equipe ou app</span>
        <kbd className="ml-auto hidden shrink-0 rounded border border-input bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground md:inline">
          Ctrl+K
        </kbd>
      </button>

      <DialogPrimitive.Root open={open} onOpenChange={handleOpenChange}>
        <DialogPrimitive.Portal>
          <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
          <DialogPrimitive.Content
            aria-describedby={undefined}
            onOpenAutoFocus={(e) => {
              e.preventDefault();
              inputRef.current?.focus();
            }}
            className="fixed left-1/2 top-24 z-50 w-full max-w-lg -translate-x-1/2 overflow-hidden rounded-lg border bg-card shadow-lg outline-none"
          >
            <DialogPrimitive.Title className="sr-only">Busca global</DialogPrimitive.Title>
            <div className="flex items-center gap-2 border-b px-3">
              <Search className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
              <input
                ref={inputRef}
                value={term}
                onChange={(e) => setTerm(e.target.value)}
                onKeyDown={handleInputKeyDown}
                placeholder="Buscar pessoa, equipe ou app"
                aria-label="Buscar pessoa, equipe ou app"
                role="combobox"
                aria-expanded={open}
                aria-controls="global-search-listbox"
                aria-activedescendant={activeItem?.id}
                aria-autocomplete="list"
                autoComplete="off"
                spellCheck={false}
                className="h-12 flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
              />
              {loading && <Loader2 className="h-4 w-4 shrink-0 animate-spin text-muted-foreground" aria-hidden="true" />}
              <DialogPrimitive.Close
                aria-label="Fechar busca"
                className="shrink-0 rounded-sm p-1 text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <X className="h-4 w-4" aria-hidden="true" />
              </DialogPrimitive.Close>
            </div>

            <div
              ref={listRef}
              role="listbox"
              id="global-search-listbox"
              aria-label="Resultados da busca"
              className="max-h-[60vh] overflow-y-auto p-2"
            >
              {/* Esqueleto de carregamento das seções remotas - Telas continua
                  aparecendo embaixo, porque não depende dessa resposta. */}
              {loading && (
                <div className="space-y-1 px-1 pb-2" aria-hidden="true">
                  <Skeleton className="h-9 w-full rounded-md" />
                  <Skeleton className="h-9 w-full rounded-md" />
                  <Skeleton className="h-9 w-full rounded-md" />
                </div>
              )}

              {GROUP_ORDER.map((groupName) => {
                const items = grouped.get(groupName);
                if (items === undefined || items.length === 0) return null;
                return (
                  <div key={groupName} className="py-1">
                    <p
                      id={`global-search-group-${groupName}`}
                      className="px-2 py-1 text-[10px] font-semibold uppercase tracking-[0.08em] text-muted-foreground"
                    >
                      {groupName}
                    </p>
                    <div role="group" aria-labelledby={`global-search-group-${groupName}`}>
                      {items.map((item) => {
                        const isActive = item.id === activeItem?.id;
                        const Icon = item.icon;
                        return (
                          <div
                            key={item.id}
                            id={item.id}
                            data-key={item.id}
                            role="option"
                            aria-selected={isActive}
                            onMouseEnter={() => setActiveIndex(flatItems.indexOf(item))}
                            onClick={() => activate(item)}
                            className={cn(
                              "flex cursor-pointer items-center gap-3 rounded-md px-2 py-2 text-sm",
                              isActive ? "bg-accent text-accent-foreground" : "text-foreground hover:bg-accent/60",
                            )}
                          >
                            <Icon className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                            <span className="min-w-0 flex-1 truncate">
                              {item.label}
                              {item.sublabel !== undefined && (
                                <span className="ml-2 truncate text-xs text-muted-foreground">{item.sublabel}</span>
                              )}
                            </span>
                          </div>
                        );
                      })}
                    </div>
                  </div>
                );
              })}

              {!loading && flatItems.length === 0 && (
                <div className="flex flex-col items-center gap-2 px-4 py-10 text-center">
                  <Search className="h-8 w-8 text-muted-foreground" aria-hidden="true" />
                  <p className="text-sm font-medium">Nada encontrado</p>
                  <p className="text-xs text-muted-foreground">
                    {term.trim().length > 0
                      ? `Nenhum resultado para "${term.trim()}".`
                      : "Digite para buscar pessoas, aplicativos ou dispositivos."}
                  </p>
                </div>
              )}
            </div>

            <div className="flex items-center gap-3 border-t px-3 py-2 text-[11px] text-muted-foreground">
              <span className="inline-flex items-center gap-1">
                <ArrowUp className="h-3 w-3" aria-hidden="true" />
                <ArrowDown className="h-3 w-3" aria-hidden="true" />
                navegar
              </span>
              <span className="inline-flex items-center gap-1">
                <CornerDownLeft className="h-3 w-3" aria-hidden="true" />
                abrir
              </span>
              <span className="ml-auto">Esc fecha</span>
            </div>
          </DialogPrimitive.Content>
        </DialogPrimitive.Portal>
      </DialogPrimitive.Root>
    </>
  );
}
