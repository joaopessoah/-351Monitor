import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { api, refreshAccessToken, setAccessToken } from "./api";

export type AuthStatus = "loading" | "authenticated" | "anonymous";

interface AuthContextValue {
  status: AuthStatus;
  /** Chamado após login/MFA/convite bem-sucedido com o access token recebido. */
  signIn: (accessToken: string) => void;
  /** Revoga o refresh token no servidor e limpa a sessão local. */
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>("loading");
  const queryClient = useQueryClient();

  // Na carga do app, tenta restaurar a sessão pelo refresh cookie httpOnly.
  useEffect(() => {
    let cancelled = false;
    refreshAccessToken().then((ok) => {
      if (!cancelled) setStatus(ok ? "authenticated" : "anonymous");
    });
    return () => {
      cancelled = true;
    };
  }, []);

  const signIn = useCallback((token: string) => {
    setAccessToken(token);
    setStatus("authenticated");
  }, []);

  const signOut = useCallback(async () => {
    try {
      await api<void>("/auth/logout", { method: "POST" });
    } catch {
      // A sessão local é encerrada mesmo se a revogação remota falhar.
    }
    setAccessToken(null);
    queryClient.clear();
    setStatus("anonymous");
  }, [queryClient]);

  const value = useMemo(() => ({ status, signIn, signOut }), [status, signIn, signOut]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (ctx === null) {
    throw new Error("useAuth deve ser usado dentro de <AuthProvider>");
  }
  return ctx;
}
