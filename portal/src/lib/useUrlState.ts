// =============================================================================
// Estado de filtros na URL (sobre useSearchParams com replace: true): o parser
// valida os parâmetros crus e devolve o default quando ausentes/inválidos, no
// modelo do isIsoDate + from/to da AppsPage. O serializer devolve chave ->
// valor; null REMOVE o parâmetro (valores default devem virar null para a URL
// ficar limpa e compartilhável). O setter checa igualdade antes de escrever:
// nunca gera entrada de histórico nem loop com os useEffect que resetam página.
//
// Parâmetros interdependentes (ex.: group_by + sort + page) devem viver em UM
// ÚNICO useUrlState: o setSearchParams do react-router não é batched e duas
// escritas no mesmo tick se atropelam. Codecs devem ter identidade estável
// (constante de módulo ou useMemo) - o valor parseado é memoizado por eles.
// =============================================================================

import { useCallback, useMemo } from "react";
import { useSearchParams } from "react-router-dom";

export interface UrlStateCodec<T> {
  /** Lê e valida o estado a partir da URL - retorna o default quando ausente/inválido. */
  parse: (params: URLSearchParams) => T;
  /** Serializa o estado: chave -> valor do parâmetro; null remove (use para o default). */
  serialize: (value: T) => Record<string, string | null>;
}

export function useUrlState<T>({ parse, serialize }: UrlStateCodec<T>): [T, (next: T) => void] {
  const [searchParams, setSearchParams] = useSearchParams();

  const value = useMemo(() => parse(searchParams), [parse, searchParams]);

  const setValue = useCallback(
    (next: T) => {
      const entries = Object.entries(serialize(next));
      // Igualdade antes de escrever: nada muda, nada navega (nem replace).
      if (entries.every(([key, val]) => searchParams.get(key) === val)) return;
      setSearchParams(
        (prev) => {
          const merged = new URLSearchParams(prev);
          for (const [key, val] of entries) {
            if (val === null) merged.delete(key);
            else merged.set(key, val);
          }
          return merged;
        },
        { replace: true },
      );
    },
    [searchParams, serialize, setSearchParams],
  );

  return [value, setValue];
}
