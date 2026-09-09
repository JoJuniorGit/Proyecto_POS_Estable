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

echo [1/4] Deteniendo servicio de Windows 'PosBackendService'...
powershell -Command ^
  "$svc = Get-Service -Name 'PosBackendService' -ErrorAction SilentlyContinue; " ^
  "if ($svc -and $svc.Status -ne 'Stopped') { " ^
  "    Stop-Service -Name 'PosBackendService' -Force -ErrorAction SilentlyContinue; " ^
  "    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10)); " ^
  "}"

:: Si el proceso sigue vivo tras el timeout de parada suave, terminarlo
taskkill /F /IM Backend.API.exe >nul 2>&1

echo [2/4] Resolviendo ruta de instalacion y copiando binarios...
powershell -Command ^
  "$target = $null; " ^
  "$nssmDir = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\PosBackendService\Parameters' -ErrorAction SilentlyContinue).AppDirectory; " ^
  "if ($nssmDir -and (Test-Path $nssmDir)) { $target = $nssmDir } " ^
  "elseif (Test-Path \"${env:ProgramFiles}\Sistema POS Administrador\BackendAPI\") { $target = \"${env:ProgramFiles}\Sistema POS Administrador\BackendAPI\" } " ^
  "elseif (Test-Path \"${env:ProgramFiles(x86)}\Sistema POS Administrador\BackendAPI\") { $target = \"${env:ProgramFiles(x86)}\Sistema POS Administrador\BackendAPI\" } " ^
  "if (-not $target) { Write-Error 'Directorio de instalacion del servicio no encontrado.'; exit 1 } " ^
  "Write-Host \"Destino resuelto: $target\"; " ^
  "Copy-Item -Path '.\publish\BackendAPI\*' -Destination $target -Recurse -Force -Exclude 'appsettings.Production.json','appsettings.Development.json','certs','logs'; " ^
  "Write-Host 'Archivos actualizados correctamente sin desplegar secretos de desarrollo.'"

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Fallo la actualizacion de binarios.
    pause
    exit /b 1
)

echo [3/4] Iniciando servicio 'PosBackendService'...
powershell -Command ^
  "$svc = Get-Service -Name 'PosBackendService' -ErrorAction SilentlyContinue; " ^
  "if ($svc) { " ^
  "    Start-Service -Name 'PosBackendService' -ErrorAction SilentlyContinue; " ^
  "    $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(10)); " ^
  "}"

echo [4/4] Verificando estado del servicio...
sc.exe query PosBackendService | findstr "STATE"

echo.
echo =======================================================
echo   ACTUALIZACION COMPLETADA CON EXITO.
echo =======================================================
echo.
echo Presione cualquier tecla para salir...
pause >nul
