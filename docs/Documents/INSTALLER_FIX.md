# INSTALLER FIX — Make the installer apply firewall rules + service env vars automatically

**Task for the next agent:** modify the installer so that after installing/updating the POS, it
automatically (1) opens firewall ports 5000/5001 for external devices and (2) sets the NSSM service
environment variables. Both must be **idempotent** (safe to run on every install and every update).

## 1. Facts already verified (do not redo discovery)

- **Source repo:** `C:\Users\DELL 3580\Documents\GitHub\Administrador` (this repo; backend is
  `Backend.API` targeting `net10.0`).
- **Installer:** Inno Setup 6 (EXE magic `MZP`). The compiled installer is
  `C:\Users\DELL 3580\Documents\POS_System_Setup_v1.0.0.exe`. **The `.iss` source is NOT in this
  repo** — you must create it (suggest `Installer/SistemaPOS_Setup.iss`). Installed app lives at
  `C:\Program Files (x86)\Sistema POS Administrador` with folders `BackendAPI`, `DesktopClient`,
  `UpdaterService`.
- **Backend listens on all interfaces** on ports 5000 (HTTP) and 5001 (HTTPS) — binding is already
  correct (`0.0.0.0` + `[::]`, process `Backend.API.exe`). Only the firewall blocks external access.
- **Windows Firewall is enabled on all three profiles** (Domain/Private/Public), so no rule = blocked.
- **Current firewall state (broken):** 8 duplicate rules named `Sistema POS - Backend API (TCP 5000)`
  covering **only port 5000** (recreated by a reinstall); **no rule for 5001**. Verified in
  `HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules`.
- **Service:** `PosBackendService`, runs `Backend.API.exe` via
  `C:\Program Files (x86)\Sistema POS Administrador\BackendAPI\nssm.exe` (nssm.exe ships inside the
  BackendAPI folder), user `LocalSystem`, `AUTO_START`. It is NOT in the repo — nssm.exe must be
  included in the published BackendAPI output (copy it there during build, or download nssm 2.24).
- **Env vars the service needs** (currently set manually via `nssm set PosBackendService
  AppEnvironmentExtra`; a clean install loses them — the installer must set them):
  - `ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=postgres`
  - `SystemSettings__AdminSeedPassword=Admin123!`
- **No Inno Setup compiler (ISCC.exe) is installed** on this machine — installing Inno Setup 6 is a
  prerequisite for compiling the `.iss`; the agent may skip compiling if it cannot install software,
  but the `.iss` must still be syntactically correct.

## 2. What to implement

Create three files in the repo:

### 2.1 `Installer/Configure-PosService.ps1` (the core; idempotent, run elevated)

```powershell
# Configure-PosService.ps1 — firewall + NSSM service env vars. Idempotent. Run as admin.
param(
    [string]$InstallDir  = "C:\Program Files (x86)\Sistema POS Administrador",
    [string]$ServiceName = "PosBackendService",
    [string]$BackendExe  = "$InstallDir\BackendAPI\Backend.API.exe",
    [string]$Nssm        = "$InstallDir\BackendAPI\nssm.exe",
    [string]$ConnectionString = "Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=postgres",
    [string]$AdminSeedPassword = "Admin123!"
)
$ErrorActionPreference = 'Stop'
$LogFile = "$InstallDir\BackendAPI\logs\installer.log"
New-Item -ItemType Directory -Force -Path (Split-Path $LogFile) | Out-Null
function Log([string]$m) { Write-Host $m; Add-Content -Path $LogFile -Value "$(Get-Date -Format s) $m" }

# ---- 1) Firewall: remove ALL stale/duplicate POS rules, then add 5000 + 5001 ----
Log "Firewall: removing stale 'Sistema POS - Backend API*' rules..."
Get-NetFirewallRule -DisplayName 'Sistema POS - Backend API*' -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
foreach ($port in 5000, 5001) {
    $name = "Sistema POS - Backend API (TCP $port)"
    Log "Firewall: adding rule '$name' (inbound TCP $port, local subnet only)"
    netsh advfirewall firewall add rule name="$name" dir=in action=allow protocol=TCP localport=$port remoteip=localsubnet profile=any | Out-Null
}

# ---- 2) NSSM service: install if missing, else keep existing ----
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    Log "Service: installing $ServiceName..."
    & $Nssm install $ServiceName $BackendExe
    if ($LASTEXITCODE -ne 0) { throw "nssm install failed (exit $LASTEXITCODE)" }
}
& $Nssm set $ServiceName AppDirectory "$InstallDir\BackendAPI" | Out-Null
& $Nssm set $ServiceName Start SERVICE_AUTO_START | Out-Null

# ---- 3) Env vars: merge so existing extras (e.g. a real DB password) are preserved ----
$desired = @{
    "ConnectionStrings__DefaultConnection" = $ConnectionString
    "SystemSettings__AdminSeedPassword"    = $AdminSeedPassword
}
$current = (& $Nssm get $ServiceName AppEnvironmentExtra 2>$null) -split "`r?`n" | Where-Object { $_ -match '^[^=]+=' }
$merged = @{}
foreach ($line in $current) {
    $kv = $line -split '=', 2
    if ($kv.Count -eq 2) { $merged[$kv[0]] = $kv[1] }
}
foreach ($k in $desired.Keys) { $merged[$k] = $desired[$k] }
$args = @($ServiceName, "AppEnvironmentExtra") + ($merged.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
Log "Service: setting AppEnvironmentExtra ($($merged.Count) variables)"
& $Nssm set @args
if ($LASTEXITCODE -ne 0) { throw "nssm set AppEnvironmentExtra failed (exit $LASTEXITCODE)" }

# ---- 4) Start / restart the service ----
if ((Get-Service -Name $ServiceName).Status -ne 'Running') {
    Log "Service: starting $ServiceName..."
    & $Nssm start $ServiceName
} else {
    Log "Service: restarting $ServiceName..."
    & $Nssm restart $ServiceName
}
Log "Done."
```

