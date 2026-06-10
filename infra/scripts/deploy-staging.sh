#!/usr/bin/env bash
# =============================================================================
# +351 Monitor — deploy de STAGING via SSH (chamado pelo CI ou manualmente)
#
# Pré-requisitos na VPS (Hostinger, Ubuntu + Docker — ver infra/README.md):
#   - repo clonado em /opt/m351 (ou STAGING_DIR)
#   - infra/.env preenchido (a partir de infra/.env.example)
#   - usuário de deploy no grupo docker
#
# Variáveis (sobrescrevem os placeholders):
#   STAGING_SSH_HOST  host/IP da VPS        (PLACEHOLDER abaixo)
#   STAGING_SSH_USER  usuário SSH           (default: deploy)
#   STAGING_DIR       diretório do repo     (default: /opt/m351)
#   SSH_KEY_FILE      caminho da chave SSH  (opcional; senão usa o agente/default)
#
# Enquanto as imagens NÃO são publicadas em registry (GHCR é etapa futura),
# o deploy faz `git pull` + `docker compose up -d --build` na própria VPS.
# Quando houver GHCR: trocar por `compose pull` + `up -d` puros.
# =============================================================================
set -euo pipefail

STAGING_SSH_HOST="${STAGING_SSH_HOST:-staging.SEU-DOMINIO.com.br}" # PLACEHOLDER
STAGING_SSH_USER="${STAGING_SSH_USER:-deploy}"
STAGING_DIR="${STAGING_DIR:-/opt/m351}"
SSH_KEY_FILE="${SSH_KEY_FILE:-}"

SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=15)
if [ -n "$SSH_KEY_FILE" ]; then
  SSH_OPTS+=(-i "$SSH_KEY_FILE")
fi

echo "[deploy] staging: ${STAGING_SSH_USER}@${STAGING_SSH_HOST}:${STAGING_DIR}"

ssh "${SSH_OPTS[@]}" "${STAGING_SSH_USER}@${STAGING_SSH_HOST}" bash -s -- "$STAGING_DIR" <<'REMOTE'
set -euo pipefail
DIR="$1"
cd "$DIR"

echo "[deploy] atualizando código (git pull --ff-only)"
git pull --ff-only

echo "[deploy] docker compose pull (imagens de terceiros) + up -d --build"
docker compose -f infra/docker-compose.staging.yml --env-file infra/.env pull --ignore-buildable || true
docker compose -f infra/docker-compose.staging.yml --env-file infra/.env up -d --build --remove-orphans

echo "[deploy] limpando imagens órfãs"
docker image prune -f >/dev/null

echo "[deploy] containers ativos:"
docker compose -f infra/docker-compose.staging.yml --env-file infra/.env ps
REMOTE

echo "[deploy] concluído"
