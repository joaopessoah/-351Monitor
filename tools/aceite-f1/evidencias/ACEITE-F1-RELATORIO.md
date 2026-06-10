# Aceite F1 — teste em VM limpa

- **Status geral:** REPROVADO — 2 de 4 criterios FAIL
- **Data (UTC):** 2026-06-10 12:58
- **VM:** runner GitHub Actions efemero (VM limpa recem-provisionada)
- **Run:** https://github.com/joaopessoah/-351Monitor/actions/runs/27275992424
- **Commit testado:** 1eaa113b8c3ea9c21228fe650d21b5f3cb9b3e3d
- **Staging:** https://painel.2-25-193-15.sslip.io (commit na VPS: d1773ea)
- **Tenant do teste:** slug `f1-aceite-27275992424` (tenant_id 019eb181-ee94-7704-8efa-6de956fcb417) — criado so para este aceite; pode ser removido
- **Device:** 019eb181-fa8e-73c4-b41b-744d99a19950

## Ambiente da VM

```
OS: Microsoft Windows Server 2025 Datacenter 10.0.26100 | hostname: runnervmlu3mh | usuario: runneradmin | sessionId do job: 2
quser:
 USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
>runneradmin           console             2  Active      none   6/10/2026 12:05 PM
```

Observacao: se o job nao roda em sessao interativa (sessionId 0), o helper de sessao
nao e lancado e a coleta se restringe a heartbeats de maquina (state=no_session) —
os 4 criterios da F1 nao dependem de eventos de janela. A coleta de janela ativa em
sessao interativa foi validada no E2E local (commit 18bb61d).

## Criterios (docs/PROMPT-DESENVOLVIMENTO.md, secao 10 — F1 "Pronto quando")

| # | Criterio | Resultado | Evidencia |
|---|----------|-----------|-----------|
| C1 | Eventos em raw_events do tenant certo em < 2 min, com seq/tz_offset_min/boot_id | **PASS** | primeiro evento 9s apos start do servico (10s apos o enroll); NULLs tz/boot/seq = 0/0/0 de 1 eventos; current_state=[no_data|2026-06-10 12:29:07.561411+00] |
| C2 | Queda de rede 10 min -> eventos chegam depois sem perda nem duplicata | **PASS** | offline_logado=True vazados_na_queda=0 gaps=0 dup_event_id=0 dup_seq=0 dropped=0 count/esperado=63/63 eventos_na_janela=50 duplicates_acks=0 drenagem=542s |
| C3 | idle_threshold_sec mudado no banco -> agente aplica e emite POLICY_APPLIED | **FAIL** | POLICY_APPLIED v2 em TIMEOUTs; devices.config_version=1; log do agente: True |
| C4 | UNENROLL para a coleta e zera a fila local | **FAIL** | delivered_at=TIMEOUT; fila events=9/dead=0; identidade removida=False; log=False; ingestao congelada=True |



## Linha do tempo

