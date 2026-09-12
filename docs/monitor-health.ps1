<#
  monitor-health.ps1 - Monitoreo de salud y medicion SLO del POS CommandCenter.

  Consulta /health (y opcionalmente /api/health/details para la frescura del
  backup) y notifica por webhook cuando el servicio falla N veces consecutivas o
  el backup no esta fresco. Programar con el Programador de tareas (cada 5 min).

  Modo consola (headless, por defecto, para el Programador de tareas):
    - Cada corrida agrega una muestra al CSV de disponibilidad y, con DataDir y
      RequestsUrl+Token, la latencia p95 por endpoint; con -Summarize calcula la
      disponibilidad y el p95 de la ventana y escribe slo-summary.json.

  Modo dashboard (-Dashboard): abre una ventana WinForms con el estado en vivo,
  la frescura del backup, el resumen SLO vs metas, la tendencia historica y el
  log. La logica headless no cambia: el dashboard reutiliza las mismas funciones
  y presenta una vista de solo lectura (no agrega muestras ni envia alertas).

  Configuracion compartida: un archivo JSON (por defecto monitor-config.json
  junto al script) define los valores; los parametros de linea de comandos
  tienen precedencia. Ver monitor-config.json.example.

  Requisitos: Windows PowerShell 5.1 o PowerShell 7+.
#>
param(
    [string]$HealthUrl = "http://localhost:5000/health",
    [string]$DetailsUrl = "",
    [string]$Token = "",
    [string]$NotifyUrl = "",
    [int]$FailsToAlert = 3,
    [string]$StateFile = "",
    [string]$LogFile = "",
    [string]$DataDir = "",
    [string]$RequestsUrl = "",
    [string]$EndpointFilter = "",
    [int]$WindowHours = 14,
    [int]$RefreshSeconds = 15,
    [switch]$Summarize,
    [string]$Config = "",
    [switch]$Dashboard
)
$ErrorActionPreference = "Stop"

function Write-Log {
    param([string]$Message, [string]$Level = "INFO", [string]$LogFile = "")
    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    if (-not [string]::IsNullOrWhiteSpace($LogFile)) {
        try { Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8 } catch { }
    }
    Write-Host $line
}

