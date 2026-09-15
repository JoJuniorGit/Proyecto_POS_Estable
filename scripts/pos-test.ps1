# =====================================================================
# pos-test.ps1 - Control de Pruebas POS unificado (rev 8.120)
#
# Un unico punto de control para el ciclo completo de una prueba de
# estres contra cualquier backend de staging (interno o externo):
#   provision -> stress -> monitoreo concurrente -> reporte -> limpieza.
#
# Usa scripts/stress-test.py como motor de carga, docs/monitor-health.ps1
# como sonda dedicada (muestreador) y cleanup-stress-data.ps1 para la
# limpieza. Genera una carpeta de resultados por corrida en results\ con
# el reporte unificado (report.json + report.md) que correlaciona SLO,
# latencias y diagnostico HTTP.
#
# Acciones (-Action):
#   Menu       - muestra un menu interactivo (por defecto sin -Action).
#   Preflight  - verifica /health, tasa BCV y login de administracion.
#   Provision  - asegura usuario BOT, piscina SKU-TEST y restock.
#   Stress     - ejecuta el motor de carga (delega a stress-test.py).
#   Monitor    - ejecuta una sonda headless (-Dashboard abre la UI).
#   Campaign   - ciclo completo: provision + muestreador + stress + reporte.
#   Report     - regenera report.json/report.md desde una corrida existente.
#   Cleanup    - elimina los datos sinteticos de la corrida (delega a
#                cleanup-stress-data.ps1).
#
# Configuracion: scripts/pos-test-config.json (ver pos-test-config.json.example).
# La linea de comandos tiene precedencia. Las credenciales se resuelven desde
# Backend.API\secrets.json (claves Stress.AdminPassword/Stress.StressPassword),
# la configuracion, variables de entorno POS_TEST_* o prompt (en ese orden).
#
# Requisitos: PowerShell 7 (pwsh).
# Uso:
#   pwsh -File scripts/pos-test.ps1                       # menu interactivo
#   pwsh -File scripts/pos-test.ps1 -Action Campaign       # campana directa
#   pwsh -File scripts/pos-test.ps1 -Action Campaign -BaseUrl http://192.168.1.5:5000
#   pwsh -File scripts/pos-test.ps1 -Action Report -RunId 20260913-023000_estacion-01_pos-test
# =====================================================================

[CmdletBinding()]
param(
    [ValidateSet("Menu", "Preflight", "Provision", "Stress", "Monitor", "Report", "Campaign", "Cleanup")]
    [string]$Action = "Menu",
    [string]$Config = "",
    [string]$BaseUrl = "",
    [string]$ConfirmStaging = "",
    [string]$SecretsFile = "",
    [string]$ConnectionString = "",
    [string]$AdminUser = "",
    [string]$AdminPassword = "",
    [string]$StressUser = "",
    [string]$StressPassword = "",
    [int]$Transactions = 0,
    [int]$Duration = 0,
    [int]$Cashiers = 0,
    [int]$QtyMin = 0,
    [int]$QtyMax = 0,
    [double]$ThinkMin = -1,
    [double]$ThinkMax = -1,
    [int]$MaxProductsPerSale = 0,
    [int]$ProductCount = 0,
    [long]$RestockAmount = 0,
    [string]$ProductFilter = "",
    [double]$Rate = 0,
    [double]$Timeout = 0,
    [string]$Out = "",
    [switch]$Insecure,
    [switch]$NoProvision,
    [switch]$NoMonitor,
    [switch]$Dashboard,
    [int]$SamplerSeconds = 0,
    [int]$WindowHours = 0,
    [string]$MonitorConfig = "",
    [string]$ResultsDir = "",
    [string]$RunId = "",
    [string]$PythonExe = "python",
    [string]$SkuPrefix = "",
    [switch]$DeleteProducts,
    [switch]$DeleteUser,
    [string]$Confirm = ""
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core") {
    throw "Se requiere PowerShell 7 (pwsh): Invoke-WebRequest -SkipHttpErrorCheck no existe en Windows PowerShell 5.1."
}
$repoRoot = Split-Path -Path $PSScriptRoot -Parent
Set-Location $repoRoot
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Write-Step {
    param([string]$Title)
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

function Get-CfgNode {
    param([object[]]$Path)
    $node = $cfg
    foreach ($key in $Path) {
        if ($null -eq $node) { break }
        $node = $node.$key
    }
    return $node
}

function PickString {
    param([string]$CliValue, [object[]]$CfgPath)
    if ($CliValue) { return $CliValue }
    $value = Get-CfgNode $CfgPath
    if ($null -ne $value) {
        $asString = [string]$value
        if ($asString) { return $asString }
    }
    return ""
}

function PickInt {
    param([int]$CliValue, [object[]]$CfgPath, [int]$Default = 0)
    if ($CliValue -ne 0) { return $CliValue }
    $value = Get-CfgNode $CfgPath
    if ($null -ne $value) {
        try {
            $asInt = [int]$value
            if ($asInt -ne 0) { return $asInt }
        } catch { }
    }
    return $Default
}

function PickLong {
    param([long]$CliValue, [object[]]$CfgPath, [long]$Default = 0)
    if ($CliValue -ne 0) { return $CliValue }
    $value = Get-CfgNode $CfgPath
    if ($null -ne $value) {
        try {
            $asLong = [long]$value
            if ($asLong -ne 0) { return $asLong }
        } catch { }
    }
    return $Default
}

function PickDouble {
    param([double]$CliValue, [object[]]$CfgPath, [double]$Default = 0.0)
    if ($CliValue -gt 0) { return $CliValue }
    $value = Get-CfgNode $CfgPath
    if ($null -ne $value) {
        try {
            $asDouble = [double]$value
            if ($asDouble -gt 0) { return $asDouble }
        } catch { }
    }
    return $Default
}

function Resolve-Secret {
    param([string[]]$Paths)
    $node = $secrets
    foreach ($key in $Paths) {
        if ($null -eq $node) { break }
        $node = $node.$key
    }
    if ($null -eq $node) { return "" }
    return [string]$node
}

function Resolve-Password {
    param([string]$CliValue, [string[]]$SecretPath, [string]$EnvName, [switch]$NoPrompt)
    if ($CliValue) { return $CliValue }
    $fromSecret = Resolve-Secret $SecretPath
    if ($fromSecret) { return $fromSecret }
    $fromEnv = [Environment]::GetEnvironmentVariable($EnvName)
    if ($fromEnv) { return $fromEnv }
    if ($NoPrompt) { return "" }
    $secure = Read-Host "Ingrese la contrasena (${EnvName})" -AsSecureString
    if ($null -eq $secure -or $secure.Length -eq 0) { throw "No se pudo resolver la contrasena ${EnvName}." }
    return [System.Net.NetworkCredential]::new("", $secure).Password
}

function Resolve-ConnectionString {
    param([string]$CliValue, [hashtable]$Res)
    if ($CliValue) { return $CliValue }
    if ($Res -and $Res.connectionString) { return $Res.connectionString }
    $secretCs = Resolve-Secret @("ConnectionStrings", "DefaultConnection")
    if ($secretCs) { return $secretCs }
    $secretCs = Resolve-Secret @("ConnectionStrings__DefaultConnection")
    if ($secretCs) { return $secretCs }
    $candidates = @(
        (Join-Path $repoRoot "Backend.API\appsettings.Development.json"),
        (Join-Path $repoRoot "Backend.API\appsettings.json")
    )
    foreach ($appsettings in $candidates) {
        if (Test-Path -LiteralPath $appsettings) {
            try {
                $json = Get-Content -Raw -LiteralPath $appsettings | ConvertFrom-Json
                $cs = $json.ConnectionStrings.DefaultConnection
                if ($cs) { return [string]$cs }
            } catch { }
        }
    }
    return ""
}

function Invoke-Api {
    param([string]$Method, [string]$Path, [object]$Body, [string]$Token)
    $headers = @{ "Content-Type" = "application/json; charset=utf-8" }
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }
    $jsonBody = if ($null -eq $Body) { $null } else { $Body | ConvertTo-Json -Depth 6 }
    $timeoutSec = [int][Math]::Max(1, [double]$res.timeout)
    $response = Invoke-WebRequest "$($res.baseUrl)$Path" -Method $Method -Headers $headers -Body $jsonBody -SkipHttpErrorCheck -TimeoutSec $timeoutSec
    $text = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    [pscustomobject]@{ Status = $response.StatusCode; Body = $text }
}

