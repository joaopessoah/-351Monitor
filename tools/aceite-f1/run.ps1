#Requires -Version 7
<#
  Aceite F1 — "instalar o agente numa VM limpa" (docs/PROMPT-DESENVOLVIMENTO.md, secao 10, F1).

  Roda num runner Windows do GitHub Actions (VM efemera recem-provisionada = VM limpa)
  e valida os 4 criterios de pronto da F1 contra o STAGING real:

    C1. Instalar o agente com a key -> em < 2 min eventos crus em raw_events do tenant
        certo, com seq / tz_offset_min / boot_id persistidos.
    C2. Derrubar a rede 10 min -> eventos chegam depois SEM perda nem duplicata
        (conferir duplicates no ack do retry).
    C3. Mudar idle_threshold_sec no banco -> agente aplica no proximo ack e emite POLICY_APPLIED.
    C4. UNENROLL para a coleta e zera a fila local.

  Acesso ao staging: SSH (secret STAGING_SSH_KEY) + docker exec no Postgres/API da VPS.
  A "queda de rede" e um bloqueio de firewall (saida) para o IP do staging — o runner
  continua falando com o GitHub, mas o agente perde o servidor por completo.

  Evidencias: tools/aceite-f1/evidencias/ (commitadas na branch pelo workflow).
#>

param(
    [string]$SshKeyPath = $env:F1_SSH_KEY_PATH,
    [string]$VpsHost    = $(if ($env:STAGING_SSH_HOST) { $env:STAGING_SSH_HOST } else { '2.25.193.15' }),
    [string]$VpsUser    = $(if ($env:STAGING_SSH_USER) { $env:STAGING_SSH_USER } else { 'root' }),
    [int]$OutageSeconds = 600
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PSNativeCommandUseErrorActionPreference = $false

# ---------------------------------------------------------------- constantes
$EvidDir      = Join-Path $PSScriptRoot 'evidencias'
$InstallDir   = 'C:\M351\MonitorAgent'   # sem espacos: evita quoting do sc.exe
$DataDir      = Join-Path $env:ProgramData 'M351\MonitorAgent'
$ServiceName  = 'MonitorAgentService'
$PgContainer  = 'm351-staging-postgres-1'
$ApiContainer = 'm351-staging-api-1'
$FwRule       = 'F1-ACEITE-BLOQUEIO-STAGING'
$RunId        = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { (Get-Random -Maximum 999999) }
$Slug         = "f1-aceite-$RunId"
$OwnerEmail   = "joao.pessoa+f1a$RunId@benner.com.br"

New-Item -ItemType Directory -Force $EvidDir | Out-Null

$Criterios  = [System.Collections.Generic.List[object]]::new()
$Transcript = [System.Collections.Generic.List[string]]::new()
$script:EnrollKey = $null
$script:TenantId  = $null
$script:DeviceId  = $null
$script:ApiUrl    = $null

# ---------------------------------------------------------------- helpers
function Log([string]$msg) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $msg
    Write-Host $line
    $Transcript.Add($line)
}

function Add-Criterio([string]$id, [string]$nome, [bool]$pass, [string]$evidencia) {
    $r = if ($pass) { 'PASS' } else { 'FAIL' }
    $Criterios.Add([pscustomobject]@{ Id = $id; Criterio = $nome; Resultado = $r; Evidencia = $evidencia })
    Log "== $id [$r] $nome :: $evidencia"
}

# ---------------------------------------------------------------- ssh
# A borda da VPS (Hostinger) corta rajadas de conexoes ssh novas: no run 27274310907,
# 8 conexoes em 10 s fizeram a porta 22 passar a dar timeout para o IP do runner
# (fail2ban zerado — bloqueio upstream). Por isso TODO o trafego de verificacao usa
# UMA sessao ssh persistente com um servidor de jobs no lado remoto: jobs SQL (psql
# via docker exec) e CMD (bash), respostas terminadas por "__DONE__ <exit>".
$RemoteServerScript = @'
#!/usr/bin/env bash
echo "__READY__"
mode=""; buf=""
while IFS= read -r line; do
  line="${line%$'\r'}"              # tolera CR vindo de cliente Windows
  line="${line#$'\xef\xbb\xbf'}"    # tolera BOM no 1o write do stdin
  case "$line" in
    __SQL_BEGIN__) mode=sql; buf="" ;;
    __CMD_BEGIN__) mode=cmd; buf="" ;;
    __SQL_END__)
      printf '%s' "$buf" | docker exec -i m351-staging-postgres-1 psql -U m351 -d m351_staging -tA -q -v ON_ERROR_STOP=1 2>&1
      echo "__DONE__ $?"
      mode="" ;;
    __CMD_END__)
      bash -c "$buf" 2>&1
      echo "__DONE__ $?"
      mode="" ;;
    __QUIT__) exit 0 ;;
    *) if [ -n "$mode" ]; then buf="${buf}${line}"$'\n'; fi ;;
  esac
