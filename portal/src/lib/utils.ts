import { clsx, type ClassValue } from "clsx";

/** Combina classes condicionalmente (estilo shadcn/ui, sem tailwind-merge). */
export function cn(...inputs: ClassValue[]): string {
  return clsx(inputs);
}
