# Infra — +351 Monitor (staging)

Staging roda em **1 VPS Hostinger (Ubuntu 22.04+, datacenter São Paulo)** com Docker Compose: `caddy` (TLS automático) → `api` (ASP.NET Core 8 + SPA em wwwroot) + `worker`, `seq` (logs) e `postgres:16`.

> **Residência BR (LGPD):** todos os dados (banco, logs, backups) ficam em território brasileiro. A VPS DEVE estar no datacenter de **São Paulo**; backups não saem do Brasil.
>
> **Adaptação declarada:** o spec pede Postgres **gerenciado** (Seção 4); no staging da VPS o Postgres roda em **container** (volume nomeado + healthcheck). Produção migra para gerenciado (Brazil South / sa-east-1).

## Subir o staging na VPS

```bash
# 1. Pré-requisitos (uma vez)
apt-get update && apt-get install -y git docker.io docker-compose-v2
useradd -m -G docker deploy        # usuário de deploy (chave SSH em ~/.ssh/authorized_keys)

# 2. Clonar o repo
git clone <URL-DO-REPO> /opt/m351

# 3. Segredos
cp /opt/m351/infra/.env.example /opt/m351/infra/.env
nano /opt/m351/infra/.env        # preencher domínio, senhas, JWT key, Sentry DSN

# 4. Subir (build local — ainda sem registry)
cd /opt/m351
docker compose -f infra/docker-compose.staging.yml --env-file infra/.env up -d --build
```

DNS: aponte `STAGING_DOMAIN` (A record) para o IP da VPS **antes** do primeiro `up` — o Caddy emite o certificado Let's Encrypt automaticamente. Firewall: liberar só 22/80/443.

## Onde ficam os segredos

| Onde | O quê |
|---|---|
| `infra/.env` na VPS (NUNCA commitado; `.gitignore` cobre) | senha do Postgres, `JWT_SIGNING_KEY`, Sentry DSN, hash de admin do Seq |
| GitHub → Settings → Secrets and variables → Actions | `STAGING_SSH_HOST`, `STAGING_SSH_KEY` (chave privada OpenSSH), `STAGING_SSH_USER` (opcional, default `deploy`) |

Sem os secrets `STAGING_SSH_*`, o job `deploy-staging` do CI faz **skip com aviso** (não falha). Com eles, todo push na `main` deploya automaticamente (build+test → docker → SSH).

## Backup e restore

- **Backup diário** (pg_dump custom, retenção 14 dias): `infra/scripts/backup.sh` — agende no cron da VPS:
  ```
  15 2 * * * /usr/bin/bash /opt/m351/infra/scripts/backup.sh >> /var/log/m351-backup.log 2>&1
  ```
  Dumps em `/var/backups/m351/` (mantenha no Brasil; acesso restrito).
- **Backup no Windows/dev**: `infra/scripts/backup.ps1` (instruções de Task Scheduler no cabeçalho do script).
- **Restore** (testar periodicamente — requisito da Seção 7.7):
  ```bash
  docker compose -f infra/docker-compose.staging.yml --env-file infra/.env \
    exec -T postgres pg_restore -U m351 -d m351_staging --clean --if-exists < /var/backups/m351/ARQUIVO.dump
  ```

## Observabilidade

- **Seq** (logs Serilog): exposto só em loopback da VPS — `ssh -L 8341:127.0.0.1:8341 deploy@<vps>` e abra `http://localhost:8341`.
- **Sentry**: configure `SENTRY_DSN` no `.env` (API e worker reportam exceções não tratadas e erros dos jobs; vazio = desativado).
- **Healthcheck**: `https://<STAGING_DOMAIN>/healthz` (banco conectável) e `https://<STAGING_DOMAIN>/readyz` (banco + última manutenção com sucesso há menos de 26 h).

## Monitoramento do próprio SaaS

Quem monitora o monitor: sem estes passos, uma queda do staging (ou do worker, ou do backup) passa despercebida.

1. **Monitor de uptime gratuito** (UptimeRobot ou Better Stack): crie 2 monitores HTTP
   apontando para `https://<STAGING_DOMAIN>/healthz` e `https://<STAGING_DOMAIN>/readyz`,
   intervalo de 5 min, alerta por e-mail. O `/readyz` só responde 200 quando o banco
   conecta E a última execução com sucesso em `maintenance_runs` tem menos de 26 horas,
   ou seja, ele detecta worker parado mesmo com a API de pé.
2. **healthchecks.io** (plano gratuito): crie 3 checks:
   - **backup** (período: 1 dia, grace: 3 h): cole a URL de ping em
     `HEALTHCHECKS_BACKUP_URL` no `infra/.env`. O `backup.sh` pinga só após dump e
     cópia off-site validada; ping ausente = backup quebrado.
   - **worker** (período: 5 min, grace: 10 min): cole a URL em
     `HEALTHCHECKS_WORKER_URL` no `infra/.env`. O worker pinga a cada 5 min
     (DeadManSwitchJob); ping ausente = worker morto ou travado.
   - **disco** (período: 30 min, grace: 1 h): cole a URL em `HEALTHCHECKS_DISK_URL`
     no `infra/.env`. O `check-disk.sh` pinga sucesso abaixo de 80% de uso e
     `URL/fail` acima.
3. **Aplicar**: depois de preencher o `.env`, `docker compose ... up -d` para o worker
   reler a env (o backup lê o `.env` a cada execução do cron).

## Higiene de disco

- **Rotação de logs de container**: todos os serviços do compose usam o driver
  `json-file` com `max-size: 10m` e `max-file: 3` (âncora `x-logging` no topo do
  `docker-compose.staging.yml`); sem isso, logs de container enchem o disco.
- **Limites de memória**: `mem_limit` de 768m na api e no worker e 1g no seq. O
  postgres fica **sem** limite de propósito (o banco não pode ser alvo do OOM killer
  do cgroup; o teto dos demais é o que protege a RAM dele).
- **check-disk.sh**: agende no cron da VPS; acima de 80% de uso sai com erro e pinga
  a URL de falha do check (`HEALTHCHECKS_DISK_URL/fail`):
  ```
  */30 * * * * /usr/bin/bash /opt/351monitor/infra/scripts/check-disk.sh >> /var/log/m351-disk.log 2>&1
  ```
- **Retenção do Seq**: o volume `seq_data` cresce sem limite se nenhuma política de
  retenção existir. Aplique uma (ex.: apagar eventos após 14 dias) por um dos caminhos:
  - **UI** (recomendado, uma vez): túnel `ssh -L 8341:127.0.0.1:8341 deploy@<vps>`,
    abra `http://localhost:8341`, Settings, Retention, "Add retention policy".
  - **seqcli**: `seqcli retention create --after 14d --delete-all-events -s http://127.0.0.1:8341`
    (com `-a <api-key>` se o Seq tiver autenticação).
  - **deploy**: o `deploy-staging.sh` tem um passo opcional, desligado por padrão;
    exporte `SEQ_RETENTION_DAYS=14` na chamada do deploy para aplicá-lo via seqcli
    (idempotente, só cria quando nenhuma política existe).

## Dev local

```bash
docker compose -f infra/docker-compose.dev.yml up -d   # só o Seq (UI em :8341, ingestão em :5341)
```
Postgres em dev é local (`m351_dev`), fora de container; API/portal rodam com `dotnet run` / `npm run dev`.
