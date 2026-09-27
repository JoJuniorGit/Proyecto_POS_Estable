@echo off
setlocal
set "SERVICE_NAME=PosBackendService"
set "BAT_DIR=%~dp0"
set "NSSM=%BAT_DIR%BackendAPI\nssm.exe"

net session >nul 2>&1
if %errorlevel% == 0 goto :isadmin

echo Solicitando permisos de administrador para reiniciar el servicio %SERVICE_NAME%...
powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs" 2>nul
exit /b

:isadmin
if exist "%NSSM%" (
    "%NSSM%" restart %SERVICE_NAME%
) else (
    sc.exe stop %SERVICE_NAME% >nul
    sc.exe start %SERVICE_NAME% >nul
)

if %errorlevel% == 0 (
    echo Servicio %SERVICE_NAME% reiniciado correctamente.
) else (
    echo No se pudo reiniciar el servicio %SERVICE_NAME%. Verifique que el sistema POS este instalado.
)
timeout /t 6 /nobreak >nul
exit /b