done
'@

# Conexao ssh avulsa (1 uso na fase 0, para subir o servidor de jobs). Virgula unaria
# no return: preserva o array quando ha 1 so linha.
function Invoke-SshOnce([string]$remoteCmd, [string]$stdinText) {
    if ($null -ne $stdinText -and $stdinText.Length -gt 0) {
        $raw = ($stdinText -replace "`r", '') | ssh -i $SshKeyPath -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 "$VpsUser@$VpsHost" $remoteCmd 2>&1
    } else {
        $raw = ssh -i $SshKeyPath -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 "$VpsUser@$VpsHost" $remoteCmd 2>&1
    }
    $stdout = @($raw | Where-Object { $_ -is [string] })
    if ($LASTEXITCODE -ne 0) {
        $all = ($raw | ForEach-Object { "$_" }) -join "`n"
        throw "ssh falhou (exit $LASTEXITCODE) cmd=[$remoteCmd]:`n$all"
    }
    return ,$stdout
}

function Start-RemoteSession {
    for ($t = 1; $t -le 3; $t++) {
        $psi = [Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = 'ssh'
        foreach ($a in @('-i', $SshKeyPath, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=accept-new', '-o', 'ServerAliveInterval=30', '-o', 'ConnectTimeout=15', "$VpsUser@$VpsHost", 'bash /tmp/f1-server.sh')) { $psi.ArgumentList.Add($a) }
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        $psi.StandardInputEncoding = [Text.UTF8Encoding]::new($false)  # sem BOM no 1o write
        $proc = [Diagnostics.Process]::Start($psi)
        $proc.BeginErrorReadLine()  # drena stderr para nao deadlockar o pipe
        $ready = $proc.StandardOutput.ReadLine()
        if ($ready -eq '__READY__') {
            $proc.StandardInput.AutoFlush = $true
            $script:SqlProc = $proc
            Log "sessao ssh persistente estabelecida (pid local $($proc.Id))"
            return
        }
        try { $proc.Kill() } catch {}
        if ($t -lt 3) { Log "sessao ssh nao abriu (tentativa $t/3) — aguardando 30 s"; Start-Sleep -Seconds 30 }
    }
    throw 'nao foi possivel estabelecer a sessao ssh persistente'
}

function Stop-RemoteSession {
    if ($script:SqlProc -and -not $script:SqlProc.HasExited) {
        try { $script:SqlProc.StandardInput.Write("__QUIT__`n"); $script:SqlProc.StandardInput.Flush() } catch {}
        if (-not $script:SqlProc.WaitForExit(5000)) { try { $script:SqlProc.Kill() } catch {} }
    }
    $script:SqlProc = $null
}

function Invoke-RemoteJob([string]$kind, [string]$payload) {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if (-not $script:SqlProc -or $script:SqlProc.HasExited) { Start-RemoteSession }
        try {
            $in = $script:SqlProc.StandardInput
            # Write nao traduz \n (so WriteLine usaria CRLF) — o bash remoto exige LF puro
            $in.Write("__${kind}_BEGIN__`n")
            $in.Write(($payload -replace "`r", '') + "`n")
            $in.Write("__${kind}_END__`n")
            $in.Flush()
            $lines = [System.Collections.Generic.List[string]]::new()
            while ($true) {
                $line = $script:SqlProc.StandardOutput.ReadLine()
                if ($null -eq $line) { throw 'sessao ssh persistente caiu' }
                if ($line -match '^__DONE__ (\d+)$') { return @{ code = [int]$Matches[1]; lines = $lines } }
                $lines.Add($line)
            }
        } catch {
            Stop-RemoteSession
            if ($attempt -ge 2) { throw }
            Log "job remoto falhou ($_) — reabrindo a sessao ssh"
        }
    }
}

function Invoke-RemoteCmd([string]$cmd) {
    $r = Invoke-RemoteJob 'CMD' $cmd
    if ($r.code -ne 0) { throw "cmd remoto falhou (exit $($r.code)) cmd=[$cmd]:`n$($r.lines -join "`n")" }
    return ,@($r.lines)
}

function Invoke-Sql([string]$sql) {
    $r = Invoke-RemoteJob 'SQL' $sql
    if ($r.code -ne 0) { throw "psql falhou (exit $($r.code)) sql=[$sql]:`n$($r.lines -join "`n")" }
    return ,@($r.lines | Where-Object { $_ -ne '' })
}

function Invoke-SqlScalar([string]$sql) {
    $r = @(Invoke-Sql $sql)
    if ($r.Count -eq 0) { return $null }
    return $r[0].Trim()
}

# Le a fila local do agente (SQLite) sem brigar com o lock do servico (WAL + timeout).
function Get-QueueSnapshot {
    $py = @"
import sqlite3, json, os
p = r'$DataDir\queue.db'
if not os.path.exists(p):
    print(json.dumps({'exists': False})); raise SystemExit
con = sqlite3.connect(p, timeout=15)
cur = con.cursor()
out = {'exists': True}
out['events'] = cur.execute('SELECT count(*) FROM events').fetchone()[0]
out['events_unsent'] = cur.execute('SELECT count(*) FROM events WHERE sent = 0').fetchone()[0]
out['dead_letter'] = cur.execute('SELECT count(*) FROM dead_letter').fetchone()[0]
kv = {}
for k, v in cur.execute('SELECT key, value FROM kv').fetchall():
    kv[k] = ('<%d bytes>' % len(v)) if k.endswith('_enc') else (v.decode('utf-8', 'replace') if isinstance(v, (bytes, bytearray)) else str(v))
out['kv'] = kv
print(json.dumps(out))
"@
    $out = $py | python - 2>&1
    $stdout = (@($out | Where-Object { $_ -is [string] }) -join "`n")
    if ($LASTEXITCODE -ne 0) { throw "leitura da fila local falhou: $(($out | ForEach-Object { "$_" }) -join "`n")" }
    return $stdout | ConvertFrom-Json
}

function Read-ServiceLog {
    $files = Get-ChildItem -Path (Join-Path $DataDir 'logs') -Filter 'service-*.log' -ErrorAction SilentlyContinue
    $text = ''
    foreach ($f in $files) {
        # FileShare ReadWrite: o servico mantem o arquivo aberto para append
        $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
        try {
            $sr = New-Object IO.StreamReader($fs)
            $text += $sr.ReadToEnd() + "`n"
        } finally { $fs.Dispose() }
    }
    return $text
}

function Wait-Until([scriptblock]$cond, [int]$timeoutSec, [int]$pollSec = 10, [string]$what = 'condicao') {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        if (& $cond) { return $sw.Elapsed.TotalSeconds }
        Start-Sleep -Seconds $pollSec
    }
    return -1  # timeout
}

function Save-Evidence([string]$name, [string]$content) {
    $p = Join-Path $EvidDir $name
    [IO.File]::WriteAllText($p, $content)
}

# ---------------------------------------------------------------- fase 0: contexto
function Phase-Context {
    Log "=== FASE 0: contexto da VM ==="
    $os = (Get-CimInstance Win32_OperatingSystem)
    $sessionId = (Get-Process -Id $PID).SessionId
    $quser = try { (quser 2>&1 | ForEach-Object { "$_" }) -join "`n" } catch { "quser indisponivel: $_" }
    $script:SessionInfo = "OS: $($os.Caption) $($os.Version) | hostname: $env:COMPUTERNAME | usuario: $env:USERNAME | sessionId do job: $sessionId`nquser:`n$quser"
    Log $script:SessionInfo
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'python nao encontrado no runner (necessario para inspecionar a fila SQLite)' }
    if (-not $SshKeyPath -or -not (Test-Path $SshKeyPath)) { throw "chave SSH nao encontrada em '$SshKeyPath'" }
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { throw "VM nao esta limpa: servico $ServiceName ja existe" }
    if (Test-Path $DataDir) { throw "VM nao esta limpa: $DataDir ja existe" }

    # uma unica conexao avulsa: sobe o servidor de jobs e coleta o contexto da VPS.
    # sed remove BOM e CRs que o pipe do PowerShell para comando nativo introduz
    # (preambulo UTF-8 + CRLF final apendado) — sem isso o bash quebra no parse.
    $upCmd = 'sed -e ''1s/^\xef\xbb\xbf//'' -e ''s/\r$//'' > /tmp/f1-server.sh && echo ok && hostname && cd /opt/351monitor && git rev-parse --short HEAD && grep ''^STAGING_DOMAIN='' infra/.env'
    $ctx = Invoke-SshOnce $upCmd $RemoteServerScript
    Log "SSH ao staging OK: $($ctx -join ' | ')"
    $script:StagingCommit = @($ctx | Where-Object { $_ -match '^[0-9a-f]{7,12}$' })[0]

    Start-RemoteSession

    $domLine = @($ctx | Where-Object { $_ -like 'STAGING_DOMAIN=*' })[0]
    if (-not $domLine) { throw "STAGING_DOMAIN nao encontrado no contexto da VPS: $($ctx -join ' | ')" }
    $domain = $domLine.Split('=', 2)[1].Trim()
    $script:ApiUrl = "https://$domain"

    # C2 bloqueia por IP: o dominio da API precisa resolver exclusivamente para o IP da VPS
    $ips = @([Net.Dns]::GetHostAddresses($domain) | ForEach-Object { $_.IPAddressToString })
    $foraDoBloqueio = @($ips | Where-Object { $_ -ne $VpsHost })
    if ($foraDoBloqueio.Count -gt 0 -or $ips -notcontains $VpsHost) {
        throw "dominio $domain resolve para [$($ips -join ', ')] — o bloqueio de firewall em $VpsHost nao derrubaria o agente (C2 seria invalido)"
    }
    Log "DNS validado: $domain -> $($ips -join ', ') (coberto pelo bloqueio da C2)"

    $hz = Invoke-WebRequest -Uri "$($script:ApiUrl)/healthz" -TimeoutSec 20
    Log "API de staging: $($script:ApiUrl)/healthz -> HTTP $($hz.StatusCode) $($hz.Content)"
}

