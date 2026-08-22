// =============================================================================
// Seletor de equipe por etiqueta (F5, ?tag): "me mostra só o comercial" é a
// primeira pergunta de quem tem mais de 30 máquinas. O mesmo controle aparece
// na Visão Geral, na Linha do Tempo e nos relatórios que aceitam o filtro, e o
// backend usa o mesmo parâmetro em todos eles.
//
// LINHA VERMELHA DO PRODUTO: o seletor RECORTA a equipe exibida, uma de cada
// vez. Não existe, e não pode passar a existir, comparação de equipes lado a
// lado nem ranking entre elas - metas e comparativos seguem agregados.
//
// As opções saem das etiquetas que os próprios dispositivos já carregam (o
// mesmo array `tags` que a tabela de /dispositivos mostra e o diálogo de
// etiquetas edita): não há cadastro separado de equipes no produto. Sem
// nenhuma etiqueta cadastrada o controle não aparece - um seletor com uma
// opção só é ruído.
//
// O estado vive na URL (?tag=), como todos os outros filtros do produto: o
// link reproduz exatamente o recorte visível. Nunca no localStorage.
// =============================================================================

import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { Users } from "lucide-react";
import { api } from "@/lib/api";
import type { UrlStateCodec } from "@/lib/useUrlState";
import type { DeviceItem, PagedResponse } from "@/lib/types";
import { cn } from "@/lib/utils";

/**
 * ?tag= na URL - null (ausente) é "todas as equipes" e some do link. O valor é
 * usado cru: as etiquetas já são gravadas normalizadas pelo PATCH /devices, e o
 * backend trata vazio como ausência de filtro.
 */
export const TAG_CODEC: UrlStateCodec<string | null> = {
  parse: (params) => {
    const raw = params.get("tag");
    return raw !== null && raw.trim().length > 0 ? raw : null;
  },
  serialize: (value) => ({ tag: value }),
};

/** `&tag=...` pronto para concatenar na query string (vazio sem filtro). */
export function tagParam(tag: string | null): string {
  return tag !== null ? `&tag=${encodeURIComponent(tag)}` : "";
}

/**
 * Etiquetas conhecidas da organização, derivadas dos dispositivos (MESMA
 * queryKey do filtro de dispositivos dos relatórios e da Linha do Tempo: o
 * TanStack resolve do cache, sem requisição extra). Arquivados ficam fora, como
 * em todo recorte do produto. Ordenação pt-BR, sem duplicatas.
 */
export function useTeamTags(): { tags: string[] } {
  const devicesQuery = useQuery({
    queryKey: ["devices", { page_size: 100 }],
    queryFn: () => api<PagedResponse<DeviceItem>>("/devices?page_size=100"),
    staleTime: 60_000,
  });

  const tags = useMemo(() => {
    const unique = new Set<string>();
    for (const device of devicesQuery.data?.items ?? []) {
      if (device.status === "archived") continue;
      for (const tag of device.tags ?? []) {
        if (tag.trim().length > 0) unique.add(tag);
      }
    }
    return [...unique].sort((a, b) => a.localeCompare(b, "pt-BR"));
  }, [devicesQuery.data]);

  return { tags };
}

/**
 * Seletor de equipe (h-9, mesma altura dos demais controles de filtro). Uma
 * equipe por vez, e sempre com a opção de voltar para a organização inteira.
 */
export function TeamTagSelect({
  tags,
  value,
  onChange,
  disabled = false,
}: {
  tags: string[];
  value: string | null;
  onChange: (tag: string | null) => void;
  disabled?: boolean;
}) {
  // Sem etiqueta nenhuma cadastrada não há equipe a escolher.
  if (tags.length === 0) return null;

  // ?tag= de um deep-link cuja etiqueta sumiu (renomeada/removida): mantém a
  // opção no seletor para o controle refletir o recorte que a tela está usando.
  const options = value !== null && !tags.includes(value) ? [value, ...tags] : tags;

  return (
    <span className="inline-flex items-center gap-1.5">
      <Users className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
      <select
        aria-label="Equipe"
        value={value ?? ""}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value === "" ? null : e.target.value)}
        className={cn(
          "h-9 min-w-[11rem] rounded-md border border-input bg-card px-3 text-sm",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
          "disabled:pointer-events-none disabled:opacity-40",
        )}
      >
        <option value="">Todas as equipes</option>
        {options.map((tag) => (
          <option key={tag} value={tag}>
            {tag}
          </option>
        ))}
      </select>
    </span>
  );
}
