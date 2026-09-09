# =====================================================================
# Configure-PosService.ps1 — Configuración robusta e idempotente del servicio POS y Firewall
# =====================================================================
# Función:
#   1. Valida la disponibilidad de nssm.exe con auto-recuperación de rutas.
#   2. Configura reglas de Windows Firewall (TCP 5000 y 5001) para la subred local,
#      eliminando reglas duplicadas o residuales de instalaciones previas.
#   3. Registra o actualiza el servicio Windows 'PosBackendService' con NSSM.
#   4. Aplica la Política de Fusión de Variables de Entorno (AppEnvironmentExtra):
#      - SECRETOS FUERA de AppEnvironmentExtra: cadena de conexión, contraseña semilla,
#        clave JWT y contraseña del certificado viven ÚNICAMENTE en secrets.json
#        (ACL restrictiva), legibles solo por SYSTEM/Administradores/el servicio.
#      - Variables NO sensibles (negocio, usuario semilla): se actualizan con los
#        valores provistos, conservando cualquier variable personalizada.
#      - Se RETIRAN los secretos heredados de instalaciones legacy (EnvExtra -> secrets.json).
#   5. Inicia o reinicia el servicio con un bucle de reintentos resiliente.
# =====================================================================

[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files (x86)\Sistema POS Administrador",
    [string]$ServiceName = "PosBackendService",
    [string]$BackendExe = "$InstallDir\BackendAPI\Backend.API.exe",
    [string]$Nssm = "$InstallDir\BackendAPI\nssm.exe",
    [string]$ConnectionString = "",
    # 8.9-B1: sin valor por defecto - se exige una clave fuerte para el admin semilla (solo primera instalación).
    [string]$AdminSeedPassword = "",
    [string]$AdminSeedUsername = "admin",
    [string]$AdminSeedName = "Administrador Principal",
    [string]$BusinessName = "Mi Negocio POS",
    [string]$JwtSecretKey = "",
    [string]$HttpsCertPassword = "",
    [switch]$RotateJwt = $false,
    # 8.29-A1: backups automáticos por defecto; -SkipScheduledBackup los omite/elimina.
    [switch]$SkipScheduledBackup = $false
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

    # 8U-B1: Virtual Account (SID NT SERVICE\<nombre>) en vez de LocalSystem: solo los
    # permisos necesarios para el servicio, sin privilegios Machine-wide. La Virtual Account
    # necesita ACL de lectura/ejecución sobre el directorio del backend.
    try {
        $virtualAccount = "NT SERVICE\$ServiceName"
        & $Nssm set $ServiceName ObjectName $virtualAccount | Out-Null
        if ($LASTEXITCODE -eq 0) {
            # Conceder Modificar sobre el directorio del backend (lectura/ejecución de binarios
            # + escritura de logs y certs) a la cuenta virtual. Solo esta carpeta, no Machine-wide.
            $dirAcl = Get-Acl -Path $BackendDir
            $vaRule = New-Object System.Security.AccessControl.FileSystemAccessRule($virtualAccount, "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
            $dirAcl.AddAccessRule($vaRule)
            Set-Acl -Path $BackendDir -AclObject $dirAcl
            Log "Servicio registrado con Virtual Account (NT SERVICE\$ServiceName) y ACL sobre $BackendDir."
        } else {
            Log "Aviso: no se pudo fijar Virtual Account; se conserva la cuenta por defecto de NSSM." "WARN"
        }
    } catch {
        Log "Aviso al configurar Virtual Account: $($_.Exception.Message)" "WARN"
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

# --- Política de Fusión (8.29-A1) ---
# SECRETOS FUERA de AppEnvironmentExtra. La cadena de conexión, la contraseña semilla,
# la clave JWT y la contraseña del certificado viven ÚNICAMENTE en secrets.json
# (ACL restrictiva). AppEnvironmentExtra del servicio es legible por cualquier proceso
# con permisos de consulta (WMI/administrador de tareas/registro). Además se RETIRAN
# aquí los secretos heredados de instalaciones legacy para migrarlos a secrets.json.

$legacySecretKeys = @(
    "ConnectionStrings__DefaultConnection",
    "SystemSettings__AdminSeedPassword",
    "JWT_SETTINGS_KEY",
    "HTTPS_CERT_PASSWORD"
)

$secretsRemoved = @()
foreach ($secretKey in $legacySecretKeys) {
    if ($merged.ContainsKey($secretKey)) {
        $secretsRemoved += $secretKey
        $merged.Remove($secretKey) | Out-Null
    }
}
if ($secretsRemoved.Count -gt 0) {
    Log "Secretos retirados de AppEnvironmentExtra (migración a secrets.json): $($secretsRemoved -join ', ')"
}

# Variables NO sensibles del negocio y del usuario semilla (siguen en AppEnvironmentExtra)
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
Log "Aplicando $($merged.Count) variables de entorno NO sensibles a AppEnvironmentExtra..."
& $Nssm set @setArgs
if ($LASTEXITCODE -ne 0) {
    $msg = "Error crítico: 'nssm set $ServiceName AppEnvironmentExtra' falló con código $LASTEXITCODE"
    Log $msg "ERROR"
    throw $msg
}

# ---------------------------------------------------------------------
# 4.1 (8.29-A1) Archivo de secretos protegido (secrets.json con ACL restrictiva):
# ÚNICA fuente de secretos del servicio. Se PRESERVA lo existente (reinstalaciones y
# actualizaciones) y solo se crea/reemplaza cuando falta, está vacío o degrada
# (clave débil o heredada). El backend lo carga con máxima precedencia.
# ---------------------------------------------------------------------
$secretsFile = Join-Path $BackendDir "secrets.json"
$connString = $null
$seedPass = $null
$jwtKey = $null
$certPass = $null

if (Test-Path $secretsFile) {
    try {
        $existingSecrets = Get-Content -Path $secretsFile -Raw | ConvertFrom-Json
        if ($null -ne $existingSecrets) {
            # Convierte formato anidado actual y formato legacy (claves planas __).
            $connString = $existingSecrets.ConnectionStrings.DefaultConnection
            if ([string]::IsNullOrWhiteSpace($connString)) { $connString = $existingSecrets."ConnectionStrings__DefaultConnection" }
            $seedPass = $existingSecrets.SystemSettings.AdminSeedPassword
            if ([string]::IsNullOrWhiteSpace($seedPass)) { $seedPass = $existingSecrets."SystemSettings__AdminSeedPassword" }
            $jwtKey = $existingSecrets.JwtSettings.Key
            if ([string]::IsNullOrWhiteSpace($jwtKey)) { $jwtKey = $existingSecrets."JWT_SETTINGS_KEY" }
            $certPass = $existingSecrets.Kestrel.Certificates.Default.Password
            if ([string]::IsNullOrWhiteSpace($certPass)) { $certPass = $existingSecrets."HTTPS_CERT_PASSWORD" }
        }
    } catch {
        Log "Aviso: no se pudo leer secrets.json existente; se recreará desde parámetros." "WARN"
    }
}

$needsWrite = -not (Test-Path $secretsFile)

# Cadena de conexión: fuera de AppEnvironmentExtra; solo en secrets.json.
if ([string]::IsNullOrWhiteSpace($connString)) {
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw "No se pudo resolver la cadena de conexión: proporcione -ConnectionString o un secrets.json previo válido."
    }
    $connString = $ConnectionString
    $needsWrite = $true
}

# Contraseña del administrador semilla: requerida solo en la primera instalación.
if ([string]::IsNullOrWhiteSpace($seedPass)) {
    if ([string]::IsNullOrWhiteSpace($AdminSeedPassword)) {
        throw "Se requiere -AdminSeedPassword (o un secrets.json previo válido) para la primera instalación."
    }
    $seedPass = $AdminSeedPassword
    $needsWrite = $true
} else {
    Log "Conservando contraseña de administrador semilla preexistente (secrets.json)."
}

# Clave JWT: preservar salvo rotación explícita; respetar -JwtSecretKey si se fuerza
# (instalación manual) y generar CSPRNG 512 bits si falta o es débil.
$isJwtWeak = ([string]::IsNullOrWhiteSpace($jwtKey)) -or
             ($jwtKey.Length -lt 32) -or
             ($jwtKey -eq "ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795")

if (-not $isJwtWeak -and -not $RotateJwt) {
    Log "Conservando clave secreta JWT preexistente (secrets.json, actualización detectada)."
} else {
    $manualJwt = $JwtSecretKey
    $isManualJwtValid = (-not [string]::IsNullOrWhiteSpace($manualJwt)) -and
                        ($manualJwt.Length -ge 32) -and
                        ($manualJwt -ne "ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795")
    if ($isManualJwtValid) {
        $jwtKey = $manualJwt
        Log "Usando clave JWT provista por parámetro (-JwtSecretKey)."
    } else {
        $bytes = New-Object byte[] 64
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $jwtKey = [System.BitConverter]::ToString($bytes).Replace("-", "").ToLower()
        Log "Generada nueva clave criptográfica aleatoria de 512 bits para JWT."
    }
    $needsWrite = $true
}

# Contraseña del certificado HTTPS: preservar o generar CSPRNG.
$isCertWeak = ([string]::IsNullOrWhiteSpace($certPass)) -or
              ($certPass.Length -lt 16) -or
              ($certPass -eq "<legacy-default>")

if (-not $isCertWeak) {
    Log "Conservando contraseña de certificado HTTPS preexistente (secrets.json)."
} else {
    if (-not [string]::IsNullOrWhiteSpace($HttpsCertPassword)) {
        $certPass = $HttpsCertPassword
    } else {
        $bytes = New-Object byte[] 16
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $certPass = [System.BitConverter]::ToString($bytes).Replace("-", "")
        Log "Generada nueva contraseña criptográfica aleatoria para el certificado HTTPS."
    }
    $needsWrite = $true
}

# Escritura en formato ANIDADO compatible con el sistema de configuración .NET:
# ConnectionStrings:DefaultConnection, SystemSettings:AdminSeedPassword, JwtSettings:Key
# y Kestrel:Certificates:Default:Password. Las claves planas __ solo tienen semántica
# para variables de entorno, NO para archivos JSON; no se escriben más.
if ($needsWrite) {
    try {
        $secretMap = [ordered]@{
            ConnectionStrings = @{ DefaultConnection = $connString }
            SystemSettings    = @{ AdminSeedPassword = $seedPass }
            JwtSettings       = @{ Key = $jwtKey }
            Kestrel           = @{ Certificates = @{ Default = @{ Password = $certPass } } }
        }
        $secretJson = $secretMap | ConvertTo-Json -Depth 10
        [System.IO.File]::WriteAllText($secretsFile, $secretJson, [System.Text.Encoding]::UTF8)
        Log "Secretos escritos en $secretsFile (formato anidado, ACL restrictiva)."
    } catch {
        throw "No se pudo escribir $secretsFile; los secretos quedan solo en el archivo protegido: $($_.Exception.Message)"
    }
}

# ACL: heredación bloqueada; SYSTEM/Administradores (FullControl) + la cuenta del
# servicio (8U-B1: Virtual Account) con lectura, para que el backend pueda leerlos.
try {
    $acl = Get-Acl -Path $secretsFile
    $acl.SetAccessRuleProtection($true, $false)
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $adminSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $ruleSystem = New-Object System.Security.AccessControl.FileSystemAccessRule($systemSid, "FullControl", "None", "None", "Allow")
    $ruleAdmin = New-Object System.Security.AccessControl.FileSystemAccessRule($adminSid, "FullControl", "None", "None", "Allow")
    $acl.ResetAccessRule($ruleSystem)
    $acl.AddAccessRule($ruleAdmin)
    try {
        $vaSid = (New-Object System.Security.Principal.NTAccount("NT SERVICE\$ServiceName")).Translate([System.Security.Principal.SecurityIdentifier])
        $ruleService = New-Object System.Security.AccessControl.FileSystemAccessRule($vaSid, "Read", "None", "None", "Allow")
        $acl.AddAccessRule($ruleService)
    } catch {
        # La Virtual Account aún no existe (servicio viejo o fallo previo); se ignora.
    }
    Set-Acl -Path $secretsFile -AclObject $acl
    Log "ACL aplicada sobre $secretsFile (SYSTEM, Administradores y cuenta del servicio)."
} catch {
    Log "Aviso al aplicar ACL sobre ${secretsFile}: $($_.Exception.Message)" "WARN"
}

# 4.1b Protección ACL del Registro (Restringir parámetros del servicio a SYSTEM y Administradores)
try {
    $serviceRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName\Parameters"
    if (Test-Path $serviceRegPath) {
        $acl = Get-Acl -Path $serviceRegPath
        $acl.SetAccessRuleProtection($true, $false) # Bloquear herencia
        $systemSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
        $adminSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        
        $ruleSystem = New-Object System.Security.AccessControl.RegistryAccessRule($systemSid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
        $ruleAdmin = New-Object System.Security.AccessControl.RegistryAccessRule($adminSid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
        
        $acl.ResetAccessRule($ruleSystem)
        $acl.AddAccessRule($ruleAdmin)
        Set-Acl -Path $serviceRegPath -AclObject $acl
        Log "Permisos ACL restringidos en $serviceRegPath a SYSTEM y Administradores."
    }
} catch {
    Log "Aviso al aplicar ACL sobre el registro de NSSM: $($_.Exception.Message)" "WARN"
}

# ---------------------------------------------------------------------
# 4.2 (8.27-A02) Certificado HTTPS del PUESTO: se genera en la máquina destino con
# CN/SAN del equipo. Un pfx stale (del puesto de build) o una contraseña nueva se
# detectan y regeneran aquí, de modo que HTTPS_CERT_PASSWORD y el pfx siempre casan
# y el backend nunca intente arrancar en Producción sin certificado real.
# ---------------------------------------------------------------------
$certDir = "$BackendDir\certs"
$pfxPath = "$certDir\pos-https.pfx"
$certTools = Join-Path $InstallDir "tools\create-https-cert.ps1"
if (-not (Test-Path $certTools)) {
    $certTools = Join-Path $PSScriptRoot "..\..\scripts\create-https-cert.ps1"
}
if (-not (Test-Path $certTools)) {
    Log "AVISO: scripts/create-https-cert.ps1 no encontrado; el backend abortará sin HTTPS en Producción." "WARN"
} else {
    $needsRegen = $false
    if (-not (Test-Path $pfxPath)) {
        $needsRegen = $true
    } else {
        try {
            $probeCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $certPass)
            if ($probeCert.Subject -notmatch [regex]::Escape("CN=$env:COMPUTERNAME")) {
                $needsRegen = $true
            }
        } catch {
            # El pfx no se abre con la contraseña actual (cambió o es un artefacto stale).
            $needsRegen = $true
        }
    }
    if ($needsRegen) {
        Log "Generando certificado HTTPS con SANs del puesto ($env:COMPUTERNAME)..."
        # La contraseña del pfx la lee create-https-cert.ps1 desde secrets.json
        # (nunca viaja por línea de comandos del proceso PowerShell).
        $certArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$certTools`" -SecretsFile `"$secretsFile`""
        $certProc = Start-Process -FilePath "powershell.exe" -ArgumentList $certArgs -Wait -PassThru -NoNewWindow
        if ($certProc.ExitCode -ne 0) {
            Log "ADVERTENCIA: create-https-cert.ps1 retornó código $($certProc.ExitCode); el backend abortará sin HTTPS en Producción." "WARN"
        } else {
            Log "Certificado HTTPS del puesto listo: $pfxPath"
        }
    } else {
        Log "Certificado HTTPS del puesto ya existe y es válido con la contraseña actual."
    }
}

# ---------------------------------------------------------------------
# 4.3 (8.29-A6) Backup diario: agenda una tarea programada que ejecuta
# backup-postgres.ps1 todos los días a las 03:00. ACTIVO POR DEFECTO en toda
# instalación; -SkipScheduledBackup lo omite o elimina si ya existía.
# ---------------------------------------------------------------------
$taskName = "Sistema POS - Backup PostgreSQL"
$backupScript = Join-Path $InstallDir "tools\backup-postgres.ps1"

if ($SkipScheduledBackup) {
    if (Test-Path $backupScript) {
        $existingTask = Get-ScheduledTask -TaskName $taskName -TaskPath "\" -ErrorAction SilentlyContinue
        if ($null -ne $existingTask) {
            Unregister-ScheduledTask -TaskName $taskName -TaskPath "\" -Confirm:$false | Out-Null
            Log "Backup desactivado: tarea programada '$taskName' eliminada." "WARN"
        }
    }
} else {
    if (Test-Path $backupScript) {
        try {
            $taskTr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$backupScript`""
            & schtasks.exe /Create /TN $taskName /SC DAILY /ST 03:00 /RU SYSTEM /RL HIGHEST /TR $taskTr /F | Out-Null
            Log "Tarea programada asegurada: $taskName (diaria 03:00)." "SUCCESS"
        } catch {
            Log "Aviso al crear la tarea de backup: $($_.Exception.Message)" "WARN"
        }
    } else {
        Log "AVISO: backup-postgres.ps1 no encontrado en tools; no se agenda backup." "WARN"
    }
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
