import { PagePlaceholder } from "@/components/PagePlaceholder";

export function ChavesPage() {
  return (
    <PagePlaceholder
      title="Chaves de instalação"
      description="Geração e revogação de enrollment keys (o segredo é exibido uma única vez) e bloco msiexec pronto para instalação silenciosa via GPO/Intune/RMM."
      phase="F1"
    />
  );
}
