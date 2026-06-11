// =============================================================================
// Filtros compartilhados das telas de relatório (extraídos da AppsPage na
// F3.5): presets de período de 7/14/30/92 dias e multi-select de devices.
// Usados por /apps, /relatorios/jornada e /relatorios/uso - mesma aparência
// (controles h-9) e mesmo comportamento em todas as telas.
// =============================================================================

import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Check, ChevronDown, MonitorSmartphone } from "lucide-react";
import { api } from "@/lib/api";
import { addDays, localDateOf } from "@/lib/format";
import type { DeviceItem, PagedResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

/** Presets do período (a spec permite até 92 dias por consulta). */
export const PERIOD_PRESETS = [7, 14, 30, 92] as const;

// Classes do grupo segmentado (mesmo padrão da timeline/dashboard).
export const segmentedButton = "rounded-[5px] px-3 text-xs font-medium transition-colors";
export const segmentedOn = "bg-primary/10 text-primary";
export const segmentedOff = "text-muted-foreground hover:bg-accent hover:text-accent-foreground";

export interface DateRange {
  from: string;
  to: string;
}

/**
 * Período por preset (default 7 dias) ancorado em "hoje" no FUSO DO TENANT -
 * range null enquanto o fuso (GET /me) não chegou.
 */
export function useReportRange(timezone: string | null) {
  const todayStr = timezone !== null ? localDateOf(new Date(), timezone) : null;
  const [preset, setPreset] = useState<number>(7);
  const range = useMemo<DateRange | null>(() => {
    if (todayStr === null) return null;
    return { from: addDays(todayStr, -(preset - 1)), to: todayStr };
  }, [todayStr, preset]);
  return { todayStr, range, activePreset: preset, applyPreset: setPreset };
}

/**
 * Devices do filtro: arquivados ficam fora (os relatórios os excluem por
 * default de toda forma); pausados/revogados entram - podem ter histórico
 * dentro do período. Ordenados pelo nome de exibição pt-BR.
 */
export function useFilterDevices(): { devices: DeviceItem[] } {
  const devicesQuery = useQuery({
    queryKey: ["devices", { page_size: 100 }],
    queryFn: () => api<PagedResponse<DeviceItem>>("/devices?page_size=100"),
    staleTime: 60_000,
  });
  const devices = useMemo(() => {
    const items = (devicesQuery.data?.items ?? []).filter((d) => d.status !== "archived");
    return [...items].sort((a, b) =>
      (a.display_name ?? a.hostname).localeCompare(b.display_name ?? b.hostname, "pt-BR"),
    );
  }, [devicesQuery.data]);
  return { devices };
}

/** Grupo segmentado de presets de período - todos os controles de filtro em h-9. */
export function PeriodPresetGroup({
  active,
  onSelect,
  disabled = false,
}: {
  active: number | null;
  onSelect: (days: number) => void;
  disabled?: boolean;
}) {
  return (
    <div
      role="group"
      aria-label="Período"
      className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
    >
      {PERIOD_PRESETS.map((days) => (
        <button
          key={days}
          type="button"
          aria-pressed={active === days}
          disabled={disabled}
          onClick={() => onSelect(days)}
          className={cn(
            segmentedButton,
            active === days ? segmentedOn : segmentedOff,
            "disabled:pointer-events-none disabled:opacity-40",
          )}
        >
          {days} dias
        </button>
      ))}
    </div>
  );
}

/** Multi-select de devices em dropdown - o menu fica aberto para marcar vários. */
export function DeviceMultiSelect({
  devices,
  selected,
  onToggle,
  onClear,
}: {
  devices: DeviceItem[];
  selected: string[];
  onToggle: (id: string) => void;
  onClear: () => void;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" className="h-9">
          <MonitorSmartphone className="h-4 w-4" aria-hidden />
          {selected.length === 0
            ? "Todos os dispositivos"
            : selected.length === 1
              ? "1 dispositivo"
              : `${selected.length} dispositivos`}
          <ChevronDown className="h-3.5 w-3.5 text-muted-foreground" aria-hidden />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-72 w-64 overflow-y-auto">
        <DropdownMenuLabel>Dispositivos</DropdownMenuLabel>
        {devices.length === 0 && (
          <p className="px-2 py-1.5 text-sm text-muted-foreground">Nenhum dispositivo.</p>
        )}
        {devices.map((d) => {
          const checked = selected.includes(d.id);
          return (
            <DropdownMenuItem
              key={d.id}
              onSelect={(e) => {
                // Mantém o menu aberto para marcar vários devices.
                e.preventDefault();
                onToggle(d.id);
              }}
              aria-checked={checked}
              role="menuitemcheckbox"
            >
              <span
                aria-hidden
                className={cn(
                  "flex h-4 w-4 shrink-0 items-center justify-center rounded-sm border",
                  checked ? "border-primary bg-primary text-primary-foreground" : "border-input",
                )}
              >
                {checked && <Check className="h-3 w-3" aria-hidden />}
              </span>
              <span className="truncate">{d.display_name ?? d.hostname}</span>
            </DropdownMenuItem>
          );
        })}
        {selected.length > 0 && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={onClear}>Limpar seleção</DropdownMenuItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
