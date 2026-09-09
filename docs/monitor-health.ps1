<#
  monitor-health.ps1 - Monitoreo de salud del POS CommandCenter.
  Consulta /health (y opcionalmente /api/health/details para la frescura del backup)
  y notifica por webhook cuando el servicio falla N veces consecutivas o el backup
  no esta fresco. Programar con el Programador de tareas (cada 5 min).
#>
param(
    [string]$HealthUrl = "http://localhost:5000/health",
    [string]$DetailsUrl = "",
    [string]$Token = "",
    [string]$NotifyUrl = "",
    [int]$FailsToAlert = 3,
    [string]$StateFile = "$PSScriptRoot\health_state.txt",
    [string]$LogFile = "$PSScriptRoot\monitor.log"
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

$fails = 0
if (Test-Path -LiteralPath $StateFile) {
    try { $fails = [int]((Get-Content -LiteralPath $StateFile -Raw).Trim()) } catch { $fails = 0 }
}

$healthy = $false
try {
    $resp = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 10 -ErrorAction Stop
    if ($resp.status -eq "Healthy" -and $resp.database -eq "Connected") {
        $healthy = $true
        $fails = 0
        Write-Log "OK: /health -> $($resp.status) / $($resp.database)"
    } else {
        $fails++
        Write-Log "/health devolvio status=$($resp.status), database=$($resp.database)" "WARN"
    }
} catch {
    $fails++
    Write-Log "/health no disponible: $($_.Exception.Message)" "WARN"
}

$backupFresh = $true
if ($healthy -and -not [string]::IsNullOrWhiteSpace($DetailsUrl)) {
    try {
        $headers = @{ }
        if (-not [string]::IsNullOrWhiteSpace($Token)) { $headers["Authorization"] = "Bearer $Token" }
        $details = Invoke-RestMethod -Uri $DetailsUrl -Headers $headers -TimeoutSec 10 -ErrorAction Stop
        $backupFresh = ($details.lastBackupFresh -eq $true)
        if ($backupFresh) {
            Write-Log "Backup OK: ultimo a los $($details.lastBackupAgeMinutes) min."
        } else {
            Write-Log "Backup NO fresco (age $($details.lastBackupAgeMinutes) min)." "WARN"
        }
    } catch {
        Write-Log "No se pudo consultar /api/health/details: $($_.Exception.Message)" "WARN"
    }
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