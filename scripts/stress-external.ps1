# =====================================================================
# Prueba de Estrés contra Backend Externo (scripts/stress-external.ps1)
# Refresh 8.115: wrapper fino sobre scripts/pos-test.ps1 -Action Campaign.
# Toda la logica (pre-flight, provision, restock, muestreador de
# monitoreo, reporte unificado) vive ahora en pos-test.ps1; este script
# conserva la CLI historica de la rev 8.114 para no romper el runbook.
# Uso:
#   pwsh -File scripts/stress-external.ps1 -BaseUrl http://192.168.1.5:5000 `
#        -AdminPassword <pass> -StressPassword <pass>
# =====================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [string]$AdminUser = "Admin",
    [string]$AdminPassword,
    [string]$StressUser = "BOT_STRESS_TEST",
    [string]$StressPassword,
    [int]$Transactions = 60,
    [int]$Cashiers = 4,
    [int]$QtyMin = 1,
    [int]$QtyMax = 3,
    [int]$ProductCount = 30,
    [long]$RestockAmount = 1000000,
    [string]$ProductFilter = "SKU-TEST",
    [double]$Rate,
    [double]$Timeout = 60.0,
    [string]$Out,
    [int]$MaxProductsPerSale = 0,
    [switch]$Insecure,
    [switch]$NoProvision
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core") {
    throw "Se requiere PowerShell 7 (pwsh): Invoke-WebRequest -SkipHttpErrorCheck no existe en Windows PowerShell 5.1."
}
$repoRoot = Split-Path -Path $PSScriptRoot -Parent
Set-Location $repoRoot

$posTest = Join-Path $PSScriptRoot "pos-test.ps1"
$mapArgs = @{
    Action         = "Campaign"
    BaseUrl        = $BaseUrl
    AdminUser      = $AdminUser
    AdminPassword  = $AdminPassword
    StressUser     = $StressUser
    StressPassword = $StressPassword
    Transactions   = $Transactions
    Cashiers       = $Cashiers
    QtyMin         = $QtyMin
    QtyMax         = $QtyMax
    ProductCount   = $ProductCount
    RestockAmount  = $RestockAmount
    ProductFilter  = $ProductFilter
    Timeout        = $Timeout
}
if ($Rate -gt 0) { $mapArgs.Rate = $Rate }
if ($Out) { $mapArgs.Out = $Out }
if ($MaxProductsPerSale -gt 0) { $mapArgs.MaxProductsPerSale = $MaxProductsPerSale }
if ($Insecure) { $mapArgs.Insecure = $true }
if ($NoProvision) { $mapArgs.NoProvision = $true }

& $posTest @mapArgs
exit $LASTEXITCODE