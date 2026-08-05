# Site institucional mais351monitor.com.br — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finalizar e versionar o site institucional aprovado (landing + privacidade + 404 + assets sociais) pronto para upload na Hostinger.

**Architecture:** Site 100% estático (HTML/CSS/JS puro, fontes self-hosted) vivendo em `site/` no monorepo. O protótipo aprovado existe em `<scratchpad>/site/` e vira a base; páginas novas reutilizam `assets/css/styles.css`. Assets raster (og/ícones) são gerados por screenshot headless do Edge a partir de templates HTML temporários.

**Tech Stack:** HTML5, CSS3 (tokens em `:root`), JS vanilla, Edge headless (`--screenshot`) para PNGs, Apache `.htaccess` (Hostinger usa LiteSpeed, compatível).

## Global Constraints

- Paleta e tipografia exatamente as do spec: fundo `#0B0F1A`, verde `#B6FF3C`, Space Grotesk + Open Sans (self-hosted, sem Google Fonts remoto).
- Nenhuma chamada a domínio externo em nenhuma página.
- Contato: WhatsApp `5511992209235`, e-mail `bruna@mais351monitor.com.br`.
- Copy em pt-BR; sem preços; sem depoimentos/prova social inventada.
- Toda página tem: `lang="pt-BR"`, meta description, canonical, favicon, theme-color `#0B0F1A`.
- Fatos sobre o produto (coleta, retenção, segurança) só os documentados em `docs/design/04-lgpd-seguranca.md`.

---

### Task 1: Mover o protótipo aprovado para `site/` no monorepo

**Files:**
- Create: `site/` (copiado de `<scratchpad>/site/`: `index.html`, `assets/css/styles.css`, `assets/js/main.js`, `assets/img/favicon.svg`, `assets/fonts/*.woff2`)

**Interfaces:**
- Produces: árvore `site/` que todas as tasks seguintes editam (caminho canônico daqui em diante).

- [ ] **Step 1:** Copiar a árvore do scratchpad para `C:\dev\351-monitor\site\` (excluir a pasta `shots/`).
- [ ] **Step 2:** Verificar: `site/index.html` abre no navegador com fontes e favicon carregando (caminhos relativos intactos).
- [ ] **Step 3:** Commit: `feat(site): landing institucional aprovada (design dark + pulso da marca)`

### Task 2: Página `privacidade.html`

**Files:**
- Create: `site/privacidade.html`
- Modify: `site/assets/css/styles.css` (acrescentar bloco `/* Página de conteúdo */` com `.page-hero`, `.prose`, `.prose table`)

**Interfaces:**
- Consumes: classes existentes (`nav`, `container`, `eyebrow`, `lgpd-col*`, `footer`).
- Produces: URL `/privacidade.html` linkada no footer de `index.html` (link já existe).

Conteúdo (derivado de `docs/design/04-lgpd-seguranca.md`, nesta ordem):
1. Hero da página: eyebrow "+ Política de Privacidade", H1 "Privacidade e transparência", data de atualização, aviso "documento em linguagem simples; versão contratual no DPA".
2. "Quem é quem" — papéis LGPD: cliente = controladora; +351 Monitor = operadora; nuvem = suboperador. Uma linha para cada, do §1.1 do doc.
3. "O que o agente coleta" — lista fechada do §3.1 (máquina/usuário, sessões, liga/desliga, app/janela ativa com título mascarável, snapshot de processos sem argumentos, ociosidade só o fato, heartbeat).
4. "O que o agente nunca coleta" — compromisso público do §3.1 (keylog, prints/gravação, arquivos/e-mails/mensagens/área de transferência, áudio/webcam, geolocalização, URLs completas, senhas, máquinas não provisionadas). Reusar visual das colunas da landing.
5. "Para que os dados são usados" — as 3 finalidades fechadas do §3.2.
6. "Base legal" — legítimo interesse da controladora; termo de ciência é transparência, não consentimento (§1.2, em linguagem simples).
7. "Transparência para quem é monitorado" — ícone visível sem opção de ocultar, aviso no primeiro acesso, tela "o que é coletado", janela de expediente (default 07:00–20:00 dias úteis), mascaramento de títulos por padrão, navegação privada nunca coletada (REQ-PRIV-01..05).
8. "Por quanto tempo guardamos" — tabela de retenção do §3.4 (brutos 90 dias configurável 30–180; agregados 24 meses configurável 12–36; auditoria 24 meses fixa; backups ciclo máx. 35 dias).
9. "Seus direitos como titular" — exercidos perante o empregador (controladora); ferramentas de exportação e exclusão; prazo legal de 15 dias (§3.5).
10. "Fim de contrato" — 30 dias somente-exportação, exclusão definitiva + certificado (§3.6).
11. "Segurança" — TLS 1.2+/1.3, criptografia em repouso, isolamento por cliente testado em CI, auditoria de acesso imutável, MFA para administradores, backups criptografados no Brasil, aviso de incidente à controladora em até 48 h (§4).
12. "Onde os dados ficam" — datacenter no Brasil (São Paulo).
13. "Contato do Encarregado (DPO)" — bruna@mais351monitor.com.br.
14. Rodapé de aviso: "Esta página descreve o produto; a relação contratual é regida pelos Termos e pelo DPA. Sujeita a revisão jurídica antes da disponibilidade geral."

- [ ] **Step 1:** Escrever `privacidade.html` com nav simplificada (logo + link "← voltar ao site") e footer completo iguais aos da landing.
- [ ] **Step 2:** Acrescentar CSS `.page-hero`/`.prose` (títulos h2 com âncora, listas, tabela com bordas `--border-soft`, `max-width` 780px).
- [ ] **Step 3:** Verificar: screenshot headless sem sobreposições; link do footer da landing → página abre; link "voltar" → landing.
- [ ] **Step 4:** Commit: `feat(site): página pública de privacidade (LGPD §3.3)`

### Task 3: `404.html` + `.htaccess`

**Files:**
- Create: `site/404.html`, `site/.htaccess`

**Interfaces:**
- Produces: `ErrorDocument 404 /404.html` referenciado pelo `.htaccess`.

- [ ] **Step 1:** `404.html`: tela dark centrada, símbolo do monitor com linha achatada (flatline — piada visual da marca), "404 — Perdemos o sinal desta página.", botão "Voltar ao início" e link WhatsApp.
- [ ] **Step 2:** `.htaccess` exatamente:

```apache
AddDefaultCharset utf-8
ErrorDocument 404 /404.html