```
[12:28:58] === FASE 0: contexto da VM ===
[12:28:58] OS: Microsoft Windows Server 2025 Datacenter 10.0.26100 | hostname: runnervmlu3mh | usuario: runneradmin | sessionId do job: 2
quser:
 USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
>runneradmin           console             2  Active      none   6/10/2026 12:05 PM
[12:29:00] SSH ao staging OK: ok | srv1745505 | d1773ea | STAGING_DOMAIN=painel.2-25-193-15.sslip.io
[12:29:01] sessao ssh persistente estabelecida (pid local 4248)
[12:29:01] DNS validado: painel.2-25-193-15.sslip.io -> 2.25.193.15 (coberto pelo bloqueio da C2)
[12:29:01] API de staging: https://painel.2-25-193-15.sslip.io/healthz -> HTTP 200 {"status":"ok"}
[12:29:01] === FASE 1: criar tenant + enrollment key no staging ===
[12:29:03] create-org:
Organização criada com sucesso.
  Tenant ID : 019eb181-ee94-7704-8efa-6de956fcb417
  Nome      : F1-Aceite-27275992424
  Slug      : f1-aceite-27275992424
  Owner     : joao.pessoa+f1a27275992424@benner.com.br
  Convite   : https://painel.2-25-193-15.sslip.io/convite/<redigido>
[12:29:03 INF] E-mail (Dev) gravado em /tmp/dev-mail/20260610T122903170_joao.pessoa_f1a27275992424@benner.com.br.txt para joao.pessoa+f1a27275992424@benner.com.br
[12:29:03] tenant_id = 019eb181-ee94-7704-8efa-6de956fcb417
[12:29:04] enrollment key gerada: ek_Qbge... (redigida)
[12:29:04] === FASE 2: instalar agente (servico real) e validar C1 (< 2 min) ===
[12:29:06] enroll OK:
[12:29:05 INF] Registrando este dispositivo em https://painel.2-25-193-15.sslip.io …
[12:29:06 INF] Device registrado: device_id=019eb181-fa8e-73c4-b41b-744d99a19950 (config v1).
[12:29:06 INF] Enrollment concluído. device_id=019eb181-fa8e-73c4-b41b-744d99a19950
[12:29:06 INF] O token do device foi cifrado com DPAPI (escopo máquina) na fila local.
[12:29:06] servico MonitorAgentService criado (LocalSystem) e iniciado
[12:29:15] device: 019eb181-fa8e-73c4-b41b-744d99a19950|runnervmlu3mh|active|1|0
[12:29:16] device_current_state: no_data|2026-06-10 12:29:07.561411+00
[12:29:16] == C1 [PASS] Eventos em raw_events do tenant certo em < 2 min, com seq/tz_offset_min/boot_id :: primeiro evento 9s apos start do servico (10s apos o enroll); NULLs tz/boot/seq = 0/0/0 de 1 eventos; current_state=[no_data|2026-06-10 12:29:07.561411+00]
[12:29:16] === FASE 3: queda de rede de 600 s (bloqueio de firewall p/ 2.25.193.15) ===
[12:29:16] pre-queda: max(seq)=1 count=1
[12:29:17] firewall: saida para 2.25.193.15 BLOQUEADA (sem SSH durante a queda)
[12:29:17] verificado: 443 do staging inalcancavel
[12:31:17] queda em andamento: 120s / 600 s
[12:33:22] queda em andamento: 245s / 600 s
[12:35:22] queda em andamento: 365s / 600 s
[12:37:22] queda em andamento: 486s / 600 s
[12:39:23] queda em andamento: 606s / 600 s
[12:39:23] log do agente registrou modo offline: True
[12:39:23] fila local ao fim da queda: events=53 (nao enviados=53) dead_letter=0
[12:39:24] firewall: bloqueio removido — aguardando drenagem (backoff N14: proximo retry pode demorar ate ~12 min)
[12:49:02] sessao ssh persistente estabelecida (pid local 980)
[12:49:03] == C2 [PASS] Queda de rede 10 min -> eventos chegam depois sem perda nem duplicata :: offline_logado=True vazados_na_queda=0 gaps=0 dup_event_id=0 dup_seq=0 dropped=0 count/esperado=63/63 eventos_na_janela=50 duplicates_acks=0 drenagem=542s
[12:49:03] === FASE 4: mudar idle_threshold_sec no banco -> POLICY_APPLIED ===
[12:49:03] config: idle_threshold_sec 300 -> 600, config_version 1 -> 2
[12:53:08] == C3 [FAIL] idle_threshold_sec mudado no banco -> agente aplica e emite POLICY_APPLIED :: POLICY_APPLIED v2 em TIMEOUTs; devices.config_version=1; log do agente: True
[12:53:08] === FASE 5: UNENROLL -> para coleta e zera fila local ===
[12:56:31] max(received_at) apos UNENROLL: 2026-06-10 12:49:22.685176+00 — aguardando 90 s para provar congelamento
[12:58:02] == C4 [FAIL] UNENROLL para a coleta e zera a fila local :: delivered_at=TIMEOUT; fila events=9/dead=0; identidade removida=False; log=False; ingestao congelada=True
```