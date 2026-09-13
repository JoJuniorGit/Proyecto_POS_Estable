# =====================================================================
# Prueba de Estrés contra Backend Externo (scripts/stress-external.ps1)
# Refresh 8.114: orquesta salud + provisionamiento/mantenimiento del
# staging remoto (usuario BOT, piscina SKU-TEST, restock masivo) y luego
# delega la carga a scripts/stress-test.py apuntando a la URL remota.
# Uso:
#   pwsh -File scripts/stress-external.ps1 -BaseUrl http://192.168.1.5:5000 `
#        -AdminPassword <pass> -StressPassword <pass>
# =====================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [string]$AdminUser = "Admin",
    [Parameter(Mandatory = $true)][string]$AdminPassword,
    [string]$StressUser = "BOT_STRESS_TEST",
    [Parameter(Mandatory = $true)][string]$StressPassword,
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
$BaseUrl = $BaseUrl.TrimEnd('/')
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
if (-not $Out) {
    $Out = Join-Path $env:TEMP ("reporte-estres-externo-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
}

function Invoke-Api {
    param([string]$Method, [string]$Path, [object]$Body, [string]$Token)
    $headers = @{ "Content-Type" = "application/json; charset=utf-8" }
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }
    $json = if ($null -eq $Body) { $null } else { $Body | ConvertTo-Json -Depth 6 }
    $response = Invoke-WebRequest "$BaseUrl$Path" -Method $Method -Headers $headers -Body $json -SkipHttpErrorCheck -TimeoutSec ([int][math]::Max(1, $Timeout))
    $text = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    [pscustomobject]@{ Status = $response.StatusCode; Body = $text }
}

function Concat-Error {
    param($ApiResult)
    if (-not $ApiResult) { return "sin respuesta" }
    try { $parsed = $ApiResult.Body | ConvertFrom-Json; return ($parsed.message ?? $parsed.title ?? $ApiResult.Body) } catch { return $ApiResult.Body }
}

Write-Host "=== Pre-flight externo: $BaseUrl (rev 8.114) ===" -ForegroundColor Cyan
$health = Invoke-Api "GET" "/health" $null $null
if ($health.Status -ne 200) { throw "Fallo /health: HTTP $($health.Status) $(Concat-Error $health)" }
$healthJson = $health.Body | ConvertFrom-Json
if ($healthJson.status -ne "Healthy" -or $healthJson.database -ne "Connected") {
    throw "El backend no esta Healthy/Connected: $($health.Body)"
}
Write-Host "/health -> $($healthJson.status) (database: $($healthJson.database))" -ForegroundColor Green

$rate = $Rate
if (-not $rate) {
    $today = Invoke-Api "GET" "/api/exchange-rate/today" $null $null
    if ($today.Status -ne 200) { throw "Fallo tasa BCV: HTTP $($today.Status) $(Concat-Error $today)" }
    $rate = [double]($today.Body | ConvertFrom-Json).value
}
if ($rate -le 0) { throw "La tasa BCV del dia es 0 en staging. Carguela antes de la prueba." }
Write-Host "Tasa BCV del dia: $rate" -ForegroundColor Green

$login = Invoke-Api "POST" "/api/auth/login" @{ cedula = $AdminUser; password = $AdminPassword; platform = "desktop" } $null
if ($login.Status -ne 200 -or -not ($login.Body | ConvertFrom-Json).token) {
    throw "Fallo login de administrador ($AdminUser): HTTP $($login.Status) $(Concat-Error $login)"
}
$adminToken = ($login.Body | ConvertFrom-Json).token
Write-Host "Sesion de administracion iniciada como $AdminUser" -ForegroundColor Green

