# =====================================================================
# cleanup-stress-data.ps1 — Ejecuta la limpieza de datos sintéticos 8.108
# =====================================================================
# 8.108: elimina de la base de staging (Sales + Inventory comparten cadena
# DefaultConnection) las ventas generadas por stress-test.py y, opcionalmente,
# los productos SKU-TEST-* y el usuario cajero de prueba. NO recibe
# credenciales por línea de comandos: las lee de BackendAPI\secrets.json
# (mismo archivo protegido con ACL que usa backup-postgres.ps1).
#
# Modo conservador: por defecto solo elimina ventas y sus dependencias
# (SaleItems, SalePayments, CashTransactions, OutboxMessages, IdempotentRequests,
# StockMovements y StockMovements_Archive). Borrar productos o usuario exige
# -Confirm YES.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File cleanup-stress-data.ps1
#   powershell -File cleanup-stress-data.ps1 -DeleteProducts -DeleteUser -Confirm YES
#
# IMPORTANTE: ejecute scripts/backup-postgres.ps1 antes para tener volcado.
# Borrar ventas NO reinicia la secuencia de facturación (GenerateNextInvoiceNumberAsync).
# =====================================================================

[CmdletBinding()]
param(
    [string]$ConnectionString = "",
    [string]$SecretsFile = "",
    [string]$SqlDir = "",
    [string]$PgPath = "",
    [string]$CashierCedula = "",
    [string]$SkuPrefix = "",
    [switch]$DeleteProducts,
    [switch]$DeleteUser,
    [string]$Confirm = ""
)

$ErrorActionPreference = "Stop"

function Write-Log([string]$message, [string]$level = "INFO") {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Write-Host "[$timestamp] [$level] $message"
}

$wantDestructive = $DeleteProducts -or $DeleteUser
if ($wantDestructive -and $Confirm -ne "YES") {
    throw "Borrar productos o usuario requiere -Confirm YES. Verifique el staging y haga backup antes."
}

# ---------------------------------------------------------------------
# 1. Resolución de la cadena de conexión (nunca por línea de comandos)
# ---------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    if ([string]::IsNullOrWhiteSpace($SecretsFile)) {
        $SecretsFile = Join-Path (Split-Path -Path $PSScriptRoot -Parent) "BackendAPI\secrets.json"
    }
    if (Test-Path -LiteralPath $SecretsFile) {
        try {
            $secrets = Get-Content -Raw -LiteralPath $SecretsFile | ConvertFrom-Json
            $ConnectionString = $secrets.ConnectionStrings.DefaultConnection
            if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
                $ConnectionString = $secrets."ConnectionStrings__DefaultConnection"
            }
            Write-Log "Cadena de conexión leída de $SecretsFile (sin exponer credenciales)."
        } catch {
            throw "No se pudo leer ${SecretsFile}: $($_.Exception.Message)"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw "No se pudo resolver la cadena de conexión: use -ConnectionString o garantice la existencia de BackendAPI\secrets.json."
}

function Get-SecretPart([string]$connString, [string]$name) {
    foreach ($part in $connString -split ';') {
        if ($part -match "^$([regex]::Escape($name))\s*=\s*(.*)$") {
            return $Matches[1].Trim()
        }
    }
    return ""
}

$csHost = Get-SecretPart $ConnectionString "Host"
$csPort = Get-SecretPart $ConnectionString "Port"
$csDb   = Get-SecretPart $ConnectionString "Database"
$csUser = Get-SecretPart $ConnectionString "Username"
$csPass = Get-SecretPart $ConnectionString "Password"

if ([string]::IsNullOrWhiteSpace($csHost) -or [string]::IsNullOrWhiteSpace($csDb)) {
    throw "La cadena de conexión no contiene Host o Database."
}

# ---------------------------------------------------------------------
# 2. Localización de psql (PATH o rutas comunes de instalación)
# ---------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($PgPath)) {
    $cmd = Get-Command psql -ErrorAction SilentlyContinue
    if ($cmd) {
        $PgPath = $cmd.Source
    } else {
        $candidates = @(
            "C:\Program Files\PostgreSQL\18\bin\psql.exe",
            "C:\Program Files\PostgreSQL\17\bin\psql.exe",
            "C:\Program Files\PostgreSQL\16\bin\psql.exe",
            "C:\Program Files\PostgreSQL\15\bin\psql.exe"
        )
        $PgPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not (Test-Path -LiteralPath $PgPath)) {
    throw "psql.exe no encontrado. Proporcione -PgPath."
}

# ---------------------------------------------------------------------
# 3. Invocación de psql con los scripts de limpieza (ventas + opcionales)
# ---------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($SqlDir)) {
    $SqlDir = $PSScriptRoot
}

function Invoke-PsqlScript([string]$scriptName, [string[]]$extraVariables) {
    $sqlFile = Join-Path $SqlDir $scriptName
    if (-not (Test-Path -LiteralPath $sqlFile)) {
        throw "No se encontró el script SQL: $sqlFile"
    }
    $psqlArgs = @(
        "-h", $csHost,
        "-p", $([string]::IsNullOrWhiteSpace($csPort) ? "5432" : $csPort),
        "-U", $csUser,
        "-d", $csDb,
        "-v", "ON_ERROR_STOP=1"
    )
    $psqlArgs += $extraVariables
    $psqlArgs += @("-f", $sqlFile)
    Write-Log "Script SQL: $sqlFile"
    & $PgPath @psqlArgs
    if ($LASTEXITCODE -ne 0) {
        throw "psql falló con código $LASTEXITCODE. Los cambios fueron revertidos por ON_ERROR_STOP."
    }
}

$previousPgPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $csPass
    Write-Log "psql: $PgPath"
    Write-Log "Destino DB: $csDb en $csHost`:$csPort"

    $coreVariables = @()
    if (-not [string]::IsNullOrWhiteSpace($CashierCedula)) {
        $coreVariables += @("-v", "cashier_cedula=$CashierCedula")
    }
    Invoke-PsqlScript "cleanup-stress-data.sql" $coreVariables

    if ($DeleteProducts) {
        $productsVariables = @()
        if (-not [string]::IsNullOrWhiteSpace($SkuPrefix)) {
            $productsVariables += @("-v", "sku_prefix=$SkuPrefix")
        }
        Invoke-PsqlScript "cleanup-stress-data-products.sql" $productsVariables
    }

    if ($DeleteUser) {
        $userVariables = @()
        if (-not [string]::IsNullOrWhiteSpace($CashierCedula)) {
            $userVariables += @("-v", "cashier_cedula=$CashierCedula")
        }
        Invoke-PsqlScript "cleanup-stress-data-user.sql" $userVariables
    }
} finally {
    $env:PGPASSWORD = $previousPgPassword
}

Write-Log ("=== Limpieza de datos de estrés completada ({0}) ===" -f $csDb) "SUCCESS"
exit 0