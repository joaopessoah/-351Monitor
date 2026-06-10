import { PagePlaceholder } from "@/components/PagePlaceholder";

export function LinhaDoTempoPage() {
  return (
    <PagePlaceholder
      title="Linha do Tempo"
      description="Timeline do dia por dispositivo (F2) e por equipe (F3), com os estados Ativo, Ocioso, Bloqueado, Desligada/suspensa e Sem comunicação, tooltip por intervalo e fallback em tabela."
      phase="F2/F3"
    />
  );
}
