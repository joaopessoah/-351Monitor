#!/usr/bin/env bash
# =============================================================================
# +351 Monitor — backup lógico diário do PostgreSQL de staging (container)
# pg_dump em formato custom + retenção local de 14 dias + cópia off-site
# opcional (e cifrada) via rclone.
#
# Uso (na VPS, qualquer diretório):
#   bash /opt/m351/infra/scripts/backup.sh
#
# Agendamento diário (cron, 02:15 — como root ou usuário no grupo docker):
#   crontab -e
#   15 2 * * * /usr/bin/bash /opt/m351/infra/scripts/backup.sh >> /var/log/m351-backup.log 2>&1
#
# Restore (ver também infra/README.md e docs/runbooks/backup-restore.md):
#   docker compose -f /opt/m351/infra/docker-compose.staging.yml --env-file /opt/m351/infra/.env \
#     exec -T postgres pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists < ARQUIVO.dump
#
# Lembrete LGPD: o dump contém dados pessoais — mantenha os arquivos SOMENTE
# em armazenamento no Brasil e com acesso restrito (residência BR). O destino
# off-site precisa ser região brasileira (S3 sa-east-1, Azure Brazil South ou
# Magalu Object Storage) e o provedor escolhido entra na lista de
# subprocessadores publicada.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$(dirname "$SCRIPT_DIR")"

COMPOSE_FILE="${COMPOSE_FILE:-$INFRA_DIR/docker-compose.staging.yml}"
ENV_FILE="${ENV_FILE:-$INFRA_DIR/.env}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/m351}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"

# Carrega POSTGRES_* e OFFSITE_* do .env do staging
if [ -f "$ENV_FILE" ]; then
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
fi
POSTGRES_DB="${POSTGRES_DB:-m351_staging}"
POSTGRES_USER="${POSTGRES_USER:-m351}"

# --- Cópia off-site (opcional) -----------------------------------------------
# OFFSITE_CRYPT_REMOTE: remote do tipo "crypt" do rclone (cifra do lado do cliente).
#   Tem PRECEDÊNCIA: quando definido, é ele que recebe o upload e é contra ele
#   que o rclone check valida.
# OFFSITE_RCLONE_REMOTE: remote puro (sem crypt). Continua funcionando para quem
#   já usava, mas depende de SSE no bucket para o dado não subir em claro.
# OFFSITE_SSE_CONFIRMADO: "sim" declara que o bucket tem cifra em repouso
#   (SSE-S3/SSE-KMS/Azure SSE) ativa e verificada pelo responsável de infra.
# OFFSITE_RETENTION_DAYS: retenção remota, 30 dias por padrão (teto do DPA: 35).
OFFSITE_CRYPT_REMOTE="${OFFSITE_CRYPT_REMOTE:-}"
OFFSITE_RCLONE_REMOTE="${OFFSITE_RCLONE_REMOTE:-}"
OFFSITE_SSE_CONFIRMADO="${OFFSITE_SSE_CONFIRMADO:-}"
OFFSITE_RETENTION_DAYS="${OFFSITE_RETENTION_DAYS:-30}"

mkdir -p "$BACKUP_DIR"
STAMP="$(date +%Y%m%d_%H%M%S)"
OUT="$BACKUP_DIR/m351_${POSTGRES_DB}_${STAMP}.dump"

echo "[backup] iniciando pg_dump de ${POSTGRES_DB} -> ${OUT}"
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres \
  pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom > "$OUT"

# Integridade: um dump truncado/corrompido não pode passar por bom nem subir
# para o off-site. pg_restore --list lê o índice do arquivo custom sem restaurar.
echo "[backup] verificando integridade do dump"
if ! docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres \
      pg_restore --list > /dev/null < "$OUT"; then
  rm -f "$OUT"
  echo "[backup] ERRO: dump inválido (pg_restore --list falhou). Arquivo removido." >&2
  exit 1
fi

# Retenção local: apaga dumps com mais de RETENTION_DAYS dias
find "$BACKUP_DIR" -name 'm351_*.dump' -type f -mtime +"$RETENTION_DAYS" -delete

