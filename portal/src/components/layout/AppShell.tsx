import { useState } from "react";
import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import {
  AppWindow,
  BarChart3,
  CalendarClock,
  ChevronDown,
  LayoutDashboard,
  LogOut,
  Menu,
  MonitorSmartphone,
  PanelLeftClose,
  PanelLeftOpen,
  Settings,
  ShieldCheck,
  X,
} from "lucide-react";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { PREF_SIDEBAR_COLLAPSED, readPref, writePref } from "@/lib/prefs";
import { BrandLogo } from "@/components/BrandLogo";
import { roleLabels, timezoneBadge } from "@/lib/format";
import type { MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ShellSkeleton } from "./ShellSkeleton";
import { FormError } from "@/components/FormError";
import { genericErrorMessage } from "@/lib/messages";

const navItems = [
  { to: "/visao-geral", label: "Visão Geral", icon: LayoutDashboard },
  { to: "/linha-do-tempo", label: "Linha do Tempo", icon: CalendarClock },
  { to: "/apps", label: "Aplicativos", icon: AppWindow },
  { to: "/relatorios", label: "Relatórios", icon: BarChart3 },
  { to: "/dispositivos", label: "Dispositivos", icon: MonitorSmartphone },
  { to: "/configuracoes", label: "Configurações", icon: Settings },
] as const;

/** Links de navegação do shell - reusados pela sidebar e pelo drawer mobile. */
function ShellNav({
  slug,
  collapsed = false,
  onNavigate,
}: {
  slug: string;
  collapsed?: boolean;
  onNavigate?: () => void;
}) {
  const linkClass = ({ isActive }: { isActive: boolean }) =>
    cn(
      "flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors",
      collapsed && "justify-center px-2",
      isActive
        ? "bg-primary/10 text-primary"
        : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
    );
  return (
    <nav className="flex-1 space-y-1 overflow-y-auto p-2" aria-label="Navegação principal">
      {navItems.map(({ to, label, icon: Icon }) => (
        <NavLink key={to} to={to} title={label} onClick={onNavigate} className={linkClass}>
          <Icon className="h-4 w-4 shrink-0" />
          {!collapsed && <span>{label}</span>}
        </NavLink>
      ))}
      <NavLink
        to={`/transparencia/${slug}`}
        title="Transparência"
        onClick={onNavigate}
        className={linkClass}
      >
        <ShieldCheck className="h-4 w-4 shrink-0" />
        {!collapsed && <span>Transparência</span>}
      </NavLink>
    </nav>
  );
}

/** Layout persistente das rotas protegidas: sidebar colapsável + topbar;
    abaixo de md a sidebar some e a navegação vira um drawer (hambúrguer). */
