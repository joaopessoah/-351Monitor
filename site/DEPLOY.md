# Publicar o site na Hostinger

O domínio `mais351monitor.com.br` já está registrado e apontado na Hostinger (hoje serve a página estacionada). Publicar = colocar os arquivos desta pasta em `public_html`.

## O que sobe (e o que não sobe)

Sobe **todo o conteúdo desta pasta `site/`**, exceto este `DEPLOY.md`:

```
index.html          privacidade.html     404.html
.htaccess           robots.txt           sitemap.xml
assets/  (css, js, fonts, img)
```

> Atenção: `.htaccess` começa com ponto e pode ficar oculto no gerenciador de arquivos — confirme que ele subiu (ative "mostrar arquivos ocultos").

## Opção A — Gerenciador de arquivos do hPanel (mais simples)

1. Compacte o conteúdo da pasta `site/` em um zip **sem a pasta-mãe** (selecione os arquivos e pastas de dentro → enviar para zip). No PowerShell:
   `Compress-Archive -Path C:\dev\351-monitor\site\* -DestinationPath C:\dev\351-monitor\site-deploy.zip -Force`
2. hPanel → **Sites** → mais351monitor.com.br → **Gerenciador de Arquivos**.
3. Entre em `public_html` e **apague o conteúdo atual** (a página estacionada — normalmente um `default.php`).
4. **Upload** do zip → botão direito → **Extract** (extrair) dentro de `public_html`.
5. Apague o zip e o `DEPLOY.md` extraído (se subiu junto).

## Opção B — FTP

1. hPanel → **Arquivos → Contas FTP**: anote host (`ftp.mais351monitor.com.br` ou IP), usuário e senha (crie uma conta se não houver).
2. Com FileZilla/WinSCP: conecte na porta 21, navegue até `public_html`, apague o conteúdo antigo e arraste o conteúdo de `site/`.

## Opção C — Automático via GitHub Actions (configurado)

O workflow `.github/workflows/deploy-site.yml` envia a pasta `site/` para `public_html` por FTPS **a cada push na `main` que altere `site/**`** (ou manualmente em GitHub → Actions → "Deploy site institucional" → Run workflow).

Configuração única (uma vez):

1. Crie o site no hPanel (**Site PHP/HTML personalizado**, domínio mais351monitor.com.br) — o hosting precisa existir.
2. hPanel → **Arquivos → Contas FTP**: anote o **host** (IP ou `ftp.mais351monitor.com.br`) e o **usuário** principal, e defina/anote a **senha**.
3. No GitHub, no repositório: **Settings → Secrets and variables → Actions → New repository secret**, crie os três:
   - `SITE_FTP_HOST` — o host do passo 2 (sem `ftp://`)
   - `SITE_FTP_USER` — o usuário
   - `SITE_FTP_PASSWORD` — a senha
4. Primeira publicação: apague o placeholder da Hostinger em `public_html` (File Manager) e rode o workflow manualmente (Actions → Run workflow). Nas seguintes, é só dar push.

Notas: o workflow usa espelhamento incremental (só envia o que mudou) e mantém um arquivo de estado `.ftp-deploy-sync-state.json` no servidor — o `.htaccess` já bloqueia acesso público a ele. Se a conexão FTPS falhar na sua conta, troque `protocol: ftps` por `ftp` no workflow.

## Depois de subir — checklist

1. hPanel → **Segurança → SSL**: confirme certificado ativo para o domínio e **ative "Forçar HTTPS"**.
2. Teste no navegador (janela anônima):
   - `https://www.mais351monitor.com.br` → landing carrega com fontes e favicon;
   - `https://www.mais351monitor.com.br/privacidade.html` → página de privacidade;
   - `https://www.mais351monitor.com.br/qualquercoisa` → página 404 personalizada (prova que o `.htaccess` subiu);
   - clique em "Agendar demonstração" → abre o WhatsApp com a mensagem pronta.
3. Teste o cartão de compartilhamento: envie o link para você mesmo no WhatsApp — deve aparecer a imagem escura com o logo (og.png). Se aparecer versão antiga em outra rede, force a atualização em https://developers.facebook.com/tools/debug/ (Instagram/Facebook) colando a URL.
4. Google: cadastre a propriedade em https://search.google.com/search-console (verificação por DNS na própria Hostinger) e envie o `sitemap.xml`.

## Atualizações futuras

Edite os arquivos em `site/` no repositório, commit, e repita o upload (só dos arquivos alterados, se preferir). O site não tem build — o que está na pasta é o que vai ao ar.