# ---------------------------------------------------------------- fase 1: backoffice
function Phase-Backoffice {
    Log "=== FASE 1: criar tenant + enrollment key no staging ==="
    $orgOut = (Invoke-RemoteCmd "docker exec $ApiContainer dotnet M351.Api.dll create-org --name F1-Aceite-$RunId --owner-email $OwnerEmail --slug $Slug") -join "`n"
    # redige o token do link de convite do Owner (vai para log publico do Actions/evidencias)
    $orgRedigido = ($orgOut -replace 'token=[A-Za-z0-9_\-\.]+', 'token=<redigido>') -replace 'convite/[A-Za-z0-9_\-\.]+', 'convite/<redigido>'
    Log "create-org:`n$orgRedigido"

    $script:TenantId = Invoke-SqlScalar "SELECT id FROM organizations WHERE slug = '$Slug';"
    if (-not $script:TenantId) { throw "org '$Slug' nao encontrada apos create-org" }
    Log "tenant_id = $($script:TenantId)"

    $keyOut = (Invoke-RemoteCmd "docker exec $ApiContainer dotnet M351.Api.dll create-enrollment-key --org-slug $Slug --label aceite-f1-$RunId") -join "`n"
    if ($keyOut -match 'ek_[A-Za-z0-9]+') { $script:EnrollKey = $Matches[0] } else { throw "enrollment key nao encontrada na saida:`n$keyOut" }
    Log "enrollment key gerada: $($script:EnrollKey.Substring(0,7))... (redigida)"
}

