# =====================================================================
# Configure-PosService.ps1 — Configuración robusta e idempotente del servicio POS y Firewall
# =====================================================================
# Función:
#   1. Valida la disponibilidad de nssm.exe con auto-recuperación de rutas.
#   2. Configura reglas de Windows Firewall (TCP 5000 y 5001) para la subred local,
#      eliminando reglas duplicadas o residuales de instalaciones previas.
#   3. Registra o actualiza el servicio Windows 'PosBackendService' con NSSM.
#   4. Aplica la Política de Fusión de Variables de Entorno (AppEnvironmentExtra):
#      - Variables Sensibles (SystemSettings__AdminSeedPassword): NO se sobrescriben si ya existen.
#      - Variables Criptográficas (JWT_SETTINGS_KEY): Se preservan a menos que se use -RotateJwt.
#      - Variables de Conexión y Negocio: Se actualizan con los valores provistos,
#        conservando cualquier variable personalizada agregada manualmente.
#      - Variables Nuevas: Se agregan automáticamente.
#   5. Inicia o reinicia el servicio con un bucle de reintentos resiliente.
# =====================================================================

[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files (x86)\Sistema POS Administrador",
    [string]$ServiceName = "PosBackendService",
    [string]$BackendExe = "$InstallDir\BackendAPI\Backend.API.exe",
    [string]$Nssm = "$InstallDir\BackendAPI\nssm.exe",
    [string]$ConnectionString = "Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=postgres",
    [string]$AdminSeedPassword = "Admin123!",
    [string]$AdminSeedUsername = "admin",
    [string]$AdminSeedName = "Administrador Principal",
    [string]$BusinessName = "Mi Negocio POS",
    [string]$JwtSecretKey = "",
    [switch]$RotateJwt = $false
)

$ErrorActionPreference = 'Stop'

# Inicialización de directorio de logs
$BackendDir = "$InstallDir\BackendAPI"
$LogDir = "$BackendDir\logs"
$LogFile = "$LogDir\installer.log"

if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
}

function Log([string]$message, [string]$level = "INFO") {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $formatted = "[$timestamp] [$level] $message"
    Write-Host $formatted
    Add-Content -Path $LogFile -Value $formatted -Encoding utf8
}

Log "=== Iniciando configuración del servicio POS y Firewall ==="

# Generar clave JWT aleatoria segura si no fue provista o si coincide con la predeterminada débil
if ([string]::IsNullOrWhiteSpace($JwtSecretKey) -or $JwtSecretKey -eq "ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795") {
    $bytes = New-Object byte[] 64
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $JwtSecretKey = [System.BitConverter]::ToString($bytes).Replace("-", "").ToLower()
    Log "Generada nueva clave criptográfica aleatoria de 512 bits para JWT."
}

# ---------------------------------------------------------------------
# 1. Validación y Auto-Recuperación de NSSM
# ---------------------------------------------------------------------
if (-not (Test-Path $Nssm)) {
    Log "NSSM no encontrado en la ruta destino '$Nssm'. Buscando rutas alternativas..." "WARN"
    
    $candidatePaths = @(
        Join-Path $PSScriptRoot "nssm.exe",
        Join-Path $PSScriptRoot "..\installer\nssm.exe",
        Join-Path $PSScriptRoot "..\..\installer\nssm.exe",
        (Get-Command nssm -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    )

    $foundCandidate = $null
    foreach ($cand in $candidatePaths) {
        if ($cand -and (Test-Path $cand)) {
            $foundCandidate = $cand
            break
        }
    }

    if ($foundCandidate) {
        Log "NSSM localizado en: $foundCandidate. Copiando a: $Nssm" "INFO"
        if (-not (Test-Path $BackendDir)) {
            New-Item -ItemType Directory -Force -Path $BackendDir | Out-Null
        }
        Copy-Item -Path $foundCandidate -Destination $Nssm -Force
    } else {
        $msg = "CRÍTICO: No se encontró nssm.exe en ninguna ubicación conocida. Imposible configurar el servicio."
        Log $msg "ERROR"
        throw $msg
    }
}

Log "Binario NSSM verificado en: $Nssm"

# ---------------------------------------------------------------------
# 2. Configuración de Windows Firewall (Idempotente)
# ---------------------------------------------------------------------
Log "Firewall: Limpiando reglas residuales previas que coincidan con 'Sistema POS - Backend API*'..."
try {
    Get-NetFirewallRule -DisplayName 'Sistema POS - Backend API*' -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
} catch {
    Log "Aviso al limpiar reglas de firewall previas: $_" "WARN"
}

# Puertos a exponer: 5000 (HTTP API/Web POS) y 5001 (HTTPS para cámara/escáner LAN)
$ports = @(5000, 5001)
foreach ($port in $ports) {
    $ruleName = "Sistema POS - Backend API (TCP $port)"
    Log "Firewall: Creando regla entrante '$ruleName' (TCP $port, subred local)..."
    $netshCmd = "advfirewall firewall add rule name=""$ruleName"" dir=in action=allow protocol=TCP localport=$port remoteip=localsubnet profile=any"
    $proc = Start-Process -FilePath "netsh.exe" -ArgumentList $netshCmd -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -ne 0) {
        Log "Advertencia: netsh retornó código $($proc.ExitCode) al crear regla para el puerto $port" "WARN"
    }
}

