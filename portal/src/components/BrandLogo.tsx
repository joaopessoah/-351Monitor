// =============================================================================
// Logo da +351 Monitor (mesma marca SVG do site institucional): monitor com o
// pulso verde. `word` controla o wordmark ao lado; `compact` mostra só "+351"
// (sidebar colapsada). Cores herdam do contexto: currentColor no traço do
// monitor, verde da marca no pulso.
// =============================================================================

import { cn } from "@/lib/utils";

interface BrandLogoProps {
  /** Exibe o wordmark "+351 Monitor" ao lado da marca. */
  word?: boolean;
  /** Wordmark curto "+351" (sidebar colapsada). Ignorado sem `word`. */
  compact?: boolean;
  /** Tamanho da marca SVG em px (default 26). */
  size?: number;
  className?: string;
}

export function BrandLogo({ word = true, compact = false, size = 26, className }: BrandLogoProps) {
  return (
    <span className={cn("inline-flex items-center gap-2.5 text-foreground", className)}>
      <svg
        viewBox="0 0 64 64"
        fill="none"
        width={size}
        height={size}
        aria-hidden
        className="shrink-0"
      >
        <rect x="3" y="7" width="58" height="42" rx="7" stroke="currentColor" strokeWidth="3.6" />
        <path
          d="M11 30h7l5.5-11L31 41l5-11 3.5 6H53"
          stroke="#B6FF3C"
          strokeWidth="3.8"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
        <path d="M23 57h18" stroke="currentColor" strokeWidth="3.6" strokeLinecap="round" />
        <path d="M32 49v8" stroke="currentColor" strokeWidth="3.6" />
      </svg>
      {word && (
        <span className="whitespace-nowrap font-display text-base font-semibold tracking-tight">
          <em className="not-italic text-primary">+</em>351{!compact && <span className="font-medium text-muted-foreground"> Monitor</span>}
        </span>
      )}
    </span>
  );
}
