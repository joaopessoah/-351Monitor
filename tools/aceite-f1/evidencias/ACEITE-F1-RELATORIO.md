# Aceite F1 — teste em VM limpa

- **Status geral:** APROVADO — 4/4 criterios PASS: F1 fechada
- **Data (UTC):** 2026-06-10 14:09
- **VM:** runner GitHub Actions efemero (VM limpa recem-provisionada)
- **Run:** https://github.com/joaopessoah/-351Monitor/actions/runs/27280595130
- **Commit testado:** c9a3d4f8d5ebeede15e7c254ae96d8492f865a2d
- **Staging:** https://painel.2-25-193-15.sslip.io (commit na VPS: d1773ea)
- **Tenant do teste:** slug `f1-aceite-27280595130` (tenant_id 019eb1ca-6d88-727b-9f74-52eef706cda0) — criado so para este aceite; pode ser removido
- **Device:** 019eb1ca-7a3c-7e55-ae34-bbecdd3a24f1

## Ambiente da VM

```
OS: Microsoft Windows Server 2025 Datacenter 10.0.26100 | hostname: runnervmlu3mh | usuario: runneradmin | sessionId do job: 2
quser:
 USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
>runneradmin           console             2  Active      none   6/10/2026 12:55 PM
```

Observacao: se o job nao roda em sessao interativa (sessionId 0), o helper de sessao
nao e lancado e a coleta se restringe a heartbeats de maquina (state=no_session) —
os 4 criterios da F1 nao dependem de eventos de janela. A coleta de janela ativa em
sessao interativa foi validada no E2E local (commit 18bb61d).

## Criterios (docs/PROMPT-DESENVOLVIMENTO.md, secao 10 — F1 "Pronto quando")

| # | Criterio | Resultado | Evidencia |
|---|----------|-----------|-----------|
| C1 | Eventos em raw_events do tenant certo em < 2 min, com seq/tz_offset_min/boot_id | **PASS** | primeiro evento 8s apos start do servico (9s apos o enroll); NULLs tz/boot/seq = 0/0/0 de 1 eventos; current_state=[no_data|2026-06-10 13:48:18.898673+00] |
| C2 | Queda de rede 10 min -> eventos chegam depois sem perda nem duplicata | **PASS** | offline_logado=True vazados_na_queda=0 gaps=0 dup_event_id=0 dup_seq=0 dropped=0 count/esperado=61/61 eventos_na_janela=50 duplicates_acks=0 drenagem=441s |
| C3 | idle_threshold_sec mudado no banco -> agente aplica e emite POLICY_APPLIED | **PASS** | POLICY_APPLIED v2 em 51s; devices.config_version=2; log do agente: True |
| C4 | UNENROLL para a coleta e zera a fila local | **PASS** | delivered_at=ok; fila events=0/dead=0; identidade removida=True; log=True; ingestao congelada=True |



## Linha do tempo

