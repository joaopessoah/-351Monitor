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

STAGING_SSH_HOST="${STAGING_SSH_HOST:-2.25.193.15}"
STAGING_SSH_USER="${STAGING_SSH_USER:-root}"
STAGING_DIR="${STAGING_DIR:-/opt/351monitor}"
SSH_KEY_FILE="${SSH_KEY_FILE:-}"

SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=15)
if [ -n "$SSH_KEY_FILE" ]; then
  SSH_OPTS+=(-i "$SSH_KEY_FILE")
fi

echo "[deploy] staging: ${STAGING_SSH_USER}@${STAGING_SSH_HOST}:${STAGING_DIR}"

ssh "${SSH_OPTS[@]}" "${STAGING_SSH_USER}@${STAGING_SSH_HOST}" bash -s -- "$STAGING_DIR" "${SEQ_RETENTION_DAYS:-}" <<'REMOTE'
set -euo pipefail
DIR="$1"
SEQ_RETENTION_DAYS="${2:-}"
cd "$DIR"

echo "[deploy] sincronizando código com origin/main (staging nunca tem commits locais)"
git fetch origin main
git reset --hard origin/main

echo "[deploy] docker compose pull (imagens de terceiros) + up -d --build"
docker compose -f infra/docker-compose.staging.yml --env-file infra/.env pull --ignore-buildable || true
docker compose -f infra/docker-compose.staging.yml --env-file infra/.env up -d --build --remove-orphans

# Retenção do Seq (DESLIGADA por padrão): exporte SEQ_RETENTION_DAYS=N ao chamar o
# deploy para aplicar uma política de retenção de N dias via seqcli. Idempotente: só
# cria quando NENHUMA política existe. Requer seqcli instalado na VPS e, se o Seq
# tiver autenticação, SEQ_API_KEY exportado no ambiente remoto. Alternativa manual
# (UI ou seqcli avulso): ver infra/README.md, seção "Higiene de disco".
if [ -n "$SEQ_RETENTION_DAYS" ]; then
  if command -v seqcli >/dev/null 2>&1; then
    SEQ_URL="http://127.0.0.1:8341"
    EXISTENTES="$(seqcli retention list -s "$SEQ_URL" ${SEQ_API_KEY:+-a "$SEQ_API_KEY"} 2>/dev/null || true)"
    if [ -z "$EXISTENTES" ]; then
      if seqcli retention create --after "${SEQ_RETENTION_DAYS}d" --delete-all-events -s "$SEQ_URL" ${SEQ_API_KEY:+-a "$SEQ_API_KEY"}; then
        echo "[deploy] retenção do Seq criada: ${SEQ_RETENTION_DAYS} dias"
      else
        echo "[deploy] aviso: seqcli retention create falhou; aplique pela UI (infra/README.md)"
      fi
    else
      echo "[deploy] retenção do Seq já existe, nada a fazer"
    fi
  else
    echo "[deploy] aviso: SEQ_RETENTION_DAYS definido mas o seqcli não está instalado na VPS"
  fi
fi

echo "[deploy] limpando imagens órfãs"
docker image prune -f >/dev/null

echo "[deploy] containers ativos:"
docker compose -f infra/docker-compose.staging.yml --env-file infra/.env ps
REMOTE

echo "[deploy] concluído"
