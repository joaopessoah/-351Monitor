# Runbook — Backup e Restore (PostgreSQL)

> Cumpre a F4 ("backup/restore testado") e os NFRs da Seção 7.7 / 9.6 do PROMPT-DESENVOLVIMENTO.
> Backup/restore é **procedimento operacional**, não código da aplicação — este runbook é a fonte.

## Estratégia (duas camadas)

1. **PITR do PostgreSQL gerenciado (camada primária).** Em produção o banco é um
   PostgreSQL gerenciado em região brasileira (Azure Database for PostgreSQL Flexible
   Server — Brazil South, ou AWS RDS sa-east-1). O provedor faz *point-in-time recovery*
   contínuo; configurar a janela de retenção do provedor para **≤ 35 dias** (ver LGPD abaixo).
2. **`pg_dump` lógico (camada extra, portável).** Dump diário em formato *custom* via
   `infra/scripts/backup.sh` (Linux/VPS de staging) ou `infra/scripts/backup.ps1` (Windows/dev).
   Serve para restore seletivo, migração entre provedores e cópia fria fora do provedor —
   sempre em armazenamento **no Brasil** e com acesso restrito.

## Agendamento (staging — VPS)

`backup.sh` roda via cron às 02:15 (após os jobs de retenção 02:00–03:00, para o dump já
refletir o estado pós-purga do dia):

```
15 2 * * * /usr/bin/bash /opt/351monitor/infra/scripts/backup.sh >> /var/log/m351-backup.log 2>&1
```

Saída: `/var/backups/m351/m351_<db>_<stamp>.dump`. Retenção local: **14 dias** (`RETENTION_DAYS`).

Antes de qualquer cópia, o script valida o dump com `pg_restore --list`: arquivo truncado
ou corrompido é apagado e o backup falha, para não passar por bom nem subir para o off-site.

## Cópia off-site cifrada (obrigatória antes do primeiro cliente)

Um dump é a base inteira em um arquivo só, com dados pessoais dentro. Ele **não pode**
sair da VPS sem cifra. O `backup.sh` faz a cópia com `rclone` e reconhece duas posturas:

| Variável (`infra/.env`) | O que faz |
| --- | --- |
| `OFFSITE_CRYPT_REMOTE` | Remote do tipo `crypt` do rclone. Cifra no cliente, o dump sai da VPS já ilegível. **Forma preferida** e tem precedência no upload e no `rclone check`. |
| `OFFSITE_RCLONE_REMOTE` | Remote puro, mantido por compatibilidade. A cifra fica por conta do bucket, então só é aceitável junto com `OFFSITE_SSE_CONFIRMADO=sim`. |
| `OFFSITE_SSE_CONFIRMADO` | `sim` declara que a cifra em repouso do bucket (SSE-S3, SSE-KMS ou Azure SSE) foi conferida no console do provedor. |
| `OFFSITE_RETENTION_DAYS` | Retenção remota, padrão **30 dias** (teto do DPA: 35). |

**Se nenhuma cifra estiver declarada**, o script imprime um aviso grave no log e
**continua mesmo assim**. A escolha é deliberada e está comentada no próprio
`backup.sh`: um dump íntegro que subiu sem cifra ainda é melhor que ficar sem cópia
do dia, e falhar o backup por um problema de postura trocaria um risco por outro
maior. O aviso é gritante justamente para não virar rotina, trate como incidente.

Fluxo da cópia: `rclone copy` do dump, `rclone check --one-way` do arquivo recém-enviado
(com crypt o rclone compara o conteúdo já decifrado; para conferência byte a byte, rode
manualmente com `--download`), e por fim `rclone delete --min-age` aplicando a retenção
remota. Com object lock em modo compliance a exclusão só ocorre depois do prazo de
bloqueio: nesse caso o script apenas avisa, não falha.

### Provisionamento do bucket, requisitos que não são negociáveis

1. **Região brasileira.** As opções válidas são **AWS S3 `sa-east-1`**, **Azure Blob
   Brazil South** ou **Magalu Object Storage**. Backblaze B2 **não** tem região no
   Brasil e violaria o compromisso público de hospedagem 100% Brasil, não use.