```
[13:48:09] === FASE 0: contexto da VM ===
[13:48:09] OS: Microsoft Windows Server 2025 Datacenter 10.0.26100 | hostname: runnervmlu3mh | usuario: runneradmin | sessionId do job: 2
quser:
 USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
>runneradmin           console             2  Active      none   6/10/2026 12:55 PM
[13:48:11] SSH ao staging OK: ok | srv1745505 | d1773ea | STAGING_DOMAIN=painel.2-25-193-15.sslip.io
[13:48:12] sessao ssh persistente estabelecida (pid local 1792)
[13:48:12] DNS validado: painel.2-25-193-15.sslip.io -> 2.25.193.15 (coberto pelo bloqueio da C2)
[13:48:12] API de staging: https://painel.2-25-193-15.sslip.io/healthz -> HTTP 200 {"status":"ok"}
[13:48:12] === FASE 1: criar tenant + enrollment key no staging ===
[13:48:14] create-org:
Organização criada com sucesso.
  Tenant ID : 019eb1ca-6d88-727b-9f74-52eef706cda0
  Nome      : F1-Aceite-27280595130
  Slug      : f1-aceite-27280595130
  Owner     : joao.pessoa+f1a27280595130@benner.com.br
  Convite   : https://painel.2-25-193-15.sslip.io/convite/<redigido>
[13:48:14 INF] E-mail (Dev) gravado em /tmp/dev-mail/20260610T134814271_joao.pessoa_f1a27280595130@benner.com.br.txt para joao.pessoa+f1a27280595130@benner.com.br
[13:48:14] tenant_id = 019eb1ca-6d88-727b-9f74-52eef706cda0
[13:48:16] enrollment key gerada: ek_WxbM... (redigida)
[13:48:16] === FASE 2: instalar agente (servico real) e validar C1 (< 2 min) ===
[13:48:17] enroll OK:
[13:48:16 INF] Registrando este dispositivo em https://painel.2-25-193-15.sslip.io …
[13:48:17 INF] Device registrado: device_id=019eb1ca-7a3c-7e55-ae34-bbecdd3a24f1 (config v1).
[13:48:17 INF] Enrollment concluído. device_id=019eb1ca-7a3c-7e55-ae34-bbecdd3a24f1
[13:48:17 INF] O token do device foi cifrado com DPAPI (escopo máquina) na fila local.
[13:48:17] servico MonitorAgentService criado (LocalSystem) e iniciado
[13:48:26] device: 019eb1ca-7a3c-7e55-ae34-bbecdd3a24f1|runnervmlu3mh|active|1|0
[13:48:26] device_current_state: no_data|2026-06-10 13:48:18.898673+00
[13:48:26] == C1 [PASS] Eventos em raw_events do tenant certo em < 2 min, com seq/tz_offset_min/boot_id :: primeiro evento 8s apos start do servico (9s apos o enroll); NULLs tz/boot/seq = 0/0/0 de 1 eventos; current_state=[no_data|2026-06-10 13:48:18.898673+00]
[13:48:26] === FASE 3: queda de rede de 600 s (bloqueio de firewall p/ 2.25.193.15) ===
[13:48:27] pre-queda: max(seq)=1 count=1
[13:48:29] firewall: saida para 2.25.193.15 BLOQUEADA (sem SSH durante a queda)
[13:48:29] verificado: 443 do staging inalcancavel
[13:50:29] queda em andamento: 120s / 600 s
[13:52:29] queda em andamento: 240s / 600 s
[13:54:29] queda em andamento: 360s / 600 s
[13:56:29] queda em andamento: 481s / 600 s
[13:58:29] queda em andamento: 601s / 600 s
[13:58:29] log do agente registrou modo offline: True
[13:58:30] fila local ao fim da queda: events=53 (nao enviados=53) dead_letter=0
[13:58:30] firewall: bloqueio removido — aguardando drenagem (backoff N14: proximo retry pode demorar ate ~12 min)
[14:06:28] sessao ssh persistente estabelecida (pid local 6200)
[14:06:29] == C2 [PASS] Queda de rede 10 min -> eventos chegam depois sem perda nem duplicata :: offline_logado=True vazados_na_queda=0 gaps=0 dup_event_id=0 dup_seq=0 dropped=0 count/esperado=61/61 eventos_na_janela=50 duplicates_acks=0 drenagem=441s
[14:06:29] === FASE 4: mudar idle_threshold_sec no banco -> POLICY_APPLIED ===
[14:06:30] config: idle_threshold_sec 300 -> 600, config_version 1 -> 2
[14:07:21] == C3 [PASS] idle_threshold_sec mudado no banco -> agente aplica e emite POLICY_APPLIED :: POLICY_APPLIED v2 em 51s; devices.config_version=2; log do agente: True
[14:07:21] === FASE 5: UNENROLL -> para coleta e zera fila local ===
[14:08:12] max(received_at) apos UNENROLL: 2026-06-10 14:07:45.57661+00 — aguardando 90 s para provar congelamento
[14:09:43] == C4 [PASS] UNENROLL para a coleta e zera a fila local :: delivered_at=ok; fila events=0/dead=0; identidade removida=True; log=True; ingestao congelada=True
```