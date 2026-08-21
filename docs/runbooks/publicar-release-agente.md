# Runbook — Publicar release do agente (auto-update, F4.2)

> O canal de auto-update é a tabela `agent_releases` (canal único `stable`). O CLI
> `publish-agent-release` calcula o SHA-256 do MSI, copia-o para `Releases:Directory`
> (no staging: volume `releases_data` montado em `/var/lib/m351/releases` do container
> da api) e marca a versão como `is_current`, tudo numa transação auditada.

## Pré-requisitos

- MSI construído pelo CI (artifact `MonitorAgent-msi` do job `agent-msi`, retenção de
  1 dia, baixe do run mais recente) ou por `agent/installer/build-agent-msi.ps1`.
- Acesso SSH à VPS de staging.
- Nomeie o arquivo com a versão (ex.: `MonitorAgent-1.1.0.msi`): o nome vira o
  `file_name` servido em `/api/v1/agent/releases/{file}` e um nome repetido sobrescreve
  o arquivo anterior no volume.

## Passo a passo (staging)

```bash
# 1. Copiar o MSI para a VPS
scp MonitorAgent-1.1.0.msi deploy@<vps>:/tmp/

# 2. Copiar o MSI para dentro do container da api (o CLI roda lá e precisa ler o arquivo)
ssh deploy@<vps>
docker cp /tmp/MonitorAgent-1.1.0.msi m351-staging-api-1:/tmp/

# 3. Publicar: copia para /var/lib/m351/releases (volume releases_data), insere em
#    agent_releases e marca como current. --server-url deixa a URL do manifesto absoluta.
docker exec m351-staging-api-1 dotnet M351.Api.dll publish-agent-release \
  --version 1.1.0 \
  --file /tmp/MonitorAgent-1.1.0.msi \
  --min-version 1.0.0 \
  --server-url https://<STAGING_DOMAIN>

# 4. Limpar o arquivo temporário
docker exec m351-staging-api-1 rm /tmp/MonitorAgent-1.1.0.msi
rm /tmp/MonitorAgent-1.1.0.msi
```

A saída do passo 3 imprime canal, versão, SHA-256 e a URL do manifesto, guarde no
ticket de operação.

## Testar o manifesto

`GET /api/v1/agent/update-manifest` é autenticado por DEVICE TOKEN (o mesmo `dt_...`
que o agente usa), não por JWT do portal:

```bash
curl -sS -H "Authorization: Bearer dt_SEU-DEVICE-TOKEN" \
  https://<STAGING_DOMAIN>/api/v1/agent/update-manifest
# esperado: {"version":"1.1.0","url":".../api/v1/agent/releases/MonitorAgent-1.1.0.msi",
#            "sha256":"<hex64>","min_version":"1.0.0"}
# 204 No Content = nenhum release publicado no canal
```

Sem device token à mão, confira direto no banco:

```bash
docker exec m351-staging-postgres-1 psql -U m351 -d m351_staging \
  -c "SELECT channel, version, min_version, is_current FROM agent_releases ORDER BY id;"
```

O teste fim a fim é observar um agente enrolado baixar e aplicar a versão (o agente
valida o SHA-256 do download antes de instalar).

## Rollback

Move o `is_current` para uma versão JÁ publicada, sem redeploy e sem tocar nas máquinas:

```bash
docker exec m351-staging-api-1 dotnet M351.Api.dll rollback-agent-release --version 1.0.3
```

## Observações

- O volume `releases_data` persiste entre deploys (os MSIs não somem no `up -d`).
- O diretório `/var/lib/m351/releases` já nasce com dono `app:app` na imagem
  (infra/docker/api.Dockerfile), o CLI grava sem precisar de root.
- Cada MSI tem ~85 MB: remova do volume os arquivos de versões antigas que já não
  estejam referenciadas em `agent_releases` se o disco apertar (ver check-disk.sh).
