import type { ReactNode } from "react";
import { BrandLogo } from "@/components/BrandLogo";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

interface AuthCardProps {
  title: string;
  description?: string;
  children: ReactNode;
  footer?: ReactNode;
  /** Conteúdo extra do painel de marca (desktop) - ex.: bullets de confiança do login. */
  brandPanelExtra?: ReactNode;
}

const institutionalLinkClass =
  "underline-offset-4 transition-colors hover:text-foreground hover:underline";

/** Rodapé institucional presente em TODAS as telas que usam o AuthCard. */
function InstitutionalFooter() {
  return (
    <p className="flex flex-wrap items-center justify-center gap-x-3 gap-y-1 text-center text-xs text-muted-foreground">
      <a
        href="https://mais351monitor.com.br/privacidade.html"
        target="_blank"
        rel="noreferrer"
        className={institutionalLinkClass}
      >
        Política de Privacidade
      </a>
      <a
        href="https://mais351monitor.com.br/termos.html"
        target="_blank"
        rel="noreferrer"
        className={institutionalLinkClass}
      >
        Termos de Uso
      </a>
      <a href="mailto:bruna@mais351monitor.com.br" className={institutionalLinkClass}>
        Suporte
      </a>
      <a
        href="https://mais351monitor.com.br"
        target="_blank"
        rel="noreferrer"
        className={institutionalLinkClass}
      >
        mais351monitor.com.br
      </a>
    </p>
  );
}

/**
 * Moldura das telas públicas de autenticação (login, MFA, recuperação, convite):
 * no desktop, painel de marca à esquerda (pulso + frase do site) e formulário à
 * direita; no mobile, logo acima do cartão. Só composição - nenhuma lógica.
 */
export function AuthCard({ title, description, children, footer, brandPanelExtra }: AuthCardProps) {
  return (
    <div className="flex min-h-screen bg-background">
      {/* Painel de marca (desktop): o pulso do logo vivo + o lema do site. */}
      <div
        className="relative hidden flex-col justify-between overflow-hidden border-r p-12 lg:flex lg:w-[44%]"
        style={{
          background:
            "radial-gradient(640px 360px at 12% -12%, rgba(182, 255, 60, 0.08), transparent 62%)",
        }}
      >
        <BrandLogo size={30} />
        <svg
          viewBox="0 0 560 120"
          preserveAspectRatio="none"
          aria-hidden
          className="absolute inset-x-[-2%] bottom-[34%] opacity-90"
        >
          <path
            d="M0 78 H96 l26-44 34 74 24-52 16 22 H320 l22-36 30 58 20-30 H560"
            fill="none"
            stroke="#B6FF3C"
            strokeWidth="2.2"
            strokeLinecap="round"
            strokeLinejoin="round"
            style={{ filter: "drop-shadow(0 0 14px rgba(182, 255, 60, 0.35))" }}
          />
        </svg>
        <div>
          <p className="max-w-[15em] font-display text-2xl font-medium tracking-tight text-foreground">
            Informação para a <span className="text-primary">empresa</span>. Respeito para as{" "}
            <span className="text-primary">pessoas</span>.
          </p>
          <p className="mt-3 text-sm text-muted-foreground">
            Monitoramento transparente de produtividade, com LGPD por design.
          </p>
          {brandPanelExtra}
        </div>
      </div>

      {/* Painel do formulário: cartão centralizado (única coluna no mobile). */}
      <div className="flex flex-1 items-center justify-center p-4">
        <div className="w-full max-w-md space-y-6">
          <div className="text-center lg:hidden">
            <BrandLogo size={28} className="justify-center" />
            <p className="mt-2 text-sm text-muted-foreground">
              Monitoramento transparente de estações de trabalho
            </p>
          </div>
          <Card>
            <CardHeader>
              <CardTitle className="font-display">{title}</CardTitle>
              {description !== undefined && <CardDescription>{description}</CardDescription>}
            </CardHeader>
            <CardContent>{children}</CardContent>
          </Card>
          {footer !== undefined && (
            <div className="text-center text-sm text-muted-foreground">{footer}</div>
          )}
          {/* Kit de confiança: rodapé institucional fora do card, discreto.
              Sem CNPJ: o número não consta em site/privacidade.html nem em
              site/termos.html - nada é inventado aqui. */}
          <InstitutionalFooter />
        </div>
      </div>
    </div>
  );
}