# ---------------------------------------------------------------- fase 2: instalar + C1
function Phase-InstallAndC1 {
    Log "=== FASE 2: instalar agente (servico real) e validar C1 (< 2 min) ==="
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item (Join-Path $PSScriptRoot '..\..\agent\publish\win-x64\MonitorAgentService.exe') $InstallDir
    Copy-Item (Join-Path $PSScriptRoot '..\..\agent\publish\win-x64\MonitorAgentSession.exe') $InstallDir

    $script:TEnroll = Get-Date
    $enrollOut = & (Join-Path $InstallDir 'MonitorAgentService.exe') --enroll $script:EnrollKey --server $script:ApiUrl 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "enroll falhou (exit $LASTEXITCODE):`n$($enrollOut -join "`n")" }
    Log "enroll OK:`n$($enrollOut -join "`n")"

    sc.exe create $ServiceName binPath= "$InstallDir\MonitorAgentService.exe" start= auto DisplayName= "M351 Monitor Agent" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc create falhou ($LASTEXITCODE)" }
    sc.exe start $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc start falhou ($LASTEXITCODE)" }
    $script:TServiceStart = Get-Date
    Log "servico $ServiceName criado (LocalSystem) e iniciado"

    # C1: eventos do tenant certo em < 2 min
    $elapsed = Wait-Until -timeoutSec 180 -pollSec 8 -what 'primeiro evento em raw_events' -cond {
        $n = Invoke-SqlScalar "SELECT count(*) FROM raw_events r JOIN devices d ON d.id = r.device_id WHERE d.tenant_id = '$($script:TenantId)';"
        [int]$n -gt 0
    }
    if ($elapsed -lt 0) {
        Add-Criterio 'C1' 'Eventos em raw_events do tenant certo em < 2 min' $false 'timeout de 180 s sem nenhum evento'
        throw 'C1 falhou — abortando (sem eventos nao ha como seguir)'
    }
    $sinceStart  = ((Get-Date) - $script:TServiceStart).TotalSeconds
    $sinceEnroll = ((Get-Date) - $script:TEnroll).TotalSeconds

    $script:DeviceId = Invoke-SqlScalar "SELECT id FROM devices WHERE tenant_id = '$($script:TenantId)' ORDER BY last_seen_at DESC NULLS LAST LIMIT 1;"
    if (-not $script:DeviceId) { throw 'device nao encontrado no tenant' }
    $devRow = (Invoke-Sql "SELECT id, hostname, status, config_version, tz_offset_min FROM devices WHERE id = '$($script:DeviceId)';") -join "`n"
    Log "device: $devRow"

    $nulls = Invoke-SqlScalar @"
SELECT count(*) FILTER (WHERE tz_offset_min IS NULL) || '/' ||
       count(*) FILTER (WHERE boot_id IS NULL) || '/' ||
       count(*) FILTER (WHERE seq IS NULL) || '/' || count(*)
FROM raw_events WHERE device_id = '$($script:DeviceId)';
"@
    $parts = $nulls.Split('/')
    $fieldsOk = ($parts[0] -eq '0') -and ($parts[1] -eq '0') -and ($parts[2] -eq '0') -and ([int]$parts[3] -gt 0)

    $sample = (Invoke-Sql "SELECT seq, event_type, occurred_at, tz_offset_min, boot_id, received_at FROM raw_events WHERE device_id = '$($script:DeviceId)' ORDER BY seq LIMIT 25;") -join "`n"
    Save-Evidence 'c1-eventos-iniciais.txt' "device_id=$($script:DeviceId) tenant_id=$($script:TenantId)`nseq|event_type|occurred_at|tz_offset_min|boot_id|received_at`n$sample"

    $dcs = (Invoke-Sql "SELECT state, last_contact_at FROM device_current_state WHERE device_id = '$($script:DeviceId)';") -join ' | '
    Log "device_current_state: $dcs"

    $timeOk = $sinceStart -le 120 -and $sinceEnroll -le 180
    Add-Criterio 'C1' 'Eventos em raw_events do tenant certo em < 2 min, com seq/tz_offset_min/boot_id' ($timeOk -and $fieldsOk) `
        ("primeiro evento {0:N0}s apos start do servico ({1:N0}s apos o enroll); NULLs tz/boot/seq = {2}/{3}/{4} de {5} eventos; current_state=[{6}]" -f $sinceStart, $sinceEnroll, $parts[0], $parts[1], $parts[2], $parts[3], $dcs)
}

# ---------------------------------------------------------------- fase 3: C2 (queda de rede)
function Phase-OutageC2 {
    Log "=== FASE 3: queda de rede de $OutageSeconds s (bloqueio de firewall p/ $VpsHost) ==="
    $preMax   = [int](Invoke-SqlScalar "SELECT coalesce(max(seq),0) FROM raw_events WHERE device_id = '$($script:DeviceId)';")
    $preCount = [int](Invoke-SqlScalar "SELECT count(*) FROM raw_events WHERE device_id = '$($script:DeviceId)';")
    Log "pre-queda: max(seq)=$preMax count=$preCount"

    # encerra a sessao ssh antes do bloqueio (seria cortada no meio); ela reabre
    # sozinha no primeiro job apos a restauracao da rede
    Stop-RemoteSession

    netsh advfirewall firewall add rule name=$FwRule dir=out action=block remoteip=$VpsHost | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'netsh add rule falhou' }
    Log "firewall: saida para $VpsHost BLOQUEADA (sem SSH durante a queda)"

    # prova de que o bloqueio pegou (conexao bloqueada lanca em Task.Wait -> $false)
    $tcp = New-Object Net.Sockets.TcpClient
    $conn = $tcp.ConnectAsync($VpsHost, 443)
    $reached = try { $conn.Wait(4000) -and $tcp.Connected } catch { $false }
    $tcp.Dispose()
    if ($reached) { throw 'bloqueio de firewall NAO surtiu efeito (443 ainda alcancavel)' }
    Log 'verificado: 443 do staging inalcancavel'

    # marco da janela DEPOIS do bloqueio confirmado: todo evento com occurred_at >= este
    # instante so pode ter chegado ao servidor apos a restauracao da rede
    $script:OutageStartUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ssZ')

    $interactive = (Get-Process -Id $PID).SessionId -gt 0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $i = 0
    while ($sw.Elapsed.TotalSeconds -lt $OutageSeconds) {
        if ($interactive) {
            # gera atividade de janela na sessao (so tem efeito se o helper estiver vivo)
            try {
                $p = Start-Process notepad -PassThru
                Start-Sleep -Seconds 12
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            } catch {}
        }
        Start-Sleep -Seconds 18
        $i++
        if ($i % 4 -eq 0) { Log ("queda em andamento: {0:N0}s / $OutageSeconds s" -f $sw.Elapsed.TotalSeconds) }
    }
    $script:OutageEndUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ssZ')

    # texto real do log tem acento: "Servidor inacessível — ..." (BatchSender.cs:135)
    $offlineLogged = (Read-ServiceLog) -match 'Servidor inacess[ií]vel'
    Log "log do agente registrou modo offline: $offlineLogged"
    $queueDuring = try { Get-QueueSnapshot } catch { $null }
    if ($queueDuring) { Log "fila local ao fim da queda: events=$($queueDuring.events) (nao enviados=$($queueDuring.events_unsent)) dead_letter=$($queueDuring.dead_letter)" }

    netsh advfirewall firewall delete rule name=$FwRule | Out-Null
    Log 'firewall: bloqueio removido — aguardando drenagem (backoff N14: proximo retry pode demorar ate ~12 min)'

    # drenagem: nada NAO-ENVIADO na fila local (o agente marca sent=1 no ack; a delecao
    # fisica e em ciclos de purge de 10 min — por isso o criterio e events_unsent)
    $drained = Wait-Until -timeoutSec 960 -pollSec 20 -what 'drenagem da fila' -cond {
        $q = try { Get-QueueSnapshot } catch { $null }
        $q -and $q.exists -and [int]$q.events_unsent -eq 0
    }
    if ($drained -lt 0) { Log 'AVISO: fila local nao drenou em 16 min' }
    Start-Sleep -Seconds 35  # um ciclo extra de batch para o ultimo ack assentar

    # asserts de perda/duplicata
    $dupEvent = (Invoke-Sql "SELECT event_id, count(*) FROM raw_events WHERE device_id = '$($script:DeviceId)' GROUP BY event_id HAVING count(*) > 1;")
    $dupSeq   = (Invoke-Sql "SELECT seq, count(*) FROM raw_events WHERE device_id = '$($script:DeviceId)' GROUP BY seq HAVING count(*) > 1;")
    $gaps     = (Invoke-Sql @"
WITH s AS (SELECT seq, lag(seq) OVER (ORDER BY seq) AS prev FROM raw_events WHERE device_id = '$($script:DeviceId)')
SELECT prev, seq, seq - prev - 1 FROM s WHERE prev IS NOT NULL AND seq - prev > 1;
"@)
    $dropped  = [int](Invoke-SqlScalar "SELECT count(*) FROM raw_events WHERE device_id = '$($script:DeviceId)' AND event_type = 'EVENTS_DROPPED';")
    $agg      = Invoke-SqlScalar "SELECT count(*) || '/' || (max(seq) - min(seq) + 1) FROM raw_events WHERE device_id = '$($script:DeviceId)';"
    $inWindow = [int](Invoke-SqlScalar "SELECT count(*) FROM raw_events WHERE device_id = '$($script:DeviceId)' AND occurred_at >= '$($script:OutageStartUtc)' AND occurred_at <= '$($script:OutageEndUtc)';")
    # prova de que a queda foi real: nenhum evento da janela chegou ao servidor DURANTE a queda
    $leaked   = [int](Invoke-SqlScalar "SELECT count(*) FROM raw_events WHERE device_id = '$($script:DeviceId)' AND occurred_at >= '$($script:OutageStartUtc)' AND occurred_at <= '$($script:OutageEndUtc)' AND received_at <= '$($script:OutageEndUtc)';")
    $seqMaxDb = Invoke-SqlScalar "SELECT d.seq_max || '/' || (SELECT max(seq) FROM raw_events r WHERE r.device_id = d.id) FROM devices d WHERE d.id = '$($script:DeviceId)';"

    # duplicates observados nos acks (log do agente)
    $ackLines = ([regex]::Matches((Read-ServiceLog), 'Lote enviado: .*duplicates=(\d+).*'))
    $dupTotal = ($ackLines | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    $aggParts = $agg.Split('/')

    Save-Evidence 'c2-pos-queda.txt' @"
queda (UTC): $($script:OutageStartUtc) -> $($script:OutageEndUtc)
pre-queda: max(seq)=$preMax count=$preCount
duplicatas por event_id: $($dupEvent.Count) linha(s)
duplicatas por seq:      $($dupSeq.Count) linha(s)
gaps de seq:             $($gaps.Count) linha(s) $(if ($gaps.Count) { "`n" + ($gaps -join "`n") })
EVENTS_DROPPED:          $dropped
count/esperado(max-min+1): $agg
devices.seq_max/max(raw): $seqMaxDb
eventos com occurred_at DENTRO da janela de queda: $inWindow
eventos da janela recebidos AINDA DURANTE a queda (deve ser 0): $leaked
agente registrou modo offline no log: $offlineLogged
soma de 'duplicates' nos acks logados pelo agente: $dupTotal
"@

    $pass = $offlineLogged -and ($leaked -eq 0) -and
            ($dupEvent.Count -eq 0) -and ($dupSeq.Count -eq 0) -and ($gaps.Count -eq 0) -and ($dropped -eq 0) -and
            ($aggParts[0] -eq $aggParts[1]) -and ($inWindow -ge 5) -and ($drained -ge 0)
    Add-Criterio 'C2' 'Queda de rede 10 min -> eventos chegam depois sem perda nem duplicata' $pass `
        "offline_logado=$offlineLogged vazados_na_queda=$leaked gaps=$($gaps.Count) dup_event_id=$($dupEvent.Count) dup_seq=$($dupSeq.Count) dropped=$dropped count/esperado=$agg eventos_na_janela=$inWindow duplicates_acks=$dupTotal drenagem=$([int]$drained)s"
}

