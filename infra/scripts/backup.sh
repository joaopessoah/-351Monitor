#!/usr/bin/env bash
# =============================================================================
# +351 Monitor — backup lógico diário do PostgreSQL de staging (container)
# pg_dump em formato custom + retenção de 14 dias.
#
# Uso (na VPS, qualquer diretório):
#   bash /opt/m351/infra/scripts/backup.sh
#
# Agendamento diário (cron, 02:15 — como root ou usuário no grupo docker):
#   crontab -e
#   15 2 * * * /usr/bin/bash /opt/m351/infra/scripts/backup.sh >> /var/log/m351-backup.log 2>&1
#
# Restore (ver também infra/README.md):
#   docker compose -f /opt/m351/infra/docker-compose.staging.yml --env-file /opt/m351/infra/.env \
#     exec -T postgres pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists < ARQUIVO.dump
#
# Lembrete LGPD: o dump contém dados pessoais — mantenha os arquivos SOMENTE
# em armazenamento no Brasil e com acesso restrito (residência BR).
#
# Cópia OFF-SITE (opcional, fortemente recomendada): com OFFSITE_RCLONE_REMOTE e
# RCLONE_CONFIG definidos em infra/.env, o dump é copiado via rclone para um
# object storage em região BRASILEIRA e a cópia é validada com rclone check.
# Sem essas variáveis o script loga um aviso e segue (o dump local continua).
# HEALTHCHECKS_BACKUP_URL (opcional): pingada SÓ após tudo dar certo, para o
# healthchecks.io alertar quando o backup deixar de rodar.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$(dirname "$SCRIPT_DIR")"

COMPOSE_FILE="${COMPOSE_FILE:-$INFRA_DIR/docker-compose.staging.yml}"
ENV_FILE="${ENV_FILE:-$INFRA_DIR/.env}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/m351}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"

# Carrega POSTGRES_* do .env do staging
if [ -f "$ENV_FILE" ]; then
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
fi
POSTGRES_DB="${POSTGRES_DB:-m351_staging}"
POSTGRES_USER="${POSTGRES_USER:-m351}"

mkdir -p "$BACKUP_DIR"
STAMP="$(date +%Y%m%d_%H%M%S)"
OUT="$BACKUP_DIR/m351_${POSTGRES_DB}_${STAMP}.dump"

echo "[backup] iniciando pg_dump de ${POSTGRES_DB} -> ${OUT}"
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres \
  pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom > "$OUT"

# Retenção: apaga dumps com mais de RETENTION_DAYS dias
find "$BACKUP_DIR" -name 'm351_*.dump' -type f -mtime +"$RETENTION_DAYS" -delete

echo "[backup] ok: $OUT ($(du -h "$OUT" | cut -f1))"

# -----------------------------------------------------------------------------
# Cópia off-site (rclone): o dump local fica no MESMO disco da VPS — sem esta
# cópia, perder a VPS significa perder banco E backups de uma vez.
# O bucket DEVE estar em região brasileira (residência BR — ver infra/.env.example).
# Falha no upload/validação = exit não-zero (set -e), SEM ping no healthchecks,
# para o cron e o alerta registrarem o problema.
# -----------------------------------------------------------------------------
DUMP_NAME="$(basename "$OUT")"
OFFSITE_RETENTION_DAYS="${OFFSITE_RETENTION_DAYS:-30}"

if [ -n "${OFFSITE_RCLONE_REMOTE:-}" ] && [ -n "${RCLONE_CONFIG:-}" ]; then
  export RCLONE_CONFIG
  echo "[backup] copiando ${DUMP_NAME} para o remoto off-site (${OFFSITE_RCLONE_REMOTE})"
  rclone copy "$OUT" "$OFFSITE_RCLONE_REMOTE"

  # Validação da cópia: rclone check compara tamanho/hash do arquivo recém-enviado
  # (--one-way: só confere que o local existe no remoto; --include limita ao dump do dia)
  echo "[backup] validando a cópia remota (rclone check)"
  rclone check "$BACKUP_DIR" "$OFFSITE_RCLONE_REMOTE" --one-way --include "$DUMP_NAME"

  # Retenção remota: 30 dias (dentro do teto de 35 dias declarado no DPA).
  # Não-fatal: o upload do dia já foi validado; uma falha aqui não perde backup.
  rclone delete "$OFFSITE_RCLONE_REMOTE" --min-age "${OFFSITE_RETENTION_DAYS}d" --include 'm351_*.dump' \
    || echo "[backup] aviso: limpeza da retenção remota falhou (upload do dia validado, seguindo)"

  echo "[backup] off-site ok: ${OFFSITE_RCLONE_REMOTE}/${DUMP_NAME}"
else
  echo "[backup] AVISO: backup off-site NÃO configurado — defina OFFSITE_RCLONE_REMOTE e RCLONE_CONFIG em infra/.env (o dump local foi gerado normalmente)"
fi

# Ping de sucesso no healthchecks.io (só chega aqui se dump + off-site deram certo)
if [ -n "${HEALTHCHECKS_BACKUP_URL:-}" ]; then
  curl -fsS -m 10 -o /dev/null "$HEALTHCHECKS_BACKUP_URL"
  echo "[backup] healthchecks.io pingado (sucesso)"
fi
