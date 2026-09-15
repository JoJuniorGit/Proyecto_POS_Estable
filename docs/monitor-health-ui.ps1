<#
  monitor-health-ui.ps1 - Ventana WinForms del monitor de salud del POS.
  Se carga con dot-sourcing desde monitor-health.ps1 cuando se usa -Dashboard.
  Reutiliza las funciones headless (Invoke-MonitorProbe, Get-SloSummary,
  Write-SloSummary, Save-MonitorConfig) del script principal. No agrega muestras
  al CSV ni envia alertas: es una vista de solo lectura + resumen bajo demanda.
#>

function Get-MonitorTrend {
    $data = @()
    $p95 = @()
    if ($script:Settings.DataDir) {
        $availabilityCsv = Join-Path $script:Settings.DataDir "slo-availability.csv"
        if (Test-Path -LiteralPath $availabilityCsv) {
            try {
                $rows = @(Import-Csv -LiteralPath $availabilityCsv | Select-Object -Last 60)
                foreach ($row in $rows) {
                    $data += [pscustomobject]@{
                        Ok        = ([string]$row.ok -eq "True" -or [string]$row.ok -eq "1" -or [string]$row.ok -eq "true")
                        LatencyMs = [double]$row.latencyMs
                    }
                }
            } catch { }
        }
        $p95Csv = Join-Path $script:Settings.DataDir "slo-endpoint-p95.csv"
        if (Test-Path -LiteralPath $p95Csv) {
            try {
                $rows = @(Import-Csv -LiteralPath $p95Csv | Select-Object -Last 60)
                foreach ($row in $rows) { $p95 += [double]$row.p95MaxMs }
            } catch { }
        }
    }
    return [pscustomobject]@{ Data = $data; P95 = $p95 }
}

function Update-MonitorLog {
    if (-not $script:TxtLog) { return }
    if ([string]::IsNullOrWhiteSpace($script:Settings.LogFile)) { return }
    if (Test-Path -LiteralPath $script:Settings.LogFile) {
        try {
            $lines = @(Get-Content -LiteralPath $script:Settings.LogFile -Tail 200 -ErrorAction SilentlyContinue)
            $script:TxtLog.Lines = [string[]]$lines
            $script:TxtLog.SelectionStart = $script:TxtLog.TextLength
            $script:TxtLog.ScrollToCaret()
        } catch { }
    }
}

function Set-HealthyLabel {
    param($Label, [bool]$Healthy, [string]$UpText, [string]$DownText)
    if (-not $Label) { return }
    if ($Healthy) {
        $Label.Text = $UpText
        $Label.BackColor = [System.Drawing.Color]::FromArgb(220, 252, 231)
        $Label.ForeColor = [System.Drawing.Color]::FromArgb(21, 128, 61)
    } else {
        $Label.Text = $DownText
        $Label.BackColor = [System.Drawing.Color]::FromArgb(254, 226, 226)
        $Label.ForeColor = [System.Drawing.Color]::FromArgb(185, 28, 28)
    }
}

