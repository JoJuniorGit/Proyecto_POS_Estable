<#
  monitor-health.ps1 - Monitoreo de salud y medicion SLO del POS CommandCenter.
  Consulta /health (y opcionalmente /api/health/details para la frescura del backup)
  y notifica por webhook cuando el servicio falla N veces consecutivas o el backup
  no esta fresco. Programar con el Programador de tareas (cada 5 min).

  Medicion SLO (opcional, parametro DataDir):
  - Cada corrida agrega una muestra al CSV de disponibilidad (timestamp, ok,
    statusCode, latencia) y, si se indican RequestsUrl+Token, las latencias p95
    por endpoint al CSV slo-p95.csv (P95Milliseconds expuesto por /api/health/requests).
  - Con -Summarize se computa la disponibilidad de la ventana (WindowHours) y el
    p95 maximo observado, escribiendo slo-summary.json para la evidencia de los
    SLO del piloto (roadmap 4.3).
#>
param(
    [string]$HealthUrl = "http://localhost:5000/health",
    [string]$DetailsUrl = "",
    [string]$Token = "",
    [string]$NotifyUrl = "",
    [int]$FailsToAlert = 3,
    [string]$StateFile = "$PSScriptRoot\health_state.txt",
    [string]$LogFile = "$PSScriptRoot\monitor.log",
    [string]$DataDir = "",
    [string]$RequestsUrl = "",
    [string]$EndpointFilter = "",
    [int]$WindowHours = 14,
    [switch]$Summarize
)
$ErrorActionPreference = "Stop"

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    try { Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8 } catch { }
    Write-Host $line
}

function Send-Notification {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($NotifyUrl)) { return }
    try {
        $body = @{ text = $Text } | ConvertTo-Json
        Invoke-RestMethod -Uri $NotifyUrl -Method Post -Body $body -ContentType "application/json" | Out-Null
        Write-Log "Notificacion enviada: $Text" "NOTIFY"
    } catch {
        Write-Log "Fallo al enviar notificacion: $($_.Exception.Message)" "ERROR"
    }
}

function Append-Csv {
    param([string]$Path, [string]$Header, [string]$Line)
    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            Set-Content -LiteralPath $Path -Value $Header -Encoding UTF8
        }
        Add-Content -LiteralPath $Path -Value $Line -Encoding UTF8
    } catch {
        Write-Log "No se pudo escribir $Path : $($_.Exception.Message)" "ERROR"
    }
}

function Get-Percentile {
    param([double[]]$Values, [double]$Pct)
    if ($Values.Length -eq 0) { return 0 }
    $sorted = @($Values | Sort-Object)
    $index = [math]::Ceiling($sorted.Length * $Pct) - 1
    if ($index -lt 0) { $index = 0 }
    return [math]::Round($sorted[$index], 2)
}

$fails = 0
if (Test-Path -LiteralPath $StateFile) {
    try { $fails = [int]((Get-Content -LiteralPath $StateFile -Raw).Trim()) } catch { $fails = 0 }
}

$healthy = $false
$statusCode = -1
$probeLatencyMs = 0
$healthResp = $null
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $healthResp = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 10 -StatusCodeVariable sc -ErrorAction Stop
    $statusCode = $sc
    $sw.Stop()
    $probeLatencyMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 2)
} catch {
    $sw.Stop()
    $probeLatencyMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 2)
    $fails++
    Write-Log "/health no disponible: $($_.Exception.Message)" "WARN"
}
if ($healthResp) {
    if ($healthResp.status -eq "Healthy" -and $healthResp.database -eq "Connected") {
        $healthy = $true
        $fails = 0
        Write-Log "OK: /health -> $($healthResp.status) / $($healthResp.database)"
    } else {
        $fails++
        Write-Log "/health devolvio status=$($healthResp.status), database=$($healthResp.database)" "WARN"
    }
}

$backupFresh = $true
$backupAgeMinutes = $null
if ($healthy -and -not [string]::IsNullOrWhiteSpace($DetailsUrl)) {
    try {
        $headers = @{ }
        if (-not [string]::IsNullOrWhiteSpace($Token)) { $headers["Authorization"] = "Bearer $Token" }
        $details = Invoke-RestMethod -Uri $DetailsUrl -Headers $headers -TimeoutSec 10 -ErrorAction Stop
        $backupFresh = ($details.lastBackupFresh -eq $true)
        $backupAgeMinutes = $details.lastBackupAgeMinutes
        if ($backupFresh) {
            Write-Log "Backup OK: ultimo a los $backupAgeMinutes min."
        } else {
            Write-Log "Backup NO fresco (age $backupAgeMinutes min)." "WARN"
        }
    } catch {
        Write-Log "No se pudo consultar /api/health/details: $($_.Exception.Message)" "WARN"
    }
}

