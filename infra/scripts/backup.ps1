# =============================================================================
# +351 Monitor - backup logico do PostgreSQL (Windows / dev) com retencao 14 dias
# pg_dump em formato custom (-Fc). Compativel com Windows PowerShell 5.1.
#
# Uso:
#   $env:PGPASSWORD = 'postgres'
#   powershell -NoProfile -ExecutionPolicy Bypass -File infra\scripts\backup.ps1
#
# Parametros opcionais (defaults = banco dev local):
#   -PgHost localhost -Port 5432 -Database m351_dev -User postgres `
#   -BackupDir C:\Backups\m351 -RetentionDays 14
#
# pg_dump.exe: usa o PATH; se nao estiver no PATH, defina $env:PGBIN, ex.:
#   $env:PGBIN = 'C:\Program Files\PostgreSQL\16\bin'
#
# Agendamento diario 02:15 (Task Scheduler, executar como SYSTEM ou usuario de servico):
#   schtasks /Create /TN "M351 Backup Postgres" /SC DAILY /ST 02:15 /RU SYSTEM `
#     /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\dev\351-monitor\infra\scripts\backup.ps1"
#   (a senha deve estar em %APPDATA%\postgresql\pgpass.conf do usuario da tarefa,
#    formato: localhost:5432:m351_dev:postgres:SENHA - evita PGPASSWORD em texto claro)
#
# Restore:
#   pg_restore -h localhost -p 5432 -U postgres -d m351_dev --clean --if-exists ARQUIVO.dump
#
# Lembrete LGPD: o dump contem dados pessoais - armazene somente em midia/storage
# no Brasil e com acesso restrito (residencia BR).
# =============================================================================
param(
    [string]$PgHost = "localhost",
    [int]$Port = 5432,
    [string]$Database = "m351_dev",
    [string]$User = "postgres",
    [string]$BackupDir = "C:\Backups\m351",
    [int]$RetentionDays = 14
)

$ErrorActionPreference = "Stop"

# Localiza o pg_dump
$pgDump = "pg_dump"
if ($env:PGBIN) { $pgDump = Join-Path $env:PGBIN "pg_dump.exe" }

if (-not (Test-Path $BackupDir)) {
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$out = Join-Path $BackupDir "m351_${Database}_$stamp.dump"

if (-not $env:PGPASSWORD) {
    Write-Warning "PGPASSWORD nao definido - pg_dump usara pgpass.conf ou falhara na autenticacao."
}

Write-Output "[backup] iniciando pg_dump de $Database -> $out"
# IMPORTANTE: usar --file (nao redirecionamento >) - o PowerShell corromperia
# a saida binaria do formato custom ao redirecionar.
& $pgDump --host $PgHost --port $Port --username $User --dbname $Database --format=custom --file $out
if ($LASTEXITCODE -ne 0) {
    if (Test-Path $out) { Remove-Item -Force $out }
    throw "pg_dump falhou com codigo $LASTEXITCODE"
}

# Retencao: apaga dumps com mais de $RetentionDays dias
$limite = (Get-Date).AddDays(-$RetentionDays)
Get-ChildItem -Path $BackupDir -Filter "m351_*.dump" -File |
    Where-Object { $_.LastWriteTime -lt $limite } |
    Remove-Item -Force

Write-Output "[backup] ok: $out"
