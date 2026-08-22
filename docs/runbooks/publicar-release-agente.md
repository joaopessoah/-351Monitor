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

## Acompanhar o rollout

Depois de publicar, a leitura é no portal, em **Dispositivos**, no card "Versões do
agente na frota" (`GET /devices/version-summary`): quantas máquinas estão em cada
versão e quais falharam ao atualizar nos últimos 7 dias.

Um release saudável vai transferindo máquinas da versão antiga para a nova ao longo
de ~6 h (a cadência de checagem do agente é 6 h com jitter de até 30 min), sem falhas
listadas. Quando aparece falha, o motivo diz o que fazer, ele é a ETAPA que reprovou,
nunca texto livre:

| Motivo no card | Evento | O que investigar |
|---|---|---|
| Falha no download do instalador | `download` | O MSI está no volume `releases_data`? A URL do manifesto responde da rede das máquinas? |
| Instalador com conteúdo divergente do publicado | `hash` | O arquivo do volume foi trocado sem republicar. Republique com `publish-agent-release`. |
| Assinatura do instalador recusada | `signature` | Só ocorre com `verify_authenticode=true` no install.json: o MSI não está assinado, ou está assinado por outro signatário que não o `expected_signer_cn`. |
| Instalação não pôde ser iniciada | `install` | A máquina não conseguiu subir o `msiexec` (política de execução, disco, permissão). Nada foi instalado, o agente tenta de novo no próximo ciclo. |

Falha de update NÃO derruba o agente: nada é instalado e a tentativa se repete no
ciclo seguinte. Uma frota inteira parada na versão antiga com o MESMO motivo é sinal
de problema no release, não nas máquinas, e o caminho é o rollback abaixo.

Para conferir direto no banco:

```bash
docker exec m351-staging-postgres-1 psql -U m351 -d m351_staging \
  -c "SELECT agent_version, count(*) FROM devices WHERE status = 'active' GROUP BY 1 ORDER BY 1;"
docker exec m351-staging-postgres-1 psql -U m351 -d m351_staging \
  -c "SELECT hostname, last_update_failure_reason, last_update_target_version, last_update_failure_at
      FROM devices WHERE last_update_failure_at > now() - interval '7 days' ORDER BY last_update_failure_at DESC;"
```

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
