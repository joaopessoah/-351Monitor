# Runbook — Metas de consumo do agente (gate de release)

> Cumpre a F4 ("metas de consumo do agente verificadas") e a Seção 6.8 / DoD 11.3.
> É **gate de release**: medir antes de publicar uma versão nova do agente.

## Alvos (Seção 6.8) — medidos em VM 2 vCPU / 4 GB, Windows 10/11 x64

| Métrica | Alvo |
|---|---|
| CPU média (serviço + helper somados) | **< 1%** (pico < 5% por 1 s no polling de janela) |
| RAM working set somada (os 2 processos) | **< 100 MB** |
| Disco total (binários + fila + logs) | **< 400 MB** |
| Rede | **< 5 MB/dia/dispositivo** (lotes gzip) |

Processos: `MonitorAgentService.exe` (serviço, Session 0) e `MonitorAgentSession.exe` (helper, por sessão).

## Como medir

Rodar numa VM limpa com o agente instalado (MSI) e enrolado, durante uma sessão de uso real
de ~30–60 min (abrir/trocar apps, ociosidade, lock/unlock) para exercitar o polling.

### CPU + RAM (PowerShell)

```powershell
# Amostra a cada 5s por 10 min; reporta media de CPU% (normalizada por nucleo) e working set somado.
$procs = 'MonitorAgentService','MonitorAgentSession'
1..120 | ForEach-Object {
  $s = Get-Counter ($procs | ForEach-Object { "\Process($_)\% Processor Time","\Process($_)\Working Set - Private" }) -ErrorAction SilentlyContinue
  # % Processor Time vem somado por todos os nucleos: dividir por $env:NUMBER_OF_PROCESSORS p/ % do sistema.
  Start-Sleep -Seconds 5
}
```

Alternativa visual: **Performance Monitor** (perfmon) com contadores `% Processor Time` e
`Working Set - Private` dos dois processos; ou o **Gerenciador de Tarefas** (aba Detalhes) para
um sanity check rápido. A CPU média deve ficar < 1% do total da VM (lembrar de dividir o
`% Processor Time` pelo número de núcleos).

### Disco

```powershell
'%ProgramFiles%\M351\MonitorAgent','%ProgramData%\M351\MonitorAgent' | ForEach-Object {
  $p = [Environment]::ExpandEnvironmentVariables($_)
  if (Test-Path $p) { '{0}: {1:N1} MB' -f $p, ((Get-ChildItem $p -Recurse -File | Measure-Object Length -Sum).Sum / 1MB) }
}
```

Soma deve ficar < 400 MB. Os binários self-contained dominam (~260 MB os dois exes); fila SQLite
e logs são caps controlados (N8: fila ≤ 100 MB; logs ≤ 10 × 5 MB = 50 MB).

### Rede

Estimativa: heartbeat 60 s + lotes a cada 30 s, **comprimidos com gzip** (`BatchSender` já envia
`Content-Encoding: gzip`, `CompressionLevel.Fastest`; títulos comprimem ~85%). Para um dia típico
fica bem abaixo de 5 MB. Medir de forma direta com o **Monitor de Recursos** (resmon → Rede,
filtrar pelos 2 processos) ao longo de um dia, ou capturar o volume de upload dos lotes.

## Resultado / gate

- Registrar as 4 métricas medidas antes de cada release do agente.
- Se alguma estourar o alvo, investigar antes de publicar (candidatos: frequência de polling,
  acúmulo de logs em Debug — manter `verbose_debug=false`, tamanho da fila por backlog offline).

## Dependência externa

Esta verificação exige uma **VM de medição (2 vCPU/4 GB)** e não é reproduzível no CI nem no
ambiente de dev — é validação manual do operador (Joao) antes do release.
