# =====================================================================
# Script de Compilación y Publicación Autónoma (.NET Self-Contained)
# =====================================================================

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Path $PSScriptRoot -Parent
Set-Location $rootDir

# Matar procesos que bloquean DLLs en bin/ y obj/
$processes = @("Desktop.Client", "Backend.API", "VBCSCompiler")
foreach ($proc in $processes) {
    Stop-Process -Name $proc -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 300

Write-Host "[1/5] Limpiando carpetas de salida preexistentes..." -ForegroundColor Cyan
if (Test-Path "$rootDir\publish") { Remove-Item "$rootDir\publish" -Recurse -Force }
if (Test-Path "$rootDir\dist_installer") { Remove-Item "$rootDir\dist_installer" -Recurse -Force }

New-Item -ItemType Directory -Path "$rootDir\publish\BackendAPI" | Out-Null
New-Item -ItemType Directory -Path "$rootDir\publish\DesktopClient" | Out-Null
New-Item -ItemType Directory -Path "$rootDir\publish\UpdaterService" | Out-Null

Write-Host "[2/5] Compilando React Web.Frontend en Backend.API/wwwroot..." -ForegroundColor Cyan
Set-Location "$rootDir\Web.Frontend"
if (Test-Path "package.json") {
    npm run build
}
Set-Location $rootDir

Write-Host "[3/5] Publicando Backend.API (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
dotnet publish "$rootDir\Backend.API\Backend.API.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\BackendAPI"

Write-Host "[4/5] Publicando Desktop.Client (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
dotnet publish "$rootDir\Desktop.Client\Desktop.Client.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\DesktopClient"

Write-Host "[5/5] Publicando UpdaterService (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
dotnet publish "$rootDir\UpdaterService\UpdaterService.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\UpdaterService"

Write-Host "=== Publicación Autónoma completada exitosamente ===" -ForegroundColor Green
