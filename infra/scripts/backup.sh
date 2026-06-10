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
