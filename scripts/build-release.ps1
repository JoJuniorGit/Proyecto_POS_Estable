# =====================================================================
# Script de Compilación y Publicación Autónoma (.NET Self-Contained)
# =====================================================================

$ErrorActionPreference = "Stop"

# =====================================================================
# GUARDA DE EJECUCIÓN ÚNICA (lock exclusivo + verificación defensiva de procesos)
# Debe ejecutarse ANTES de matar procesos o borrar carpetas: dos instancias
# concurrentes se pisaban el publish/ (limpieza + builds cruzados).
# =====================================================================
$lockPath = Join-Path $PSScriptRoot '.build-release.lock'
$lockStream = $null
try {
    $lockStream = [System.IO.File]::Open(
        $lockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
} catch [System.IO.IOException] {
    Write-Host "ABORTANDO: ya hay otra instancia de build-release.ps1 en ejecución (lock: $lockPath)." -ForegroundColor Red
    exit 1
} catch {
    Write-Host "ABORTANDO: no se pudo crear el lockfile ($lockPath): $($_.Exception.Message). Verifique permisos de escritura en scripts/." -ForegroundColor Red
    exit 1
}

# Comprobación defensiva: otros procesos PowerShell ejecutando este script. Se excluye el proceso
# actual y su cadena de ancestros (wrappers de herramientas/CI pueden contener el nombre en la
# línea de comandos del intérprete padre).
$ancestorIds = @()
$cursor = $PID
for ($i = 0; $i -lt 10 -and $cursor) {
    $ancestorIds += $cursor
    $cursor = (Get-CimInstance Win32_Process -Filter "ProcessId=$cursor" -ErrorAction SilentlyContinue).ParentProcessId
}
$otherInstances = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ProcessId -notin $ancestorIds -and
        ($_.Name -in @('pwsh.exe', 'powershell.exe')) -and
        $_.CommandLine -match 'build-release\.ps1'
    })
if ($otherInstances.Count -gt 0) {
    Write-Host "ABORTANDO: se detectaron $($otherInstances.Count) instancia(s) adicional(es) de build-release.ps1 (PID: $($otherInstances.ProcessId -join ', '))." -ForegroundColor Red
    $lockStream.Dispose()
    Remove-Item $lockPath -ErrorAction SilentlyContinue
    exit 1
}

# Limpieza SÍNCRONA y verificada: enumera, borra archivo por archivo y la carpeta, y RECIÉN
# entonces reporta "Limpiados N archivo(s)". Reintenta 2 veces (500 ms) ante handles bloqueados
# (antivirus/indexer) y aborta si algún archivo sobrevive. Nunca puede reportar N > M: el conteo
# se toma de la lista enumerada antes de borrar.
function Remove-ReleaseFolder {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        return 0
    }

    $files = @(Get-ChildItem $Path -Recurse -File -Force -ErrorAction Stop)
    $fileCount = $files.Count
    $attempt = 0
    while ($true) {
        try {
            foreach ($file in $files) {
                if ($file.Exists) {
                    Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
                }
            }
            if (Test-Path $Path) {
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            }
            break
        } catch {
            $attempt++
            if ($attempt -ge 3) {
                Write-Host "ABORTANDO: no se pudo limpiar '$Path' (archivo bloqueado): $($_.Exception.Message)" -ForegroundColor Red
                throw "build-release abortado: limpieza de '$Path' imposible tras 3 intentos (500 ms entre intentos)."
            }
            Start-Sleep -Milliseconds 500
            $files = @(Get-ChildItem $Path -Recurse -File -Force -ErrorAction SilentlyContinue)
        }
    }

    $remaining = @(Get-ChildItem $Path -Recurse -File -Force -ErrorAction SilentlyContinue)
    if ($remaining.Count -ne 0) {
        throw "build-release abortado: limpieza incompleta en '$Path' ($($remaining.Count) archivo(s) sobrevivieron)."
    }

    # Mensaje de éxito SOLO tras verificar el borrado real (síncrono).
    Write-Host "Limpiados $fileCount archivo(s) de '$Path'." -ForegroundColor DarkGray
    return $fileCount
}