<IfModule mod_expires.c>
  ExpiresActive On
  ExpiresByType text/css "access plus 7 days"
  ExpiresByType application/javascript "access plus 7 days"
  ExpiresByType font/woff2 "access plus 30 days"
  ExpiresByType image/svg+xml "access plus 30 days"
  ExpiresByType image/png "access plus 30 days"
</IfModule>
```

- [ ] **Step 3:** Verificar 404.html no navegador (headless screenshot).
- [ ] **Step 4:** Commit: `feat(site): página 404 e .htaccess (charset, cache, error page)`

### Task 4: Assets sociais — `og.png`, `favicon-32.png`, `apple-touch-icon.png`

**Files:**
- Create: `site/assets/img/og.png` (1200×630), `site/assets/img/favicon-32.png`, `site/assets/img/apple-touch-icon.png` (180×180)
- Create (temporário, fora do site): `og-template.html`, `icon-template.html`
- Modify: `site/index.html` e `site/privacidade.html` (links de ícone) — o `og:image` já aponta para `assets/img/og.png`

**Interfaces:**
- Consumes: tokens/SVG da marca.
- Produces: caminhos `assets/img/og.png`, `assets/img/favicon-32.png`, `assets/img/apple-touch-icon.png` referenciados nos `<head>`.

- [ ] **Step 1:** `og-template.html` (1200×630, body sem margem): fundo `#0B0F1A` com grade sutil, lockup horizontal (símbolo 120px + "+351 Monitor"), tagline "PRODUTIVIDADE EM TEMPO REAL" em verde espaçado, linha de pulso atravessando, url `mais351monitor.com.br` no rodapé.
- [ ] **Step 2:** Screenshot Edge headless `--window-size=1200,630` → `og.png`; conferir visualmente.
- [ ] **Step 3:** `icon-template.html`: quadrado cheio `#0B0F1A` com símbolo centralizado (78% da área); screenshots `--window-size=180,180` → `apple-touch-icon.png` e `--window-size=32,32` → `favicon-32.png`.
- [ ] **Step 4:** Adicionar aos dois `<head>`: `<link rel="icon" type="image/png" sizes="32x32" href="assets/img/favicon-32.png">` e `<link rel="apple-touch-icon" href="assets/img/apple-touch-icon.png">` e `<meta name="theme-color" content="#0B0F1A">`.
- [ ] **Step 5:** Commit: `feat(site): og image e ícones png gerados da marca`

### Task 5: `robots.txt` + `sitemap.xml`

**Files:**
- Create: `site/robots.txt`, `site/sitemap.xml`

- [ ] **Step 1:** `robots.txt`:

```
User-agent: *
Allow: /

Sitemap: https://www.mais351monitor.com.br/sitemap.xml
```

- [ ] **Step 2:** `sitemap.xml` com `https://www.mais351monitor.com.br/` (priority 1.0) e `https://www.mais351monitor.com.br/privacidade.html` (0.5), `lastmod` 2026-08-05.
- [ ] **Step 3:** Commit: `feat(site): robots.txt e sitemap.xml`

### Task 6: `DEPLOY.md` — passo a passo Hostinger

**Files:**
- Create: `site/DEPLOY.md`

- [ ] **Step 1:** Escrever guia: (1) hPanel → Sites → mais351monitor.com.br → Gerenciador de arquivos; (2) limpar `public_html` (remover `default.php` da página estacionada); (3) upload do conteúdo de `site/` (zip → extrair, sem a pasta-mãe; excluir `DEPLOY.md`); (4) ativar SSL/forçar HTTPS no hPanel; (5) testar `https://www.mais351monitor.com.br`, `/privacidade.html`, uma URL inexistente (404) e o preview de compartilhamento no WhatsApp; (6) alternativa via FTP (host, porta 21, credenciais do hPanel).
- [ ] **Step 2:** Commit: `docs(site): guia de publicação na Hostinger`

### Task 7: Verificação final

- [ ] **Step 1:** Grep em `site/*.html`: nenhuma ocorrência de `brun@` (só `bruna@`), nenhum `http://` externo, todos os `wa.me/5511992209235`.
- [ ] **Step 2:** Screenshots headless finais: landing desktop completa, landing mobile completa, privacidade desktop, 404 — inspecionar cada uma.
- [ ] **Step 3:** Abrir `site/index.html` no navegador do usuário para aceite final.

## Self-review

- Cobertura do spec: todos os arquivos da árvore do spec têm task (index/css/js/fontes → T1; privacidade → T2; 404/.htaccess → T3; og/ícones → T4; robots/sitemap → T5; DEPLOY → T6). ✓
- Sem placeholders; conteúdos e comandos concretos. ✓
- Consistência de nomes de arquivos entre tasks (`assets/img/og.png` etc.). ✓
