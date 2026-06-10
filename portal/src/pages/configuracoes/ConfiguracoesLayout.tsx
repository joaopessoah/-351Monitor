import { NavLink, Outlet } from "react-router-dom";
import { cn } from "@/lib/utils";

const tabs = [
  { to: "/configuracoes/usuarios", label: "Usuários" },
  { to: "/configuracoes/chaves", label: "Chaves de instalação" },
  { to: "/configuracoes/categorias", label: "Categorias" },
  { to: "/configuracoes/privacidade", label: "Privacidade" },
] as const;

/** Layout de Configurações com sub-navegação em abas. */
export function ConfiguracoesLayout() {
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