> Notes for the agent: `nssm get AppEnvironmentExtra` returns one `KEY=value` per line; if the value
> was never set, it prints the type name — the filter above drops non-`KEY=` lines. If you prefer
> PowerShell-native firewall commands, `New-NetFirewallRule` is equivalent; `netsh` is shown because
> the deployed rules were created that way. Keep scope `remoteip=localsubnet` (do NOT open to all
> networks).

### 2.2 `Installer/SistemaPOS_Setup.iss` (minimal working skeleton)

```iss
#define MyAppName "Sistema POS Administrador"
#define MyAppVersion "1.0.0"
#define MyAppExeName "Desktop.Client.exe"
#define BackendPublish "..\Backend.API\bin\Release\net10.0\publish"
#define ClientPublish  "..\Desktop.Client\bin\Release\net10.0\publish"

[Setup]
AppId={{8B4A2C1E-5D10-4A2B-9F8E-7C3D2A1B4E00}}   ; keep ONE stable GUID forever (upgrades reuse it)
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Soluciones POS
DefaultDirName={autopf}\{#MyAppName}
PrivilegesRequired=admin                       ; REQUIRED: firewall + service need admin
OutputDir=..\Installer\Output
OutputBaseFilename=POS_System_Setup_v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
UninstallDisplayName={#MyAppName}

[Files]
; order matters: BackendAPI first so the service path exists before configuration runs
Source: "{#BackendPublish}\*"; DestDir: "{app}\BackendAPI";     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ClientPublish}\*";  DestDir: "{app}\DesktopClient";   Flags: ignoreversion recursesubdirs createallsubdirs
; UpdaterService publish path — adjust to its real output folder if it differs:
Source: "..\UpdaterService\bin\Release\*\publish\*"; DestDir: "{app}\UpdaterService"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Configure-PosService.ps1"; DestDir: "{app}\tools";      Flags: ignoreversion

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Configure-PosService.ps1"" -InstallDir ""{app}"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Configuring Windows Firewall and POS service..."

[UninstallRun]
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "stop PosBackendService"; Flags: runhidden
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "remove PosBackendService confirm"; Flags: runhidden
```

> Adjust `BackendPublish`/`ClientPublish`/UpdaterService paths to the real publish output. Verify the
> publish output actually contains `nssm.exe` (deployed app has it inside `BackendAPI\`); if not, add
> `Source: "nssm.exe"; DestDir: "{app}\BackendAPI"` with the binary next to the `.iss`.

### 2.3 `Installer/README.md` (short)

State: how to build (`ISCC.exe Installer\SistemaPOS_Setup.iss` from the repo root; Inno Setup 6 must
be installed), what the script does, and that the installer is now self-healing for firewall + env
vars on every install/update (fixes the "reinstall loses env vars" and "5001 not open" issues).

## 3. Verification (must pass before considering it done)

Run these from an **elevated** shell after a clean install:

1. Firewall — exactly two POS rules, both `Action=Allow|Dir=In|Protocol=6|RA4/RA6=LocalSubnet`:
   `Get-NetFirewallRule -DisplayName 'Sistema POS - Backend API*' | Select DisplayName,Enabled,Action,Direction`
   (or: `reg query "HKLM\SYSTEM\...\FirewallRules" /f "Sistema POS" /d`).
2. Service env vars persisted:
   `& "C:\Program Files (x86)\Sistema POS Administrador\BackendAPI\nssm.exe" get PosBackendService AppEnvironmentExtra`
   → must show both `ConnectionStrings__DefaultConnection` and `SystemSettings__AdminSeedPassword`.
3. Service running: `Get-Service PosBackendService` → `Running`.
4. Backend healthy from the network: `Invoke-WebRequest http://<LAN-IP>:5000/health` → 200
   `"database":"Connected"`.
5. **Idempotency:** run `Configure-PosService.ps1` a second time → no duplicate firewall rules, env
   vars unchanged, service still running.

## 4. Constraints / gotchas

- Everything in this doc that changes machine state requires elevation; the installer already runs
  elevated via `PrivilegesRequired=admin`.
- Do NOT change ports, rule scope (keep `localsubnet`), or the env var names — the deployed system
  and its docs depend on them.
- The UpdaterService folder exists only in the deployed app, not in this repo; do not block on it —
  copy the published binaries as in section 2.2 and adjust paths if its publish layout differs.
- If the build output folders don't exist yet, `dotnet publish` them first (`net10.0`).