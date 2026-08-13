# build-clean.ps1 - Script de compilación optimizado que elimina bloqueos de archivos
# y aplica restauración condicional (--no-restore) para acelerar builds incrementales.
#
# Uso: .\build-clean.ps1 [argumentos adicionales para dotnet build]
# Ejemplo: .\build-clean.ps1 -c Release
#          .\build-clean.ps1 --force-restore

param(
    [switch]$ForceRestore
)

$ErrorActionPreference = "SilentlyContinue"

# 1. Matar procesos que bloquean DLLs en bin/ y obj/
$processes = @("Desktop.Client", "Backend.API", "VBCSCompiler")
foreach ($proc in $processes) {
    $running = Get-Process -Name $proc -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "  Cerrando $proc (PID $($running.Id -join ', '))..." -ForegroundColor Yellow
        Stop-Process -Name $proc -Force -ErrorAction SilentlyContinue
    }
}

# 2. Breve espera para liberar handles de archivo
Start-Sleep -Milliseconds 300

$rootDir = $PSScriptRoot
$slnPath = Join-Path $rootDir "CommandCenter.slnx"
$timestampFile = Join-Path $rootDir ".last-restore-timestamp"

# 3. Detectar si algún archivo de proyecto/configuración ha cambiado desde el último restore
$projectFiles = Get-ChildItem -Path $rootDir -Recurse -Include *.csproj, *.slnx, Directory.*.props, packages.config -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$shouldRestore = $false
if ($ForceRestore -or !(Test-Path $timestampFile)) {
    $shouldRestore = $true
} else {
    $lastRestoreTime = (Get-Item $timestampFile).LastWriteTime
    foreach ($file in $projectFiles) {
        if ($file.LastWriteTime -gt $lastRestoreTime) {
            $shouldRestore = $true
            Write-Host "  [Restore requerido] Se detectó modificación en $($file.Name)" -ForegroundColor Yellow
            break
        }
    }
}

# Reconstruir los argumentos pasados
$extraArgsList = @()
if ($args.Count -gt 0) {
    foreach ($a in $args) {
        if ($a -ne "--force-restore") {
            $extraArgsList += $a
        }
    }
}

if (!$shouldRestore) {
    $extraArgsList += "--no-restore"
}

$extraArgs = $extraArgsList -join ' '

$ErrorActionPreference = "Continue"
Write-Host ""
Write-Host "  dotnet build `"$slnPath`" $extraArgs" -ForegroundColor Cyan
Write-Host ""

$cmd = "dotnet build `"$slnPath`" $extraArgs"
Invoke-Expression $cmd

$exitCode = $LASTEXITCODE

# 4. Si la compilación fue exitosa, actualizar la marca de tiempo de restauración
if ($exitCode -eq 0) {
    Get-Date | Out-File -FilePath $timestampFile -Encoding utf8
}

exit $exitCode
