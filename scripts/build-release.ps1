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

Write-Host "[1/6] Limpiando carpetas de salida preexistentes..." -ForegroundColor Cyan
if (Test-Path "$rootDir\publish") { Remove-Item "$rootDir\publish" -Recurse -Force }
if (Test-Path "$rootDir\publish_backend") { Remove-Item "$rootDir\publish_backend" -Recurse -Force }
if (Test-Path "$rootDir\dist_installer") { Remove-Item "$rootDir\dist_installer" -Recurse -Force }

New-Item -ItemType Directory -Path "$rootDir\publish\BackendAPI" | Out-Null
New-Item -ItemType Directory -Path "$rootDir\publish\DesktopClient" | Out-Null
New-Item -ItemType Directory -Path "$rootDir\publish\UpdaterService" | Out-Null

Write-Host "[2/6] Compilando React Web.Frontend en Backend.API/wwwroot..." -ForegroundColor Cyan
Set-Location "$rootDir\Web.Frontend"
if (Test-Path "package.json") {
    npm run build
}
Set-Location $rootDir

Write-Host "[3/6] Publicando Backend.API (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
dotnet publish "$rootDir\Backend.API\Backend.API.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\BackendAPI"

Write-Host "[4/6] Publicando Desktop.Client (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
dotnet publish "$rootDir\Desktop.Client\Desktop.Client.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\DesktopClient"

Write-Host "[5/6] Publicando UpdaterService (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
dotnet publish "$rootDir\UpdaterService\UpdaterService.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\UpdaterService"

# 8.27-A04: verificación de que los artefactos publicados NO contienen literales de
# credenciales conocidos (p. ej. PosHttpsDev2026! o la clave JWT dev histórica). Si un
# publish stale o un cambio deja escapar estos valores, el build se ABORTA antes de
# empaquetar el instalador que recibiría un cliente.
Write-Host "[6/6] Escaneando publish/ por secretos conocidos..." -ForegroundColor Cyan
$forbidden = @('PosHttpsDev2026!', 'ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795')
$leakLines = foreach ($needle in $forbidden) {
    Get-ChildItem "$rootDir\publish" -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.json', '.config', '.xml', '.txt', '.iss', '.ps1' } |
        Select-String -SimpleMatch -Pattern $needle -ErrorAction SilentlyContinue |
        ForEach-Object { "{0}:{1} -> {2}" -f $_.Path, $_.LineNumber, $needle }
}
if ($leakLines) {
    Write-Host "ABORTANDO: secreto conocido detectado en artefactos publicados:" -ForegroundColor Red
    $leakLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "build-release abortado: los artefactos contienen literales de credenciales conocidos."
}
Write-Host "Scrub de secretos OK: publish/ sin literales conocidos." -ForegroundColor Green

# 8.30-A4: el certificado HTTPS de desarrollo (certs\pos-https.pfx) tampoco debe empaquetarse:
# en el sitio el certificado se genera por estacion (scripts/create-https-cert.ps1) y se provee
# por Windows Store o pfx local fuera del publish. Si vuelve a colarse en CUALQUIER subcarpeta del
# publish (BackendAPI, DesktopClient o UpdaterService), el build se ABORTA.
$leakedPfx = Get-ChildItem "$rootDir\publish" -Recurse -File -Filter "*.pfx" -ErrorAction SilentlyContinue
if ($leakedPfx) {
    Write-Host "ABORTANDO: se empaquetaron certificados .pfx de desarrollo en el publish Release:" -ForegroundColor Red
    $leakedPfx | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
    throw "build-release abortado: los certificados de desarrollo no deben viajar en el instalador de cliente."
}
Write-Host "Verificacion de pfx OK: publish/ sin certs .pfx de desarrollo." -ForegroundColor Green

Write-Host "=== Publicación Autónoma completada exitosamente ===" -ForegroundColor Green
