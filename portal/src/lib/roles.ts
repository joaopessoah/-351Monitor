import type { MeResponse, Role } from "./types";

/**
 * Papéis com poder de administração no portal (espelho do PolicyAdminPlus do
 * backend): criar/editar categorias e mapear apps. Viewer é somente leitura.
 */
export function isAdminRole(role: Role | undefined): boolean {
  return role === "admin" || role === "owner";
}

/** Conveniência sobre a resposta (possivelmente ainda carregando) de GET /me. */
export function isAdmin(me: MeResponse | undefined): boolean {
  return isAdminRole(me?.user.role);
}

/**
 * Apenas Proprietário (espelho do PolicyOwnerOnly do backend): ações de maior
 * risco como a EXCLUSÃO definitiva de dados do titular (DSR) e a exportação
 * completa do tenant. Admin NÃO basta.
 */
export function isOwnerRole(role: Role | undefined): boolean {
  return role === "owner";
}

export function isOwner(me: MeResponse | undefined): boolean {
  return isOwnerRole(me?.user.role);
}
