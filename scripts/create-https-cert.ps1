# =====================================================================
# Genera el certificado autofirmado HTTPS para Backend.API
# ---------------------------------------------------------------------
# Salida:  Backend.API\certs\pos-https.pfx
# Password: PosHttpsDev2026!  (debe coincidir con HttpsCertPassword en Program.cs)
#
# El certificado incluye SANs para localhost, el nombre del equipo y las
# IPs IPv4 actuales de la red local, de modo que https://<ip>:5001 sirve
# la app. Los dispositivos de la red verán una advertencia "no confiable"
# la primera vez (certificado autofirmado); pueden continuar, o instalar
# el .cer en el almacén "Entidades de certificación raíz de confianza".
#
# No requiere permisos de administrador (usa el almacén CurrentUser y
# luego elimina el certificado, dejando solo el archivo .pfx).
# =====================================================================

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Path $PSScriptRoot -Parent
$certDir = Join-Path $rootDir "Backend.API\certs"
$certPath = Join-Path $certDir "pos-https.pfx"
$certPassword = "PosHttpsDev2026!"

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
