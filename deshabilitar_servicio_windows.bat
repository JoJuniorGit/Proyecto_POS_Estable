@echo off
setlocal
cd /d "%~dp0"

:: Verificar privilegios de Administrador
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo Solicitando permisos de Administrador...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo =======================================================
echo   DESHABILITANDO SERVICIO DE FONDO 'PosBackendService'
echo =======================================================
echo.
echo Esto liberara el puerto 5000 para que start.bat o Visual Studio
echo ejecuten el Backend directamente sin conflictos.
echo.

sc.exe config PosBackendService start= disabled
sc.exe stop PosBackendService
timeout /t 2 /nobreak >nul
taskkill /F /IM Backend.API.exe /T >nul 2>&1
taskkill /F /IM nssm.exe /T >nul 2>&1

echo.
echo =======================================================
echo   SERVICIO DESHABILITADO.
echo   Ahora puedes ejecutar start.bat con control total.
echo =======================================================
echo.
pause