if (-not [string]::IsNullOrWhiteSpace($DataDir)) {
    try { New-Item -ItemType Directory -Path $DataDir -Force | Out-Null } catch { }
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $availabilityCsv = Join-Path $DataDir "slo-availability.csv"
    Append-Csv -Path $availabilityCsv -Header "timestamp,ok,statusCode,latencyMs" -Line "$timestamp,$healthy,$statusCode,$probeLatencyMs"
    if (-not [string]::IsNullOrWhiteSpace($RequestsUrl) -and -not [string]::IsNullOrWhiteSpace($Token)) {
        try {
            $reqHeaders = @{ Authorization = "Bearer $Token" }
            $reqMetrics = Invoke-RestMethod -Uri $RequestsUrl -Headers $reqHeaders -TimeoutSec 10 -ErrorAction Stop
            $p95Csv = Join-Path $DataDir "slo-p95.csv"
            $rows = @($reqMetrics.endpoints)
            if ($rows.Count -gt 0) {
                if (-not [string]::IsNullOrWhiteSpace($EndpointFilter)) {
                    $rows = @($rows | Where-Object { $_.endpoint -like "*$EndpointFilter*" })
                }
                foreach ($e in $rows) {
                    Append-Csv -Path $p95Csv -Header "timestamp,endpoint,total,errors,avgMs,maxMs,p95Ms" -Line "$timestamp,$($e.endpoint),$($e.totalCount),$($e.errorCount),$([math]::Round($e.averageMilliseconds,2)),$([math]::Round($e.maxMilliseconds,2)),$($e.p95Milliseconds)"
                }
            }
            Write-Log "SLO p95: $($rows.Count) endpoint(s) registrados."
        } catch {
            Write-Log "No se pudo consultar /api/health/requests: $($_.Exception.Message)" "WARN"
        }
    }
}

if ($Summarize -and -not [string]::IsNullOrWhiteSpace($DataDir)) {
    $availabilityCsv = Join-Path $DataDir "slo-availability.csv"
    $availSamples = @()
    if (Test-Path -LiteralPath $availabilityCsv) {
        $availSamples = @(Import-Csv -LiteralPath $availabilityCsv | Where-Object { $_.timestamp -ne "" } | Where-Object { ([datetime]$_.timestamp) -ge (Get-Date).AddHours(-$WindowHours) })
    }
    $availabilityPct = 0
    $samplesTotal = $availSamples.Count
    $samplesOk = @($availSamples | Where-Object { $_.ok -eq "True" -or $_.ok -eq $true }).Count
    if ($samplesTotal -gt 0) { $availabilityPct = [math]::Round(($samplesOk / $samplesTotal) * 100, 2) }

    $latencies = @($availSamples | ForEach-Object { [double]$_.latencyMs })
    $latencyP95 = Get-Percentile -Values $latencies -Pct 0.95

    $p95Csv = Join-Path $DataDir "slo-p95.csv"
    $p95MaxMs = 0
    if (Test-Path -LiteralPath $p95Csv) {
        $p95Rows = @(Import-Csv -LiteralPath $p95Csv | Where-Object { $_.timestamp -ne "" } | Where-Object { ([datetime]$_.timestamp) -ge (Get-Date).AddHours(-$WindowHours) })
        if ($p95Rows.Count -gt 0) {
            $p95MaxMs = [math]::Round((@($p95Rows | ForEach-Object { [double]$_.p95Ms }) | Measure-Object -Maximum).Maximum, 2)
        }
    }

    $summary = [ordered]@{
        generatedAt = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
        windowHours = $WindowHours
        availabilityPct = $availabilityPct
        targetAvailabilityPct = 99.5
        samplesTotal = $samplesTotal
        samplesOk = $samplesOk
        probeLatencyP95Ms = $latencyP95
        endpointP95MaxMs = $p95MaxMs
        targetEndpointP95Ms = 500
        backupFresh = $backupFresh
        backupAgeMinutes = $backupAgeMinutes
    }
    $summaryPath = Join-Path $DataDir "slo-summary.json"
    try { $summary | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding UTF8 } catch { }
    Write-Log "SLO resumen (ventana $WindowHours h): disponibilidad=$availabilityPct% ($samplesOk/$samplesTotal), probe p95=$latencyP95 ms, endpoint p95 max=$p95MaxMs ms."
}

if ($fails -ge $FailsToAlert) {
    Send-Notification "POS ALERTA: /health no responde ($fails fallos consecutivos)."
    Write-Log "ALERTA: $fails fallos consecutivos." "ALERT"
}
if ($healthy -and -not $backupFresh) {
    Send-Notification "POS ALERTA: backup no fresco (RPO en riesgo)."
    Write-Log "ALERTA backup: no fresco." "ALERT"
}

try { Set-Content -LiteralPath $StateFile -Value $fails } catch { }