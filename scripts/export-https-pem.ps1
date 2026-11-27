#requires -Version 7.0
<#
.SYNOPSIS
    Exporta el PFX HTTPS del backend a PEM (certificado + clave) para Caddy/Nginx.

.DESCRIPTION
    Caddy y Nginx en Windows no consumen PFX directamente. Este script usa el mismo
    certificado que genera create-https-cert.ps1 y escribe:
      deploy/caddy/certs/pos-https.crt
      deploy/caddy/certs/pos-https.key
    La clave privada intenta restringirse con icacls; si el proceso no es administrador,
    se conserva el archivo y se imprime un aviso.
#>
[CmdletBinding()]
param(
    [string]$PfxPath = "",
    [string]$Password = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    $PfxPath = Join-Path $root 'Backend.API/certs/pos-https.pfx'
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root 'deploy/caddy/certs'
}
if ([string]::IsNullOrWhiteSpace($Password)) {
    $Password = $env:HTTPS_CERT_PASSWORD
}
if (-not (Test-Path -LiteralPath $PfxPath)) {
    throw "No se encontró el PFX: $PfxPath. Ejecute scripts/create-https-cert.ps1 primero."
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $PfxPath,
    $Password,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
if ($null -eq $rsa) {
    throw "El PFX no contiene una clave RSA privada exportable: $PfxPath"
}

function ConvertTo-Pem {
    param([string]$Label, [byte[]]$Bytes)
    $base64 = [Convert]::ToBase64String($Bytes)
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine("-----BEGIN $Label-----")
    for ($i = 0; $i -lt $base64.Length; $i += 64) {
        [void]$builder.AppendLine($base64.Substring($i, [Math]::Min(64, $base64.Length - $i)))
    }
    [void]$builder.AppendLine("-----END $Label-----")
    return $builder.ToString()
}

$crtPath = Join-Path $OutputDir 'pos-https.crt'
$keyPath = Join-Path $OutputDir 'pos-https.key'
$certPem = ConvertTo-Pem -Label 'CERTIFICATE' -Bytes $cert.RawData
$keyPem = ConvertTo-Pem -Label 'PRIVATE KEY' -Bytes $rsa.ExportPkcs8PrivateKey()

Set-Content -LiteralPath $crtPath -Value $certPem -Encoding ascii
Set-Content -LiteralPath $keyPath -Value $keyPem -Encoding ascii

try {
    & icacls $keyPath /inheritance:r /grant:r "$($env:USERNAME):(R)" "SYSTEM:(R)" "Administrators:(R)" | Out-Null
}
catch {
    Write-Warning "No se pudieron restringir las ACL de $keyPath. Haga que solo la cuenta del servicio Caddy pueda leerlo."
}

$rsa.Dispose()
$cert.Dispose()

Write-Host "Certificado PEM: $crtPath"
Write-Host "Clave privada PEM: $keyPath"
Write-Host "Mantenga la clave fuera de Git (deploy/caddy/certs/ está ignorado)."
