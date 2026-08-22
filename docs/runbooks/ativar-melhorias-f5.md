# Ativar as melhorias da F5 (checklist do operador)

Boa parte do que foi entregue na F5 fica **dormente até você preencher uma variável de
ambiente ou tomar uma decisão externa**. Isso é deliberado: nada liga sozinho em staging nem
começa a mandar e-mail para cliente sem alguém decidir. Este runbook é a ordem de ativação.

Tudo abaixo mora em `infra/.env` (na VPS) e, quando indicado, em segredos do GitHub. O
template comentado está em `infra/.env.example`.

---

## 1. Proteger os dados (fazer primeiro, antes de qualquer cliente real)

### 1.1 Backup off-site verificável

O `pg_dump` diário já roda, mas **no mesmo disco da VPS**. Sem os itens abaixo, perder o disco
é perder os dados e os backups juntos.

1. Crie um bucket em **região brasileira** (S3 `sa-east-1`, Azure Brazil South ou Magalu). Nunca
   fora do Brasil: hospedagem 100% BR é compromisso público do produto e cláusula de DPA.
2. Ative object lock (proteção contra ransomware) e retenção de **30 dias** (o teto do DPA é 35).
3. Configure o `rclone` na VPS com um `rclone.conf` fora do repositório, acesso restrito.
4. Preencha em `infra/.env`:
   - `OFFSITE_RCLONE_REMOTE=m351offsite:m351-backups`
   - `RCLONE_CONFIG=/etc/m351/rclone.conf`
   - `OFFSITE_RETENTION_DAYS=30`
5. Crie um check no healthchecks.io (plano gratuito serve) e preencha `HEALTHCHECKS_BACKUP_URL`.
   O ping só acontece **depois** de o upload ser validado, então "ping ausente" significa
   backup ausente, e o healthchecks avisa.
6. Adicione o provedor de object storage à **lista de subprocessadores do DPA**
   (`docs/runbooks/kit-lgpd.md`).

### 1.2 Teste de restore automatizado

O workflow `.github/workflows/restore-test.yml` roda no dia 1 de cada mês (e sob
`workflow_dispatch`) usando os segredos `STAGING_SSH_*` que o deploy já usa. Sem eles, o job faz
skip com aviso. Dispare **uma vez manualmente** agora: backup não testado é backup hipotético,
e a evidência do restore é anexo do kit LGPD em procurement.

### 1.3 Quem monitora o monitor

1. Monitor de uptime externo gratuito (UptimeRobot ou Better Stack) apontando para
   `https://SEU-DOMINIO/healthz` e `https://SEU-DOMINIO/readyz`.
   - `/healthz` = a API responde e o banco conecta.
   - `/readyz` = o acima **mais** a idade da última execução de job com sucesso (menor que 26 h).
     Num banco recém-criado, `/readyz` responde 503 até a primeira rodada dos jobs noturnos.
     Isso é o comportamento esperado, não configure alarme antes da primeira noite.
2. Dead-man switch do worker: crie um segundo check no healthchecks.io e preencha
   `HEALTHCHECKS_WORKER_URL`. Sem a variável, o job nem é registrado.
3. Dead-man switch do disco: terceiro check, `HEALTHCHECKS_DISK_URL`, com a linha de cron do
   `infra/scripts/check-disk.sh` (o cabeçalho do script traz a linha pronta).
4. Sentry: preencha `SENTRY_DSN` e a API e o worker passam a reportar exceções. Vazio = desligado.

---

## 2. Ligar o e-mail (pré-requisito de digest, alertas e jornada semanal)

O worker agora envia e-mail e usa **os mesmos** `Email__*` da API, já plumbados no compose.
Confirme na VPS que `EMAIL_PROVIDER=Smtp` e que `SMTP_HOST`, `SMTP_USERNAME`, `SMTP_PASSWORD` e
`SMTP_FROM_ADDRESS` estão preenchidos. Em dev, `Email__Provider=Dev` grava `.txt` em disco.

Depois de configurar, valide sem esperar segunda-feira: os serviços são invocáveis por teste
(`WeeklyDigestTests`, `FleetHealthTests`, `JornadaWeeklyReportTests`) e o `RunOnceAsync` aceita o
instante como parâmetro.

| Feature | Quando dispara | Quem recebe | Como desligar |
|---|---|---|---|
| Resumo semanal | Segunda 08h **no fuso de cada org** | Owner e Admin ativos | Preferência do usuário no portal |
| Alertas de frota | A cada 15 min, só em horário de trabalho da org | Owner e Admin, **só plano `pro`** | Preferência do usuário no portal |
| Jornada semanal | Segunda 07h no fuso da org | Quem assinou no portal, **só plano `pro`** | Toggle na tela do Relatório de Jornada ou em Configurações |

**Como a jornada semanal funciona por dentro:** o job de 5 em 5 minutos enfileira, na janela das
07h locais, um export `jornada_csv` da semana fechada no MESMO pipeline assíncrono do botão
"Exportar CSV", com `requested_by` apontando para o assinante (a trilha `export_csv` continua
respondendo quem gerou o arquivo). Quando o `ExportService` fecha o arquivo, o ciclo seguinte
manda o e-mail. O corpo leva **link** para o download autenticado no portal, **nunca anexo**:
planilha de jornada é dado pessoal da equipe e não circula por e-mail. O disclaimer da Portaria
671 vai verbatim no corpo. Semana sem nenhum dispositivo ainda gera e-mail, dizendo que não houve
atividade, para silêncio na segunda nunca ser lido como produto quebrado.