# ---------------------------------------------------------------- fase 4: C3 (POLICY_APPLIED)
function Phase-PolicyC3 {
    Log '=== FASE 4: mudar idle_threshold_sec no banco -> POLICY_APPLIED ==='
    $vAtual = [int](Invoke-SqlScalar "SELECT config_version FROM tenant_agent_configs WHERE tenant_id = '$($script:TenantId)';")
    $vNova = $vAtual + 1
    Invoke-Sql "UPDATE tenant_agent_configs SET idle_threshold_sec = 600, config_version = config_version + 1, updated_at = now() WHERE tenant_id = '$($script:TenantId)';" | Out-Null
    Log "config: idle_threshold_sec 300 -> 600, config_version $vAtual -> $vNova"

    # ">=" e nao "=": um retry da sessao ssh pode reexecutar o UPDATE (version +2)
    $elapsed = Wait-Until -timeoutSec 240 -pollSec 10 -what 'POLICY_APPLIED' -cond {
        $n = Invoke-SqlScalar "SELECT count(*) FROM raw_events WHERE device_id = '$($script:DeviceId)' AND event_type = 'POLICY_APPLIED' AND (payload->>'config_version')::int >= $vNova;"
        [int]$n -gt 0
    }
    $devVer = [int](Invoke-SqlScalar "SELECT config_version FROM devices WHERE id = '$($script:DeviceId)';")
    $logHit = (Read-ServiceLog) -match "Config v\d+ aplicada"
    $evRow = (Invoke-Sql "SELECT seq, occurred_at, payload, received_at FROM raw_events WHERE device_id = '$($script:DeviceId)' AND event_type = 'POLICY_APPLIED' ORDER BY seq DESC LIMIT 3;") -join "`n"
    Save-Evidence 'c3-policy-applied.txt' "config_version esperada: $vNova`ndevices.config_version: $devVer`nlog do agente contem 'Config v$vNova aplicada': $logHit`neventos POLICY_APPLIED:`n$evRow"

    $pass = ($elapsed -ge 0) -and ($devVer -ge $vNova) -and $logHit
    Add-Criterio 'C3' 'idle_threshold_sec mudado no banco -> agente aplica e emite POLICY_APPLIED' $pass `
        ("POLICY_APPLIED v$vNova em {0}s; devices.config_version=$devVer; log do agente: $logHit" -f $(if ($elapsed -ge 0) { [int]$elapsed } else { 'TIMEOUT' }))
}

# ---------------------------------------------------------------- fase 5: C4 (UNENROLL)
function Phase-UnenrollC4 {
    Log '=== FASE 5: UNENROLL -> para coleta e zera fila local ==='
    Invoke-Sql "INSERT INTO device_commands (id, tenant_id, device_id, type, payload) VALUES (gen_random_uuid(), '$($script:TenantId)', '$($script:DeviceId)', 'UNENROLL', '{}'::jsonb);" | Out-Null

    $delivered = Wait-Until -timeoutSec 180 -pollSec 10 -what 'delivered_at do UNENROLL' -cond {
        $d = Invoke-SqlScalar "SELECT count(*) FROM device_commands WHERE device_id = '$($script:DeviceId)' AND type = 'UNENROLL' AND delivered_at IS NOT NULL;"
        [int]$d -gt 0
    }
    Start-Sleep -Seconds 20

    $q = Get-QueueSnapshot
    $queueZero = $q.exists -and ([int]$q.events -eq 0) -and ([int]$q.dead_letter -eq 0)
    $kvKeys = @($q.kv.PSObject.Properties.Name)
    $identityGone = ($kvKeys -notcontains 'device_id') -and ($kvKeys -notcontains 'device_token_enc') -and ($q.kv.unenrolled -eq '1')

    $svcLog = Read-ServiceLog
    $logHit = ($svcLog -match 'Comando UNENROLL recebido') -and ($svcLog -match 'UNENROLL: parando helpers e coleta')

    # congelamento: nada mais chega ao servidor
    $m1 = Invoke-SqlScalar "SELECT coalesce(max(received_at)::text,'-') FROM raw_events WHERE device_id = '$($script:DeviceId)';"
    Log "max(received_at) apos UNENROLL: $m1 — aguardando 90 s para provar congelamento"
    Start-Sleep -Seconds 90
    $m2 = Invoke-SqlScalar "SELECT coalesce(max(received_at)::text,'-') FROM raw_events WHERE device_id = '$($script:DeviceId)';"
    $frozen = ($m1 -eq $m2)

    $cmdRow = (Invoke-Sql "SELECT type, created_at, delivered_at FROM device_commands WHERE device_id = '$($script:DeviceId)';") -join "`n"
    Save-Evidence 'c4-unenroll.txt' @"
device_commands:
$cmdRow
fila local: events=$($q.events) dead_letter=$($q.dead_letter)
kv keys: $($kvKeys -join ', ')
kv.unenrolled: $($q.kv.unenrolled)
log do agente (UNENROLL): $logHit
max(received_at) t0=$m1 t0+90s=$m2 congelado=$frozen
"@

    $pass = ($delivered -ge 0) -and $queueZero -and $identityGone -and $logHit -and $frozen
    Add-Criterio 'C4' 'UNENROLL para a coleta e zera a fila local' $pass `
        "delivered_at=$(if ($delivered -ge 0) { 'ok' } else { 'TIMEOUT' }); fila events=$($q.events)/dead=$($q.dead_letter); identidade removida=$identityGone; log=$logHit; ingestao congelada=$frozen"
}

# ---------------------------------------------------------------- relatorio
function Write-Report([string]$status, [string]$erro) {
    $runUrl = if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { '(execucao local)' }
    $rows = ($Criterios | ForEach-Object { "| $($_.Id) | $($_.Criterio) | **$($_.Resultado)** | $($_.Evidencia) |" }) -join "`n"
    $md = @"
# Aceite F1 — teste em VM limpa

- **Status geral:** $status
- **Data (UTC):** $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm'))
- **VM:** runner GitHub Actions efemero (VM limpa recem-provisionada)
- **Run:** $runUrl
- **Commit testado:** $env:GITHUB_SHA
- **Staging:** $($script:ApiUrl) (commit na VPS: $($script:StagingCommit))
- **Tenant do teste:** slug ``$Slug`` (tenant_id $($script:TenantId)) — criado so para este aceite; pode ser removido
- **Device:** $($script:DeviceId)

## Ambiente da VM

``````
$($script:SessionInfo)
``````

Observacao: se o job nao roda em sessao interativa (sessionId 0), o helper de sessao
nao e lancado e a coleta se restringe a heartbeats de maquina (state=no_session) —
os 4 criterios da F1 nao dependem de eventos de janela. A coleta de janela ativa em
sessao interativa foi validada no E2E local (commit 18bb61d).

## Criterios (docs/PROMPT-DESENVOLVIMENTO.md, secao 10 — F1 "Pronto quando")

| # | Criterio | Resultado | Evidencia |
|---|----------|-----------|-----------|
$rows

$(if ($erro) { "## Erro fatal`n``````
$erro
``````" })

## Linha do tempo

``````
$($Transcript -join "`n")
``````
"@
    if ($script:EnrollKey) { $md = $md.Replace($script:EnrollKey, 'ek_<REDIGIDA>') }
    Save-Evidence 'ACEITE-F1-RELATORIO.md' $md

    # copia logs do agente como .txt (o .gitignore exclui *.log)
    try {
        Get-ChildItem -Path (Join-Path $DataDir 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object {
            $dst = Join-Path $EvidDir ($_.BaseName + '.servico.txt')
            $txt = Read-ServiceLog
            if ($script:EnrollKey) { $txt = $txt.Replace($script:EnrollKey, 'ek_<REDIGIDA>') }
            [IO.File]::WriteAllText($dst, $txt)
        }
        Get-ChildItem -Path "C:\Users\*\AppData\Local\M351\MonitorAgent\logs" -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $EvidDir ($_.BaseName + '.sessao.txt'))
        }
    } catch { Log "copia de logs falhou: $_" }
}

# ---------------------------------------------------------------- main
$fatal = $null
try {
    Phase-Context
    Phase-Backoffice
    Phase-InstallAndC1
    Phase-OutageC2
    Phase-PolicyC3
    Phase-UnenrollC4
} catch {
    $fatal = "$_`n$($_.ScriptStackTrace)"
    Log "ERRO FATAL: $fatal"
} finally {
    # nunca deixar o bloqueio de firewall para tras
    netsh advfirewall firewall delete rule name=$FwRule 2>$null | Out-Null
    try { sc.exe stop $ServiceName 2>$null | Out-Null; Start-Sleep -Seconds 5 } catch {}
    try { Stop-RemoteSession } catch {}

    $fails = @($Criterios | Where-Object { $_.Resultado -eq 'FAIL' }).Count
    $total = $Criterios.Count
    $status = if ($fatal) { "ERRO FATAL (criterios avaliados: $total/4)" }
              elseif ($fails -eq 0 -and $total -eq 4) { 'APROVADO — 4/4 criterios PASS: F1 fechada' }
              else { "REPROVADO — $fails de $total criterios FAIL" }
    Write-Report $status $fatal
    Log "=== RESULTADO: $status ==="
    if ($env:GITHUB_STEP_SUMMARY) { Get-Content (Join-Path $EvidDir 'ACEITE-F1-RELATORIO.md') -Raw | Out-File $env:GITHUB_STEP_SUMMARY -Encoding utf8NoBOM }
}

if ($fatal -or $fails -gt 0 -or $total -lt 4) { exit 1 }
exit 0