function Concat-Error {
    param($ApiResult)
    if (-not $ApiResult) { return "sin respuesta" }
    try {
        $parsed = $ApiResult.Body | ConvertFrom-Json
        foreach ($candidate in @($parsed.message, $parsed.title, $parsed.detail)) {
            if ($null -ne $candidate) { return $candidate }
        }
        return $ApiResult.Body
    } catch { return $ApiResult.Body }
}

function Test-StagingPreflight {
    param([hashtable]$Res)
    Write-Step "Pre-flight de staging: $($Res.baseUrl)"
    if ($Res.confirmStaging -ne "YES") {
        throw "Debe explicitar -ConfirmStaging YES para proteger contra ejecuciones accidentales en produccion."
    }
    $health = Invoke-Api "GET" "/health" $null $null
    if ($health.Status -ne 200) { throw "Fallo /health: HTTP $($health.Status) $(Concat-Error $health)" }
    $healthJson = $health.Body | ConvertFrom-Json
    if ($healthJson.status -ne "Healthy" -or $healthJson.database -ne "Connected") {
        throw "El backend no esta Healthy/Connected: $($health.Body)"
    }
    Write-Host "/health -> $($healthJson.status) (database: $($healthJson.database))" -ForegroundColor Green

    $rate = [double]$Res.rate
    if ($rate -le 0) {
        $today = Invoke-Api "GET" "/api/exchange-rate/today" $null $null
        if ($today.Status -ne 200) { throw "Fallo tasa BCV: HTTP $($today.Status) $(Concat-Error $today)" }
        $rate = [double]($today.Body | ConvertFrom-Json).value
    }
    if ($rate -le 0) { throw "La tasa BCV del dia es 0 en staging ($($Res.baseUrl)). Carguela antes de la prueba." }
    $parsedRate = [Math]::Round($rate, 2)
    Write-Host "Tasa BCV del dia: $parsedRate" -ForegroundColor Green
    return $parsedRate
}

function Login-Admin {
    param([hashtable]$Res)
    $login = Invoke-Api "POST" "/api/auth/login" @{ cedula = $Res.adminUser; password = $Res.adminPassword; platform = "desktop" } $null
    if ($login.Status -ne 200 -or -not ($login.Body | ConvertFrom-Json).token) {
        throw "Fallo login de administrador ($($Res.adminUser)): HTTP $($login.Status) $(Concat-Error $login)"
    }
    $adminToken = ($login.Body | ConvertFrom-Json).token
    Write-Host "Sesion de administracion iniciada como $($Res.adminUser)" -ForegroundColor Green
    return $adminToken
}

function Invoke-Provision {
    param([hashtable]$Res)
    Write-Step "Provisionamiento / mantenimiento del staging ($($Res.environmentName))"
    $adminToken = Login-Admin $Res

    $user = Invoke-Api "POST" "/api/users" @{ cedula = $Res.stressUser; name = $Res.stressUser; password = $Res.stressPassword; role = 1 } $adminToken
    if ($user.Status -eq 201) {
        Write-Host "Usuario de estres creado: $($Res.stressUser)" -ForegroundColor Green
    }
    elseif ($user.Status -eq 400 -and (Concat-Error $user) -match "ya existe") {
        Write-Host "Usuario de estres ya existia: $($Res.stressUser)" -ForegroundColor Green
    }
    else {
        throw "Fallo creacion del usuario de estres: HTTP $($user.Status) $(Concat-Error $user)"
    }

    $filterEnc = [uri]::EscapeDataString($Res.productFilter)
    $found = @{}
    $page = 1
    do {
        $catalog = Invoke-Api "GET" "/api/products?filter=$filterEnc&page=$page&pageSize=100" $null $adminToken
        if ($catalog.Status -ne 200) { throw "Fallo catalogo de productos: HTTP $($catalog.Status) $(Concat-Error $catalog)" }
        $json = $catalog.Body | ConvertFrom-Json
        $items = if ($null -ne $json.items) { @($json.items) } else { @($json.Items) }
        foreach ($item in $items) { if ($item.sku) { $found[([string]$item.sku).ToLower()] = $item } }
        if ($items.Count -lt 100) { break }
        $page++
    } while ($true)

    $created = 0
    for ($i = 1; $i -le $Res.productCount; $i++) {
        $sku = $Res.productFilter + "-" + $i.ToString("D3")
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
            $catalogParsed = $catalog.Body | ConvertFrom-Json
            $items = if ($null -ne $catalogParsed.items) { @($catalogParsed.items) } else { @() }
            foreach ($item in $items) { if ($item.sku) { $found[([string]$item.sku).ToLower()] = $item } }
            if ($items.Count -lt 100) { break }
            $page++
        } while ($true)
        Write-Host "Productos creados en esta corrida: $created" -ForegroundColor Green
    }

    $restocked = 0
    $skipped = 0
    $reason = "pos-test provision (rev 8.120)"
    foreach ($item in $found.Values) {
        $current = [double]$item.stockQuantity
        if ($current -ge $Res.restockAmount) { $skipped++; continue }
        $delta = [long]($Res.restockAmount - $current)
        $adjust = Invoke-Api "POST" "/api/products/$($item.id)/adjust-stock" @{ quantityChange = $delta; reason = $reason } $adminToken
        if ($adjust.Status -eq 204) { $restocked++ } else { throw "Fallo restock del producto $($item.sku): HTTP $($adjust.Status) $(Concat-Error $adjust)" }
    }
    Write-Host "Productos SKU disponibles: $($found.Count) | restock a $($Res.restockAmount): aplicados=$restocked ya-cubiertos=$skipped" -ForegroundColor Green
    return $adminToken
}

function New-RunFolder {
    param([hashtable]$Res)
    if (-not (Test-Path -LiteralPath $Res.resultsDir)) { New-Item -ItemType Directory -Path $Res.resultsDir -Force | Out-Null }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $machine = [System.Net.Dns]::GetHostName().ToLowerInvariant() -replace '[^a-z0-9-]', '-'
    $dir = Join-Path $Res.resultsDir ("{0}_{1}_pos-test" -f $stamp, $machine)
    New-Item -ItemType Directory -Path (Join-Path $dir "monitoring") -Force | Out-Null
    $runProfile = [ordered]@{
        environmentName    = $Res.environmentName
        baseUrl            = $Res.baseUrl
        rate               = $Res.rate
        timeout            = $Res.timeout
        transactions       = $Res.transactions
        duration           = $Res.duration
        cashiers           = $Res.cashiers
        qtyMin             = $Res.qtyMin
        qtyMax             = $Res.qtyMax
        thinkMin           = $Res.thinkMin
        thinkMax           = $Res.thinkMax
        maxProductsPerSale = $Res.maxProductsPerSale
        productCount       = $Res.productCount
        restockAmount      = $Res.restockAmount
        productFilter      = $Res.productFilter
        samplerSeconds     = $Res.samplerSeconds
        windowHours        = $Res.windowHours
    }
    $runProfile | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dir "run-profile.json") -Encoding UTF8
    return $dir
}

function Resolve-RunDir {
    param([hashtable]$Res)
    if (-not $RunId) { throw "Se requiere -RunId para esta accion (nombre de la carpeta bajo $($Res.resultsDir))." }
    $dir = Join-Path $Res.resultsDir $RunId
    if (-not (Test-Path -LiteralPath $dir)) { throw "No se encontro la corrida en '$dir'." }
    return $dir
}