echo "[backup] dump local ok: $OUT ($(du -h "$OUT" | cut -f1))"

# --- Postura de cifra do off-site --------------------------------------------
# DECISÃO DELIBERADA: a ausência de cifra declarada AVISA e NÃO falha o backup.
# Um dump local íntegro que subiu sem cifra ainda é melhor que backup nenhum;
# falhar aqui deixaria a empresa sem cópia do dia por um problema de postura.
# Por isso o aviso é gritante e repetido no log, para ser impossível ignorar.
DESTINO_OFFSITE="${OFFSITE_CRYPT_REMOTE:-$OFFSITE_RCLONE_REMOTE}"

if [ -n "$DESTINO_OFFSITE" ]; then
  if [ -n "$OFFSITE_CRYPT_REMOTE" ]; then
    echo "[backup] cifra: remote crypt do rclone ($OFFSITE_CRYPT_REMOTE), cifrado antes de sair da VPS"
  elif [ "$OFFSITE_SSE_CONFIRMADO" = "sim" ]; then
    echo "[backup] cifra: SSE declarada no bucket de destino (OFFSITE_SSE_CONFIRMADO=sim)"
  else
    echo "[backup] ############################################################" >&2
    echo "[backup] ##  AVISO GRAVE: BACKUP OFF-SITE SEM CIFRA DECLARADA      ##" >&2
    echo "[backup] ############################################################" >&2
    echo "[backup] O dump contém DADOS PESSOAIS de clientes e vai subir para" >&2
    echo "[backup] ${DESTINO_OFFSITE} sem nenhuma cifra declarada:" >&2
    echo "[backup]   - OFFSITE_CRYPT_REMOTE não definido (sem cifra no cliente)" >&2
    echo "[backup]   - OFFSITE_SSE_CONFIRMADO diferente de \"sim\" (SSE do bucket não confirmada)" >&2
    echo "[backup] Risco: vazamento do bucket expõe a base inteira em claro, com" >&2
    echo "[backup] dever de comunicação à ANPD e aos titulares (LGPD art. 48)." >&2
    echo "[backup] Corrija em infra/.env, veja docs/runbooks/backup-restore.md." >&2
    echo "[backup] O backup CONTINUA de propósito: cópia sem cifra ainda é melhor" >&2
    echo "[backup] que ficar sem cópia. Isso NÃO torna a situação aceitável." >&2
    echo "[backup] ############################################################" >&2
  fi

  if ! command -v rclone > /dev/null 2>&1; then
    echo "[backup] ERRO: OFFSITE configurado mas rclone não está instalado nesta máquina." >&2
    exit 1
  fi

  NOME_DUMP="$(basename "$OUT")"
  echo "[backup] enviando $NOME_DUMP -> $DESTINO_OFFSITE"
  rclone copy "$OUT" "$DESTINO_OFFSITE" --no-traverse

  # Validação: confere o arquivo recém-enviado contra o destino (crypt inclusive,
  # o rclone compara o conteúdo já decifrado). --one-way ignora o que existe só
  # no remoto (dumps antigos ainda dentro da retenção remota).
  echo "[backup] validando cópia remota com rclone check"
  rclone check "$BACKUP_DIR" "$DESTINO_OFFSITE" --one-way --include "$NOME_DUMP"

  # Retenção remota: dentro do teto de 35 dias declarado no DPA.
  # Com object lock em modo compliance a exclusão só ocorre depois do prazo de
  # bloqueio, então o erro aqui é aviso, não falha do backup.
  if ! rclone delete "$DESTINO_OFFSITE" --min-age "${OFFSITE_RETENTION_DAYS}d" --include 'm351_*.dump'; then
    echo "[backup] aviso: retenção remota não pôde apagar arquivos (object lock ainda vigente?)" >&2
  fi

  echo "[backup] off-site ok: $NOME_DUMP em $DESTINO_OFFSITE"
else
  echo "[backup] aviso: nenhuma cópia off-site configurada (OFFSITE_CRYPT_REMOTE / OFFSITE_RCLONE_REMOTE vazios)" >&2
fi

echo "[backup] ok: $OUT"
