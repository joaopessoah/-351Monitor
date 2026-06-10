# Aceite F1 — teste em VM limpa

- **Status geral:** ERRO FATAL (criterios avaliados: 0/4)
- **Data (UTC):** 2026-06-10 11:59
- **VM:** runner GitHub Actions efemero (VM limpa recem-provisionada)
- **Run:** https://github.com/joaopessoah/-351Monitor/actions/runs/27274310907
- **Commit testado:** e6a90e6d1d4c5e45e8bd4eaf7dcb6ba0ef33649b
- **Staging:** https://painel.2-25-193-15.sslip.io (commit na VPS: d1773ea)
- **Tenant do teste:** slug `f1-aceite-27274310907` (tenant_id 019eb165-9d63-73c2-98b3-adaab29f6692) — criado so para este aceite; pode ser removido
- **Device:** 

## Ambiente da VM

```
OS: Microsoft Windows Server 2025 Datacenter 10.0.26100 | hostname: runnervmlu3mh | usuario: runneradmin | sessionId do job: 2
quser:
 USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
>runneradmin           console             2  Active      none   6/10/2026 11:52 AM
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
psql falhou (exit 255) sql=[SELECT count(*) FROM raw_events r JOIN devices d ON d.id = r.device_id WHERE d.tenant_id = '019eb165-9d63-73c2-98b3-adaab29f6692';]:
ssh: connect to host 2.25.193.15 port 22: Connection timed out
at Invoke-Sql, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 96
at Invoke-SqlScalar, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 102
at <ScriptBlock>, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 231
at Wait-Until, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 149
at Phase-InstallAndC1, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 230
at <ScriptBlock>, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 495
at <ScriptBlock>, D:\a\_temp\eccd89be-d507-4a09-8d0e-6633ccffa499.ps1: line 2
at <ScriptBlock>, <No file>: line 1
```

## Linha do tempo

```
[11:58:01] === FASE 0: contexto da VM ===
[11:58:01] OS: Microsoft Windows Server 2025 Datacenter 10.0.26100 | hostname: runnervmlu3mh | usuario: runneradmin | sessionId do job: 2
quser:
 USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
>runneradmin           console             2  Active      none   6/10/2026 11:52 AM
[11:58:03] SSH ao staging OK: ok | srv1745505 | d1773ea
[11:58:05] DNS validado: painel.2-25-193-15.sslip.io -> 2.25.193.15 (coberto pelo bloqueio da C2)
[11:58:05] API de staging: https://painel.2-25-193-15.sslip.io/healthz -> HTTP 200 {"status":"ok"}
[11:58:05] === FASE 1: criar tenant + enrollment key no staging ===
[11:58:07] create-org:
Organização criada com sucesso.
  Tenant ID : 019eb165-9d63-73c2-98b3-adaab29f6692
  Nome      : F1-Aceite-27274310907
  Slug      : f1-aceite-27274310907
  Owner     : joao.pessoa+f1a27274310907@benner.com.br
  Convite   : https://painel.2-25-193-15.sslip.io/convite/<redigido>
[11:58:07 INF] E-mail (Dev) gravado em /tmp/dev-mail/20260610T115807383_joao.pessoa_f1a27274310907@benner.com.br.txt para joao.pessoa+f1a27274310907@benner.com.br
[11:58:08] tenant_id = 019eb165-9d63-73c2-98b3-adaab29f6692
[11:58:10] enrollment key gerada: ek_aVIl... (redigida)
[11:58:10] === FASE 2: instalar agente (servico real) e validar C1 (< 2 min) ===
[11:58:11] enroll OK:
[11:58:11 INF] Registrando este dispositivo em https://painel.2-25-193-15.sslip.io …
[11:58:11 INF] Device registrado: device_id=019eb165-adc1-7a10-bd27-c9ae418077b8 (config v1).
[11:58:11 INF] Enrollment concluído. device_id=019eb165-adc1-7a10-bd27-c9ae418077b8
[11:58:11 INF] O token do device foi cifrado com DPAPI (escopo máquina) na fila local.
[11:58:11] servico MonitorAgentService criado (LocalSystem) e iniciado
[11:58:26] ssh transporte falhou (255), tentativa 1/3 — aguardando 10 s
[11:58:51] ssh transporte falhou (255), tentativa 2/3 — aguardando 10 s
[11:59:17] ERRO FATAL: psql falhou (exit 255) sql=[SELECT count(*) FROM raw_events r JOIN devices d ON d.id = r.device_id WHERE d.tenant_id = '019eb165-9d63-73c2-98b3-adaab29f6692';]:
ssh: connect to host 2.25.193.15 port 22: Connection timed out
at Invoke-Sql, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 96
at Invoke-SqlScalar, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 102
at <ScriptBlock>, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 231
at Wait-Until, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 149
at Phase-InstallAndC1, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 230
at <ScriptBlock>, D:\a\-351Monitor\-351Monitor\tools\aceite-f1\run.ps1: line 495
at <ScriptBlock>, D:\a\_temp\eccd89be-d507-4a09-8d0e-6633ccffa499.ps1: line 2
at <ScriptBlock>, <No file>: line 1
```