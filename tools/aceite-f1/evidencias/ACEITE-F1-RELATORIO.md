# Aceite F1 — teste em VM limpa

- **Status geral:** ERRO FATAL (criterios avaliados: 0/4)
- **Data (UTC):** 2026-06-10 12:18
- **VM:** runner GitHub Actions efemero (VM limpa recem-provisionada)
- **Run:** https://github.com/joaopessoah/-351Monitor/actions/runs/27275379104
- **Commit testado:** 6e3dae81f4e79a570792982fb1b694a964e2968b
- **Staging:**  (commit na VPS: )
- **Tenant do teste:** slug `f1-aceite-27275379104` (tenant_id ) — criado so para este aceite; pode ser removido
- **Device:** 

## Ambiente da VM

```
OS: Microsoft Windows Server 2025 Datacenter 10.0.26100 | hostname: runnervmlu3mh | usuario: runneradmin | sessionId do job: 2
quser:
 USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
>runneradmin           console             2  Active      none   6/10/2026 12:10 PM
```

Observacao: se o job nao roda em sessao interativa (sessionId 0), o helper de sessao
nao e lancado e a coleta se restringe a heartbeats de maquina (state=no_session) —
os 4 criterios da F1 nao dependem de eventos de janela. A coleta de janela ativa em
sessao interativa foi validada no E2E local (commit 18bb61d).

## Criterios (docs/PROMPT-DESENVOLVIMENTO.md, secao 10 — F1 "Pronto quando")

| # | Criterio | Resultado | Evidencia |
|---|----------|-----------|-----------|


## Erro fatal
```
ssh falhou (exit 255) cmd=[sed -e '1s/^\xef\xbb\xbf//' -e 's/\r$//' > /tmp/f1-server.sh && echo ok && hostname && cd /opt/351monitor && git rev-parse --short HEAD && grep '^STAGING_DOMAIN=' infra/.env]:
ssh: connect to host 2.25.193.15 port 22: Connection timed out
at Invoke-SshOnce, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 108
at Phase-Context, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 260
at <ScriptBlock>, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 588
at <ScriptBlock>, D:\a\_temp\042ad4f7-3ac5-430a-bb9d-7f43746e81a7.ps1: line 2
at <ScriptBlock>, <No file>: line 1
```

## Linha do tempo

```
[12:17:52] === FASE 0: contexto da VM ===
[12:17:53] OS: Microsoft Windows Server 2025 Datacenter 10.0.26100 | hostname: runnervmlu3mh | usuario: runneradmin | sessionId do job: 2
quser:
 USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
>runneradmin           console             2  Active      none   6/10/2026 12:10 PM
[12:18:09] ERRO FATAL: ssh falhou (exit 255) cmd=[sed -e '1s/^\xef\xbb\xbf//' -e 's/\r$//' > /tmp/f1-server.sh && echo ok && hostname && cd /opt/351monitor && git rev-parse --short HEAD && grep '^STAGING_DOMAIN=' infra/.env]:
ssh: connect to host 2.25.193.15 port 22: Connection timed out
at Invoke-SshOnce, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 108
at Phase-Context, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 260
at <ScriptBlock>, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 588
at <ScriptBlock>, D:\a\_temp\042ad4f7-3ac5-430a-bb9d-7f43746e81a7.ps1: line 2
at <ScriptBlock>, <No file>: line 1
```