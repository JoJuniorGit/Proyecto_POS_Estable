# =====================================================================
# backup-postgres.ps1 — Backup automático de la base de datos del POS
# =====================================================================
# 8.27-A4: continuidad operativa para instalaciones en clientes. Crea un
# volcado PostgreSQL en formato custom (pg_dump -Fc) con compresión máxima,
# retención por días y registro de auditoría. NO recibe credenciales por
# línea de comandos: las lee de BackendAPI\secrets.json (el mismo archivo
# protegido con ACL que escribe Configure-PosService.ps1 / setup.iss).
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File backup-postgres.ps1
#   powershell -File backup-postgres.ps1 -BackupDir "D:\Backups" -RetentionDays 21
#
# Tarea programada sugerida (ejecución diaria 03:00 como SYSTEM):
#   schtasks /Create /TN "Sistema POS - Backup PostgreSQL" /SC DAILY /ST 03:00 ^
#     /RU SYSTEM /RL HIGHEST ^
#     /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\Program Files (x86)\Sistema POS Administrador\tools\backup-postgres.ps1\"" /F
# =====================================================================

[CmdletBinding()]
param(
    [string]$ConnectionString = "",
    [string]$SecretsFile = "",
    [string]$BackupDir = "C:\Backups\CommandCenter",
    [int]$RetentionDays = 14,
    [string]$PgDumpPath = ""
)

$ErrorActionPreference = "Stop"

function Write-Log([string]$message, [string]$level = "INFO") {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Write-Host "[$timestamp] [$level] $message"
}

Write-Log "=== Iniciando backup de PostgreSQL ==="

# ---------------------------------------------------------------------
# 1. Resolución de la cadena de conexión (nunca por línea de comandos)
# ---------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    if ([string]::IsNullOrWhiteSpace($SecretsFile)) {
        # Mismo layout relativo en repositorio y en instalación: {root}\BackendAPI\secrets.json
        $SecretsFile = Join-Path (Split-Path -Path $PSScriptRoot -Parent) "BackendAPI\secrets.json"
    }
    if (Test-Path -LiteralPath $SecretsFile) {
        try {
            $secrets = Get-Content -Raw -LiteralPath $SecretsFile | ConvertFrom-Json
            # 8.29-A1: formato ANIDADO (ConnectionStrings.DefaultConnection) con
            # fallback al formato legacy (claves planas "__").
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

# ---------------------------------------------------------------------
# 2. Parseo seguro de la cadena de conexión
# ---------------------------------------------------------------------
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
# 3. Localización de pg_dump (PATH o rutas comunes de instalación)
# ---------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($PgDumpPath)) {
    $cmd = Get-Command pg_dump -ErrorAction SilentlyContinue
    if ($cmd) {
        $PgDumpPath = $cmd.Source
    } else {
        $candidates = @(
            "C:\Program Files\PostgreSQL\18\bin\pg_dump.exe",
            "C:\Program Files\PostgreSQL\17\bin\pg_dump.exe",
            "C:\Program Files\PostgreSQL\16\bin\pg_dump.exe",
            "C:\Program Files\PostgreSQL\15\bin\pg_dump.exe"
        )
        $PgDumpPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not (Test-Path -LiteralPath $PgDumpPath)) {
    throw "pg_dump.exe no encontrado. Proporcione -PgDumpPath."
}

# ---------------------------------------------------------------------
# 4. Ejecución del volcado (formato custom + blobs + compresión máxima)
# ---------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $BackupDir)) {
    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
}
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$dumpFile = Join-Path $BackupDir "$($csDb)_$stamp.dump"

Write-Log "pg_dump: $PgDumpPath"
Write-Log "Destino: $dumpFile"

$previousPgPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $csPass
    $args = @(
        "-h", $csHost,
        "-p", $([string]::IsNullOrWhiteSpace($csPort) ? "5432" : $csPort),
        "-U", $csUser,
        "-d", $csDb,
        "-F", "c",
        "-b",
        "-Z", "9",
        "-f", $dumpFile
    )
    $proc = Start-Process -FilePath $PgDumpPath -ArgumentList $args -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -ne 0) {
        throw "pg_dump falló con código $($proc.ExitCode). No se creó el archivo. La base de datos queda intacta."
    }

    $size = (Get-Item -LiteralPath $dumpFile).Length
    Write-Log ("Backup completado: {0:N1} MB -> {1}" -f ($size / 1MB), $dumpFile) "SUCCESS"
} finally {
    $env:PGPASSWORD = $previousPgPassword
}

# ---------------------------------------------------------------------
# 5. Retención: elimina respaldos vencidos conservando solo .dump
# ---------------------------------------------------------------------
$cutoff = (Get-Date).AddDays(-$RetentionDays)
$expired = @(Get-ChildItem -LiteralPath $BackupDir -Filter "*.dump" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt $cutoff })
foreach ($file in $expired) {
    Remove-Item -LiteralPath $file.FullName -Force
    Write-Log "Retención: eliminado $($file.Name) (más de $RetentionDays días)." "INFO"
}

Write-Log ("=== Backup finalizado. Total respaldos actuales: {0} ===" -f (@(Get-ChildItem -LiteralPath $BackupDir -Filter "*.dump" -ErrorAction SilentlyContinue).Count)) "SUCCESS"
exit 0