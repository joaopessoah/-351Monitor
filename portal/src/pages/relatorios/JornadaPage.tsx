import { PagePlaceholder } from "@/components/PagePlaceholder";

export function JornadaPage() {
  return (
    <div className="space-y-4">
      <PagePlaceholder
        title="Relatório de Jornada"
        description="Uma linha por dispositivo × dia: Primeiro evento, Último evento, tempo ligada, tempo ativo, tempo ocioso e tempo bloqueado, com totais por dispositivo."
        phase="F3"
      />
      <p className="rounded-md border bg-muted/50 px-4 py-3 text-xs text-muted-foreground">
        Relatório gerencial de uso da estação de trabalho. Não constitui registro eletrônico de
        ponto (Portaria 671/MTE) e não substitui o controle de jornada do art. 74 da CLT.
      </p>
    </div>
  );
}
