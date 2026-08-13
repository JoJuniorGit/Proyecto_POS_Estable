@echo off
set PORT=5000
echo ==========================================
echo   Cleaning up previous instances...
echo ==========================================

echo Stopping Desktop Client...
taskkill /F /IM Desktop.Client.exe /T >nul 2>&1

echo Stopping Backend API...
taskkill /F /IM Backend.API.exe /T >nul 2>&1

echo Releasing Port %PORT% (Backend)...
FOR /F "tokens=5" %%T IN ('netstat -a -n -o ^| findstr :%PORT%') DO (
    taskkill /F /PID %%T /T >nul 2>&1
)

echo Releasing Port 5173 (Web Frontend)...
FOR /F "tokens=5" %%T IN ('netstat -a -n -o ^| findstr :5173') DO (
    taskkill /F /PID %%T /T >nul 2>&1
)

echo.
echo ==========================================
echo   Starting POS System
echo ==========================================

echo [1/3] Launching Backend API...
start "Backend API" cmd /k "cd /d %~dp0 && dotnet run --project Backend.API\Backend.API.csproj --urls http://*:%PORT%"

echo Waiting for API (polling localhost:%PORT%)...
:wait
powershell -Command "try { $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', %PORT%); if ($c.Connected) { $c.Close(); exit 0 } } catch { exit 1 }"
if %ERRORLEVEL% NEQ 0 (
    powershell -Command "try { $c = New-Object System.Net.Sockets.TcpClient('::1', %PORT%); if ($c.Connected) { $c.Close(); exit 0 } } catch { exit 1 }"
)
if %ERRORLEVEL% NEQ 0 (
    timeout /t 1 /nobreak >nul
    goto wait
)
echo API is ready.

echo [2/3] Launching Desktop Client...
start "Desktop Client" cmd /k "cd /d %~dp0 && dotnet run --project Desktop.Client\Desktop.Client.csproj"

echo Waiting 5 seconds for Desktop Client to compile...
timeout /t 5 /nobreak >nul

echo [3/3] Launching Web Frontend...
start "Web Frontend" cmd /k "cd /d %~dp0\Web.Frontend && npm run dev"

echo.
echo ==========================================
echo   System Started Successfully.
echo   - Backend API:     http://localhost:%PORT%
echo   - Web Frontend:    http://localhost:5173
echo   - Desktop Client:  Running
echo ==========================================
timeout /t 5
