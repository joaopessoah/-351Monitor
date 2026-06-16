import { NavLink, Outlet } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { isAdmin } from "@/lib/roles";
import type { MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";

const baseTabs = [
  { to: "/configuracoes/usuarios", label: "Usuários" },
  { to: "/configuracoes/chaves", label: "Chaves de instalação" },
  { to: "/configuracoes/categorias", label: "Categorias" },
  { to: "/configuracoes/privacidade", label: "Privacidade" },
] as const;

// Auditoria fica restrita a Owner/Admin (PolicyAdminPlus): o Viewer NÃO vê a aba
// nem a rota. A página em si também se protege (gate defensivo), mas a aba só
// aparece quando o papel permite — assim o Viewer nunca tropeça no link.
const auditTab = { to: "/configuracoes/auditoria", label: "Auditoria" } as const;

/** Layout de Configurações com sub-navegação em abas. */
export function ConfiguracoesLayout() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const tabs = isAdmin(meQuery.data) ? [...baseTabs, auditTab] : baseTabs;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Configurações</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Administração da organização: usuários do portal, chaves de instalação, categorias e
          privacidade.
        </p>
      </div>
      <nav className="flex flex-wrap gap-1 border-b" aria-label="Seções de configurações">
        {tabs.map((tab) => (
          <NavLink
            key={tab.to}
            to={tab.to}
            className={({ isActive }) =>
              cn(
                "-mb-px border-b-2 px-4 py-2 text-sm font-medium transition-colors",
                isActive
                  ? "border-primary text-primary"
                  : "border-transparent text-muted-foreground hover:border-border hover:text-foreground",
              )
            }
          >
            {tab.label}
          </NavLink>
        ))}
      </nav>
      <Outlet />
    </div>
  );
}
