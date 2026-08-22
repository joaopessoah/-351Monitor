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
4. **Configure a cifra antes de ligar a cópia.** O dump é a base inteira em um arquivo só,
   com dados pessoais dentro, e não pode sair da VPS em claro. Caminho preferido: um remote
   do tipo `crypt` do rclone, que cifra ainda na VPS. Guarde a senha do `crypt` no cofre,
   sem ela não existe restore, e ela não pode viver só na máquina que está sendo copiada.
5. Preencha em `infra/.env`:
   - `OFFSITE_CRYPT_REMOTE=m351crypt:` (remote `crypt`, tem precedência no upload e no check)
   - ou, só se a cifra ficar por conta do bucket, `OFFSITE_RCLONE_REMOTE=m351offsite:m351-backups`
     **mais** `OFFSITE_SSE_CONFIRMADO=sim`, que você só declara depois de conferir a cifra em
     repouso no console do provedor
   - `RCLONE_CONFIG=/etc/m351/rclone.conf`
   - `OFFSITE_RETENTION_DAYS=30`
   Sem nenhuma das duas posturas o `backup.sh` imprime um aviso grave e **continua mesmo
   assim**, de propósito: dump íntegro sem cifra ainda vale mais que ficar sem cópia do dia.
   Se esse aviso aparecer no log, trate como incidente, não como ruído.
6. Crie um check no healthchecks.io (plano gratuito serve) e preencha `HEALTHCHECKS_BACKUP_URL`.
   O ping só acontece **depois** de o upload ser validado, então "ping ausente" significa
   backup ausente, e o healthchecks avisa.
7. Adicione o provedor de object storage à **lista de subprocessadores do DPA**
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
- **O texto do aviso de ciência também é editável (fechamento da F5).** Em
  **Configurações > Coleta**, o Proprietário escreve o corpo do aviso na linguagem da empresa
  e vê o preview do texto final. O fecho é acrescentado pelo próprio agente, na máquina, e
  nenhuma configuração o remove: é ele que deixa explícito que aquilo registra **ciência** e
  não pede consentimento, que é o que sustenta a base legal. Por isso o servidor recusa texto
  com HTML ou marcação, texto que não caiba na janela já contando o fecho, e texto que imite
  pedido de consentimento ou aceite.
- **Salvar o texto reexibe o aviso em toda a frota** (sobe `notice_version`). Combine a
  mudança com a controladora antes de salvar, e não faça isso no meio de um piloto sem avisar.
  Atenção: o `notice_acked_at` de quem já deu ciência **não** é zerado, então o painel segue
  contando esses dispositivos como cientes até o agente reexibir o aviso e mandar o
  `NOTICE_ACK` novo.

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

## 8. Painel de atividade fora do horário de trabalho

Este não depende de variável de ambiente: depende de **duas configurações da organização
cliente**, e sem elas a tela mostra uma explicação em vez de um número.

1. **Horário de trabalho declarado** em `/configuracoes/organizacao` (dias da semana e hora de
   início e fim). Sem isso o endpoint responde `horario_nao_configurado` e a tela convida a
   configurar, em vez de fingir que o tempo fora do horário é zero.
2. **Janela de coleta contínua** em `/configuracoes/coleta`. Se a controladora escolheu coletar
   **somente durante o horário de trabalho**, não existe dado fora dele por construção: o
   endpoint responde `coleta_restrita_ao_horario` e a exportação recusa com 409, para nunca
   gerar um CSV de zeros que seria lido como "ninguém trabalha fora do horário".

Onde aparece: card na Visão Geral com a semana corrente, aba
`/relatorios/uso?aba=fora-do-horario` com o detalhe por dispositivo, e o kind de exportação
`fora_horario_csv`.

**Isto é um indicador de equilíbrio, nunca um controle de ponto.** O número soma apenas tempo
**ativo** fora da janela declarada, no fuso do tenant. Máquina ligada e ociosa às 22h não entra,
justamente para o indicador não inflar e virar leitura de jornada estendida. Não existe coluna
de excedente, saldo nem comparação com carga horária esperada, e o disclaimer da Portaria 671
acompanha a tela e a última linha do CSV. Ao apresentar para o cliente, trate como conversa de
saúde de equipe, jamais como insumo de hora extra ou banco de horas.

Uma divergência conhecida e esperada: o tempo ativo deste painel sai de `activity_intervals` e
o da aba de Uso ao lado sai dos agregados diários, então os dois podem diferir por poucos
segundos no mesmo recorte. O percentual é internamente consistente porque numerador e
denominador saem da mesma fonte.

---

## 9. O que passou a ter porta de entrada no fechamento da F5

Diferente das seções acima, **nada aqui depende de variável de ambiente**: já está no ar assim
que a `main` for publicada. O checklist é de conferência e de conversa com o cliente.

- **Filtro de equipe nos relatórios e no dashboard.** O seletor aparece sozinho quando a
  organização tem pelo menos uma etiqueta em `Dispositivos`. Se o cliente não vê o seletor, o
  que falta é etiquetar dispositivo, não configuração. Confira que o CSV exportado com filtro
  traz o mesmo recorte da tela. Ao apresentar, deixe claro que é recorte de visualização e que
  o produto não compara equipes lado a lado nem monta ranking entre elas.
- **Sugestão do dicionário de apps.** Na tela de Aplicativos, apps sem categoria ganham a
  sugestão do dicionário brasileiro, aplicável em um clique ou em lote. O lote **sempre** passa
  por uma prévia que mostra quantos apps e quais categorias antes de qualquer escrita. Se a
  organização renomeou as categorias de fábrica, a sugestão simplesmente não aparece, porque a
  tradução é pelo nome exato da categoria. Chame de "sugestão do dicionário" na frente do
  cliente, nunca de "categorização automática".
- **Vigilância de rollout do agente.** O card "Versões do agente na frota", em Dispositivos,
  já funciona hoje para a distribuição de versões. As **falhas** de auto-update (`UPDATE_FAILED`)
  só aparecem depois que as máquinas subirem para uma versão do agente que emita o evento:
  rollout agente-primeiro, igual ao do `AGENT_ERROR`. Confira o card depois de cada publicação
  de release, é a resposta a "o update chegou em todo mundo?".
- **Link de transparência por dispositivo.** O token chega ao agente pela config na próxima
  reentrega, e a partir daí o tray abre a página daquela máquina em vez da página da
  organização. No portal, o endereço fica no menu de ações da linha, em Dispositivos, visível
  só para Admin+. Dispositivo revogado não mostra o link. Use isso no onboarding: é o argumento
  de transparência mais forte que existe, o funcionário vê a própria página.
- **Cartão de preview do portal.** `portal/index.html` passou a emitir `og:image` e as metatags
  de preview, então o link do painel colado no WhatsApp ou no LinkedIn mostra a marca. Confira
  uma vez, depois do deploy, colando a URL do painel em uma conversa de teste.

---

## Ordem recomendada

1. Seção 1 inteira (backup, restore, monitoramento). Antes do primeiro agente em cliente.
2. Seção 2 (SMTP) e o plano `pro` do primeiro piloto que for pago.
3. Seção 3 (demo) junto com a prospecção.
4. Seção 4 (GHCR) antes de operar produção com clientes pagantes.
5. Seções 5 e 6 no dia do certificado e na assinatura do primeiro DPA.
6. Seção 7 quando o piloto começar.
7. Seção 8 no onboarding de cada cliente, junto com a decisão da janela de coleta.
8. Seção 9 não tem ordem: é conferência pós-deploy e roteiro de conversa com o cliente.