2. **Object lock ligado** (WORM), para ransomware não conseguir apagar a cópia fria.
   Prazo de bloqueio alinhado à retenção: 30 dias.
3. **Cifra em repouso ativa** no bucket, mesmo quando já existe remote `crypt`.
4. **Retenção de 30 dias**, dentro do teto de **35 dias** declarado no DPA (ver abaixo).
5. **Credencial dedicada** ao backup, com escopo só nesse bucket, e sem permissão de
   apagar objeto antes do prazo.
6. **Somar o provedor à lista de subprocessadores** publicada na página de privacidade
   e no DPA. Escolher o bucket é decisão contratual, não só de infra.
7. **Senha do remote `crypt` no cofre.** Sem ela não existe restore, e ela não pode
   viver só na VPS que está sendo copiada.

## Restore — teste em staging (item "pronto quando" da F4)

Procedimento validável (executar na VPS de staging — exige acesso SSH, ver memória `staging-acesso`):

1. Escolher um dump recente em `/var/backups/m351/`.
2. (Opcional, recomendado) restaurar num banco temporário para não tocar o staging vivo:
   ```
   docker compose -f /opt/351monitor/infra/docker-compose.staging.yml --env-file /opt/351monitor/infra/.env \
     exec -T postgres psql -U "$POSTGRES_USER" -d postgres -c 'CREATE DATABASE m351_restore_test;'
   docker compose ... exec -T postgres pg_restore -U "$POSTGRES_USER" -d m351_restore_test < ARQUIVO.dump
   ```
3. Conferir contagens-chave no banco restaurado (ex.: `SELECT count(*) FROM raw_events;`,
   `activity_intervals`, `daily_device_summaries`, `audit_log`, `devices`) e comparar com o
   banco vivo (ordens de grandeza coerentes).
4. Restore *in-place* (recuperação real de desastre): `pg_restore --clean --if-exists -d "$POSTGRES_DB"`.
5. Registrar a data do teste de restore (este runbook ou o ticket de operação).

> **Atenção partições.** `raw_events`/`activity_intervals`/`audit_log` são particionadas por
> tempo. O `pg_dump --format=custom` captura as partições existentes no momento do dump; após
> o restore, o job `PartitionMaintenance` (02:00 BRT) recria as partições futuras e dropa as
> expiradas normalmente. Nenhuma ação manual de partição é necessária no restore.

## LGPD / DPA

- **Residência:** dumps contêm dados pessoais — armazenar **somente no Brasil**, com acesso restrito.
  Vale igual para a cópia off-site: só S3 `sa-east-1`, Azure Brazil South ou Magalu Object Storage.
- **Cifra:** cópia fora da VPS só com cifra declarada, no cliente (`crypt` do rclone) ou em
  repouso no bucket (SSE). Sem isso, um vazamento do bucket expõe a base inteira em claro e
  dispara comunicação à ANPD e aos titulares (LGPD art. 48).
- **Subprocessadores:** o provedor do bucket de backup é subprocessador. Somar à lista
  publicada na página de privacidade e ao DPA antes de ligar a cópia off-site.
- **Janela de saída do backup (DPA):** um dado expurgado pela retenção (N10 raw 90d / N11
  intervals 12m / N12 agregados 24m / N13 auditoria 24m) ainda pode residir em backup por até
  **35 dias**, é o que se declara no DPA. A retenção dos backups (PITR do provedor, os `.dump`
  locais e a cópia off-site) deve portanto ser **≤ 35 dias**; o `backup.sh` usa 14 dias local e
  30 dias remoto, dentro do limite. O prazo do object lock não pode passar de 35 dias.
- **`device_token`/segredos:** o dump guarda apenas hashes (SHA-256), não os tokens em claro.

## Perda zero pós-ack

O agente mantém buffer offline de 7 dias (N8) e o ack é pós-commit, então uma indisponibilidade
curta do banco (disponibilidade alvo 99,5%) não perde eventos: o agente retém e reenvia.