if (-not $NoProvision) {
    Write-Host "=== Provisionamiento / mantenimiento del staging remoto ===" -ForegroundColor Cyan

    $user = Invoke-Api "POST" "/api/users" @{ cedula = $StressUser; name = "Prueba Estres (BOT)"; password = $StressPassword; role = 1 } $adminToken
    if ($user.Status -eq 201) {
        Write-Host "Usuario de estres creado: $StressUser" -ForegroundColor Green
    }
    elseif ($user.Status -eq 400 -and (Concat-Error $user) -match "ya existe") {
        Write-Host "Usuario de estres ya existia: $StressUser" -ForegroundColor Green
    }
    else {
        throw "Fallo creacion del usuario de estres: HTTP $($user.Status) $(Concat-Error $user)"
    }

    $filterEnc = [uri]::EscapeDataString($ProductFilter)
    $found = @{}
    $page = 1
    do {
        $catalog = Invoke-Api "GET" "/api/products?filter=$filterEnc&page=$page&pageSize=100" $null $adminToken
        if ($catalog.Status -ne 200) { throw "Fallo catalogo de productos: HTTP $($catalog.Status) $(Concat-Error $catalog)" }
        $json = $catalog.Body | ConvertFrom-Json
        $items = @($json.items ?? $json.Items)
        foreach ($item in $items) { if ($item.sku) { $found[([string]$item.sku).ToLower()] = $item } }
        if ($items.Count -lt 100) { break }
        $page++
    } while ($true)

    $created = 0
    for ($i = 1; $i -le $ProductCount; $i++) {
        $sku = $ProductFilter + "-" + $i.ToString("D3")
        if ($found.ContainsKey($sku.ToLower())) { continue }
        $product = Invoke-Api "POST" "/api/products" @{
            name                  = "Prueba Estres $i"
            sku                   = $sku
            description           = ""
            priceUsd              = 0
            priceRetailUsd        = 0
            priceBsS              = 0
            priceWholesaleUsd     = 0
            costPriceUsd          = 0
            profitMarginRetail    = 0
            profitMarginWholesale = 0
            isActive              = $true
        } $adminToken
        if ($product.Status -in 201, 200) {
            $created++
        }
        else {
            throw "Fallo creacion del producto ${sku}: HTTP $($product.Status) $(Concat-Error $product)"
        }
    }
    if ($created -gt 0) {
        $page = 1
        do {
            $catalog = Invoke-Api "GET" "/api/products?filter=$filterEnc&page=$page&pageSize=100" $null $adminToken
            if ($catalog.Status -ne 200) { throw "Fallo re-lectura de catalogo: HTTP $($catalog.Status) $(Concat-Error $catalog)" }
            $items = @(($catalog.Body | ConvertFrom-Json).items ?? @())
            foreach ($item in $items) { if ($item.sku) { $found[([string]$item.sku).ToLower()] = $item } }
            if ($items.Count -lt 100) { break }
            $page++
        } while ($true)
        Write-Host "Productos creados en esta corrida: $created" -ForegroundColor Green
    }

    $restocked = 0
    $skipped = 0
    foreach ($item in $found.Values) {
        $current = [double]$item.stockQuantity
        if ($current -ge $RestockAmount) { $skipped++; continue }
        $delta = [long]($RestockAmount - $current)
        $adjust = Invoke-Api "POST" "/api/products/$($item.id)/adjust-stock" @{ quantityChange = $delta; reason = "stress-external restock (rev 8.114)" } $adminToken
        if ($adjust.Status -eq 204) { $restocked++ } else { throw "Fallo restock del producto $($item.sku): HTTP $($adjust.Status) $(Concat-Error $adjust)" }
    }
    Write-Host "Productos SKU-TEST disponibles: $($found.Count) | restock a ${RestockAmount}: aplicados=$restocked ya-cubiertos=$skipped" -ForegroundColor Green
}
else {
    Write-Host "Provisionamiento omitido (-NoProvision): se asume staging ya provisionado." -ForegroundColor Yellow
}

Write-Host "=== Ejecutando scripts/stress-test.py contra $BaseUrl ===" -ForegroundColor Cyan
$pythonArgs = @(
    "scripts/stress-test.py",
    "--base-url", $BaseUrl,
    "--confirm-staging", $BaseUrl,
    "--user", $StressUser,
    "--password", $StressPassword,
    "--transactions", "$Transactions",
    "--cashiers", "$Cashiers",
    "--qty-min", "$QtyMin",
    "--qty-max", "$QtyMax",
    "--filter", $ProductFilter,
    "--timeout", $Timeout.ToString($invariant),
    "--out", $Out
)
if ($Rate) { $pythonArgs += @("--rate", $Rate.ToString($invariant)) }
if ($MaxProductsPerSale -gt 0) { $pythonArgs += @("--max-products-per-sale", "$MaxProductsPerSale") }
if ($Insecure) { $pythonArgs += "--insecure" }

& python @pythonArgs
if ($LASTEXITCODE -ne 0) {
    throw "stress-test.py termino con codigo $LASTEXITCODE. Revise 409 (stock) y 429 (rate limit) en la salida."
}

if (Test-Path $Out) {
    $result = Get-Content $Out -Raw | ConvertFrom-Json
    Write-Host "=== RESULTADO (resumen) ===" -ForegroundColor Cyan
    Write-Host "Ventas completadas: $($result.completedSales) | Fallos: $($result.saleFailures) | Re-logins: $($result.relogins)" -ForegroundColor Green
    Write-Host "Duracion: $($result.elapsedSeconds) s | Tasa: $($result.rate) | JSON: $Out" -ForegroundColor Green
    foreach ($endpoint in $result.metrics.PSObject.Properties.Name) {
        $entry = $result.metrics.$endpoint
        "$endpoint`: n=$($entry.n) ok=$($entry.ok) fail=$($entry.fail) -> $((($entry.status.PSObject.Properties | ForEach-Object { "$($_.Name):$($_.Value)" }) -join ', '))"
    }
    if ($result.saleFailures -gt 0) {
        Write-Host "NOTA: 409 indica stock agotado (re-cargue staging); 429 es GeneralApiRateLimit (multiplique IPs o suba RateLimiting:GeneralApiRateLimit en staging)." -ForegroundColor Yellow
    }
}
exit $LASTEXITCODE