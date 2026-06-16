#Requires -Version 5.1
<#
.SYNOPSIS
    +351 Monitor — build do instalador MSI do agente Windows (F4.1).

.DESCRIPTION
    1. Publica os 2 exes (single-file, self-contained, win-x64, R2R, SEM trimming).
    2. GANCHO de assinatura (F5): se SIGN_THUMBPRINT ou SIGN_PFX estiver definido, assina os
       exes e o MSI com signtool (timestamp RFC3161); senao, avisa e segue sem assinar.
    3. wix build -> MonitorAgent.msi (consome a versao do agente de AgentVersionInfo.cs).
    4. Imprime o caminho e o tamanho do .msi.

    NAO instala nada (nenhum msiexec /i). A instalacao real e validacao do Joao em VM/dominio.

.NOTES
    Requer: dotnet 8 + wix v5 (dotnet tool install --global wix --version 5.0.2) com a extensao
    WixToolset.Util.wixext. Code signing real fica para a F5 — aqui so o gancho.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

# Raizes (script em agent/installer).
$InstallerDir = $PSScriptRoot
$AgentDir     = Split-Path -Parent $InstallerDir
$RepoRoot     = Split-Path -Parent $AgentDir
$Solution     = Join-Path $AgentDir "M351.Agent.sln"
$ServiceProj  = Join-Path $AgentDir "src\MonitorAgentService\MonitorAgentService.csproj"
$SessionProj  = Join-Path $AgentDir "src\MonitorAgentSession\MonitorAgentSession.csproj"
$PublishDir   = Join-Path $InstallerDir "publish"
$OutDir       = Join-Path $InstallerDir "bin"
$MsiPath      = Join-Path $OutDir "MonitorAgent.msi"

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- Versao do agente: fonte unica AgentVersionInfo.Current ("1.0.0") ---
$versionFile = Join-Path $AgentDir "src\M351.Agent.Core\AgentVersionInfo.cs"
$versionMatch = Select-String -Path $versionFile -Pattern 'Current\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"'
if (-not $versionMatch) { throw "Nao foi possivel extrair a versao de $versionFile" }
$ProductVersion = $versionMatch.Matches[0].Groups[1].Value
Write-Step "Versao do agente: $ProductVersion"

# --- 1. Publish dos 2 exes (single-file, self-contained, R2R, sem trimming) ---
Write-Step "Publicando MonitorAgentService.exe e MonitorAgentSession.exe ($Runtime)…"
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

$publishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishReadyToRun=true",
    "-p:PublishTrimmed=false",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=none",
    "-o", $PublishDir,
    "--nologo"
)
& dotnet publish $ServiceProj @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish do servico falhou (exit $LASTEXITCODE)" }
& dotnet publish $SessionProj @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish do helper falhou (exit $LASTEXITCODE)" }

$svcExe = Join-Path $PublishDir "MonitorAgentService.exe"
$sesExe = Join-Path $PublishDir "MonitorAgentSession.exe"
foreach ($e in @($svcExe, $sesExe)) {
    if (-not (Test-Path $e)) { throw "Esperava o exe publicado: $e" }
}

# --- 2. GANCHO de assinatura (Authenticode) — implementacao real fica para a F5 ---
function Invoke-SignFiles([string[]]$files) {
    $thumb = $env:SIGN_THUMBPRINT
    $pfx   = $env:SIGN_PFX
    if ([string]::IsNullOrWhiteSpace($thumb) -and [string]::IsNullOrWhiteSpace($pfx)) {
        Write-Host "    code signing pulado (F5): defina SIGN_THUMBPRINT ou SIGN_PFX para assinar." -ForegroundColor Yellow
        return
    }
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) { throw "SIGN_* definido mas signtool.exe nao foi encontrado no PATH." }
    $ts = "http://timestamp.digicert.com"  # timestamp RFC3161
    foreach ($f in $files) {
        if (-not [string]::IsNullOrWhiteSpace($thumb)) {
            & $signtool sign /fd SHA256 /tr $ts /td SHA256 /sha1 $thumb $f
        } else {
            $pwd = $env:SIGN_PFX_PASSWORD
            & $signtool sign /fd SHA256 /tr $ts /td SHA256 /f $pfx /p $pwd $f
        }
        if ($LASTEXITCODE -ne 0) { throw "signtool falhou para $f (exit $LASTEXITCODE)" }
        Write-Host "    assinado: $f" -ForegroundColor Green
    }
}

Write-Step "Assinatura dos exes (gancho F5)…"
Invoke-SignFiles -files @($svcExe, $sesExe)

# --- 3. wix build -> MonitorAgent.msi ---
Write-Step "Compilando o MSI com WiX…"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$wxs = Join-Path $InstallerDir "Package.wxs"
# -sw1149: o WiX recomenda o util:ServiceConfig no lugar do ServiceConfig nativo, mas o util NAO
# expressa DelayedAutoStart. Usamos o ServiceConfig nativo SO para delayed-auto-start (que o
# Windows honra via tabela ServiceConfig) e o util:ServiceConfig para o recovery. Aviso suprimido.
& wix build $wxs `
    -arch x64 `
    -ext WixToolset.Util.wixext `
    -sw1149 `
    -d "PublishDir=$PublishDir" `
    -d "ProductVersion=$ProductVersion" `
    -o $MsiPath
if ($LASTEXITCODE -ne 0) { throw "wix build falhou (exit $LASTEXITCODE)" }

# --- 2b. Assina o MSI (mesmo gancho) ---
Write-Step "Assinatura do MSI (gancho F5)…"
Invoke-SignFiles -files @($MsiPath)

# --- 4. Resultado ---
$len = (Get-Item $MsiPath).Length
$mb = [math]::Round($len / 1MB, 1)
Write-Step "MSI gerado:"
Write-Host "    $MsiPath" -ForegroundColor Green
Write-Host "    tamanho: $len bytes (~$mb MB)" -ForegroundColor Green