export function AppShell() {
  // Preferência persistida no navegador (todo acesso ao storage em try/catch).
  const [collapsed, setCollapsed] = useState(() => readPref(PREF_SIDEBAR_COLLAPSED) === "1");
  const [mobileNavOpen, setMobileNavOpen] = useState(false);
  const { signOut } = useAuth();
  const navigate = useNavigate();

  function toggleCollapsed(): void {
    const next = !collapsed;
    setCollapsed(next);
    writePref(PREF_SIDEBAR_COLLAPSED, next ? "1" : "0");
  }

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });

  if (meQuery.isPending) {
    return <ShellSkeleton />;
  }

  if (meQuery.isError) {
    return (
      <div className="flex min-h-screen items-center justify-center p-6">
        <div className="w-full max-w-md space-y-4 text-center">
          <h1 className="flex justify-center text-lg font-semibold">
            <BrandLogo size={26} />
          </h1>
          <FormError message={genericErrorMessage(meQuery.error)} />
          <div className="flex justify-center gap-2">
            <Button variant="outline" onClick={() => void meQuery.refetch()}>
              Tentar novamente
            </Button>
            <Button
              variant="ghost"
              onClick={() => {
                void signOut().then(() => navigate("/login", { replace: true }));
              }}
            >
              Sair
            </Button>
          </div>
        </div>
      </div>
    );
  }

  const me = meQuery.data;

  async function handleSignOut() {
    await signOut();
    navigate("/login", { replace: true });
  }

  return (
    <div className="flex min-h-screen">
      {/* Sidebar fixa - só a partir de md; abaixo disso a navegação é o drawer. */}
      <aside
        className={cn(
          "hidden shrink-0 flex-col border-r bg-card transition-[width] duration-150 md:flex",
          collapsed ? "w-16" : "w-60",
        )}
      >
        <div className={cn("flex h-14 items-center border-b px-4", collapsed && "justify-center px-2")}>
          <BrandLogo word={!collapsed} size={24} />
        </div>
        <ShellNav slug={me.organization.slug} collapsed={collapsed} />
        <div className="border-t p-2">
          <Button
            variant="ghost"
            size="sm"
            className={cn("w-full justify-start gap-3 text-muted-foreground", collapsed && "justify-center")}
            onClick={toggleCollapsed}
            aria-label={collapsed ? "Expandir menu" : "Recolher menu"}
          >
            {collapsed ? <PanelLeftOpen className="h-4 w-4" /> : <PanelLeftClose className="h-4 w-4" />}
            {!collapsed && <span>Recolher</span>}
          </Button>
        </div>
      </aside>

      {/* Drawer de navegação mobile (radix Dialog cru: o DialogContent do ui/
          centraliza com left-1/2 e o cn() sem tailwind-merge não sobrescreve).
          Radix cuida do foco ao abrir/fechar, Esc e clique no overlay. */}
      <DialogPrimitive.Root open={mobileNavOpen} onOpenChange={setMobileNavOpen}>
        <DialogPrimitive.Portal>
          <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50 md:hidden" />
          <DialogPrimitive.Content
            aria-describedby={undefined}
            className="fixed inset-y-0 left-0 z-50 flex w-72 max-w-[85vw] flex-col border-r bg-card shadow-lg outline-none md:hidden"
          >
            <DialogPrimitive.Title className="sr-only">Menu de navegação</DialogPrimitive.Title>
            <div className="flex h-14 items-center justify-between border-b px-4">
              <BrandLogo word size={24} />
              <DialogPrimitive.Close
                aria-label="Fechar menu"
                className="rounded-sm p-1 text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <X className="h-5 w-5" aria-hidden />
              </DialogPrimitive.Close>
            </div>
            <ShellNav slug={me.organization.slug} onNavigate={() => setMobileNavOpen(false)} />
          </DialogPrimitive.Content>
        </DialogPrimitive.Portal>
      </DialogPrimitive.Root>

      {/* Conteúdo */}
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-14 items-center justify-between gap-4 border-b bg-card px-6">
          <div className="flex min-w-0 items-center gap-3">
            <Button
              variant="ghost"
              size="sm"
              className="shrink-0 px-2 md:hidden"
              aria-label="Abrir menu"
              onClick={() => setMobileNavOpen(true)}
            >
              <Menu className="h-5 w-5" aria-hidden />
            </Button>
            <span className="truncate text-sm font-semibold">{me.organization.name}</span>
            <span className="hidden rounded-full bg-secondary px-2.5 py-0.5 text-xs text-secondary-foreground sm:inline">
              {timezoneBadge(me.organization.timezone)}
            </span>
          </div>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" className="gap-2">
                <span className="flex h-7 w-7 items-center justify-center rounded-full bg-primary/10 text-xs font-semibold text-primary">
                  {initials(me.user.display_name)}
                </span>
                <span className="hidden max-w-[12rem] truncate text-sm sm:inline">{me.user.display_name}</span>
                <ChevronDown className="h-4 w-4 text-muted-foreground" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-64">
              <DropdownMenuLabel>
                <div className="space-y-0.5">
                  <p className="truncate text-sm font-medium">{me.user.display_name}</p>
                  <p className="truncate text-xs font-normal text-muted-foreground">{me.user.email}</p>
                  <p className="text-xs font-normal text-muted-foreground">{roleLabels[me.user.role]}</p>
                </div>
              </DropdownMenuLabel>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => void handleSignOut()}>
                <LogOut className="h-4 w-4" />
                Sair
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </header>
        <main className="flex-1 p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter((p) => p.length > 0);
  if (parts.length === 0) return "?";
  const first = parts[0]?.charAt(0) ?? "";
  const last = parts.length > 1 ? (parts[parts.length - 1]?.charAt(0) ?? "") : "";
  return (first + last).toUpperCase();
}
