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
- **Sentry**: configure `SENTRY_DSN` no `.env`.
- **Healthcheck**: `https://<STAGING_DOMAIN>/healthz` (aponte o monitor de uptime externo aqui).

## Dev local

```bash
docker compose -f infra/docker-compose.dev.yml up -d   # só o Seq (UI em :8341, ingestão em :5341)
```
Postgres em dev é local (`m351_dev`), fora de container; API/portal rodam com `dotnet run` / `npm run dev`.