function Send-Notification {
    param([string]$Text, [string]$NotifyUrl, [string]$LogFile)
    if ([string]::IsNullOrWhiteSpace($NotifyUrl)) { return }
    try {
        Invoke-RestMethod -Uri $NotifyUrl -Method Post -Body ($Text | ConvertTo-Json) `
            -ContentType "application/json" -TimeoutSec 10 | Out-Null
        Write-Log "Notificacion enviada: $Text" "INFO" $LogFile
    } catch {
        Write-Log "No se pudo enviar la notificacion: $($_.Exception.Message)" "WARN" $LogFile
    }
}

function Get-ErrorStatusCode {
    param($ErrorRecord)
    try {
        $response = $ErrorRecord.Exception.Response
        if ($response) {
            try { if ($response.StatusCode) { return [int]$response.StatusCode } } catch { }
            try { if ($response.StatusCode.value__) { return [int]$response.StatusCode.value__ } } catch { }
        }
    } catch { }
    return -1
}

function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = $Values | Sort-Object
    $rank = [Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    if ($rank -lt 0) { $rank = 0 }
    if ($rank -ge $sorted.Count) { $rank = $sorted.Count - 1 }
    return [Math]::Round($sorted[$rank], 1)
}

function Get-SloSummary {
    param([hashtable]$Settings, [bool]$BackupFresh = $true, $BackupAgeMinutes = $null)
    $summary = [ordered]@{
        generatedAt          = (Get-Date).ToString("o")
        windowHours          = $Settings.WindowHours
        availabilityPct      = $null
        targetAvailabilityPct = 99.5
        samplesTotal         = 0
        samplesOk            = 0
        probeLatencyP95Ms    = $null
        endpointP95MaxMs     = $null
        targetEndpointP95Ms  = 500
        backupFresh          = $BackupFresh
        backupAgeMinutes     = $BackupAgeMinutes
    }
    if ([string]::IsNullOrWhiteSpace($Settings.DataDir)) { return $summary }

    $cutoff = (Get-Date).AddHours(-1 * $Settings.WindowHours)
    $availabilityCsv = Join-Path $Settings.DataDir "slo-availability.csv"
    if (Test-Path -LiteralPath $availabilityCsv) {
        try {
            $rows = Import-Csv -LiteralPath $availabilityCsv
            $recent = @($rows | Where-Object {
                $ts = [datetime]::MinValue
                [datetime]::TryParse([string]$_.timestamp, [ref]$ts) -and $ts -ge $cutoff
            })
            if ($recent.Count -gt 0) {
                $ok = @($recent | Where-Object { [string]$_.ok -eq "True" -or [string]$_.ok -eq "1" -or [string]$_.ok -eq "true" })
                $summary.samplesTotal = $recent.Count
                $summary.samplesOk = $ok.Count
                $summary.availabilityPct = [Math]::Round(100.0 * $ok.Count / $recent.Count, 2)
                $probeValues = @($recent | Where-Object { $_.latencyMs } | ForEach-Object { [double]$_.latencyMs })
                if ($probeValues.Count -gt 0) {
                    $summary.probeLatencyP95Ms = Get-Percentile -Values $probeValues -Percentile 95
                }
            }
        } catch { }
    }

    $p95Csv = Join-Path $Settings.DataDir "slo-endpoint-p95.csv"
    if (Test-Path -LiteralPath $p95Csv) {
        try {
            $rows = Import-Csv -LiteralPath $p95Csv
            $recent = @($rows | Where-Object {
                $ts = [datetime]::MinValue
                [datetime]::TryParse([string]$_.timestamp, [ref]$ts) -and $ts -ge $cutoff
            })
            if ($recent.Count -gt 0) {
                $max = ($recent | ForEach-Object { [double]$_.p95MaxMs } | Measure-Object -Maximum).Maximum
                $summary.endpointP95MaxMs = [Math]::Round([double]$max, 1)
            }
        } catch { }
    }
    return $summary
}

function Write-SloSummary {
    param([hashtable]$Settings, $Summary)
    if ([string]::IsNullOrWhiteSpace($Settings.DataDir)) { return }
    try {
        $path = Join-Path $Settings.DataDir "slo-summary.json"
        $Summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding UTF8
    } catch { }
}

function Add-CsvRow {
    param([string]$Path, [string]$Header, [string]$Line, [string]$LogFile)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { Set-Content -LiteralPath $Path -Value $Header -Encoding UTF8 }
        Add-Content -LiteralPath $Path -Value $Line -Encoding UTF8
    } catch {
        Write-Log "No se pudo escribir el CSV '$Path': $($_.Exception.Message)" "WARN" $LogFile
    }
}

function Invoke-MonitorProbe {
    param([hashtable]$Settings, [switch]$Record, [switch]$WriteSummary)
    $LogFile = $Settings.LogFile
    $timestamp = Get-Date
    $healthy = $false
    $statusCode = -1
    $probeLatencyMs = 0
    $backupFresh = $true
    $backupAgeMinutes = $null
    $fails = 0

    if (Test-Path -LiteralPath $Settings.StateFile) {
        try { $fails = [int]((Get-Content -LiteralPath $Settings.StateFile -Raw).Trim()) } catch { $fails = 0 }
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $health = Invoke-RestMethod -Uri $Settings.HealthUrl -TimeoutSec $Settings.TimeoutSec -ErrorAction Stop
        $stopwatch.Stop()
        $probeLatencyMs = [int]$stopwatch.ElapsedMilliseconds
        $statusCode = 200
        $healthy = $true
        if ($health.database) {
            Write-Log "OK: /health -> $($health.status) / db=$($health.database) (${probeLatencyMs}ms)" "INFO" $LogFile
        } else {
            Write-Log "OK: /health -> $($health.status) (${probeLatencyMs}ms)" "INFO" $LogFile
        }
    } catch {
        $stopwatch.Stop()
        $probeLatencyMs = [int]$stopwatch.ElapsedMilliseconds
        $statusCode = Get-ErrorStatusCode -ErrorRecord $_
        Write-Log "FALLO: /health no disponible (status=$statusCode): $($_.Exception.Message)" "WARN" $LogFile
    }

    if ($Settings.DetailsUrl -and $Settings.Token) {
        try {
            $headers = @{ Authorization = "Bearer $($Settings.Token)" }
            $details = Invoke-RestMethod -Uri $Settings.DetailsUrl -Headers $headers -TimeoutSec $Settings.TimeoutSec -ErrorAction Stop
            if ($null -ne $details.lastBackupAgeMinutes) {
                $backupAgeMinutes = [int]$details.lastBackupAgeMinutes
                $backupFresh = $backupAgeMinutes -le 1560
                Write-Log "Backup: ultimo hace $backupAgeMinutes min (fresco=$backupFresh)" "INFO" $LogFile
            }
        } catch {
            Write-Log "No se pudo consultar la frescura del backup: $($_.Exception.Message)" "WARN" $LogFile
        }
    }

    if ($Record) {
        if (-not $healthy) { $fails++ } else { $fails = 0 }
        if ($healthy) {
            Write-Log "Servicio disponible." "INFO" $LogFile
        } elseif ($fails -ge $Settings.FailsToAlert) {
            Write-Log "Servicio no disponible $fails corridas consecutivas -> notificando." "ERROR" $LogFile
            Send-Notification "POS health DOWN ($fails fallos consecutivos)" $Settings.NotifyUrl $LogFile
        }
        if ($healthy -and -not $backupFresh -and $Settings.DetailsUrl -and $Settings.Token) {
            Send-Notification "POS backup no fresco (hace $backupAgeMinutes min)" $Settings.NotifyUrl $LogFile
        }
        try { Set-Content -LiteralPath $Settings.StateFile -Value $fails -Encoding UTF8 } catch { }
    }

    if ($Settings.DataDir) {
        if ($Record) {
            $okValue = if ($healthy) { "True" } else { "False" }
            $availabilityCsv = Join-Path $Settings.DataDir "slo-availability.csv"
            Add-CsvRow -Path $availabilityCsv -Header "timestamp,ok,statusCode,latencyMs" `
                -Line ("{0},{1},{2},{3}" -f $timestamp.ToString("o"), $okValue, $statusCode, $probeLatencyMs) -LogFile $LogFile

            if ($healthy -and $Settings.RequestsUrl -and $Settings.Token) {
                try {
                    $headers = @{ Authorization = "Bearer $($Settings.Token)" }
                    $requests = Invoke-RestMethod -Uri $Settings.RequestsUrl -Headers $headers -TimeoutSec $Settings.TimeoutSec -ErrorAction Stop
                    $filter = @($requests | Where-Object {
                        (-not $Settings.EndpointFilter) -or ([string]$_.endpoint -match $Settings.EndpointFilter)
                    })
                    $p95Rows = @($filter | ForEach-Object {
                        $vals = @($_.durationsMs | ForEach-Object { [double]$_ })
                        $p95 = Get-Percentile -Values $vals -Percentile 95
                        if ($null -ne $p95) { [pscustomobject]@{ endpoint = $_.endpoint; p95Ms = $p95 } }
                    })
                    if ($p95Rows.Count -gt 0) {
                        $p95Csv = Join-Path $Settings.DataDir "slo-endpoint-p95.csv"
                        $maxP95 = ($p95Rows | ForEach-Object { $_.p95Ms } | Measure-Object -Maximum).Maximum
                        $detail = ($p95Rows | ForEach-Object { "$($_.endpoint):$($_.p95Ms)" }) -join ";"
                        Add-CsvRow -Path $p95Csv -Header "timestamp,p95MaxMs,detail" `
                            -Line ("{0},{1},{2}" -f $timestamp.ToString("o"), [Math]::Round([double]$maxP95, 1), $detail) -LogFile $LogFile
                        Write-Log "p95 max por endpoint=${maxP95}ms." "INFO" $LogFile
                    }
                } catch {
                    Write-Log "No se pudieron leer las latencias por endpoint: $($_.Exception.Message)" "WARN" $LogFile
                }
            }
        }
        $summary = Get-SloSummary -Settings $Settings -BackupFresh $backupFresh -BackupAgeMinutes $backupAgeMinutes
        if ($WriteSummary) {
            Write-SloSummary -Settings $Settings -Summary $summary
            Write-Log "Resumen SLO: disponibilidad=$($summary.availabilityPct)% p95=$($summary.probeLatencyP95Ms)ms." "INFO" $LogFile
        }
    } else {
        $summary = $null
    }

    return [pscustomobject]@{
        Healthy          = $healthy
        StatusCode       = $statusCode
        ProbeLatencyMs   = $probeLatencyMs
        BackupFresh      = $backupFresh
        BackupAgeMinutes = $backupAgeMinutes
        Fails            = $fails
        Summary          = $summary
        Timestamp        = $timestamp
    }
}

# -- Resolucion de configuracion compartida (CLI tiene precedencia) ----------
$configPath = $Config
if ([string]::IsNullOrWhiteSpace($configPath)) { $configPath = Join-Path $PSScriptRoot "monitor-config.json" }
$configExists = Test-Path -LiteralPath $configPath
$configDir = if ($configExists) { Split-Path -Parent $configPath } else { $PSScriptRoot }
$configMap = @{}
if ($configExists) {
    try {
        $parsed = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        if ($parsed) {
            foreach ($property in $parsed.PSObject.Properties) { $configMap[$property.Name] = $property.Value }
        }
    } catch {
        Write-Host "Advertencia: no se pudo leer la configuracion '$configPath': $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Get-ConfigString {
    param([string]$Name, [string]$Current)
    if ($configMap.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace([string]$configMap[$Name])) {
        return [string]$configMap[$Name]
    }
    return $Current
}

function Get-ConfigInt {
    param([string]$Name, [int]$Current)
    if ($configMap.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace([string]$configMap[$Name])) {
        try { return [int]$configMap[$Name] } catch { return $Current }
    }
    return $Current
}

if (-not $PSBoundParameters.ContainsKey('HealthUrl')) { $HealthUrl = Get-ConfigString "HealthUrl" $HealthUrl }
if (-not $PSBoundParameters.ContainsKey('DetailsUrl')) { $DetailsUrl = Get-ConfigString "DetailsUrl" $DetailsUrl }
if (-not $PSBoundParameters.ContainsKey('Token')) { $Token = Get-ConfigString "Token" $Token }
if (-not $PSBoundParameters.ContainsKey('NotifyUrl')) { $NotifyUrl = Get-ConfigString "NotifyUrl" $NotifyUrl }
if (-not $PSBoundParameters.ContainsKey('RequestsUrl')) { $RequestsUrl = Get-ConfigString "RequestsUrl" $RequestsUrl }
if (-not $PSBoundParameters.ContainsKey('EndpointFilter')) { $EndpointFilter = Get-ConfigString "EndpointFilter" $EndpointFilter }
if (-not $PSBoundParameters.ContainsKey('FailsToAlert')) { $FailsToAlert = Get-ConfigInt "FailsToAlert" $FailsToAlert }
if (-not $PSBoundParameters.ContainsKey('WindowHours')) { $WindowHours = Get-ConfigInt "WindowHours" $WindowHours }
if (-not $PSBoundParameters.ContainsKey('RefreshSeconds')) { $RefreshSeconds = Get-ConfigInt "RefreshSeconds" $RefreshSeconds }
if ($PSBoundParameters.ContainsKey('DataDir')) {
    # valor CLI explicito (puede ser vacio)
} else {
    $DataDir = Get-ConfigString "DataDir" ""
}
if ([string]::IsNullOrWhiteSpace($StateFile)) {
    $StateFile = Get-ConfigString "StateFile" ""
    if ([string]::IsNullOrWhiteSpace($StateFile)) { $StateFile = Join-Path $configDir "health_state.txt" }
}
if ([string]::IsNullOrWhiteSpace($LogFile)) {
    $LogFile = Get-ConfigString "LogFile" ""
    if ([string]::IsNullOrWhiteSpace($LogFile)) { $LogFile = Join-Path $configDir "monitor.log" }
}

$settings = @{
    HealthUrl      = $HealthUrl
    DetailsUrl     = $DetailsUrl
    Token          = $Token
    NotifyUrl      = $NotifyUrl
    FailsToAlert   = $FailsToAlert
    StateFile      = $StateFile
    LogFile        = $LogFile
    DataDir        = $DataDir
    RequestsUrl    = $RequestsUrl
    EndpointFilter = $EndpointFilter
    WindowHours    = $WindowHours
    RefreshSeconds = $RefreshSeconds
    TimeoutSec     = 10
    ConfigPath     = $configPath
}

function Save-MonitorConfig {
    param([string]$NewToken)
    $map = [ordered]@{
        HealthUrl      = $settings.HealthUrl
        DetailsUrl     = $settings.DetailsUrl
        Token          = $NewToken
        NotifyUrl      = $settings.NotifyUrl
        DataDir        = $settings.DataDir
        RequestsUrl    = $settings.RequestsUrl
        EndpointFilter = $settings.EndpointFilter
        WindowHours    = $settings.WindowHours
        FailsToAlert   = $settings.FailsToAlert
        RefreshSeconds = $settings.RefreshSeconds
        StateFile      = $settings.StateFile
        LogFile        = $settings.LogFile
    }
    try {
        $json = $map | ConvertTo-Json -Depth 5
        [System.IO.File]::WriteAllText($settings.ConfigPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        return $true
    } catch {
        return $false
    }
}

if ($Dashboard) {
    $uiPath = Join-Path $PSScriptRoot "monitor-health-ui.ps1"
    if (-not (Test-Path -LiteralPath $uiPath)) {
        throw "No se encontro el archivo de interfaz '$uiPath'."
    }
    . $uiPath
    Start-MonitorDashboard -Settings $settings
} else {
    Invoke-MonitorProbe -Settings $settings -Record -WriteSummary:$Summarize | Out-Null
}
