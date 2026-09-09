# =====================================================================
# Genera el certificado autofirmado HTTPS para Backend.API
# ---------------------------------------------------------------------
# Salida:  Backend.API\certs\pos-https.pfx por defecto, o el directorio
#          indicado con -CertOutputDir (el instalador lo fija a
#          {app}\BackendAPI\certs, que es donde el servicio lo busca).
# Password: la resuelve en este orden (la contraseña NUNCA debe viajar por
# línea de comandos en el flujo de instalación del servicio):
#   1) secrets.json (clave Kestrel.Certificates.Default.Password) si -SecretsFile
#      apunta a uno, o el Backend.API\secrets.json por defecto cuando existe;
#   2) variable de entorno HTTPS_CERT_PASSWORD (desarrollo);
#   3) -certPassword (SOLO para desarrollo local; el instalador no lo usa);
#   4) si no hay ninguno, se genera una CSPRNG y se persiste en secrets.json
#      (Backend.API\secrets.json) para que Backend.API la lea al arrancar.
#
# El certificado incluye SANs para localhost, el nombre del equipo y las
# IPs IPv4 actuales de la red local, de modo que https://<ip>:5001 sirve
# la app. Los dispositivos de la red verán una advertencia "no confiable"
# la primera vez (certificado autofirmado); pueden continuar, o instalar
# el .cer en el almacén "Entidades de certificación raíz de confianza".
#
# No requiere permisos de administrador (usa el almacén CurrentUser y
# luego elimina el certificado, dejando solo el archivo .pfx).
param(
    [string]$SecretsFile = "",
    [string]$certPassword = "",
    [string]$CertOutputDir = ""
)

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Path $PSScriptRoot -Parent
if (-not [string]::IsNullOrWhiteSpace($CertOutputDir)) {
    $certDir = $CertOutputDir
} else {
    $certDir = Join-Path $rootDir "Backend.API\certs"
}
$certPath = Join-Path $certDir "pos-https.pfx"

function Resolve-SecretPassword {
    param([string]$SecretsFilePath)
    $value = $null
    if (-not [string]::IsNullOrWhiteSpace($SecretsFilePath) -and (Test-Path $SecretsFilePath)) {
        $json = Get-Content -Path $SecretsFilePath -Raw | ConvertFrom-Json
        $value = $json.Kestrel.Certificates.Default.Password
        if ([string]::IsNullOrWhiteSpace($value)) { $value = $json."HTTPS_CERT_PASSWORD" }
    }
    return $value
}

$fallbackSecrets = Join-Path $rootDir "Backend.API\secrets.json"
$resolvedFromSecrets = ""

if (-not [string]::IsNullOrWhiteSpace($SecretsFile)) {
    $resolvedFromSecrets = Resolve-SecretPassword -SecretsFilePath $SecretsFile
}

if ([string]::IsNullOrWhiteSpace($resolvedFromSecrets) -and (Test-Path $fallbackSecrets)) {
    $SecretsFile = $fallbackSecrets
    $resolvedFromSecrets = Resolve-SecretPassword -SecretsFilePath $SecretsFile
}

$certPassword = "$resolvedFromSecrets"

if ([string]::IsNullOrWhiteSpace($certPassword)) {
    $certPassword = $env:HTTPS_CERT_PASSWORD
}

if ([string]::IsNullOrWhiteSpace($certPassword) -or $certPassword -eq "<legacy-default>") {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $certPassword = [System.BitConverter]::ToString($bytes).Replace("-", "")
    [Environment]::SetEnvironmentVariable("HTTPS_CERT_PASSWORD", $certPassword, "Process")
    if (-not [string]::IsNullOrWhiteSpace($SecretsFile)) {
        try {
            $existing = $null
            if (Test-Path $SecretsFile) {
                $existing = Get-Content -Path $SecretsFile -Raw | ConvertFrom-Json
            }
            if ($null -eq $existing) {
                $existing = [PSCustomObject]@{ Kestrel = [PSCustomObject]@{ Certificates = [PSCustomObject]@{ Default = [PSCustomObject]@{ Password = "" } } } }
            }
            if (-not $existing.PSObject.Properties["Kestrel"]) {
                $existing | Add-Member -MemberType NoteProperty -Name Kestrel -Value ([PSCustomObject]@{ Certificates = [PSCustomObject]@{ Default = [PSCustomObject]@{ Password = "" } } })
            }
            $existing.Kestrel.Certificates.Default.Password = $certPassword
            $existing | ConvertTo-Json -Depth 10 | Set-Content -Path $SecretsFile -Encoding UTF8
            Write-Host "Contraseña del certificado persistida en $SecretsFile." -ForegroundColor Cyan
        } catch {
            Write-Host "[AVISO] No se pudo persistir la contraseña del certificado en $SecretsFile." -ForegroundColor Yellow
        }
    }
    Write-Host "Generada contraseña aleatoria para el certificado HTTPS." -ForegroundColor Cyan
}

New-Item -ItemType Directory -Path $certDir -Force | Out-Null

# SANs: localhost, nombre del equipo e IPs IPv4 de la red local
$san = @("localhost", $env:COMPUTERNAME)
try {
    $lanIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object {
            $_.IPAddress -notlike "127.*" -and
            $_.IPAddress -notlike "169.254.*" -and
            $_.IPAddress -notlike "*:*"
        } |
        Select-Object -ExpandProperty IPAddress)
    $san += $lanIps
}
catch {
    Write-Host "[AVISO] No se pudieron enumerar las IPs de red; el certificado incluirá solo localhost y el nombre del equipo." -ForegroundColor Yellow
}

$existing = Get-ChildItem $certPath -ErrorAction SilentlyContinue
if ($existing) {
    Remove-Item $certPath -Force
}

Write-Host "Generando certificado autofirmado con SANs: $($san -join ', ')" -ForegroundColor Cyan

$cert = New-SelfSignedCertificate `
    -Subject "CN=$env:COMPUTERNAME" `
    -DnsName $san `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(10)

$pwd = ConvertTo-SecureString -String $certPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $certPath -Password $pwd -Force | Out-Null

# Limpieza: no dejar el certificado en el almacén, solo el archivo .pfx
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force

Write-Host "Certificado generado: $certPath" -ForegroundColor Green
Write-Host "Válido hasta: $($cert.NotAfter)" -ForegroundColor Green
Write-Host ""
Write-Host "HTTPS estará disponible en: https://localhost:5001 y https://<ip-de-red>:5001" -ForegroundColor Green
Write-Host "Advertencia de seguridad: el certificado es autofirmado; los navegadores mostrarán una advertencia al primer acceso." -ForegroundColor Yellow
