import type { ReactNode } from "react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

interface PagePlaceholderProps {
  title: string;
  description: string;
  /** Fase em que a funcionalidade chega (ex.: "F2", "F3"). */
  phase: string;
  children?: ReactNode;
}

/** Placeholder padrão das páginas internas durante a F0 — layout real, conteúdo por vir. */
export function PagePlaceholder({ title, description, phase, children }: PagePlaceholderProps) {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{description}</p>
      </div>
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Em construção</CardTitle>
          <CardDescription>
            Esta área será entregue na fase {phase}. A estrutura de navegação já é definitiva.
          </CardDescription>
        </CardHeader>
        {children !== undefined && <CardContent>{children}</CardContent>}
      </Card>
    </div>
  );
}