try {
    $rootDir = Split-Path -Path $PSScriptRoot -Parent
    Set-Location $rootDir

    # Matar procesos que bloquean DLLs en bin/ y obj/
    $processes = @("Desktop.Client", "Backend.API", "VBCSCompiler")
    foreach ($proc in $processes) {
        Stop-Process -Name $proc -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 300

    Write-Host "[1/7] Limpiando carpetas de salida preexistentes..." -ForegroundColor Cyan
    # El lock de ejecución única debe estar tomado antes de borrar cualquier artefacto.
    if (-not $lockStream) {
        throw "build-release abortado: el lock de ejecución única no está tomado; no se limpia nada."
    }
    Remove-ReleaseFolder -Path (Join-Path $rootDir "publish") | Out-Null
    Remove-ReleaseFolder -Path (Join-Path $rootDir "publish_backend") | Out-Null
    Remove-ReleaseFolder -Path (Join-Path $rootDir "dist_installer") | Out-Null

    New-Item -ItemType Directory -Path "$rootDir\publish\BackendAPI" | Out-Null
    New-Item -ItemType Directory -Path "$rootDir\publish\DesktopClient" | Out-Null
    New-Item -ItemType Directory -Path "$rootDir\publish\UpdaterService" | Out-Null

    Write-Host "[2/7] Compilando React Web.Frontend en Backend.API/wwwroot..." -ForegroundColor Cyan
    Set-Location "$rootDir\Web.Frontend"
    if (Test-Path "package.json") {
        npm run build
    }
    Set-Location $rootDir

    # 8.80-F3/F4: verificacion del bundle web en wwwroot (index.html + assets con
    # hash). Vite emite a Backend.API/wwwroot (outDir) con emptyOutDir; si el build
    # fallase o se omitiera, el artefacto publicaria un Backend sin UI -> abort.
    $webIndex = Join-Path $rootDir "Backend.API\wwwroot\index.html"
    $webAssets = Join-Path $rootDir "Backend.API\wwwroot\assets"
    if (-not (Test-Path $webIndex) -or -not (Test-Path $webAssets)) {
        Write-Host "ABORTANDO: bundle web ausente en Backend.API/wwwroot (falta npm run build)." -ForegroundColor Red
        throw "build-release abortado: el bundle web no se regenero en wwwroot."
    }
    $assetFiles = Get-ChildItem $webAssets -File -ErrorAction SilentlyContinue
    if (-not $assetFiles) {
        Write-Host "ABORTANDO: Backend.API/wwwroot/assets vacio: el bundle web no tiene JS/CSS." -ForegroundColor Red
        throw "build-release abortado: assets del bundle web vacios."
    }
    Write-Host "Bundle web OK: index.html + $($assetFiles.Count) assets en wwwroot." -ForegroundColor Green

    Write-Host "[3/7] Publicando Backend.API (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
    dotnet publish "$rootDir\Backend.API\Backend.API.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\BackendAPI"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish falló para Backend.API (exit $LASTEXITCODE)."
    }
    $backendExe = Join-Path $rootDir "publish\BackendAPI\Backend.API.exe"
    if (-not (Test-Path $backendExe)) {
        throw "No se generó $backendExe tras dotnet publish."
    }

    Write-Host "[4/7] Publicando Desktop.Client (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
    dotnet publish "$rootDir\Desktop.Client\Desktop.Client.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\DesktopClient"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish falló para Desktop.Client (exit $LASTEXITCODE)."
    }
    $desktopExe = Join-Path $rootDir "publish\DesktopClient\Desktop.Client.exe"
    if (-not (Test-Path $desktopExe)) {
        throw "No se generó $desktopExe tras dotnet publish."
    }

    Write-Host "[5/7] Publicando UpdaterService (.NET win-x64 Self-Contained)..." -ForegroundColor Cyan
    dotnet publish "$rootDir\UpdaterService\UpdaterService.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$rootDir\publish\UpdaterService"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish falló para UpdaterService (exit $LASTEXITCODE)."
    }
    $updaterExe = Join-Path $rootDir "publish\UpdaterService\UpdaterService.exe"
    if (-not (Test-Path $updaterExe)) {
        throw "No se generó $updaterExe tras dotnet publish."
    }

    Get-ChildItem "$rootDir\publish" -Recurse -File -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem "$rootDir\publish" -Recurse -File -Filter "secrets.json" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # # 8.27-A04: verificación de que los artefactos publicados NO contienen literales de
    # credenciales conocidos (p. ej. PosHttpsDev2026! o la clave JWT dev histórica). Si un
    # publish stale o un cambio deja escapar estos valores, el build se ABORTA antes de
    # empaquetar el instalador que recibiría un cliente.
    Write-Host "[6/7] Escaneando publish/ por secretos conocidos..." -ForegroundColor Cyan
    $forbiddenLiterals = @('PosHttpsDev2026!', 'ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795')
    $forbiddenPatterns = @('"Password"\s*:\s*"[^"]+"', '"Key"\s*:\s*"[^"]+"')
    $leakLines = @()
    foreach ($needle in $forbiddenLiterals) {
        $leakLines += Get-ChildItem "$rootDir\publish" -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.json', '.config', '.xml', '.txt', '.iss', '.ps1' } |
            Select-String -SimpleMatch -Pattern $needle -ErrorAction SilentlyContinue |
            ForEach-Object { "{0}:{1} -> {2}" -f $_.Path, $_.LineNumber, $needle }
    }
    foreach ($pattern in $forbiddenPatterns) {
        $leakLines += Get-ChildItem "$rootDir\publish" -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.json', '.config' } |
            Select-String -Pattern $pattern -ErrorAction SilentlyContinue |
            ForEach-Object { "{0}:{1} -> matched secret pattern" -f $_.Path, $_.LineNumber }
    }

    if ($leakLines) {
        Write-Host "ABORTANDO: secreto conocido detectado en artefactos publicados:" -ForegroundColor Red
        $leakLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "build-release abortado: los artefactos contienen literales o patrones de credenciales conocidos."
    }
    Write-Host "Scrub de secretos OK: publish/ sin literales ni patrones conocidos." -ForegroundColor Green

    # 8.30-A4: el certificado HTTPS de desarrollo (certs\pos-https.pfx) tampoco debe empaquetarse:
    # en el sitio el certificado se genera por estacion (scripts/create-https-cert.ps1) y se provee
    # por Windows Store o pfx local fuera del publish. Si vuelve a colarse en CUALQUIER subcarpeta del
    # publish (BackendAPI, DesktopClient o UpdaterService), el build se ABORTA.
    $leakedPfx = Get-ChildItem "$rootDir\publish" -Recurse -File -Filter "*.pfx" -ErrorAction SilentlyContinue
    if ($leakedPfx) {
        Write-Host "ABORTANDO: se empaquetaron certificados .pfx de desarrollo en el publish Release:" -ForegroundColor Red
        $leakedPfx | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
        throw "build-release abortado: los certificados de desarrollo no deben viajar en el instalador de cliente."
    }
    Write-Host "Verificacion de pfx OK: publish/ sin certs .pfx de desarrollo." -ForegroundColor Green

    # 8.29-A4/8.80: el archivo de configuracion de desarrollo debe quedar excluido del
    # publish (matriz INSTALLATION 11: appsettings.Development.json -> NO en produccion).
    # Si una publicacion accidentada lo incluye, el build se ABORTA antes del instalador.
    $leakedDevConfig = Get-ChildItem "$rootDir\publish" -Recurse -File -Filter "appsettings.Development.json" -ErrorAction SilentlyContinue
    if ($leakedDevConfig) {
        Write-Host "ABORTANDO: se empaqueto appsettings.Development.json en el publish Release:" -ForegroundColor Red
        $leakedDevConfig | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
        throw "build-release abortado: appsettings.Development.json no debe viajar en el artefacto de cliente."
    }
    Write-Host "Verificacion de config dev OK: publish/ sin appsettings.Development.json." -ForegroundColor Green

    # 8.80-F3/F4 (IMP-1): smoke de migracion "desde cero" opcional en el release. Si
    # TEST_POSTGRES_CONNECTION esta definida, se ejecutan los smokes MigratedSchema
    # (incl. el de BD vacia) ANTES de empaquetar; si fallan, se aborta. Valor por defecto
    # se toma de .env cuando exista.
    $testConn = $env:TEST_POSTGRES_CONNECTION
    if (-not $testConn) {
        $envFile = Join-Path $rootDir ".env"
        if (Test-Path $envFile) {
            $envLine = Get-Content $envFile | Where-Object { $_ -match '^TEST_POSTGRES_CONNECTION=' } | Select-Object -First 1
            if ($envLine) {
                $testConn = ($envLine -split '=', 2)[1].Trim('"').Trim()
            }
        }
    }
    if ($testConn) {
        Write-Host "[7/7] Ejecutando smoke de migracion (IMP-1 / MigratedSchema) contra TEST_POSTGRES_CONNECTION..." -ForegroundColor Cyan
        $env:TEST_POSTGRES_CONNECTION = $testConn
        dotnet test "$rootDir\CommandCenter.Tests\CommandCenter.Tests.csproj" -c Release --no-build --filter "FullyQualifiedName~MigratedSchema" 2>&1 | Select-Object -Last 3
        if ($LASTEXITCODE -ne 0) {
            throw "build-release abortado: el smoke de migracion (IMP-1/MigratedSchema) fallo en el release."
        }
        Write-Host "Smoke de migracion OK." -ForegroundColor Green
    } else {
        Write-Host "[7/7] Smoke de migracion omitido (TEST_POSTGRES_CONNECTION no definida)." -ForegroundColor Yellow
    }

    $iscc = $env:ISCC_PATH
    if ($iscc -and (Test-Path $iscc)) {
        $setupIss = Join-Path $rootDir "installer\setup.iss"

        $nssmPath = Join-Path $rootDir "installer\nssm.exe"
        if (Test-Path $nssmPath) {
            $nssmHash = (Get-FileHash -Path $nssmPath -Algorithm SHA256).Hash
            $expectedHash = "F689EE9AF94B00E9E3F0BB072B34CAAF207F32DCB4F5782FC9CA351DF9A06C97"
            if ($nssmHash -ne $expectedHash) {
                throw "ABORTANDO: El hash de nssm.exe ($nssmHash) no coincide con el esperado ($expectedHash)."
            }
            Write-Host "Hash de nssm.exe verificado OK." -ForegroundColor Green
        }

        $installerOutput = Join-Path $rootDir "dist_installer"
        New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
        Write-Host "Compilando instalador con Inno Setup (ISCC)..." -ForegroundColor Cyan
        & $iscc "/O$installerOutput" $setupIss
        if ($LASTEXITCODE -ne 0) {
            throw "iscc.exe fallo al compilar el instalador (codigo $LASTEXITCODE)."
        }
        $builtInstaller = Get-ChildItem $installerOutput -Filter *.exe | Select-Object -First 1
        if ($builtInstaller -and $env:SIGNTOOL_PATH -and (Test-Path $env:SIGNTOOL_PATH) -and $env:CODE_SIGNING_PFX -and (Test-Path $env:CODE_SIGNING_PFX)) {
            Write-Host "Firmando instalador: $($builtInstaller.Name)..." -ForegroundColor Cyan
            $certPassSecure = ConvertTo-SecureString -String $env:CODE_SIGNING_PASSWORD -AsPlainText -Force
            $cert = Import-PfxCertificate -FilePath $env:CODE_SIGNING_PFX -Password $certPassSecure -CertStoreLocation Cert:\CurrentUser\My
            & $env:SIGNTOOL_PATH sign /fd SHA256 /tr http://timestamp.digicert.com /td sha256 /sha1 $cert.Thumbprint $builtInstaller.FullName
            $exitCode = $LASTEXITCODE
            Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $cert.Thumbprint | Remove-Item -ErrorAction SilentlyContinue
            if ($exitCode -ne 0) {
                Write-Host "ADVERTENCIA: signtool fallo; el instalador queda sin firmar." -ForegroundColor Yellow
            } else {
                Write-Host "Instalador firmado correctamente." -ForegroundColor Green
            }
        } else {
            Write-Host "Firma omitida: configure SIGNTOOL_PATH y CODE_SIGNING_PFX (opcional)." -ForegroundColor Yellow
        }
    } else {
        Write-Host "ISCC no configurado (env ISCC_PATH); se omite la compilacion del instalador." -ForegroundColor Yellow
    }

    Write-Host "=== Publicación Autónoma completada exitosamente ===" -ForegroundColor Green
} finally {
    if ($lockStream) {
        $lockStream.Dispose()
        Remove-Item $lockPath -ErrorAction SilentlyContinue
    }
}
