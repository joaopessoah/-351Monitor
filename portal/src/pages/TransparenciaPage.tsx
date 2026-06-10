import { useParams } from "react-router-dom";
import { Check, X } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

const collected = [
  "Aplicativo em primeiro plano e título da janela, conforme a política de privacidade configurada pela empresa",
  "Sessões do Windows: logon, logoff, bloqueio e desbloqueio",
  "Períodos de ociosidade (apenas o fato de não haver uso de teclado/mouse, nunca o que foi digitado)",
  "Horários de ligar/desligar e suspensão da máquina",
  "Saúde do agente de monitoramento (versão, conectividade)",
];

const neverCollected = [
  "Teclas digitadas (keylogging)",
  "Capturas ou gravações de tela",
  "Conteúdo de arquivos, e-mails ou mensagens",
  "Área de transferência (copiar/colar)",
  "Webcam ou microfone",
  "Localização geográfica",
];

/**
 * Página PÚBLICA de transparência (/transparencia/:slug) — sem login.
 * Na F4 passa a renderizar a política de coleta REAL e vigente do tenant
 * (Seção 8.8 do spec); nenhum dado pessoal é exibido aqui.
 */
export function TransparenciaPage() {
  const { slug } = useParams<{ slug: string }>();

  return (
    <div className="min-h-screen bg-background">
      <header className="border-b bg-card">
        <div className="mx-auto flex h-14 max-w-3xl items-center justify-between px-4">
          <span className="text-base font-bold tracking-tight text-primary">+351 Monitor</span>
          <span className="text-xs text-muted-foreground">Página pública de transparência</span>
        </div>
      </header>
      <main className="mx-auto max-w-3xl space-y-6 px-4 py-10">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Transparência do monitoramento</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Política de coleta da organização <span className="font-medium">{slug}</span>. Esta
            página descreve o que o monitoramento corporativo coleta nas estações de trabalho, e o
            que ele jamais coleta. Nenhum dado pessoal é exibido aqui.
          </p>
        </div>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">O que é coletado</CardTitle>
            <CardDescription>
              Lista fechada de coleta, limitada ao necessário para gestão de uso das estações.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="space-y-2">
              {collected.map((item) => (
                <li key={item} className="flex items-start gap-2 text-sm">
                  <Check className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" aria-hidden="true" />
                  <span>{item}</span>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">O que NUNCA é coletado</CardTitle>
            <CardDescription>
              Estas proibições fazem parte da arquitetura do produto: o código de coleta não
              existe.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="space-y-2">
              {neverCollected.map((item) => (
                <li key={item} className="flex items-start gap-2 text-sm">
                  <X className="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
                  <span>{item}</span>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">Política vigente e retenções</CardTitle>
            <CardDescription>
              A partir da fase F4, esta página exibirá a política de coleta real e vigente da
              organização: política de títulos de janela, janela de coleta, retenções de dados,
              finalidade declarada e contato do encarregado (DPO) da empresa.
            </CardDescription>
          </CardHeader>
        </Card>
      </main>
    </div>
  );
}
