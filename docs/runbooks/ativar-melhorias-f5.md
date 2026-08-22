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
(`WeeklyDigestTests`, `FleetHealthTests`) e o `RunOnceAsync` aceita o instante como parâmetro.

| Feature | Quando dispara | Quem recebe | Como desligar |
|---|---|---|---|
| Resumo semanal | Segunda 08h **no fuso de cada org** | Owner e Admin ativos | Preferência do usuário no portal |
| Alertas de frota | A cada 15 min, só em horário de trabalho da org | Owner e Admin, **só plano `pro`** | Preferência do usuário no portal |
| Jornada semanal | Segunda 07h no fuso da org | Quem assinou no portal | Toggle do próprio usuário |

**Calibragem dos alertas** (já embutida, não precisa configurar): no máximo um e-mail por
organização por ciclo, cooldown de 24 h por dispositivo e tipo, silêncio fora do horário de
trabalho configurado. Se o cliente reclamar de ruído, o problema é a `business_hours` da org
estar vazia (aí tudo é "horário de trabalho"), não o alerta.

### Alertas são exclusivos do plano Pro

O gate lê `organizations.plan = 'pro'`. Para ativar num tenant, atualize o plano pelo backoffice
ou direto no banco. Contas em `trial` recebem o digest, mas não os alertas: é a primeira razão
objetiva de upgrade do produto, use isso na venda.

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

Preencha `CS_ALERT_EMAIL` em `infra/.env` (chega ao worker como `Cs__AlertEmail`) com o seu
e-mail. Toda segunda 09h BRT, uma hora depois do resumo semanal dos clientes, o worker apura as
contas em risco e manda **um** e-mail com a lista e um CSV anexo. Sem a variável, o job não é
registrado.

Ligue **depois** do início do piloto: antes disso a lista é vazia por definição.

**Por que isso existe:** a cobrança manual é o único sensor de churn hoje e ela avisa em D+20,
quando a decisão de cancelar já foi tomada. Estes sinais aparecem 30 a 60 dias antes, quando um
telefonema ainda salva a conta.

| Sinal | Janela | Pontos de risco |
|---|---|---|
| Nenhum login de usuário no portal | 14 dias | 30 |
| Nenhum dispositivo se comunicando | 7 dias | 30 |
| Queda de mais de 20% nos dispositivos com dados | semana contra semana | 25 |
| Nenhuma consulta a relatório nem export | 14 dias | 10 |
| 10 ou mais aplicativos em uso sem categoria | 30 dias | 5 |

O score de saúde é 100 menos a soma dos pontos: até 50 é **crítico**, até 75 é **atenção**.
Só entram no e-mail as contas com pelo menos um sinal; **semana sem conta em risco não gera
e-mail nenhum**, porque um "nada a relatar" toda segunda treina qualquer pessoa a ignorar a
mensagem. A execução fica registrada em `maintenance_runs` com as contagens, então dá para
conferir que o job rodou mesmo nas semanas silenciosas.

**Carência por idade da conta:** cada regra só vale se a organização for mais velha que a janela
dela. Sem isso, toda conta recém-criada nasceria crítica. O tenant de `DEMO_SLUG` também fica
fora da apuração: ele é re-semeado toda semana e seria um falso churn permanente.

**O CSV anexo** já sai no formato do importador do CRM interno (`Importar leads`), colunas
`empresa;contato;email;whatsapp;estacoes;origem;observacoes;cnpj`, UTF-8 com BOM. Contato e
e-mail são os do Owner ativo da conta; WhatsApp e CNPJ saem vazios porque o produto não guarda
esses campos da organização. O score e os sinais vão na coluna de observações. Contas já
cadastradas no CRM entram marcadas como duplicadas na pré-visualização, não viram lead novo.

**Isto é telemetria interna e não pode vazar para o cliente.** O conteúdo é agregado por
organização (contagem de dispositivos, datas de último acesso, contagem de ações de leitura),
nunca dado monitorado de pessoa. O job de propósito **não** aparece no dossiê da Central de
Conformidade do cliente: a saúde comercial da conta não é assunto dele.

---

## Ordem recomendada

1. Seção 1 inteira (backup, restore, monitoramento). Antes do primeiro agente em cliente.
2. Seção 2 (SMTP) e o plano `pro` do primeiro piloto que for pago.
3. Seção 3 (demo) junto com a prospecção.
4. Seção 4 (GHCR) antes de operar produção com clientes pagantes.
5. Seções 5 e 6 no dia do certificado e na assinatura do primeiro DPA.
6. Seção 7 quando o piloto começar.
