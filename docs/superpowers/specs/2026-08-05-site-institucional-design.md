# Site institucional mais351monitor.com.br — Design aprovado

**Data:** 2026-08-05 · **Status:** aprovado pelo dono do produto (João) em conversa · **Hospedagem:** Hostinger (domínio já registrado e apontado, hoje com página estacionada)

## Objetivo

Landing page de vendas do +351 Monitor: apresentar o produto, comunicar o posicionamento "gestão transparente de produtividade, não vigilância" e converter visitantes em demonstrações agendadas via WhatsApp. Substitui a página estacionada da Hostinger. Cumpre o entregável "Landing page" do item 8 de `docs/design/05-produto-mvp.md`.

## Decisões (com o porquê)

| Decisão | Escolha | Por quê |
|---|---|---|
| Formato | One-page (`index.html`) + `privacidade.html` + `404.html` | Conteúdo atual cabe em uma narrativa única; página de privacidade é exigência do doc LGPD §3.3 |
| Stack | HTML/CSS/JS puro, sem build, fontes self-hosted | Upload direto na Hostinger, edição fácil, performance máxima, sem chamadas externas (LGPD) |
| Conversão | Todos os CTAs → `wa.me/5511992209235` com mensagem pré-preenchida | Decisão do dono; menor fricção para PME BR; sem backend |
| Preços | **Não publicados** | Decisão do dono (contraria a recomendação do doc de estratégia, registrado ciente) |
| Telas do produto | Mockups estilizados em HTML/CSS/SVG (dados fictícios "Empresa Demo") | Decisão do dono; nitidez em qualquer tela e sem dependência do visual atual do portal |
| Direção visual | Dark "sala de controle": fundo `#0B0F1A`, verde `#B6FF3C`, Space Grotesk + Open Sans | Identidade oficial do logo (zip de marca); aprovada entre 3 direções propostas |
| Prova social | Nenhuma no v1 | Sem depoimentos/logos inventados; entra quando os pilotos autorizarem |
| E-mail de contato | `bruna@mais351monitor.com.br` | Confirmado pelo dono |

## Sistema visual

- **Tokens**: fundo `#0B0F1A`, superfícies `#101724`/`#141D2E`, bordas `#232A38`, verde `#B6FF3C` (CTAs, pulso, destaques — nunca em bloco grande), texto `#F2F5FA`/`#9AA6B8`/`#5C6879`.
- **Assinatura**: linha de pulso (eletrocardiograma) derivada do símbolo do logo — animada no hero (desenha ao carregar), conector dos 3 passos, divisor no CTA final, sublinhado do destaque do H1.
- **Marca**: eyebrows de seção prefixados com "+" verde (tique tipográfico da marca `+351`).
- **Dataviz dos mockups**: produtivo `#B6FF3C`, neutro `#7FB5E8`, improdutivo `#F5A854`, ocioso `#39445A` hachurado (validado: CVD ΔE 21.1, contraste ≥3:1; banda de luminosidade intencionalmente aberta — o verde da marca carrega ênfase de "produtivo", com rótulos diretos e gaps como codificação secundária).
- **Acessibilidade**: contraste AA, `prefers-reduced-motion`, foco visível, HTML semântico, skip-link.

## Estrutura da landing (ordem)

1. Nav sticky (logo SVG oficial, âncoras, CTA)
2. Hero: eyebrow + H1 "O dia de trabalho da sua equipe, finalmente visível." + lead com posicionamento + CTAs + chips de confiança + pulso animado + mockup grande "Visão geral"
3. Faixa de segmentos (ICP: contabilidade, advocacia, BPO, agências, software houses, corretoras)
4. Dor — 3 cards pelas personas (dono, RH/DP, TI)
5. Produto em 3 telas: comparativo de equipes · linha do tempo do dia · saúde dos agentes
6. Como funciona em 3 passos (MSI/GPO + onboarding assistido → transparência ao funcionário → decisão com dados)
7. Transparência & LGPD (seção-assinatura): "O que coletamos" × "O que NUNCA coletamos" + chips de recursos LGPD
8. Por que brasileiro (real, WhatsApp, kit LGPD, feito para PME)
9. "O que NÃO somos" (ponto eletrônico, spyware, antivírus/DLP)
10. FAQ — as 5 objeções do doc de estratégia
11. CTA final (demo 10 min no WhatsApp com onboarding assistido)
12. Footer (contato, redes, Política de Privacidade, tagline)

## Página de privacidade

Conteúdo derivado de `docs/design/04-lgpd-seguranca.md` (papéis controladora/operadora, lista fechada de coleta, lista do que nunca é coletado, finalidades, base legal, retenção default, direitos do titular, segurança, residência dos dados no Brasil, contato). **Pendência: revisão por advogado antes do GA** (já prevista no doc de estratégia).

## SEO e social

Meta title/description em pt-BR, canonical, Open Graph + Twitter card com imagem `assets/img/og.png` (1200×630 gerada da marca), favicon SVG + PNG + apple-touch-icon, `schema.org/SoftwareApplication` + `Organization` (sameAs Instagram/LinkedIn), `sitemap.xml`, `robots.txt`, `theme-color`.

## Arquivos

```
site/
  index.html
  privacidade.html
  404.html
  .htaccess                (charset UTF-8, ErrorDocument 404, cache de assets)
  robots.txt
  sitemap.xml
  DEPLOY.md                (passo a passo Hostinger)
  assets/
    css/styles.css
    js/main.js
    fonts/space-grotesk-var.woff2, open-sans-var.woff2
    img/favicon.svg, favicon-32.png, apple-touch-icon.png, og.png
```

## Fora de escopo (v1)

Formulário de contato, blog/SEO de conteúdo, página de preços, analytics (adicionar depois com aviso de cookies se for GA4; preferir alternativa sem cookie), múltiplos idiomas, depoimentos.

## Publicação

hPanel Hostinger → File Manager (ou FTP) → conteúdo de `site/` para `public_html/`. Domínio já aponta. Detalhes em `site/DEPLOY.md`.
