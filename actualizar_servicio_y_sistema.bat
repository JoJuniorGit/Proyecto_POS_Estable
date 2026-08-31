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
echo   ACTUALIZACION DE SERVICIO POS Y BINARIOS DEL BACKEND
echo =======================================================
echo.

echo [1/4] Deteniendo servicio de Windows 'PosBackendService'...
sc.exe stop PosBackendService >nul 2>&1
timeout /t 2 /nobreak >nul
taskkill /F /IM Backend.API.exe /T >nul 2>&1
taskkill /F /IM nssm.exe /T >nul 2>&1

echo [2/4] Copiando nuevos binarios a 'C:\Program Files (x86)\Sistema POS Administrador\BackendAPI\'...
set "TARGET_DIR=C:\Program Files (x86)\Sistema POS Administrador\BackendAPI"
if exist "%TARGET_DIR%" (
    powershell -Command "Copy-Item -Path '.\publish\BackendAPI\*' -Destination '%TARGET_DIR%' -Recurse -Force -Exclude 'appsettings.Production.json','certs','logs'"
    echo Archivos actualizados correctamente en Archivos de Programa.
) else (
    echo Directorio de instalacion no encontrado.
)

echo [3/4] Iniciando servicio 'PosBackendService'...
sc.exe start PosBackendService >nul 2>&1
timeout /t 2 /nobreak >nul

echo [4/4] Verificando estado del servicio...
sc.exe query PosBackendService | findstr "STATE"

echo.
echo =======================================================
echo   ACTUALIZACION COMPLETADA CON EXITO.
echo =======================================================
echo.
echo Presione cualquier tecla para salir...
pause >nul