**Calibragem dos alertas** (já embutida, não precisa configurar): no máximo um e-mail por
organização por ciclo, cooldown de 24 h por dispositivo e tipo, silêncio fora do horário de
trabalho configurado. Se o cliente reclamar de ruído, o problema é a `business_hours` da org
estar vazia (aí tudo é "horário de trabalho"), não o alerta.

### Alertas de frota e jornada semanal são exclusivos do plano Pro

O gate lê `organizations.plan = 'pro'` nos dois casos. Para ativar num tenant:

```bash
docker exec m351-staging-api-1 dotnet M351.Api.dll set-org-plan --org-slug empresa-x --plan pro
```

O comando aceita `trial`, `essencial` e `pro`, e não mexe no `device_limit` (o limite de
dispositivos é régua de contrato, decidida caso a caso). Contas em `trial` ou `essencial` recebem
o digest semanal, mas não os alertas nem o relatório agendado: é a primeira razão objetiva de
upgrade do produto, use isso na venda.

O gate também vale no portal e na API: fora do Pro o toggle da jornada semanal aparece
desabilitado com a nota do plano, e o `PATCH /me/email-prefs` responde 403 ao tentar **ligar** a
assinatura. **Desligar** é sempre permitido, para um downgrade não prender ninguém numa assinatura
que não consegue cancelar. As assinaturas ficam gravadas e voltam a valer se o plano subir de
novo.

---

## 3. Demo pública permanente

O tenant demo (30 dispositivos, 60 dias de dados gerados pelo pipeline real) deixa de depender
de um console aberto.

1. Semeie uma vez: CLI `seed-demo-tenant` (veja `docs/runbooks/`), anotando a credencial viewer.
2. Em `infra/.env`:
   - `DEMO_SLUG=empresa-demo` (liga o keep-alive de 60 s e o reseed de domingo 04:30 BRT)
   - `DEMO_VIEWER_PASSWORD=` uma senha dedicada. **Importante:** sem ela, cada reseed gera
     senha nova e o link já enviado a prospects para de funcionar.
   - `DEMO_DOMAIN=demo.mais351monitor.com.br` e o DNS apontando para a VPS.
3. Descomente o CTA "Explorar a demo ao vivo" em `site/index.html` (as instruções estão no
   próprio comentário do HTML).

O reseed semanal também limpa a trilha de auditoria do tenant demo, então os acessos públicos da
semana não se acumulam.

---

## 4. Deploy por imagem (GHCR)

O CI já publica `ghcr.io/<owner>/m351-api` e `m351-worker` com tag `sha-<commit>` e `staging`.
O `deploy-staging.sh` mantém `DEPLOY_BUILD=1` (build na VPS) como default **até você validar o
primeiro pull**. Depois do primeiro push de imagem bem-sucedido:

1. Configure `GHCR_TOKEN` (segredo do GitHub) conforme o `infra/README.md`.
2. Troque o default para `DEPLOY_BUILD=0` no script.
3. A partir daí, rollback é `IMAGE_TAG=sha-<commit-anterior> docker compose up -d`, em segundos.

O deploy agora também roda `backup.sh` **antes** de subir (proteção contra `AutoMigrate` com
migração destrutiva) e falha o job de CI se o `/healthz` não voltar em 60 s.

---

## 5. Auto-update do agente

1. O volume `releases_data` e `Releases__Directory` já estão no compose: um MSI publicado não se
   perde mais no próximo deploy.
2. Publique um release com o CLI `publish-agent-release` (passo a passo em
   `docs/runbooks/publicar-release-agente.md`).
3. **Verificação Authenticode**: fica desligada por flag até o certificado ser comprado. No dia
   da compra (Certum ou Sectigo, veja `comprar-certificado-codesigning.md`), ligue
   `verify_authenticode` e `expected_signer_cn` no `install.json` da versão empacotada.

---

## 6. Política de coleta e ciência (decisão da controladora, não da operadora)

- A tela **Configurações > Política de coleta** (Owner) já edita política de títulos
  (mascarado ou somente aplicativo), padrões de mascaramento, aplicativos nunca coletados,
  limiar de ociosidade e janela de coleta. Cada mudança dá bump de versão, chega à frota no
  próximo contato do agente e vai para a trilha de auditoria com o de/para.
- `FULL` (títulos sem mascaramento) **não** é selecionável no portal: exige decisão registrada
  em DPA e aplicação pela operadora. O kit LGPD já foi atualizado com essa divisão.
- Ao mudar a janela de coleta, além do `update_privacy_config` fica registrada a ação
  `collection_window_choice`: é a evidência de que **a controladora** escolheu.

---

## 7. Score de saúde de conta (CS interno)

Preencha `Cs__AlertEmail` com o seu e-mail para receber, toda segunda 09h BRT, a lista de contas
em risco (sem login há 14 dias, queda de dispositivos ativos, tenant sem eventos há 7 dias), em
formato importável no CRM interno. Sem a variável, o job não é registrado.

Ligue **depois** do início do piloto: antes disso a lista é vazia por definição.

---

## Ordem recomendada

1. Seção 1 inteira (backup, restore, monitoramento). Antes do primeiro agente em cliente.
2. Seção 2 (SMTP) e o plano `pro` do primeiro piloto que for pago.
3. Seção 3 (demo) junto com a prospecção.
4. Seção 4 (GHCR) antes de operar produção com clientes pagantes.
5. Seções 5 e 6 no dia do certificado e na assinatura do primeiro DPA.
6. Seção 7 quando o piloto começar.
