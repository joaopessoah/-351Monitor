#!/usr/bin/env bash
# =============================================================================
# +351 Monitor — deploy de STAGING via SSH (chamado pelo CI ou manualmente)
#
# Pré-requisitos na VPS (Hostinger, Ubuntu + Docker — ver infra/README.md):
#   - repo clonado em /opt/351monitor (ou STAGING_DIR)
#   - infra/.env preenchido (a partir de infra/.env.example)
#   - usuário de deploy no grupo docker
#
# Variáveis (sobrescrevem os placeholders):
#   STAGING_SSH_HOST  host/IP da VPS        (PLACEHOLDER abaixo)
#   STAGING_SSH_USER  usuário SSH           (default: deploy — nunca root)
#   STAGING_DIR       diretório do repo     (default: /opt/351monitor)
#   SSH_KEY_FILE      caminho da chave SSH  (opcional; senão usa o agente/default)
#   DEPLOY_BUILD      "1" (DEFAULT) = build local na VPS, comportamento antigo;
#                     "0" = compose pull das imagens do GHCR + up -d.
#                     DEPLOY_BUILD=1 segue como default ATÉ o primeiro push de
#                     imagem no GHCR funcionar (job docker do ci.yml) — sem esse
#                     fallback o deploy quebraria com o registry ainda vazio.
#                     Depois do primeiro push validado, troque o default para "0".
#   IMAGE_TAG         tag das imagens do GHCR (default: staging; para rollback use
#                     a tag imutável sha-<sha> de um push anterior)
#   GHCR_TOKEN        token com read:packages para docker login no ghcr.io — só
#                     necessário com DEPLOY_BUILD=0 e enquanto a VPS não tem login
#                     persistido em ~/.docker/config.json (ver infra/README.md)
#   GHCR_USER         usuário do docker login no GHCR (default: joaopessoah)
#   SEQ_RETENTION_DAYS  (opcional, desligado por padrão) aplica retenção de N dias
#                     no Seq via seqcli — ver infra/README.md, "Higiene de disco"
# =============================================================================
set -euo pipefail

STAGING_SSH_HOST="${STAGING_SSH_HOST:-2.25.193.15}"
STAGING_SSH_USER="${STAGING_SSH_USER:-deploy}"
STAGING_DIR="${STAGING_DIR:-/opt/351monitor}"
SSH_KEY_FILE="${SSH_KEY_FILE:-}"
DEPLOY_BUILD="${DEPLOY_BUILD:-1}"
IMAGE_TAG="${IMAGE_TAG:-staging}"
GHCR_USER="${GHCR_USER:-joaopessoah}"

SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=15)
if [ -n "$SSH_KEY_FILE" ]; then
  SSH_OPTS+=(-i "$SSH_KEY_FILE")
fi

echo "[deploy] staging: ${STAGING_SSH_USER}@${STAGING_SSH_HOST}:${STAGING_DIR} (DEPLOY_BUILD=${DEPLOY_BUILD} IMAGE_TAG=${IMAGE_TAG})"

# docker login no GHCR (token via stdin — nunca em argumento visível no ps remoto).
# O login fica persistido no ~/.docker/config.json da VPS: as execuções seguintes
# funcionam sem GHCR_TOKEN até o token ser revogado.
if [ "$DEPLOY_BUILD" != "1" ] && [ -n "${GHCR_TOKEN:-}" ]; then
  echo "[deploy] docker login ghcr.io (usuário ${GHCR_USER})"
  printf '%s' "$GHCR_TOKEN" | ssh "${SSH_OPTS[@]}" "${STAGING_SSH_USER}@${STAGING_SSH_HOST}" \
    "docker login ghcr.io -u '${GHCR_USER}' --password-stdin"
fi

ssh "${SSH_OPTS[@]}" "${STAGING_SSH_USER}@${STAGING_SSH_HOST}" bash -s -- \
  "$STAGING_DIR" "${SEQ_RETENTION_DAYS:-}" "$DEPLOY_BUILD" "$IMAGE_TAG" <<'REMOTE'
set -euo pipefail
DIR="$1"
SEQ_RETENTION_DAYS="${2:-}"
DEPLOY_BUILD="${3:-1}"
export IMAGE_TAG="${4:-staging}"   # exportada: o compose interpola ${IMAGE_TAG:-staging}
cd "$DIR"

echo "[deploy] sincronizando código com origin/main (staging nunca tem commits locais)"
git fetch origin main
git reset --hard origin/main

# Backup ANTES do up: a api roda AutoMigrate no start e não dá para saber daqui se há
# migração pendente; se uma migração corromper dados, este dump é o ponto de
# restauração imediato (ver docs/runbooks/backup-restore.md).
echo "[deploy] backup pré-deploy (protege o AutoMigrate)"
bash infra/scripts/backup.sh

COMPOSE="docker compose -f infra/docker-compose.staging.yml --env-file infra/.env"

if [ "$DEPLOY_BUILD" = "1" ]; then
  # Fallback (comportamento antigo): build local na VPS. Mantido como DEFAULT até o
  # primeiro push de imagem no GHCR ser validado; depois mude o default para "0"
  # aqui e no cabeçalho do script.
  echo "[deploy] DEPLOY_BUILD=1: docker compose pull (imagens de terceiros) + up -d --build"
  $COMPOSE pull --ignore-buildable || true
  $COMPOSE up -d --build --remove-orphans
else
  echo "[deploy] DEPLOY_BUILD=0: compose pull do GHCR (IMAGE_TAG=${IMAGE_TAG}) + up -d"
  $COMPOSE pull
  $COMPOSE up -d --remove-orphans
fi

# Health-gate: sem /healthz 200 em até 60 s o deploy é considerado FALHO (exit 1).
STAGING_DOMAIN="$(grep -E '^STAGING_DOMAIN=' infra/.env | cut -d= -f2- || true)"
if [ -z "$STAGING_DOMAIN" ]; then
  echo "[deploy] ERRO: STAGING_DOMAIN não encontrado em infra/.env — health-gate impossível"
  exit 1
fi
echo "[deploy] health-gate: aguardando https://${STAGING_DOMAIN}/healthz (até 60 s)"
HEALTH_OK=0
for _ in $(seq 1 12); do
  if curl -fsS -m 5 -o /dev/null "https://${STAGING_DOMAIN}/healthz"; then
    HEALTH_OK=1
    break
  fi
  sleep 5
done
if [ "$HEALTH_OK" != "1" ]; then
  echo "[deploy] ERRO: /healthz não respondeu 200 em 60 s — deploy FALHOU."
  echo "[deploy] ROLLBACK: reexecute o deploy com a tag imutável do último deploy bom:"
  echo "[deploy]   IMAGE_TAG=sha-<sha-anterior> DEPLOY_BUILD=0 bash infra/scripts/deploy-staging.sh"
  echo "[deploy] ou, direto na VPS:"
  echo "[deploy]   IMAGE_TAG=sha-<sha-anterior> docker compose -f infra/docker-compose.staging.yml --env-file infra/.env up -d"
  $COMPOSE ps || true
  exit 1
fi
echo "[deploy] health-gate ok: /healthz respondeu 200"

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
$COMPOSE ps
REMOTE

echo "[deploy] concluído"
