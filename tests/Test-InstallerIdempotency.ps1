# =====================================================================
# Test-InstallerIdempotency.ps1 - Prueba de regresion e idempotencia del instalador
# =====================================================================
# Verifica:
#   1. Analisis sintactico y de parametros de Configure-PosService.ps1.
#   2. Verificacion estructural de installer\setup.iss.
#   3. Ejecucion idempotente en dos pasadas consecutivas (si se ejecuta elevado):
#      - Cero reglas duplicadas de firewall (exactamente 2 reglas: TCP 5000 y 5001).
#      - Persistencia y preservacion de variables en AppEnvironmentExtra de NSSM.
# =====================================================================

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Path $PSScriptRoot -Parent
$scriptPath = Join-Path $rootDir "installer\Configure-PosService.ps1"
$setupPath = Join-Path $rootDir "installer\setup.iss"

Write-Host "`n=== [TEST 1] Validacion sintactica de Configure-PosService.ps1 ===" -ForegroundColor Cyan
if (-not (Test-Path $scriptPath)) {
    throw "No se encontro el archivo: $scriptPath"
}

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -gt 0) {
    throw "Errores de sintaxis encontrados en Configure-PosService.ps1:`n$($errors | Out-String)"
}
Write-Host "OK: Configure-PosService.ps1 tiene sintaxis valida de PowerShell sin errores." -ForegroundColor Green


Write-Host "`n=== [TEST 2] Verificacion de estructura en installer\setup.iss ===" -ForegroundColor Cyan
if (-not (Test-Path $setupPath)) {
    throw "No se encontro el archivo: $setupPath"
}

$setupContent = Get-Content -Path $setupPath -Raw -Encoding utf8
$requiredSnippets = @(
    "Source: ""Configure-PosService.ps1""; DestDir: ""{app}\tools""",
    "procedure ConfigureServiceWithPowerShell;",
    "ConfigureServiceWithPowerShell;"
)

foreach ($snippet in $requiredSnippets) {
    if ($setupContent -notmatch [regex]::Escape($snippet)) {
        throw "El archivo setup.iss no contiene el fragmento requerido: '$snippet'"
    }
}
Write-Host "OK: setup.iss incluye Configure-PosService.ps1 y el llamado a ConfigureServiceWithPowerShell." -ForegroundColor Green


Write-Host "`n=== [TEST 3] Validacion de ejecucion e Idempotencia ===" -ForegroundColor Cyan
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "[AVISO] La prueba de ejecucion de firewall y servicio requiere elevacion de Administrador." -ForegroundColor Yellow
    Write-Host "Para ejecutar la prueba completa de idempotencia en vivo, inicie PowerShell como Administrador." -ForegroundColor Yellow
    Write-Host "Sintaxis y estructura del instalador verificadas con exito." -ForegroundColor Green
    exit 0
}

Write-Host "Ejecutando PASADA 1 de Configure-PosService.ps1..." -ForegroundColor White
& $scriptPath -AdminSeedPassword "AdminInitialPass123!"

$rulesPass1 = @(Get-NetFirewallRule -DisplayName "Sistema POS - Backend API*" -ErrorAction SilentlyContinue)
Write-Host "Reglas de Firewall tras Pasada 1: $($rulesPass1.Count)"
if ($rulesPass1.Count -ne 2) {
    throw "ERROR: Se esperaban exactamente 2 reglas de firewall, se encontraron $($rulesPass1.Count)"
}

Write-Host "`nEjecutando PASADA 2 de Configure-PosService.ps1 (Intentando cambiar contrasena semilla a 'ShouldNotOverwrite')..." -ForegroundColor White
& $scriptPath -AdminSeedPassword "ShouldNotOverwrite"

$rulesPass2 = @(Get-NetFirewallRule -DisplayName "Sistema POS - Backend API*" -ErrorAction SilentlyContinue)
Write-Host "Reglas de Firewall tras Pasada 2: $($rulesPass2.Count)"
if ($rulesPass2.Count -ne 2) {
    throw "ERROR DE IDEMPOTENCIA: Las reglas de firewall se duplicaron tras la segunda pasada ($($rulesPass2.Count) encontradas)."
}

# Verificar preservacion de variable sensible
$nssmExe = "C:\Program Files (x86)\Sistema POS Administrador\BackendAPI\nssm.exe"
if (Test-Path $nssmExe) {
    $envRaw = & $nssmExe get PosBackendService AppEnvironmentExtra 2>$null
    if ($envRaw -match 'SystemSettings__AdminSeedPassword=([^\r\n]+)') {
        $actualPassword = $matches[1]
        Write-Host "Contrasena registrada en NSSM: $actualPassword"
        if ($actualPassword -ne "AdminInitialPass123!") {
            throw "ERROR DE POLITICA DE FUSION: La contrasena preexistente fue sobrescrita. Valor actual: $actualPassword"
        }
        Write-Host "OK: La politica de fusion preservo correctamente la contrasena sensible preexistente." -ForegroundColor Green
    }
}

Write-Host "`n=== TODAS LAS PRUEBAS DE IDEMPOTENCIA Y REGRESION PASARON EXITOSAMENTE ===" -ForegroundColor Green
