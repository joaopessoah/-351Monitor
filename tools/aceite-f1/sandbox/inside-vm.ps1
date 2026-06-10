# Roda DENTRO do Windows Sandbox (logon command do f1-aceite.wsb).
# Instala o agente como servico real e fica ouvindo comandos simples do host
# via C:\kit\ctl\commands.txt (uma linha por comando). O host (com acesso SSH
# ao staging) dirige as fases e faz as verificacoes de banco.
#
# Comandos aceitos: BLOCK | UNBLOCK | SNAPSHOT | STOPSVC | STARTSVC
# Status e respostas: C:\kit\ctl\status.log (append)

$ErrorActionPreference = 'Stop'
$Ctl = 'C:\kit\ctl'
$Bin = 'C:\kit\bin'
$InstallDir = 'C:\M351\MonitorAgent'
$DataDir = Join-Path $env:ProgramData 'M351\MonitorAgent'
$FwRule = 'F1-ACEITE-SANDBOX'

function Say([string]$m) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $m
    Add-Content -Path (Join-Path $Ctl 'status.log') -Value $line
    Write-Host $line
}

try {
    $cfg = @{}
    Get-Content (Join-Path $Ctl 'kit.config') | ForEach-Object {
        if ($_ -match '^\s*([A-Z_]+)\s*=\s*(.+)$') { $cfg[$Matches[1]] = $Matches[2].Trim() }
    }
    if (-not $cfg.ENROLL_KEY -or -not $cfg.SERVER_URL) { throw 'kit.config precisa de ENROLL_KEY e SERVER_URL' }

    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item (Join-Path $Bin 'MonitorAgentService.exe') $InstallDir
    Copy-Item (Join-Path $Bin 'MonitorAgentSession.exe') $InstallDir
    Say 'binarios copiados'

    $out = & (Join-Path $InstallDir 'MonitorAgentService.exe') --enroll $cfg.ENROLL_KEY --server $cfg.SERVER_URL 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "enroll falhou: $($out -join ' / ')" }
    Say "enroll OK: $($out -join ' / ')"

    sc.exe create MonitorAgentService binPath= "$InstallDir\MonitorAgentService.exe" start= auto DisplayName= "M351 Monitor Agent" | Out-Null
    sc.exe start MonitorAgentService | Out-Null
    Say "servico iniciado (sessao interativa: $((Get-Process -Id $PID).SessionId))"
    Say 'PRONTO — aguardando comandos em commands.txt'

    $done = $false
    while (-not $done) {
        Start-Sleep -Seconds 5
        $cmdFile = Join-Path $Ctl 'commands.txt'
        if (-not (Test-Path $cmdFile)) { continue }
        $cmds = @(Get-Content $cmdFile | Where-Object { $_ })
        Clear-Content $cmdFile
        foreach ($c in $cmds) {
            switch -Regex ($c.Trim()) {
                '^BLOCK\s+(\S+)$' {
                    netsh advfirewall firewall add rule name=$FwRule dir=out action=block remoteip=$Matches[1] | Out-Null
                    Say "BLOCK $($Matches[1]) aplicado"
                }
                '^UNBLOCK$' {
                    netsh advfirewall firewall delete rule name=$FwRule | Out-Null
                    Say 'UNBLOCK aplicado'
                }
                '^SNAPSHOT$' {
                    $log = Get-ChildItem (Join-Path $DataDir 'logs') -Filter 'service-*.log' -ErrorAction SilentlyContinue |
                        ForEach-Object { Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue }
                    Set-Content (Join-Path $Ctl 'service-log-snapshot.txt') ($log -join "`n")
                    Say 'SNAPSHOT do log gravado em service-log-snapshot.txt'
                }
                '^STOPSVC$'  { sc.exe stop MonitorAgentService | Out-Null; Say 'servico parado' }
                '^STARTSVC$' { sc.exe start MonitorAgentService | Out-Null; Say 'servico iniciado' }
                '^QUIT$'     { $done = $true; Say 'encerrando listener' }
                default      { Say "comando desconhecido: $c" }
            }
        }
    }
} catch {
    Say "ERRO: $_"
}
