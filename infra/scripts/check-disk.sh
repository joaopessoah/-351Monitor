#!/usr/bin/env bash
# =============================================================================
# +351 Monitor — verificação de espaço em disco da VPS de staging
#
# df do filesystem raiz: uso ACIMA do limite (default 80%) = exit 1 e, se
# HEALTHCHECKS_DISK_URL estiver definida em infra/.env, ping na URL de FALHA
# (padrão healthchecks.io: URL/fail); abaixo do limite, ping de sucesso.
#
# Agendamento sugerido (cron, a cada 30 minutos):
#   */30 * * * * /usr/bin/bash /opt/351monitor/infra/scripts/check-disk.sh >> /var/log/m351-disk.log 2>&1
#
# Variáveis (opcionais):
#   DISK_LIMIT_PCT        limite de uso em % (default: 80)
#   HEALTHCHECKS_DISK_URL check do healthchecks.io (sucesso na URL, falha em URL/fail)
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$(dirname "$SCRIPT_DIR")"
ENV_FILE="${ENV_FILE:-$INFRA_DIR/.env}"
LIMITE="${DISK_LIMIT_PCT:-80}"

# Carrega HEALTHCHECKS_DISK_URL (e demais) do .env do staging, se existir
if [ -f "$ENV_FILE" ]; then
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
fi

# df -P: formato POSIX estável para o awk; coluna 5 = "Use%" do filesystem raiz
USO="$(df -P / | awk 'NR==2 { gsub("%", "", $5); print $5 }')"
echo "[disk] $(date '+%Y-%m-%d %H:%M:%S') uso do filesystem raiz: ${USO}% (limite: ${LIMITE}%)"

if [ "$USO" -gt "$LIMITE" ]; then
  echo "[disk] ERRO: uso de disco acima do limite. Suspeitos usuais: dumps em /var/backups/m351, logs de container (rotação json-file no compose), volumes do Seq e do Postgres."
  df -h /
  if [ -n "${HEALTHCHECKS_DISK_URL:-}" ]; then
    # ping de FALHA (padrão healthchecks: URL/fail); não mascarar o exit 1 do script
    curl -fsS -m 10 -o /dev/null "${HEALTHCHECKS_DISK_URL}/fail" || true
  fi
  exit 1
fi

if [ -n "${HEALTHCHECKS_DISK_URL:-}" ]; then
  curl -fsS -m 10 -o /dev/null "$HEALTHCHECKS_DISK_URL"
fi
echo "[disk] ok"
