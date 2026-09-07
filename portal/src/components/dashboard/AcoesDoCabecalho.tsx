// =============================================================================
// Ações do cabeçalho da Visão Geral: "Resumo em PDF" e "Enviar por e-mail".
//
// POR QUE ELES ESTÃO DESABILITADOS: nenhum dos dois tem endpoint. O PDF depende
// da renderização no servidor e o e-mail depende do digest — as duas coisas são
// a fase seguinte. Inventar uma rota aqui produziria um botão que responde 404,
// e um botão que falha é pior do que um botão que avisa: o gestor tentaria uma
// vez, não confiaria mais na tela e abriria um chamado.
//
// POR QUE ELES APARECEM MESMO ASSIM: estão no mockup aprovado e são a promessa
// da tela. Mostrados desabilitados, com o motivo no `title`, eles comunicam o
// roteiro sem prometer o que não existe.
//
// O `title` fica no SPAN que envolve o botão, não só no botão: navegador não
// dispara evento de mouse em elemento `disabled`, então a dica do botão nunca
// apareceria. O `aria-describedby` liga o mesmo motivo ao nome acessível.
// =============================================================================

import type { ReactNode } from "react";
import { FileDown, Mail } from "lucide-react";

import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";

const MOTIVO_PDF =
  "Ainda não disponível: o resumo em PDF depende da geração no servidor, que chega na fase seguinte.";

const MOTIVO_EMAIL =
  "Ainda não disponível: o envio depende do digest por e-mail, que chega na fase seguinte.";

export function AcoesDoCabecalho({ className }: { className?: string }) {
  return (
    <div className={cn("flex flex-wrap items-center gap-2", className)}>
      <AcaoPendente id="acao-pdf" motivo={MOTIVO_PDF} label="Resumo em PDF">
        <FileDown className="h-3.5 w-3.5" aria-hidden />
      </AcaoPendente>
      <AcaoPendente id="acao-email" motivo={MOTIVO_EMAIL} label="Enviar por e-mail">
        <Mail className="h-3.5 w-3.5" aria-hidden />
      </AcaoPendente>
    </div>
  );
}

function AcaoPendente({
  id,
  motivo,
  label,
  children,
}: {
  id: string;
  motivo: string;
  label: string;
  children: ReactNode;
}) {
  return (
    <span title={motivo} className="inline-flex">
      <Button
        variant="outline"
        size="sm"
        disabled
        aria-describedby={id}
        title={motivo}
        className="h-8 gap-1.5 px-2.5 text-xs"
      >
        {children}
        {label}
      </Button>
      <span id={id} className="sr-only">
        {motivo}
      </span>
    </span>
  );
}
