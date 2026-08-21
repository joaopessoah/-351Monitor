// =============================================================================
// Preferências locais do navegador (localStorage) - APENAS conveniências de
// exibição: sidebar recolhida e visão default da linha do tempo. Filtros de
// dados vivem na URL (lib/useUrlState.ts), nunca aqui. Todo acesso fica em
// try/catch: o storage pode estar indisponível (modo privado, política do
// navegador) e a UI precisa funcionar normalmente sem ele.
// =============================================================================

export const PREF_SIDEBAR_COLLAPSED = "m351.sidebar_collapsed";
export const PREF_TIMELINE_VIEW = "m351.timeline_view";

export function readPref(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function writePref(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Storage indisponível: a preferência simplesmente não persiste.
  }
}