# ---------------------------------------------------------------------
# 3. Instalación o Actualización del Servicio con NSSM
# ---------------------------------------------------------------------
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $existingService) {
    Log "Servicio '$ServiceName' no existe. Registrando con NSSM..."
    & $Nssm install $ServiceName $BackendExe
    if ($LASTEXITCODE -ne 0) {
        $msg = "Error crítico: 'nssm install $ServiceName' falló con código de salida $LASTEXITCODE"
        Log $msg "ERROR"
        throw $msg
    }
} else {
    Log "Servicio '$ServiceName' detectado. Actualizando ruta de ejecutable..."
    & $Nssm set $ServiceName Application $BackendExe | Out-Null
}

& $Nssm set $ServiceName AppDirectory $BackendDir | Out-Null
& $Nssm set $ServiceName Start SERVICE_AUTO_START | Out-Null

# Rehabilitar en SCM por si el servicio fue deshabilitado en Windows anteriormente
Start-Process -FilePath "sc.exe" -ArgumentList "config $ServiceName start= auto" -Wait -NoNewWindow | Out-Null

# ---------------------------------------------------------------------
# 4. Fusión de Variables de Entorno (AppEnvironmentExtra)
# ---------------------------------------------------------------------
Log "Consultando variables de entorno actuales del servicio..."
$currentEnvRaw = (& $Nssm get $ServiceName AppEnvironmentExtra 2>$null)
$currentLines = $currentEnvRaw -split "`r?`n" | Where-Object { $_ -match '^[^=]+=' }

$merged = @{}
foreach ($line in $currentLines) {
    $parts = $line -split '=', 2
    if ($parts.Count -eq 2) {
        $merged[$parts[0]] = $parts[1]
    }
}

Log "Variables preexistentes en el servicio: $($merged.Count)"

# --- Política de Fusión ---

# A) Cadena de Conexión a Base de Datos
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $merged["ConnectionStrings__DefaultConnection"] = $ConnectionString
}

# B) Contraseña de Administrador Semilla (Sensible: NO sobrescribir si ya existe)
if ($merged.ContainsKey("SystemSettings__AdminSeedPassword") -and -not [string]::IsNullOrWhiteSpace($merged["SystemSettings__AdminSeedPassword"])) {
    Log "Conservando contraseña de administrador semilla preexistente en el servicio."
} else {
    if (-not [string]::IsNullOrWhiteSpace($AdminSeedPassword)) {
        $merged["SystemSettings__AdminSeedPassword"] = $AdminSeedPassword
        Log "Asignando contraseña de administrador semilla inicial."
    }
}

# C) Clave Secreta JWT (Criptográfica: Preservar a menos que se solicite rotación)
if ($merged.ContainsKey("JWT_SETTINGS_KEY") -and -not [string]::IsNullOrWhiteSpace($merged["JWT_SETTINGS_KEY"]) -and (-not $RotateJwt)) {
    Log "Conservando clave secreta JWT preexistente en el servicio."
} else {
    if (-not [string]::IsNullOrWhiteSpace($JwtSecretKey)) {
        $merged["JWT_SETTINGS_KEY"] = $JwtSecretKey
        Log "Asignando clave secreta JWT."
    }
}

# D) Parámetros de Negocio y Usuario Semilla
if (-not [string]::IsNullOrWhiteSpace($AdminSeedUsername)) {
    $merged["SystemSettings__AdminSeedUsername"] = $AdminSeedUsername
}
if (-not [string]::IsNullOrWhiteSpace($AdminSeedName)) {
    $merged["SystemSettings__AdminSeedName"] = $AdminSeedName
}
if (-not [string]::IsNullOrWhiteSpace($BusinessName)) {
    $merged["SystemSettings__BusinessName"] = $BusinessName
}

# Aplicar las variables fusionadas a NSSM vía splatting
$setArgs = @($ServiceName, "AppEnvironmentExtra") + ($merged.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
Log "Aplicando $($merged.Count) variables de entorno a AppEnvironmentExtra..."
& $Nssm set @setArgs
if ($LASTEXITCODE -ne 0) {
    $msg = "Error crítico: 'nssm set $ServiceName AppEnvironmentExtra' falló con código $LASTEXITCODE"
    Log $msg "ERROR"
    throw $msg
}

# ---------------------------------------------------------------------
# 5. Arranque / Reinicio del Servicio con Reintentos Resilientes
# ---------------------------------------------------------------------
$serviceState = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status

if ($serviceState -eq 'Running') {
    Log "Reiniciando el servicio '$ServiceName'..."
    & $Nssm restart $ServiceName
} else {
    Log "Iniciando el servicio '$ServiceName'..."
    & $Nssm start $ServiceName
}

# Bucle de verificación y reintentos (hasta 3 intentos con 5 segundos de espera)
$maxAttempts = 3
$isStarted = $false

for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    Start-Sleep -Seconds 5
    $currentStatus = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status
    Log "Verificación de estado (intento $attempt de $maxAttempts): $currentStatus"
    
    if ($currentStatus -eq 'Running') {
        $isStarted = $true
        break
    } elseif ($attempt -lt $maxAttempts) {
        Log "El servicio aún no está en estado 'Running'. Reintentando inicio..." "WARN"
        & $Nssm start $ServiceName
    }
}

if ($isStarted) {
    Log "=== El servicio '$ServiceName' se encuentra ACTIVO y EJECUTÁNDOSE correctamente ===" "SUCCESS"
} else {
    Log "ADVERTENCIA: El servicio '$ServiceName' no alcanzó el estado 'Running'. Revise: $LogDir\crash.log y $LogDir\db-errors.log" "WARN"
}

Log "=== Configuración finalizada con éxito ==="
exit 0
