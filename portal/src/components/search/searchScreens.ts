// =============================================================================
// Lista estática das telas do portal para a Busca Global (Ctrl+K/⌘K). Vive só
// no cliente - NUNCA depende de rede - então a seção "Telas" continua
// funcionando mesmo se as buscas remotas (pessoas/apps/dispositivos) falharem
// ou a organização estiver sem conexão no momento. As rotas espelham a
// navegação de components/layout/AppShell.tsx: qualquer mudança lá precisa
// ser refletida aqui também.
// =============================================================================

export interface SearchScreen {
  key: string;
  label: string;
  /** Termos extras que também casam com a tela, além do próprio rótulo. */
  keywords: string[];
  /** Transparência depende do slug da organização; as demais ignoram o argumento. */
  to: (transparenciaSlug: string) => string;
}

export const SEARCH_SCREENS: SearchScreen[] = [
  {
    key: "visao-geral",
    label: "Visão Geral",
    keywords: ["dashboard", "resumo", "indice", "kpi"],
    to: () => "/visao-geral",
  },
  {
    key: "linha-do-tempo",
    label: "Linha do Tempo",
    keywords: ["timeline", "calendario", "mapa do mes", "heatmap"],
    to: () => "/linha-do-tempo",
  },
  {
    key: "colaboradores",
    label: "Colaboradores",
    keywords: ["pessoas", "equipe", "funcionarios", "time"],
    to: () => "/colaboradores",
  },
  {
    key: "relatorios",
    label: "Relatórios",
    keywords: ["reports", "exportar", "uso", "jornada"],
    to: () => "/relatorios",
  },
  {
    key: "dispositivos",
    label: "Dispositivos",
    keywords: ["devices", "maquinas", "agentes", "computadores"],
    to: () => "/dispositivos",
  },
  {
    key: "configuracoes",
    label: "Configurações",
    keywords: ["settings", "categorias", "usuarios", "chaves"],
    to: () => "/configuracoes",
  },
  {
    key: "transparencia",
    label: "Transparência",
    keywords: ["privacidade", "auditoria publica"],
    to: (slug) => `/transparencia/${slug}`,
  },
];

/** Remove acentos e normaliza caixa - comparação tolerante a diacríticos. */
function normalize(value: string): string {
  return value
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase();
}

export interface ScreenResult {
  key: string;
  label: string;
  to: string;
}

/**
 * Filtra as telas pelo rótulo ou por uma palavra-chave. Termo vazio devolve
 * todas - é o estado "menu de atalhos" que a Busca Global mostra assim que
 * abre, antes de a pessoa digitar qualquer coisa.
 */
export function filterScreens(term: string, transparenciaSlug: string): ScreenResult[] {
  const q = normalize(term.trim());
  const matches =
    q.length === 0
      ? SEARCH_SCREENS
      : SEARCH_SCREENS.filter(
          (screen) =>
            normalize(screen.label).includes(q) ||
            screen.keywords.some((keyword) => normalize(keyword).includes(q)),
        );
  return matches.map((screen) => ({ key: screen.key, label: screen.label, to: screen.to(transparenciaSlug) }));
}
