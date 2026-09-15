@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0.."

:: Verificar privilegios de Administrador
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo Solicitando permisos de Administrador...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo =======================================================
echo   ACTUALIZACION DE SERVICIO POS Y BINARIOS DEL BACKEND
echo =======================================================
echo.

powershell -ExecutionPolicy Bypass -Command "^
    $ErrorActionPreference = 'Stop'; ^
    Write-Host '[1/4] Deteniendo servicio...'; ^
    $svc = Get-Service -Name 'PosBackendService' -ErrorAction SilentlyContinue; ^
    if ($svc -and $svc.Status -ne 'Stopped') { ^
        Stop-Service -Name 'PosBackendService' -Force; ^
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10)); ^
    } ^
    Stop-Process -Name 'Backend.API' -Force -ErrorAction SilentlyContinue; ^
    Write-Host '[2/4] Verificando integridad y respaldando...'; ^
    $src = '.\publish\BackendAPI'; ^
    if (-not (Test-Path \"$src\Backend.API.exe\")) { throw 'Falta Backend.API.exe en el origen.' } ^
    $nssmDir = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\PosBackendService\Parameters' -ErrorAction SilentlyContinue).AppDirectory; ^
    if ($nssmDir -and (Test-Path $nssmDir)) { $target = $nssmDir } ^
    elseif (Test-Path \"${env:ProgramFiles}\Sistema POS Administrador\BackendAPI\") { $target = \"${env:ProgramFiles}\Sistema POS Administrador\BackendAPI\" } ^
    elseif (Test-Path \"${env:ProgramFiles(x86)}\Sistema POS Administrador\BackendAPI\") { $target = \"${env:ProgramFiles(x86)}\Sistema POS Administrador\BackendAPI\" } ^
    else { throw 'Directorio de instalacion del servicio no encontrado.' } ^
    $backup = \"$target\_backup_$(Get-Date -f yyyyMMdd_HHmmss)\"; ^
    New-Item -ItemType Directory -Path $backup | Out-Null; ^
    Copy-Item \"$target\*\" -Destination $backup -Recurse -Force -Exclude '_backup_*'; ^
    Write-Host '[3/4] Copiando nuevos binarios...'; ^
    try { ^
        Copy-Item -Path \"$src\*\" -Destination $target -Recurse -Force -Exclude 'appsettings.Production.json','appsettings.Development.json','certs','logs','secrets.json'; ^
    } catch { ^
        Write-Host 'Error al copiar. Haciendo rollback...' -ForegroundColor Red; ^
        Copy-Item \"$backup\*\" -Destination $target -Recurse -Force; ^
        throw; ^
    } ^
    Write-Host '[4/4] Iniciando servicio...'; ^
    if ($svc) { ^
        Start-Service -Name 'PosBackendService'; ^
        $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(10)); ^
    } ^
    Write-Host 'ACTUALIZACION COMPLETADA CON EXITO.' -ForegroundColor Green; ^
"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Fallo la actualizacion.
    pause
    exit /b 1
)

pause
