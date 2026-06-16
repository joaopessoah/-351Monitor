# Roteiro — Aceite do agente numa VM (itens físicos da F4)

> Cobre os itens "pronto quando" da F4 que exigem uma máquina real e não posso executar por você:
> instalação via MSI/GPO (#1), NOTICE_ACK após primeiro logon (#5), metas de consumo (#4) e o
> auto-update e2e (#2). Faça numa **VM limpa Windows 10/11 x64, 2 vCPU / 4 GB**.

## Pré-requisitos

- Build do MSI: rodar `agent/installer/build-agent-msi.ps1` (gera `bin/MonitorAgent.msi`). No CI
  o job `agent-msi` também produz o artefato. (Sem certificado ainda → MSI não-assinado; o
  SmartScreen vai avisar no duplo-clique, mas a instalação silenciosa `/qn` funciona.)
- Uma enrollment key do staging: `docker exec m351-staging-api-1 dotnet M351.Api.dll create-enrollment-key --org-slug <slug> --label vm-aceite` (imprime `ek_...` uma vez).
- SERVERURL do staging: `https://painel.2-25-193-15.sslip.io`.

## 1. Instalação silenciosa (#1)

```
msiexec /i MonitorAgent.msi /qn ENROLLKEY=ek_XXXXXXXXXXXX SERVERURL=https://painel.2-25-193-15.sslip.io
```

Verificar:
- Serviço `MonitorAgentService` existe, **Automatic (Delayed Start)**, rodando (`sc qc MonitorAgentService`).
- Dois processos: `MonitorAgentService.exe` (Session 0) e `MonitorAgentSession.exe` (sua sessão).
- Ícone na bandeja visível, tooltip "Monitoramento corporativo ativo".
- Em < 2 min o device aparece no portal (Dispositivos) com versão do agente e último contato.
- Pastas: binários em `%ProgramFiles%\M351\MonitorAgent\`, dados/fila/logs em `%ProgramData%\M351\MonitorAgent\`.

Testar **golden image**: instalar com `NOENROLL=1` → o device NÃO deve enrolar até o primeiro boot
real (verificar que não aparece no portal logo após instalar; aparece após reiniciar).

Testar **uninstall**: `msiexec /x MonitorAgent.msi /qn` → o device deve registrar
`AGENT_STOP{reason:"uninstall"}` (conferível no painel/eventos); `%ProgramData%` preservado.

## 2. NOTICE_ACK (#5)

Logar na VM com um **usuário Windows novo** (que nunca viu o aviso). Deve aparecer o toast +
janela "Esta máquina é monitorada — Entendi". Clicar em "Entendi". No portal, a tela de
Dispositivos / saúde deve mostrar a **data de ciência (NOTICE_ACK)** preenchida para o device
(antes: "Ciência pendente"). Reiniciar/relogar o mesmo usuário NÃO deve reexibir o aviso.

## 3. GPO em domínio (#1, exige AD)

Numa máquina de domínio: criar uma GPO de instalação de software (Computer Config → Software
Installation), apontar para o MSI num share de rede, definir as propriedades públicas
(ENROLLKEY/SERVERURL) via transform `.mst` ou tabela de propriedades. Aplicar a um OU de teste,
`gpupdate /force` + reboot, confirmar instalação + enroll. (É o cenário "instala via GPO".)

## 4. Metas de consumo (#4)

Seguir `docs/runbooks/metas-consumo-agente.md` na VM sob uso real ~30–60 min:
CPU média < 1%, RAM somada < 100 MB, disco < 400 MB, rede < 5 MB/dia. Registrar os 4 números.

## 5. Auto-update e2e (#2) — FAZER COMIGO, com cuidado

> ⚠️ O agente do PC-CASA também está enrolado no staging. Publicar um release faz **todos** os
> agentes do staging (inclusive o PC-CASA) atualizarem. Fazer quando você puder acompanhar.

1. Bumpar a versão do agente, rebuildar o MSI da nova versão.
2. Publicar: `docker exec m351-staging-api-1 dotnet M351.Api.dll publish-agent-release --version 1.0.1 --file <novo.msi> --min-version 1.0.0`.
3. Em até ~6 h (ou reiniciar o serviço para forçar a checagem) o agente baixa, verifica o SHA-256,
   instala via `msiexec /qn`, e sobe reportando `AGENT_START{start_reason:"update"}`. Conferir a
   nova versão no portal.
4. Rollback: `rollback-agent-release --version 1.0.0` → o agente volta à versão anterior no
   próximo ciclo. Confirma "reverter 1 por manifesto".

## Checklist de aceite da F4 (físico)

- [ ] MSI instala via `/qn` e enrola (#1)
- [ ] NOENROLL=1 adia o enroll para o 1º boot (#1)
- [ ] Uninstall gera AGENT_STOP{uninstall} (#1)
- [ ] GPO instala em máquina de domínio (#1)
- [ ] NOTICE_ACK aparece no 1º logon e fica visível no portal (#5)
- [ ] Metas de consumo dentro dos alvos (#4)
- [ ] Auto-update aplica e rollback reverte (#2)
