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
- **Janela de saída do backup (DPA):** um dado expurgado pela retenção (N10 raw 90d / N11
  intervals 12m / N12 agregados 24m / N13 auditoria 24m) ainda pode residir em backup por até
  **35 dias** — é o que se declara no DPA. A retenção dos backups (PITR do provedor e os
  `.dump`) deve portanto ser **≤ 35 dias**; o `backup.sh` usa 14 dias, dentro do limite.
- **`device_token`/segredos:** o dump guarda apenas hashes (SHA-256), não os tokens em claro.

## Perda zero pós-ack

O agente mantém buffer offline de 7 dias (N8) e o ack é pós-commit, então uma indisponibilidade
curta do banco (disponibilidade alvo 99,5%) não perde eventos: o agente retém e reenvia.