function Apply-MonitorProbe {
    param($Probe)
    $healthy = [bool]$Probe.Healthy
    Set-HealthyLabel -Label $script:LblHealth -Healthy $healthy `
        -UpText "SERVICIO OPERATIVO" -DownText "SERVICIO CAIDO"
    if ($healthy) {
        $script:LblHealthDetail.Text = "HTTP $($Probe.StatusCode) - respuesta $($Probe.ProbeLatencyMs) ms"
    } else {
        $script:LblHealthDetail.Text = "Sin respuesta (HTTP $($Probe.StatusCode)) - fallos seguidos: $($Probe.Fails)"
    }

    $backupOk = [bool]$Probe.BackupFresh
    if ($null -eq $Probe.BackupAgeMinutes) {
        Set-HealthyLabel -Label $script:LblBackup -Healthy $false `
            -UpText "BACKUP SIN DATO" -DownText "BACKUP SIN DATO"
        $script:LblBackup.BackColor = [System.Drawing.Color]::FromArgb(241, 245, 249)
        $script:LblBackup.ForeColor = [System.Drawing.Color]::FromArgb(71, 85, 105)
        $script:LblBackupDetail.Text = "Defina DetailsUrl y Token para medir la frescura."
    } else {
        Set-HealthyLabel -Label $script:LblBackup -Healthy $backupOk `
            -UpText "BACKUP FRESCO" -DownText "BACKUP ATRASADO"
        $script:LblBackupDetail.Text = "Ultimo backup hace $($Probe.BackupAgeMinutes) min (RPO 26 h)"
    }

    $summary = $Probe.Summary
    if ($summary) {
        $availability = if ($null -ne $summary.availabilityPct) { "$($summary.availabilityPct) %" } else { "sin muestras" }
        $probeP95 = if ($null -ne $summary.probeLatencyP95Ms) { "$($summary.probeLatencyP95Ms) ms" } else { "sin muestras" }
        $endpointP95 = if ($null -ne $summary.endpointP95MaxMs) { "$($summary.endpointP95MaxMs) ms" } else { "sin muestras" }
        $script:LblAvailability.Text = "Disponibilidad ($($summary.windowHours) h): $availability  (meta >= $($summary.targetAvailabilityPct) %)"
        $script:LblProbeP95.Text = "Latencia de /health p95: $probeP95"
        $script:LblEndpointP95.Text = "Latencia por endpoint p95 (max): $endpointP95  (meta < $($summary.targetEndpointP95Ms) ms)"
        $state = if ($null -ne $summary.availabilityPct -and $summary.availabilityPct -lt $summary.targetAvailabilityPct) { "POR DEBAJO DE LA META" } else { "cumple" }
        $script:LblTargets.Text = "Evaluacion de metas: $state"
        $script:LblTargets.ForeColor = if ($state -eq "cumple") { [System.Drawing.Color]::FromArgb(21, 128, 61) } else { [System.Drawing.Color]::FromArgb(185, 28, 28) }
    } else {
        $script:LblAvailability.Text = "Disponibilidad: defina DataDir para calcular el SLO."
        $script:LblProbeP95.Text = ""
        $script:LblEndpointP95.Text = ""
        $script:LblTargets.Text = ""
    }

    $script:LblUpdated.Text = "Ultima actualizacion: $(Get-Date -Format 'HH:mm:ss')"
}

function Update-MonitorDashboard {
    try {
        if ($script:LblUpdated) { $script:LblUpdated.Text = "Actualizando..." ; $script:Form.Refresh() }
        $probe = Invoke-MonitorProbe -Settings $script:Settings
        Apply-MonitorProbe -Probe $probe
        $trend = Get-MonitorTrend
        $script:TrendData = $trend.Data
        $script:TrendP95 = $trend.P95
        if ($script:TrendPanel) { $script:TrendPanel.Invalidate() }
        Update-MonitorLog
    } catch {
        if ($script:LblUpdated) { $script:LblUpdated.Text = "Error al actualizar: $($_.Exception.Message)" }
    }
}

function Show-MonitorTokenDialog {
    try { Add-Type -AssemblyName Microsoft.VisualBasic } catch { }
    $current = "" # Do not expose current encrypted token to UI directly
    try {
        $value = [Microsoft.VisualBasic.Interaction]::InputBox(
            "Pegue el token JWT de monitoreo. Se guardara cifrado (AES) con la clave protegida monitor-token.key.",
            "Token de monitoreo", $current)
    } catch { return }
    if ([string]::IsNullOrWhiteSpace($value)) { return }

    # Cifrado AES con clave en monitor-token.key (SYSTEM + Administradores), legible por la tarea SYSTEM.
    $encrypted = Protect-MonitorToken -PlainToken $value.Trim() -Settings $script:Settings
    if (-not $encrypted) {
        [System.Windows.Forms.MessageBox]::Show(
            "No se pudo cifrar el token. Verifique que $((Get-MonitorTokenKeyFile -Settings $script:Settings)) sea escribible (ejecute como Administrador).",
            "Token de monitoreo", [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
        return
    }

    if (Save-MonitorConfig -NewToken $encrypted) {
        $script:Settings.Token = $encrypted
        Update-MonitorDashboard
    } else {
        [System.Windows.Forms.MessageBox]::Show(
            "No se pudo guardar el token en $($script:Settings.ConfigPath).", "Token de monitoreo",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
    }
}

function Show-MonitorSummaryDialog {
    $probe = Invoke-MonitorProbe -Settings $script:Settings
    $summary = $probe.Summary
    if (-not $summary) {
        [System.Windows.Forms.MessageBox]::Show(
            "Defina DataDir en monitor-config.json para calcular el resumen SLO.",
            "Resumen SLO", [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        return
    }
    Write-SloSummary -Settings $script:Settings -Summary $summary
    $lines = @(
        "Ventana: $($summary.windowHours) h",
        "Disponibilidad: $($summary.availabilityPct) % (meta >= $($summary.targetAvailabilityPct) %)",
        "Muestras: $($summary.samplesOk)/$($summary.samplesTotal)",
        "Latencia /health p95: $($summary.probeLatencyP95Ms) ms",
        "Latencia endpoint p95 (max): $($summary.endpointP95MaxMs) ms (meta < $($summary.targetEndpointP95Ms) ms)",
        "Backup fresco: $($summary.backupFresh) (hace $($summary.backupAgeMinutes) min)",
        "",
        "Guardado en: $(Join-Path $script:Settings.DataDir 'slo-summary.json')"
    )
    [System.Windows.Forms.MessageBox]::Show(
        ($lines -join [Environment]::NewLine), "Resumen SLO",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    Update-MonitorDashboard
}

function New-MonitorDashboardForm {
    $script:UiFont = New-Object System.Drawing.Font("Segoe UI", 9)
    $script:UiSmallFont = New-Object System.Drawing.Font("Segoe UI", 8)
    $script:UiTitleFont = New-Object System.Drawing.Font("Segoe UI", 14, [System.Drawing.FontStyle]::Bold)

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "POS CommandCenter - Monitor de Salud"
    $form.Size = New-Object System.Drawing.Size(960, 680)
    $form.MinimumSize = New-Object System.Drawing.Size(840, 580)
    $form.StartPosition = "CenterScreen"
    $form.Font = $script:UiFont

    $title = New-Object System.Windows.Forms.Label
    $title.Text = "Monitor de Salud y SLO"
    $title.Font = $script:UiTitleFont
    $title.Location = New-Object System.Drawing.Point(16, 12)
    $title.Size = New-Object System.Drawing.Size(420, 30)
    $form.Controls.Add($title)

    $script:LblUpdated = New-Object System.Windows.Forms.Label
    $script:LblUpdated.Text = "Esperando primera medicion..."
    $script:LblUpdated.Location = New-Object System.Drawing.Point(440, 22)
    $script:LblUpdated.Size = New-Object System.Drawing.Size(300, 20)
    $script:LblUpdated.TextAlign = "MiddleRight"
    $script:LblUpdated.ForeColor = [System.Drawing.Color]::FromArgb(100, 116, 139)
    $form.Controls.Add($script:LblUpdated)

    $script:LblHealth = New-Object System.Windows.Forms.Label
    $script:LblHealth.Location = New-Object System.Drawing.Point(16, 52)
    $script:LblHealth.Size = New-Object System.Drawing.Size(220, 34)
    $script:LblHealth.TextAlign = "MiddleCenter"
    $script:LblHealth.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $script:LblHealth.BorderStyle = "FixedSingle"
    $form.Controls.Add($script:LblHealth)

    $script:LblBackup = New-Object System.Windows.Forms.Label
    $script:LblBackup.Location = New-Object System.Drawing.Point(246, 52)
    $script:LblBackup.Size = New-Object System.Drawing.Size(220, 34)
    $script:LblBackup.TextAlign = "MiddleCenter"
    $script:LblBackup.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $script:LblBackup.BorderStyle = "FixedSingle"
    $form.Controls.Add($script:LblBackup)

    $script:LblHealthDetail = New-Object System.Windows.Forms.Label
    $script:LblHealthDetail.Location = New-Object System.Drawing.Point(16, 88)
    $script:LblHealthDetail.Size = New-Object System.Drawing.Size(450, 20)
    $script:LblHealthDetail.ForeColor = [System.Drawing.Color]::FromArgb(71, 85, 105)
    $form.Controls.Add($script:LblHealthDetail)

    $script:LblBackupDetail = New-Object System.Windows.Forms.Label
    $script:LblBackupDetail.Location = New-Object System.Drawing.Point(246, 88)
    $script:LblBackupDetail.Size = New-Object System.Drawing.Size(450, 20)
    $script:LblBackupDetail.ForeColor = [System.Drawing.Color]::FromArgb(71, 85, 105)
    $form.Controls.Add($script:LblBackupDetail)

    $btnCheck = New-Object System.Windows.Forms.Button
    $btnCheck.Text = "Comprobar ahora"
    $btnCheck.Location = New-Object System.Drawing.Point(620, 110)
    $btnCheck.Size = New-Object System.Drawing.Size(140, 30)
    $btnCheck.Add_Click({ Update-MonitorDashboard })
    $form.Controls.Add($btnCheck)

    $btnSummary = New-Object System.Windows.Forms.Button
    $btnSummary.Text = "Generar resumen SLO"
    $btnSummary.Location = New-Object System.Drawing.Point(766, 110)
    $btnSummary.Size = New-Object System.Drawing.Size(164, 30)
    $btnSummary.Add_Click({ Show-MonitorSummaryDialog })
    $form.Controls.Add($btnSummary)

    $btnToken = New-Object System.Windows.Forms.Button
    $btnToken.Text = "Token de monitoreo"
    $btnToken.Location = New-Object System.Drawing.Point(16, 110)
    $btnToken.Size = New-Object System.Drawing.Size(150, 30)
    $btnToken.Add_Click({ Show-MonitorTokenDialog })
    $form.Controls.Add($btnToken)

    $tabs = New-Object System.Windows.Forms.TabControl
    $tabs.Location = New-Object System.Drawing.Point(16, 150)
    $tabs.Size = New-Object System.Drawing.Size(914, 480)
    $tabs.Anchor = "Top,Bottom,Left,Right"
    $form.Controls.Add($tabs)

    $tabStatus = New-Object System.Windows.Forms.TabPage
    $tabStatus.Text = "Estado y SLO"
    $tabs.Controls.Add($tabStatus)

    $script:LblAvailability = New-Object System.Windows.Forms.Label
    $script:LblAvailability.Location = New-Object System.Drawing.Point(16, 20)
    $script:LblAvailability.Size = New-Object System.Drawing.Size(860, 24)
    $script:LblAvailability.Font = New-Object System.Drawing.Font("Segoe UI", 10)
    $tabStatus.Controls.Add($script:LblAvailability)

    $script:LblProbeP95 = New-Object System.Windows.Forms.Label
    $script:LblProbeP95.Location = New-Object System.Drawing.Point(16, 52)
    $script:LblProbeP95.Size = New-Object System.Drawing.Size(860, 24)
    $tabStatus.Controls.Add($script:LblProbeP95)

    $script:LblEndpointP95 = New-Object System.Windows.Forms.Label
    $script:LblEndpointP95.Location = New-Object System.Drawing.Point(16, 84)
    $script:LblEndpointP95.Size = New-Object System.Drawing.Size(860, 24)
    $tabStatus.Controls.Add($script:LblEndpointP95)

    $script:LblTargets = New-Object System.Windows.Forms.Label
    $script:LblTargets.Location = New-Object System.Drawing.Point(16, 116)
    $script:LblTargets.Size = New-Object System.Drawing.Size(860, 24)
    $script:LblTargets.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $tabStatus.Controls.Add($script:LblTargets)

    $tabTrend = New-Object System.Windows.Forms.TabPage
    $tabTrend.Text = "Tendencia"
    $tabs.Controls.Add($tabTrend)

    $script:TrendPanel = New-Object System.Windows.Forms.Panel
    $script:TrendPanel.Location = New-Object System.Drawing.Point(10, 10)
    $script:TrendPanel.Size = New-Object System.Drawing.Size(884, 430)
    $script:TrendPanel.Anchor = "Top,Bottom,Left,Right"
    $script:TrendPanel.BorderStyle = "FixedSingle"
    $script:TrendPanel.Add_Paint({
        param($sender, $e)
        $g = $e.Graphics
        $w = $sender.Width
        $h = $sender.Height
        $g.Clear([System.Drawing.Color]::FromArgb(248, 250, 252))
        $g.DrawString("Disponibilidad (barras) / latencia endpoint p95 en ms (linea azul)",
            $script:UiSmallFont, [System.Drawing.Brushes]::Gray, 8, 4)
        if (-not $script:TrendData -or $script:TrendData.Count -eq 0) {
            $g.DrawString("Sin muestras SLO. Defina DataDir y ejecute el monitor headless.",
                $script:UiFont, [System.Drawing.Brushes]::Gray, 16, 40)
            return
        }
        $left = 40
        $right = $w - 24
        $top = 24
        $bottom = $h - 28
        $g.DrawLine([System.Drawing.Pens]::LightGray, $left, $top, $left, $bottom)
        $g.DrawLine([System.Drawing.Pens]::LightGray, $left, $bottom, $right, $bottom)
        $g.DrawString("100%", $script:UiSmallFont, [System.Drawing.Brushes]::Gray, 4, $top - 4)
        $g.DrawString("0%", $script:UiSmallFont, [System.Drawing.Brushes]::Gray, 12, $bottom - 10)

        $count = $script:TrendData.Count
        $slot = [Math]::Max(2, [int](($right - $left) / [Math]::Max(1, $count)))
        for ($i = 0; $i -lt $count; $i++) {
            $sample = $script:TrendData[$i]
            $barHeight = if ($sample.Ok) { $bottom - $top } else { [int](($bottom - $top) * 0.15) }
            $x = $left + ($i * $slot)
            $color = if ($sample.Ok) { [System.Drawing.Color]::FromArgb(34, 197, 94) } else { [System.Drawing.Color]::FromArgb(239, 68, 68) }
            $brush = New-Object System.Drawing.SolidBrush($color)
            $g.FillRectangle($brush, $x, ($bottom - $barHeight), [Math]::Max(1, $slot - 1), $barHeight)
            $brush.Dispose()
        }

        if ($script:TrendP95 -and $script:TrendP95.Count -gt 0) {
            $maxP95 = ($script:TrendP95 | Measure-Object -Maximum).Maximum
            if ($maxP95 -le 0) { $maxP95 = 500 }
            $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(37, 99, 235), 2)
            $points = New-Object System.Collections.Generic.List[System.Drawing.PointF]
            $pCount = $script:TrendP95.Count
            for ($i = 0; $i -lt $pCount; $i++) {
                $x = $left + ($i * [Math]::Max(2, [int](($right - $left) / [Math]::Max(1, $pCount)))) + 1
                $ratio = [double]$script:TrendP95[$i] / [double]$maxP95
                $y = $bottom - [int](($bottom - $top) * $ratio)
                $points.Add((New-Object System.Drawing.PointF([single]$x, [single]$y)))
            }
            if ($points.Count -ge 2) { $g.DrawLines($pen, $points.ToArray()) }
            $g.DrawString("p95 max=$maxP95 ms", $script:UiSmallFont, [System.Drawing.Brushes]::RoyalBlue, $left + 4, $top)
            $pen.Dispose()
        }
        $g.DrawString("$count muestras (mas reciente a la derecha)", $script:UiSmallFont,
            [System.Drawing.Brushes]::Gray, $left + 4, $bottom + 6)
    })
    $tabTrend.Controls.Add($script:TrendPanel)

    $tabLog = New-Object System.Windows.Forms.TabPage
    $tabLog.Text = "Log"
    $tabs.Controls.Add($tabLog)

    $script:TxtLog = New-Object System.Windows.Forms.TextBox
    $script:TxtLog.Multiline = $true
    $script:TxtLog.ReadOnly = $true
    $script:TxtLog.ScrollBars = "Vertical"
    $script:TxtLog.WordWrap = $false
    $script:TxtLog.Font = New-Object System.Drawing.Font("Consolas", 9)
    $script:TxtLog.Location = New-Object System.Drawing.Point(10, 10)
    $script:TxtLog.Size = New-Object System.Drawing.Size(884, 430)
    $script:TxtLog.Anchor = "Top,Bottom,Left,Right"
    $tabLog.Controls.Add($script:TxtLog)

    return $form
}

function Start-MonitorDashboard {
    param([hashtable]$Settings)
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $script:Settings = $Settings
    $form = New-MonitorDashboardForm
    $script:Form = $form

    $script:Timer = New-Object System.Windows.Forms.Timer
    $script:Timer.Interval = [Math]::Max(5, [int]$Settings.RefreshSeconds) * 1000
    $script:Timer.Add_Tick({ Update-MonitorDashboard })
    $form.Add_Shown({
        Update-MonitorDashboard
        $script:Timer.Start()
    })
    $form.Add_FormClosed({
        if ($script:Timer) { $script:Timer.Stop(); $script:Timer.Dispose() }
    })

    [void]$form.ShowDialog()
    $form.Dispose()
}
