// =============================================================================
// Busca global (Ctrl+K/⌘K): pessoas, aplicativos e dispositivos são buscados
// em paralelo dentro de UMA única useQuery - cada chamada tem seu próprio
// `.catch(() => null)`, então o Promise.all sempre resolve (nunca rejeita),
// mesmo que uma das três fontes esteja fora do ar. A seção correspondente
// simplesmente some da UI (campo null) e as outras continuam aparecendo.
//
// Sem ranking: os itens ficam na ordem que o próprio endpoint devolve
// (alfabética, por padrão da API de pessoas) - não reordenamos por
// relevância nem por uso recente.
// =============================================================================

import { useQuery } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { addDays, localDateOf } from "@/lib/format";
import type { AppCatalogResponse, DeviceItem, PagedResponse, PeopleReportResponse } from "@/lib/types";

export interface SearchPersonResult {
  sid: string;
  name: string;
  teams: string[];
}

export interface SearchAppResult {
  id: string;
  name: string;
}

export interface SearchDeviceResult {
  id: string;
  name: string;
}

export interface GlobalSearchData {
  /** null = a fonte falhou e a seção deve sumir; [] = buscou e não achou nada. */
  people: SearchPersonResult[] | null;
  apps: SearchAppResult[] | null;
  devices: SearchDeviceResult[] | null;
}

const RESULT_PAGE_SIZE = 8;
const PEOPLE_WINDOW_DAYS = 30;
export const MIN_QUERY_LENGTH = 2;

/**
 * `timezone` vem do cache da query `["me"]` (o AppShell já a resolveu) - null
 * enquanto o /me ainda não respondeu, e nesse caso a busca remota fica
 * desabilitada (sem fuso não dá pra montar o recorte "hoje-30" corretamente).
 */
export function useGlobalSearchResults(term: string, timezone: string | null) {
  const trimmed = term.trim();
  const enabled = trimmed.length >= MIN_QUERY_LENGTH && timezone !== null;

  return useQuery({
    queryKey: ["global-search", trimmed, timezone],
    queryFn: async (): Promise<GlobalSearchData> => {
      const to = localDateOf(new Date(), timezone as string);
      const from = addDays(to, -PEOPLE_WINDOW_DAYS);

      const peopleParams = new URLSearchParams({
        from,
        to,
        q: trimmed,
        page_size: String(RESULT_PAGE_SIZE),
      });
      const appsParams = new URLSearchParams({ q: trimmed });
      const devicesParams = new URLSearchParams({ q: trimmed, page_size: String(RESULT_PAGE_SIZE) });

      const [peopleRes, appsRes, devicesRes] = await Promise.all([
        api<PeopleReportResponse>(`/people?${peopleParams.toString()}`).catch(() => null),
        api<AppCatalogResponse>(`/app-catalog?${appsParams.toString()}`).catch(() => null),
        api<PagedResponse<DeviceItem>>(`/devices?${devicesParams.toString()}`).catch(() => null),
      ]);

      return {
        people:
          peopleRes === null
            ? null
            : peopleRes.items.map((p) => ({ sid: p.windows_sid, name: p.display_name, teams: p.teams })),
        apps:
          appsRes === null
            ? null
            : appsRes.items.map((a) => ({ id: a.app_id, name: a.custom_display_name ?? a.display_name })),
        devices:
          devicesRes === null
            ? null
            : devicesRes.items.map((d) => ({ id: d.id, name: d.display_name ?? d.hostname })),
      };
    },
    enabled,
    staleTime: 30_000,
  });
}
