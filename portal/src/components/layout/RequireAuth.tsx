import { Navigate, Outlet, useLocation } from "react-router-dom";
import { useAuth } from "@/lib/auth";
import { ShellSkeleton } from "./ShellSkeleton";

/**
 * Guard de rota protegida: enquanto a sessão é restaurada exibe o skeleton do
 * shell; sem sessão, redireciona para /login preservando o destino original.
 */
export function RequireAuth() {
  const { status } = useAuth();
  const location = useLocation();

  if (status === "loading") {
    return <ShellSkeleton />;
  }
  if (status === "anonymous") {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }
  return <Outlet />;
}
