#requires -Version 7.0
<#
.SYNOPSIS
    Genera y valida la configuración de Caddy como reverse proxy TLS del POS.

.DESCRIPTION
    El backend queda escuchando solo en loopback:5000 y Caddy publica HTTPS en 8443.
    Por defecto solo escribe/valida el Caddyfile; -InstallService intenta registrar
    el proxy como servicio con NSSM si está disponible.

.EXAMPLE
    pwsh scripts/setup-caddy-proxy.ps1 -CaddyExe C:\Tools\caddy\caddy.exe -InstallService
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CaddyExe,
    [int]$ListenPort = 8443,
    [int]$BackendPort = 5000,
    [string]$CertificatePath = "",
    [string]$KeyPath = "",
    [string]$ConfigDir = "",
    [switch]$InstallService,
    [string]$ServiceName = "PosCaddyProxy"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path -LiteralPath $CaddyExe)) {
    throw "No se encontró caddy.exe: $CaddyExe"
}
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Join-Path $root 'deploy/caddy/certs/pos-https.crt'
}
if ([string]::IsNullOrWhiteSpace($KeyPath)) {
    $KeyPath = Join-Path $root 'deploy/caddy/certs/pos-https.key'
}
if ([string]::IsNullOrWhiteSpace($ConfigDir)) {
    $ConfigDir = Join-Path $env:ProgramData 'CommandCenter\ReverseProxy'
}
if (-not (Test-Path -LiteralPath $CertificatePath)) {
    throw "Falta el certificado PEM: $CertificatePath. Ejecute scripts/export-https-pem.ps1."
}
if (-not (Test-Path -LiteralPath $KeyPath)) {
    throw "Falta la clave PEM: $KeyPath. Ejecute scripts/export-https-pem.ps1."
}

New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
$caddyfile = Join-Path $ConfigDir 'Caddyfile'
$certCaddy = $CertificatePath.Replace('\', '/')
$keyCaddy = $KeyPath.Replace('\', '/')

$content = @"
{
	auto_https off
	admin off
}

:$ListenPort {
	tls "$certCaddy" "$keyCaddy"
	encode gzip

	# Caddy agrega X-Forwarded-For automáticamente; el backend solo confía en el proxy.
	reverse_proxy 127.0.0.1:$BackendPort {
		header_up X-Forwarded-Proto {scheme}
		header_up X-Forwarded-Host {host}
	}
}
"@

Set-Content -LiteralPath $caddyfile -Value $content -Encoding UTF8
Write-Host "Caddyfile generado: $caddyfile"

& $CaddyExe validate --config $caddyfile --adapter caddyfile
if ($LASTEXITCODE -ne 0) {
    throw "Caddyfile inválido (exit $LASTEXITCODE)."
}
Write-Host "Caddyfile válido."

if ($InstallService) {
    $nssm = Get-Command nssm.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    if (-not $nssm) {
        Write-Warning "nssm.exe no está disponible. Registre el servicio manualmente con:"
        Write-Host "  nssm install $ServiceName `"$CaddyExe`" `"run --config `"$caddyfile`" --adapter caddyfile`""
        Write-Host "  nssm set $ServiceName AppDirectory `"$ConfigDir`""
        Write-Host "  nssm start $ServiceName"
    }
    else {
        $runArguments = "run --config `"$caddyfile`" --adapter caddyfile"
        & $nssm install $ServiceName $CaddyExe $runArguments | Out-Null
        & $nssm set $ServiceName AppDirectory $ConfigDir | Out-Null
        & $nssm start $ServiceName | Out-Null
        Write-Host "Servicio $ServiceName instalado e iniciado con NSSM."
    }
}

Write-Host ""
Write-Host "Siguiente paso: configurar el backend con:"
Write-Host "  ReverseProxy__Enabled=true"
Write-Host "  ReverseProxy__HttpsPort=$ListenPort"
Write-Host "  Ports__Https=$ListenPort"
Write-Host "Reinicie PosBackendService y valide: https://<host>:$ListenPort/api/health"
Write-Host "Clientes: https://<host>:$ListenPort/ (el desktop fija el certificado con DPAPI)."