function Resolve-MonitorConfig {
    param([hashtable]$Res)
    $candidates = @()
    if ($Res.monitorConfig) { $candidates += $Res.monitorConfig }
    $candidates += (Join-Path $repoRoot "docs\monitor-config.json")
    $candidates += (Join-Path $env:ProgramData "CommandCenterPOS\monitoring\monitor-config.json")
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    $example = Join-Path $repoRoot "docs\monitor-config.json.example"
    $target = Join-Path $repoRoot "docs\monitor-config.json"
    if (Test-Path -LiteralPath $example) {
        try {
            Copy-Item -LiteralPath $example -Destination $target -Force
            Write-Host "monitor-config.json creado desde .example (primera vez)." -ForegroundColor Green
            return $target
        } catch { }
    }
    return ""
}

function Start-TestSampler {
    param([hashtable]$Res, [string]$RunDir)
    $monitorConfig = Resolve-MonitorConfig $Res
    if (-not $monitorConfig) {
        Write-Host "AVISO: no se encontro monitor-config.json; se omite el muestreador dedicado (-NoMonitor implicito)." -ForegroundColor Yellow
        return $null
    }
    $monitorScript = Join-Path $repoRoot "docs\monitor-health.ps1"
    $stopFile = Join-Path $RunDir "sampler.stop"
    $monitorDataDir = Join-Path $RunDir "monitoring"
    $monitorLog = Join-Path $monitorDataDir "monitor.log"
    $hostExe = (Get-Process -Id $PID).Path
    $argList = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$monitorScript`"",
        "-Config", "`"$monitorConfig`"",
        "-DataDir", "`"$monitorDataDir`"",
        "-LogFile", "`"$monitorLog`"",
        "-SamplerSeconds", "$($Res.samplerSeconds)",
        "-SamplerStopFile", "`"$stopFile`"",
        "-WindowHours", "$($Res.windowHours)"
    )
    $proc = Start-Process -FilePath $hostExe -ArgumentList $argList -WindowStyle Hidden -PassThru
    Write-Host "Muestreador de monitoreo iniciado (PID $($proc.Id)): cada $($Res.samplerSeconds) s hacia '$monitorDataDir'" -ForegroundColor Green
    return [pscustomobject]@{ Process = $proc; StopFile = $stopFile; DataDir = $monitorDataDir; Config = $monitorConfig }
}

function Stop-TestSampler {
    param($Sampler)
    if (-not $Sampler) { return }
    try { New-Item -ItemType File -Path $Sampler.StopFile -Force | Out-Null } catch { }
    Start-Sleep -Seconds 3
    if (-not $Sampler.Process.HasExited) {
        Start-Sleep -Seconds 3
    }
    if (-not $Sampler.Process.HasExited) {
        Stop-Process -Id $Sampler.Process.Id -Force -ErrorAction SilentlyContinue
        Write-Host "Muestreador forzado a detener." -ForegroundColor Yellow
    }
    Remove-Item -LiteralPath $Sampler.StopFile -Force -ErrorAction SilentlyContinue
    Write-Host "Muestreador detenido." -ForegroundColor Green
}

function Get-StressArgs {
    param([hashtable]$Res, [string]$RunDir, [string]$OutPath, [string]$StopFile = "")
    $py = Join-Path $PSScriptRoot "stress-test.py"
    $argsList = @(
        $py,
        "--base-url", $Res.baseUrl,
        "--confirm-staging", $Res.confirmStaging,
        "--user", $Res.stressUser,
        "--password", $Res.stressPassword,
        "--cashiers", "$($Res.cashiers)",
        "--qty-min", "$($Res.qtyMin)",
        "--qty-max", "$($Res.qtyMax)",
        "--think-min", $Res.thinkMin.ToString($invariant),
        "--think-max", $Res.thinkMax.ToString($invariant),
        "--filter", $Res.productFilter,
        "--timeout", $Res.timeout.ToString($invariant),
        "--out", $OutPath
    )
    if ($Res.transactions -gt 0) { $argsList += @("--transactions", "$($Res.transactions)") }
    if ($Res.duration -gt 0) { $argsList += @("--duration", "$($Res.duration)") }
    if ($Res.maxProductsPerSale -gt 0) { $argsList += @("--max-products-per-sale", "$($Res.maxProductsPerSale)") }
    if ($Res.rate -gt 0) { $argsList += @("--rate", $Res.rate.ToString($invariant)) }
    if ($Res.insecure) { $argsList += "--insecure" }
    if ($StopFile) { $argsList += @("--stop-file", $StopFile) }
    return $argsList
}

function Invoke-StressRun {
    param([hashtable]$Res, [string]$RunDir)
    $outPath = $Res.out
    if (-not $outPath) { $outPath = Join-Path $RunDir "stress.json" }
    $logPath = Join-Path $RunDir "stress.log"
    $pythonArgs = Get-StressArgs $Res $RunDir $outPath

    if (-not $Res.transactions -and -not $Res.duration) {
        Write-Host "AVISO: sin --transactions ni --duration en el perfil; stress-test.py lo requerira." -ForegroundColor Yellow
    }

    Write-Step "Ejecutando scripts/stress-test.py contra $($Res.baseUrl)"
    & $Res.pyExe @pythonArgs 2>&1 | Tee-Object -LiteralPath $logPath
    if ($LASTEXITCODE -ne 0) {
        throw "stress-test.py termino con codigo $LASTEXITCODE. Revise 409 (stock) y 429 (rate limit) en la salida."
    }
    $canonical = Join-Path $RunDir "stress.json"
    if ($outPath -ne $canonical -and (Test-Path -LiteralPath $outPath) -and -not (Test-Path -LiteralPath $canonical)) {
        Copy-Item -LiteralPath $outPath -Destination $canonical -Force
    }
    return $outPath
}

function Invoke-StressRunFinalizable {
    param([hashtable]$Res, [string]$RunDir)
    $outPath = Join-Path $RunDir "stress.json"
    $logPath = Join-Path $RunDir "stress.log"
    $stopFile = Join-Path $RunDir "stress.stop"
    Remove-Item -LiteralPath $stopFile -Force -ErrorAction SilentlyContinue
    $pythonArgs = Get-StressArgs $Res $RunDir $outPath $stopFile

    if (-not $Res.transactions -and -not $Res.duration) {
        Write-Host "AVISO: sin --transactions ni --duration en el perfil; stress-test.py lo requerira." -ForegroundColor Yellow
    }

    Write-Step "Ejecutando scripts/stress-test.py contra $($Res.baseUrl) (finalizable)"
    $job = Start-Job -ScriptBlock {
        param($exe, $pyArgs, $log)
        & $exe @pyArgs *>&1 | Tee-Object -LiteralPath $log | Out-Null
        return $LASTEXITCODE
    } -ArgumentList $Res.pyExe, $pythonArgs, $logPath

    Write-Host "Prueba en curso (job $($job.Id)). Avance: $logPath" -ForegroundColor Green
    Write-Host "Pulse Enter o F para FINALIZAR y generar el reporte." -ForegroundColor Cyan

    if (-not [Console]::IsInputRedirected) {
        while ($job.State -eq "Running") {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true)
                if ($key.Key -eq [ConsoleKey]::Enter -or $key.Key -eq [ConsoleKey]::F) { break }
            }
            Start-Sleep -Milliseconds 300
        }
    } else {
        Wait-Job $job -Timeout 3600 | Out-Null
    }

    if ($job.State -eq "Running") {
        Write-Host "Finalizando prueba (stop-file)..." -ForegroundColor Yellow
        New-Item -ItemType File -Path $stopFile -Force | Out-Null
        if (-not (Wait-Job $job -Timeout 180)) {
            Write-Host "El motor no respondio al stop-file; deteniendo el job." -ForegroundColor Yellow
            Stop-Job $job -ErrorAction SilentlyContinue
        }
    }

    $exitCode = Receive-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stopFile -Force -ErrorAction SilentlyContinue

    if ($null -ne $exitCode -and "$exitCode" -ne "0") {
        throw "stress-test.py termino con codigo $exitCode. Revise 409 (stock) y 429 (rate limit) en la salida."
    }
    return $outPath
}

function Build-HttpDiagnosis {
    param($StressJson)
    $diagnosis = [ordered]@{ status429 = 0; status409 = 0; statusOther = [ordered]@{}; hints = @() }
    foreach ($prop in $StressJson.metrics.PSObject.Properties) {
        $statusMap = $prop.Value.status
        foreach ($statusProp in $statusMap.PSObject.Properties) {
            $code = [int]$statusProp.Name
            $count = [int]$statusProp.Value
            if ($code -eq 429) { $diagnosis.status429 += $count }
            elseif ($code -eq 409) { $diagnosis.status409 += $count }
            elseif ($code -ge 400) { $diagnosis.statusOther[$statusProp.Name] = $count }
        }
    }
    if ($diagnosis.status429 -gt 0) {
        $diagnosis.hints += "429 = GeneralApiRateLimit (200 req/min por IP): baje los hilos o multiplique IPs, o suba RateLimiting:GeneralApiRateLimit en staging."
    }
    if ($diagnosis.status409 -gt 0) {
        $diagnosis.hints += "409 = conflicto/stock insuficiente: verifique restockAmount y el estado del producto en staging."
    }
    if ([int]$StressJson.saleFailures -gt 0) {
        $diagnosis.hints += "Fallos de venta > 0: correlacionar con los codigos HTTP; el SLO de la ventana suele reflejarlo."
    }
    return $diagnosis
}

function Build-Report {
    param([hashtable]$Res, [string]$RunDir, $SamplerInfo)
    $stressPath = Join-Path $RunDir "stress.json"
    if (-not (Test-Path -LiteralPath $stressPath)) {
        throw "No se encontro '$stressPath'. Ejecute -Action Campaign o -Action Stress antes del reporte."
    }
    $stressJson = Get-Content -Raw -LiteralPath $stressPath | ConvertFrom-Json

    . (Join-Path $repoRoot "docs\monitor-health.ps1") -ImportOnly

    $samplerDataDir = if ($SamplerInfo) { $SamplerInfo.DataDir } else { Join-Path $RunDir "monitoring" }
    $window = [int][Math]::Max(1, $Res.windowHours)
    $runSlo = $null
    $runSloAvailable = $false
    if (Test-Path -LiteralPath (Join-Path $samplerDataDir "slo-availability.csv")) {
        $runSettings = @{ DataDir = $samplerDataDir; WindowHours = $window }
        $runSlo = Get-SloSummary -Settings $runSettings
        if ($null -ne $runSlo -and $runSlo.samplesTotal -gt 0) { $runSloAvailable = $true }
        if ($runSloAvailable) { Write-SloSummary -Settings $runSettings -Summary $runSlo }
    }

    $continuousSlo = $null
    $continuousDataDir = ""
    $monitorConfig = Resolve-MonitorConfig $Res
    if ($monitorConfig) {
        try {
            $mcfg = Get-Content -Raw -LiteralPath $monitorConfig | ConvertFrom-Json
            if ($mcfg.DataDir) {
                $continuousDataDir = [string]$mcfg.DataDir
                if (Test-Path -LiteralPath (Join-Path $continuousDataDir "slo-availability.csv")) {
                    $contSettings = @{ DataDir = $continuousDataDir; WindowHours = $window }
                    $continuousSlo = Get-SloSummary -Settings $contSettings
                }
            }
        } catch { }
    }

    $completed = [int]$stressJson.completedSales
    $failures = [int]$stressJson.saleFailures
    $elapsed = [double]$stressJson.elapsedSeconds
    $ventasPerMin = if ($elapsed -gt 0) { [Math]::Round(($completed / ($elapsed / 60.0)), 2) } else { 0 }

    $endpointRows = @()
    foreach ($prop in $stressJson.metrics.PSObject.Properties) {
        $entry = $prop.Value
        $statusStr = ($entry.status.PSObject.Properties | ForEach-Object { "$($_.Name):$($_.Value)" }) -join "; "
        $endpointRows += [pscustomobject]@{
            endpoint = $prop.Name
            n        = [int]$entry.n
            ok       = [int]$entry.ok
            fail     = [int]$entry.fail
            avgMs    = $entry.avgMs
            p95Ms    = $entry.p95Ms
            p99Ms    = $entry.p99Ms
            status   = $statusStr
        }
    }

    $diagnosis = Build-HttpDiagnosis $stressJson

    $profilePath = Join-Path $RunDir "run-profile.json"
    $profile = if (Test-Path -LiteralPath $profilePath) { Get-Content -Raw -LiteralPath $profilePath | ConvertFrom-Json } else { $null }
    $envRate = if ($profile -and $profile.rate) { [Math]::Round([double]$profile.rate, 2) }
        elseif ($stressJson.rate) { [Math]::Round([double]$stressJson.rate, 2) }
        else { 0 }
    $envName = if ($profile -and $profile.environmentName) { [string]$profile.environmentName }
        elseif ($Res.environmentName) { [string]$Res.environmentName }
        else { "staging" }
    $envBaseUrl = if ($profile -and $profile.baseUrl) { [string]$profile.baseUrl }
        elseif ($stressJson.baseUrl) { [string]$stressJson.baseUrl }
        else { [string]$Res.baseUrl }
    $pTx = if ($profile) { [int]$profile.transactions } else { $Res.transactions }
    $pDur = if ($profile) { [int]$profile.duration } else { $Res.duration }
    $pCashiers = if ($profile) { [int]$profile.cashiers } else { $Res.cashiers }
    $pThinkMin = if ($profile) { [double]$profile.thinkMin } else { $Res.thinkMin }
    $pThinkMax = if ($profile) { [double]$profile.thinkMax } else { $Res.thinkMax }
    $pQtyMax = if ($profile) { [int]$profile.qtyMax } else { $Res.qtyMax }
    $pMaxPerSale = if ($profile) { [int]$profile.maxProductsPerSale } else { $Res.maxProductsPerSale }
    $pFilter = if ($profile) { [string]$profile.productFilter } else { $Res.productFilter }

    $report = [ordered]@{
        generatedAt = (Get-Date).ToString("o")
        action      = "report"
        runId       = (Split-Path $RunDir -Leaf)
        environment = [ordered]@{
            name    = $envName
            baseUrl = $envBaseUrl
            rate    = $envRate
            user    = [string]$stressJson.user
            cashiers = $pCashiers
        }
        stress      = [ordered]@{
            completedSales     = $completed
            saleFailures       = $failures
            relogins           = [int]$stressJson.relogins
            elapsedSeconds     = $elapsed
            salesPerMinute     = $ventasPerMin
            transactionsTarget = $pTx
            durationTarget     = $pDur
            thinkMin           = $pThinkMin
            thinkMax           = $pThinkMax
            qtyMax             = $pQtyMax
            maxProductsPerSale = $pMaxPerSale
            filter             = $pFilter
        }
        endpoints   = $endpointRows
        httpDiagnosis = $diagnosis
        sloWindow   = [ordered]@{
            samplesTotal = if ($runSlo) { [int]$runSlo.samplesTotal } else { 0 }
            samplesOk    = if ($runSlo) { [int]$runSlo.samplesOk } else { 0 }
            availabilityPct = if ($runSlo -and $null -ne $runSlo.availabilityPct) { $runSlo.availabilityPct } else { $null }
            probeLatencyP95Ms = if ($runSlo) { $runSlo.probeLatencyP95Ms } else { $null }
            endpointP95MaxMs  = if ($runSlo) { $runSlo.endpointP95MaxMs } else { $null }
        }
        sloContinuo = [ordered]@{
            dataDir = $continuousDataDir
            generatedAt = if ($continuousSlo) { [string]$continuousSlo.generatedAt } else { $null }
            availabilityPct = if ($continuousSlo) { $continuousSlo.availabilityPct } else { $null }
            probeLatencyP95Ms = if ($continuousSlo) { $continuousSlo.probeLatencyP95Ms } else { $null }
            endpointP95MaxMs = if ($continuousSlo) { $continuousSlo.endpointP95MaxMs } else { $null }
        }
        files       = [ordered]@{
            runDir       = $RunDir
            stressJson   = $stressPath
            monitoring   = $samplerDataDir
            latenciesCsv = (Join-Path $RunDir "endpoint-latencies.csv")
            reportMd     = (Join-Path $RunDir "report.md")
        }
    }

    $report["sloWindowComputed"] = $runSloAvailable

    $latRows = @("endpoint,n,ok,fail,avgMs,p95Ms,p99Ms,status")
    $latRows += $endpointRows | ForEach-Object {
        "{0},{1},{2},{3},{4},{5},{6},{7}" -f $_.endpoint, $_.n, $_.ok, $_.fail,
            ([double]$_.avgMs).ToString($invariant),
            ([double]$_.p95Ms).ToString($invariant),
            ([double]$_.p99Ms).ToString($invariant),
            $_.status
    }
    Set-Content -LiteralPath (Join-Path $RunDir "endpoint-latencies.csv") -Value ($latRows -join [Environment]::NewLine) -Encoding UTF8
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunDir "report.json") -Encoding UTF8

    $md = [System.Text.StringBuilder]::new()
    [void]$md.AppendLine("# Reporte de prueba POS - $($report.runId)")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("Generado: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | Entorno: **$envName** ($envBaseUrl) | Tasa BCV: **$envRate**")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## Resumen de estres")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| Metrica | Valor |")
    [void]$md.AppendLine("|---|---|")
    [void]$md.AppendLine("| Ventas completadas | $completed |")
    [void]$md.AppendLine("| Fallos de venta | $failures |")
    [void]$md.AppendLine("| Re-logins | $([int]$stressJson.relogins) |")
    [void]$md.AppendLine("| Duracion | $([Math]::Round($elapsed, 1)) s |")
    [void]$md.AppendLine("| Rendimiento | $ventasPerMin ventas/min |")
    [void]$md.AppendLine("| Parametros | $pCashiers cajas, qty 1-$pQtyMax, think $pThinkMin-$pThinkMax s |")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## Latencias por endpoint (ms)")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| Endpoint | N | OK | FAIL | Avg | p95 | p99 | HTTP |")
    [void]$md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|")
    foreach ($row in $endpointRows) {
        [void]$md.AppendLine("| $($row.endpoint) | $($row.n) | $($row.ok) | $($row.fail) | $($row.avgMs) | $($row.p95Ms) | $($row.p99Ms) | $($row.status) |")
    }
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## Diagnostico HTTP")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| Codigo | Cantidad |")
    [void]$md.AppendLine("|---|---:|")
    foreach ($key in @($report.httpDiagnosis.Keys)) {
        if ($key -eq "hints") { continue }
        $value = $report.httpDiagnosis[$key]
        [void]$md.AppendLine("| $key | $($value | ConvertTo-Json -Compress) |")
    }
    foreach ($hint in @($report.httpDiagnosis["hints"])) {
        [void]$md.AppendLine("- $hint")
    }
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## SLO en la ventana de la corrida (muestreador dedicado)")
    [void]$md.AppendLine("")
    if ($runSloAvailable) {
        [void]$md.AppendLine("| Metrica | Valor |")
        [void]$md.AppendLine("|---|---|")
        [void]$md.AppendLine("| Disponibilidad | $($report.sloWindow.availabilityPct)% ($($report.sloWindow.samplesOk)/$($report.sloWindow.samplesTotal) muestras) |")
        [void]$md.AppendLine("| p95 sonda | $($report.sloWindow.probeLatencyP95Ms) ms |")
        [void]$md.AppendLine("| p95 max por endpoint | $($report.sloWindow.endpointP95MaxMs) ms (meta 500 ms) |")
    } else {
        [void]$md.AppendLine("Sin muestras del muestreador en esta corrida (configure monitoring.monitorConfig).")
    }
    [void]$md.AppendLine("")
    if ($continuousSlo) {
        [void]$md.AppendLine("## SLO continuo (DataDir del monitor, no duplica la corrida)")
        [void]$md.AppendLine("")
        [void]$md.AppendLine("| Metrica | Valor |")
        [void]$md.AppendLine("|---|---|")
        [void]$md.AppendLine("| Disponibilidad | $($continuousSlo.availabilityPct)% |")
        [void]$md.AppendLine("| p95 sonda | $($continuousSlo.probeLatencyP95Ms) ms |")
        [void]$md.AppendLine("| p95 max por endpoint | $($continuousSlo.endpointP95MaxMs) ms |")
        [void]$md.AppendLine("")
    }
    [void]$md.AppendLine("## Archivos")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("- ``$($report.files.stressJson)``")
    [void]$md.AppendLine("- ``$($report.files.monitoring)`` (slo-availability.csv, slo-endpoint-p95.csv, slo-summary.json)")
    [void]$md.AppendLine("- ``$($report.files.latenciesCsv)``")
    [void]$md.AppendLine("")
    Set-Content -LiteralPath (Join-Path $RunDir "report.md") -Value $md.ToString() -Encoding UTF8

    return $report
}

function Write-CampaignSummary {
    param($Report, [string]$RunDir)
    Write-Step "RESUMEN DE LA CORRIDA"
    $stress = $Report.stress
    Write-Host "Entorno: $($Report.environment.name) ($($Report.environment.baseUrl))" -ForegroundColor Green
    Write-Host "Ventas: $($stress.completedSales) | Fallos: $($stress.saleFailures) | Re-logins: $($stress.relogins)" -ForegroundColor Green
    Write-Host "Rendimiento: $($stress.salesPerMinute) ventas/min en $($stress.elapsedSeconds) s" -ForegroundColor Green
    Write-Host "SLO ventana: disponibilidad=$($Report.sloWindow.availabilityPct)% p95_sonda=$($Report.sloWindow.probeLatencyP95Ms)ms p95_max_endpoint=$($Report.sloWindow.endpointP95MaxMs)ms" -ForegroundColor Green
    Write-Host "HTTP 429=$($Report.httpDiagnosis.status429) 409=$($Report.httpDiagnosis.status409)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "EVIDENCIA (registro diario):" -ForegroundColor Cyan
    Write-Host "  Prueba de estres $($Report.environment.name) - corrida $($Report.runId)"
    Write-Host "  - Entorno: $($Report.environment.baseUrl) | tasa $($Report.environment.rate) | ventas $($stress.completedSales) | fallos $($stress.saleFailures) | re-logins $($stress.relogins)"
    Write-Host "  - Rendimiento: $($stress.salesPerMinute) ventas/min en $($stress.elapsedSeconds) s"
    Write-Host "  - SLO ventana: disponibilidad $($Report.sloWindow.availabilityPct)% (de $($Report.sloWindow.samplesTotal) muestras) | p95 sonda $($Report.sloWindow.probeLatencyP95Ms)ms | p95 max endpoint $($Report.sloWindow.endpointP95MaxMs)ms"
    Write-Host "  - HTTP: 429=$($Report.httpDiagnosis.status429) 409=$($Report.httpDiagnosis.status409)"
    Write-Host "  - Evidencia: $RunDir (report.json, report.md, stress.json, endpoint-latencies.csv)" -ForegroundColor Green
}

function Invoke-Campaign {
    param([hashtable]$Res)
    $rate = Test-StagingPreflight $Res
    $Res["rate"] = $rate
    if (-not $Res.noProvision) { Invoke-Provision $Res }
    $runDir = New-RunFolder $Res
    $samplerInfo = $null
    if (-not $Res.noMonitor) { $samplerInfo = Start-TestSampler $Res $runDir }
    $outPath = $null
    try {
        $outPath = Invoke-StressRun $Res $runDir
    } finally {
        if ($samplerInfo) { Stop-TestSampler $samplerInfo }
    }
    $report = Build-Report $Res $runDir $samplerInfo
    Write-CampaignSummary $report $runDir
    return [pscustomobject]@{ RunDir = $runDir; ReportPath = (Join-Path $runDir "report.json") }
}

# ---------------------------------------------------------------------
# Menu interactivo
# ---------------------------------------------------------------------
function Ensure-AdminPassword {
    param([hashtable]$Res)
    $attempts = 0
    while ($true) {
        if (-not $Res.adminPassword) {
            $Res.adminPassword = Resolve-Password "" @("Stress", "AdminPassword") "POS_TEST_ADMIN_PASSWORD"
        }
        try { Login-Admin $Res | Out-Null; return } catch {
            $attempts++
            Write-Host "Contrasena invalida ($($Res.adminUser)): $($_.Exception.Message)" -ForegroundColor Yellow
            $Res.adminPassword = $null
            if ($attempts -ge 3) {
                throw "No se pudo autenticar como '$($Res.adminUser)' tras $attempts intentos. Corrija POS_TEST_ADMIN_PASSWORD, Stress.AdminPassword o la configuracion."
            }
            $secure = Read-Host "Reingrese la contrasena de $($Res.adminUser)" -AsSecureString
            if ($null -ne $secure -and $secure.Length -gt 0) {
                $Res.adminPassword = [System.Net.NetworkCredential]::new("", $secure).Password
            }
        }
    }
}

function Ensure-StressPassword {
    param([hashtable]$Res)
    if (-not $Res.stressPassword) {
        $Res.stressPassword = Resolve-Password "" @("Stress", "StressPassword") "POS_TEST_STRESS_PASSWORD"
    }
}

function Show-CurrentConfig {
    param([hashtable]$Res)
    Write-Host ""
    Write-Host "--- Configuracion actual ---" -ForegroundColor Cyan
    Write-Host "  Entorno:      $($Res.environmentName)"
    Write-Host "  Backend:      $($Res.baseUrl)"
    Write-Host "  Admin:        $($Res.adminUser)"
    Write-Host "  Stress user:  $($Res.stressUser)"
    Write-Host "  Transacciones: $($Res.transactions)"
    Write-Host "  Cajas:        $($Res.cashiers)"
    Write-Host "  Qty:          $($Res.qtyMin)-$($Res.qtyMax)"
    Write-Host "  Think:        $($Res.thinkMin)-$($Res.thinkMax) s"
    Write-Host "  Productos:    $($Res.productCount) (filter: $($Res.productFilter))"
    Write-Host "  Restock:      $($Res.restockAmount)"
    Write-Host "  Resultados:   $($Res.resultsDir)"
    Write-Host "  Sampler:      $($Res.samplerSeconds) s | Ventana: $($Res.windowHours) h"
    Write-Host "--------------------------------" -ForegroundColor Cyan
}

function Show-EditConfig {
    param([hashtable]$Res)

    Write-Host ""
    Write-Host "--- Configurar parametros de prueba ---" -ForegroundColor Cyan
    Write-Host "  (Enter mantiene el valor actual)" -ForegroundColor DarkGray
    Write-Host ""

    $fields = @(
        @{ Key = "cashiers";          Label = "Cajas (hilos concurrentes)"; Current = $Res.cashiers; Type = "int" },
        @{ Key = "thinkMin";          Label = "Think min (segundos)";      Current = $Res.thinkMin; Type = "double" },
        @{ Key = "thinkMax";          Label = "Think max (segundos)";      Current = $Res.thinkMax; Type = "double" },
        @{ Key = "qtyMin";            Label = "Qty min por venta";         Current = $Res.qtyMin;   Type = "int" },
        @{ Key = "qtyMax";            Label = "Qty max por venta";         Current = $Res.qtyMax;   Type = "int" },
        @{ Key = "productCount";      Label = "Productos de prueba";       Current = $Res.productCount; Type = "int" },
        @{ Key = "restockAmount";     Label = "Restock (unidades)";        Current = $Res.restockAmount; Type = "long" },
        @{ Key = "transactions";      Label = "Transacciones (0=auto)";    Current = $Res.transactions; Type = "int" },
        @{ Key = "productFilter";     Label = "Prefijo SKU";               Current = $Res.productFilter; Type = "string" }
    )

    foreach ($f in $fields) {
        $currentStr = "$($f.Current)"
        $raw = Read-Host "  $($f.Label) [$currentStr]"
        if ([string]::IsNullOrWhiteSpace($raw)) { continue }
        switch ($f.Type) {
            "int"    { $parsed = 0; if ([int]::TryParse($raw, [ref]$parsed)) { $Res[$f.Key] = $parsed } else { Write-Host "    (valor invalido, se mantiene: $($f.Current))" -ForegroundColor Yellow } }
            "long"   { $parsedLong = [long]0; if ([long]::TryParse($raw, [ref]$parsedLong)) { $Res[$f.Key] = $parsedLong } else { Write-Host "    (valor invalido, se mantiene: $($f.Current))" -ForegroundColor Yellow } }
            "double" { $parsed = 0.0; if ([double]::TryParse($raw, [ref]$parsed)) { $Res[$f.Key] = $parsed } else { Write-Host "    (valor invalido, se mantiene: $($f.Current))" -ForegroundColor Yellow } }
            "string" { $Res[$f.Key] = $raw }
        }
    }

    if ($Res.thinkMax -lt $Res.thinkMin) {
        Write-Host "  AVISO: thinkMax ($($Res.thinkMax)) < thinkMin ($($Res.thinkMin)). Se ajusta thinkMax." -ForegroundColor Yellow
        $Res.thinkMax = $Res.thinkMin
    }
    if ($Res.qtyMax -lt $Res.qtyMin) {
        Write-Host "  AVISO: qtyMax ($($Res.qtyMax)) < qtyMin ($($Res.qtyMin)). Se ajusta qtyMax." -ForegroundColor Yellow
        $Res.qtyMax = $Res.qtyMin
    }

    $cfgPath = if ($Config) { $Config } else { Join-Path $PSScriptRoot "pos-test-config.json" }
    $save = Read-Host "  Guardar en $(Split-Path $cfgPath -Leaf)? (s/N)"
    if ($save -eq "s" -or $save -eq "S" -or $save -eq "si" -or $save -eq "SI") {
        $existing = if (Test-Path -LiteralPath $cfgPath) {
            Get-Content -Raw -LiteralPath $cfgPath | ConvertFrom-Json
        } else { [pscustomobject]@{} }

        if (-not $existing.PSObject.Properties["profile"]) {
            $existing | Add-Member -NotePropertyName "profile" -NotePropertyValue ([pscustomobject]@{}) -Force
        }
        $p = $existing.profile
        foreach ($f in $fields) {
            $val = $Res[$f.Key]
            if ($p.PSObject.Properties[$f.Key]) { $p.$($f.Key) = $val }
            else { $p | Add-Member -NotePropertyName $f.Key -NotePropertyValue $val -Force }
        }
        $existing | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $cfgPath -Encoding UTF8
        Write-Host "  Configuracion guardada en $cfgPath" -ForegroundColor Green
    }

    Write-Host ""
    Show-CurrentConfig $Res
}

function Show-Menu {
    param([hashtable]$Res)

    while ($true) {
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "  Control de Pruebas POS (rev 8.120)" -ForegroundColor Cyan
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "  Entorno: $($Res.environmentName) | $($Res.baseUrl)" -ForegroundColor Gray
        Write-Host "----------------------------------------" -ForegroundColor DarkGray
        Write-Host "  1.  Pre-flight (verificar staging)" -ForegroundColor White
        Write-Host "  2.  Provisionar staging" -ForegroundColor White
        Write-Host "  3.  Ejecutar prueba de estres (finalizar + reporte)" -ForegroundColor White
        Write-Host "  4.  Campaign completa (provision+stress+reporte)" -ForegroundColor Green
        Write-Host "  5.  Monitorear salud (sonda headless)" -ForegroundColor White
        Write-Host "  6.  Generar reporte desde corrida existente" -ForegroundColor White
        Write-Host "  7.  Limpiar ventas de estres" -ForegroundColor Yellow
        Write-Host "  8.  Limpiar productos de prueba" -ForegroundColor Yellow
        Write-Host "  9.  Mostrar configuracion actual" -ForegroundColor White
        Write-Host "  10. Configurar parametros de prueba" -ForegroundColor Green
        Write-Host "  11. Ultimas corridas (results)" -ForegroundColor White
        Write-Host "  0.  Salir" -ForegroundColor Red
        Write-Host "========================================" -ForegroundColor Cyan
        $choice = Read-Host "Opcion"

        if ($choice -eq "0" -or $choice -eq "q" -or $choice -eq "Q") {
            Write-Host "Adios." -ForegroundColor Green
            return
        }

        try {
            switch ($choice) {
                "1" {
                    Ensure-AdminPassword $Res
                    Test-StagingPreflight $Res | Out-Null
                    Write-Host "Preflight OK." -ForegroundColor Green
                }
                "2" {
                    Ensure-AdminPassword $Res
                    Test-StagingPreflight $Res | Out-Null
                    Invoke-Provision $Res | Out-Null
                }
                "3" {
                    Ensure-StressPassword $Res
                    $runDir = New-RunFolder $Res
                    $samplerInfo = $null
                    if (-not $Res.noMonitor) { $samplerInfo = Start-TestSampler $Res $runDir }
                    try {
                        Invoke-StressRunFinalizable $Res $runDir | Out-Null
                    } catch {
                        Write-Host ""
                        Write-Host "Prueba interrumpida o con error: $($_.Exception.Message)" -ForegroundColor Yellow
                    } finally {
                        if ($samplerInfo) { Stop-TestSampler $samplerInfo }
                    }
                    if (Test-Path -LiteralPath (Join-Path $runDir "stress.json")) {
                        $report = Build-Report $Res $runDir $samplerInfo
                        Write-CampaignSummary $report $runDir
                    } else {
                        Write-Host "No se genero stress.json; no hay datos para reportar." -ForegroundColor Yellow
                        Write-Host "Corrida: $runDir" -ForegroundColor DarkGray
                    }
                }
                "4" {
                    Ensure-AdminPassword $Res
                    Ensure-StressPassword $Res
                    Invoke-Campaign $Res | Out-Null
                }
                "5" {
                    $monitorConfig = Resolve-MonitorConfig $Res
                    if (-not $monitorConfig) {
                        Write-Host "No se encontro monitor-config.json; se omite el muestreador." -ForegroundColor Yellow
                    } else {
                        $monitorScript = Join-Path $repoRoot "docs\monitor-health.ps1"
                        if ($Dashboard) { & $monitorScript -Dashboard -Config $monitorConfig }
                        else { & $monitorScript -Config $monitorConfig }
                    }
                }
                "6" {
                    if (-not (Test-Path -LiteralPath $Res.resultsDir)) {
                        Write-Host "Directorio no existe: $($Res.resultsDir)" -ForegroundColor Yellow
                        break
                    }
                    $runs = Get-ChildItem -Directory -LiteralPath $Res.resultsDir -ErrorAction SilentlyContinue |
                        Sort-Object Name -Descending | Select-Object -First 20
                    if (-not $runs) {
                        Write-Host "(sin corridas en $($Res.resultsDir))" -ForegroundColor Yellow
                        break
                    }
                    Write-Host ""
                    Write-Host "Corridas disponibles:" -ForegroundColor Cyan
                    for ($i = 0; $i -lt $runs.Count; $i++) {
                        $hasStress = Test-Path -LiteralPath (Join-Path $runs[$i].FullName "stress.json")
                        $hasReport = Test-Path -LiteralPath (Join-Path $runs[$i].FullName "report.json")
                        $markS = if ($hasStress) { "S" } else { " " }
                        $markR = if ($hasReport) { "R" } else { " " }
                        $color = if ($hasReport) { "Green" } elseif ($hasStress) { "Yellow" } else { "Gray" }
                        Write-Host ("  {0,2}. [{1}{2}] {3}" -f ($i + 1), $markS, $markR, $runs[$i].Name) -ForegroundColor $color
                    }
                    Write-Host "  [SR] = stress + reporte | [S ] = solo stress | [  ] = incompleta" -ForegroundColor DarkGray
                    Write-Host ""
                    $pick = Read-Host "Seleccione corrida (numero o Enter para cancelar)"
                    if (-not $pick) { break }
                    $idx = 0
                    if (-not [int]::TryParse($pick, [ref]$idx) -or $idx -lt 1 -or $idx -gt $runs.Count) {
                        Write-Host "Entrada no valida." -ForegroundColor Yellow
                        break
                    }
                    $runDir = $runs[$idx - 1].FullName
                    if (-not (Test-Path -LiteralPath (Join-Path $runDir "stress.json"))) {
                        Write-Host "La corrida no tiene stress.json; ejecute la prueba de estres primero." -ForegroundColor Yellow
                        break
                    }
                    $report = Build-Report $Res $runDir $null
                    Write-Host "Reporte regenerado: $(Join-Path $runDir 'report.md')" -ForegroundColor Green
                }
                "7" {
                    $cleanupScript = Join-Path $PSScriptRoot "cleanup-stress-data.ps1"
                    $cleanupSku = if ($SkuPrefix) { $SkuPrefix } else { $Res.productFilter }
                    $cleanupArgs = @{ CashierCedula = $Res.stressUser; SkuPrefix = $cleanupSku }
                    if ($Res.connectionString) { $cleanupArgs.ConnectionString = $Res.connectionString }
                    if ($SecretsFile) { $cleanupArgs.SecretsFile = $SecretsFile }
                    & $cleanupScript @cleanupArgs
                }
                "8" {
                    $cleanupScript = Join-Path $PSScriptRoot "cleanup-stress-data.ps1"
                    $cleanupSku = if ($SkuPrefix) { $SkuPrefix } else { $Res.productFilter }
                    Write-Host ""
                    Write-Host "  ATENCION: se borraran los productos de prueba (SKU '$cleanupSku')." -ForegroundColor Yellow
                    $confirmDelete = Read-Host "  Escriba YES para confirmar"
                    if ($confirmDelete -ne "YES") {
                        Write-Host "  Cancelado." -ForegroundColor Gray
                    } else {
                        $cleanupArgs = @{ SkuPrefix = $cleanupSku; DeleteProducts = $true; Confirm = "YES" }
                        if ($Res.connectionString) { $cleanupArgs.ConnectionString = $Res.connectionString }
                        if ($SecretsFile) { $cleanupArgs.SecretsFile = $SecretsFile }
                        & $cleanupScript @cleanupArgs
                    }
                }
                "9" { Show-CurrentConfig $Res }
                "10" { Show-EditConfig $Res }
                "11" {
                    if (Test-Path -LiteralPath $Res.resultsDir) {
                        $runs = Get-ChildItem -Directory -LiteralPath $Res.resultsDir -ErrorAction SilentlyContinue |
                            Sort-Object Name -Descending | Select-Object -First 10
                        if ($runs) {
                            Write-Host ""
                            Write-Host "Ultimas 10 corridas:" -ForegroundColor Cyan
                            foreach ($r in $runs) {
                                $hasStress = Test-Path -LiteralPath (Join-Path $r.FullName "stress.json")
                                $hasReport = Test-Path -LiteralPath (Join-Path $r.FullName "report.json")
                                $markS = if ($hasStress) { "S" } else { " " }
                                $markR = if ($hasReport) { "R" } else { " " }
                                $color = if ($hasReport) { "Green" } elseif ($hasStress) { "Yellow" } else { "Gray" }
                                Write-Host "  [$markS$markR] $($r.Name)" -ForegroundColor $color
                            }
                            Write-Host ""
                            Write-Host "  [SR] = stress + reporte | [S ] = solo stress | [  ] = incompleta" -ForegroundColor DarkGray
                        } else { Write-Host "(sin corridas en $($Res.resultsDir))" -ForegroundColor Gray }
                    } else { Write-Host "(directorio no existe: $($Res.resultsDir))" -ForegroundColor Gray }
                }
                default { Write-Host "Opcion no valida: $choice" -ForegroundColor Yellow }
            }
        } catch {
            Write-Host ""
            Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
        }

        Write-Host ""
        Write-Host "Presione Enter para continuar..." -ForegroundColor DarkGray
        Read-Host
    }
}

# ---------------------------------------------------------------------
# Resolucion de configuracion (CLI tiene precedencia)
# ---------------------------------------------------------------------
$cfgPath = $Config
if (-not $cfgPath -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot "pos-test-config.json"))) {
    $cfgPath = Join-Path $PSScriptRoot "pos-test-config.json"
}
if ($cfgPath) {
    if (-not (Test-Path -LiteralPath $cfgPath)) { throw "No se encontro la configuracion '$cfgPath'." }
    $cfg = Get-Content -Raw -LiteralPath $cfgPath | ConvertFrom-Json
} else {
    $cfg = $null
}

if (-not $SecretsFile) { $SecretsFile = Join-Path $repoRoot "Backend.API\secrets.json" }
$secrets = $null
if (Test-Path -LiteralPath $SecretsFile) {
    try { $secrets = Get-Content -Raw -LiteralPath $SecretsFile | ConvertFrom-Json } catch { $secrets = $null }
}

$resolvedBaseUrl = PickString $BaseUrl @("environment", "baseUrl")
if (-not $resolvedBaseUrl) { throw "No se especifico el backend de staging (-BaseUrl o environment.baseUrl en la configuracion)." }
$resolvedConfirm = PickString $ConfirmStaging @("environment", "confirmStaging")
if (-not $resolvedConfirm) { $resolvedConfirm = $resolvedBaseUrl }
if ($resolvedConfirm -ne "YES" -and $resolvedConfirm -ne $resolvedBaseUrl) {
    throw "confirmStaging ('$resolvedConfirm') no coincide con baseUrl ('$resolvedBaseUrl'). Confirme el entorno de staging."
}

$needCredentials = $Action -in @("Preflight", "Provision", "Stress", "Campaign")
$needStressCredentials = $Action -in @("Stress", "Campaign")

$res = @{
    environmentName   = PickString $null @("environment", "name")
    baseUrl           = $resolvedBaseUrl
    confirmStaging    = $resolvedConfirm
    insecure          = $Insecure
    timeout           = PickDouble $Timeout @("profile", "timeout") 60.0
    adminUser         = PickString $AdminUser @("credentials", "adminUser")
    adminPassword     = Resolve-Password $AdminPassword @("Stress", "AdminPassword") "POS_TEST_ADMIN_PASSWORD" -NoPrompt:(-not $needCredentials)
    stressUser        = PickString $StressUser @("credentials", "stressUser")
    stressPassword    = Resolve-Password $StressPassword @("Stress", "StressPassword") "POS_TEST_STRESS_PASSWORD" -NoPrompt:(-not $needStressCredentials)
    transactions      = PickInt $Transactions @("profile", "transactions") 0
    duration          = PickInt $Duration @("profile", "duration") 0
    cashiers          = PickInt $Cashiers @("profile", "cashiers") 4
    qtyMin            = PickInt $QtyMin @("profile", "qtyMin") 1
    qtyMax            = PickInt $QtyMax @("profile", "qtyMax") 3
    thinkMin          = PickDouble $ThinkMin @("profile", "thinkMin") 4.0
    thinkMax          = PickDouble $ThinkMax @("profile", "thinkMax") 20.0
    maxProductsPerSale = PickInt $MaxProductsPerSale @("profile", "maxProductsPerSale") 0
    productCount      = PickInt $ProductCount @("profile", "productCount") 30
    restockAmount     = PickLong $RestockAmount @("profile", "restockAmount") 1000000
    productFilter     = PickString $ProductFilter @("profile", "productFilter")
    rate              = PickDouble $Rate @("profile", "rate") 0.0
    samplerSeconds    = PickInt $SamplerSeconds @("monitoring", "samplerSeconds") 5
    windowHours       = PickInt $WindowHours @("monitoring", "windowHours") 14
    monitorConfig     = PickString $MonitorConfig @("monitoring", "monitorConfig")
    resultsDir        = PickString $ResultsDir @("results", "dir")
    connectionString  = PickString $ConnectionString @("database", "connectionString")
    out               = $Out
    pyExe             = $PythonExe
    noProvision       = $NoProvision
    noMonitor         = $NoMonitor
}

if (-not $res.adminUser) { $res.adminUser = "Admin" }
if (-not $res.stressUser) { $res.stressUser = "BOT_STRESS_TEST" }
if (-not $res.productFilter) { $res.productFilter = "SKU-TEST" }
if (-not $res.resultsDir) { $res.resultsDir = Join-Path $repoRoot "results" }
elseif (-not [System.IO.Path]::IsPathRooted($res.resultsDir)) { $res.resultsDir = Join-Path $repoRoot $res.resultsDir }
if (-not $res.environmentName) { $res.environmentName = "staging" }
$res.connectionString = Resolve-ConnectionString $res.connectionString $res
if ($res.thinkMax -lt $res.thinkMin) { throw "thinkMax ($($res.thinkMax)) debe ser >= thinkMin ($($res.thinkMin))." }

Write-Host "Control de Pruebas POS (rev 8.120) - accion: $Action" -ForegroundColor Cyan
Write-Host "Entorno: $($res.environmentName) | BaseUrl: $($res.baseUrl)" -ForegroundColor Cyan

switch ($Action) {
    "Menu" {
        Show-Menu $res
        exit 0
    }
    "Preflight" {
        Test-StagingPreflight $res | Out-Null
        Login-Admin $res | Out-Null
        Write-Host "Preflight OK." -ForegroundColor Green
    }
    "Provision" {
        Test-StagingPreflight $res | Out-Null
        Invoke-Provision $res | Out-Null
    }
    "Stress" {
        $runDir = if ($RunId) { Resolve-RunDir $res } else { New-RunFolder $res }
        if (-not (Test-Path -LiteralPath (Join-Path $runDir "monitoring"))) {
            New-Item -ItemType Directory -Path (Join-Path $runDir "monitoring") -Force | Out-Null
        }
        Invoke-StressRun $res $runDir | Out-Null
        if (Test-Path -LiteralPath (Join-Path $runDir "stress.json")) {
            Write-Step "Carga completada; genere el reporte con: -Action Report -RunId $((Split-Path $runDir -Leaf))"
        }
    }
    "Monitor" {
        $monitorConfig = Resolve-MonitorConfig $res
        if (-not $monitorConfig) { throw "No se encontro monitor-config.json para la accion Monitor." }
        $monitorScript = Join-Path $repoRoot "docs\monitor-health.ps1"
        if ($Dashboard) {
            & $monitorScript -Dashboard -Config $monitorConfig
        }
        else {
            & $monitorScript -Config $monitorConfig
        }
    }
    "Campaign" {
        Invoke-Campaign $res | Out-Null
    }
    "Report" {
        $runDir = Resolve-RunDir $res
        $report = Build-Report $res $runDir $null
        Write-Host "Reporte regenerado: $(Join-Path $runDir 'report.md')" -ForegroundColor Green
        Write-Host "Reporte JSON: $(Join-Path $runDir 'report.json')" -ForegroundColor Green
    }
    "Cleanup" {
        $cleanupScript = Join-Path $PSScriptRoot "cleanup-stress-data.ps1"
        $cleanupSku = if ($SkuPrefix) { $SkuPrefix } else { $res.productFilter }
        $cleanupArgs = @{
            CashierCedula = $res.stressUser
            SkuPrefix     = $cleanupSku
        }
        if ($Res.connectionString) { $cleanupArgs.ConnectionString = $Res.connectionString }
        if ($SecretsFile) { $cleanupArgs.SecretsFile = $SecretsFile }
        if ($DeleteProducts) { $cleanupArgs.DeleteProducts = $true }
        if ($DeleteUser) { $cleanupArgs.DeleteUser = $true }
        if ($Confirm) { $cleanupArgs.Confirm = $Confirm }
        & $cleanupScript @cleanupArgs
        if ($LASTEXITCODE -ne 0) { throw "cleanup-stress-data.ps1 termino con codigo $LASTEXITCODE." }
        exit $LASTEXITCODE
    }
}