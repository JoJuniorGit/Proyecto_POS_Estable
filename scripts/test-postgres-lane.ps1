<#
.SYNOPSIS
    Ejecuta el carril PostgreSQL real de CommandCenter contra un clúster aislado y temporal.

.DESCRIPTION
    8.201 (S3): cuando la credencial del PostgreSQL local no está disponible, este script crea un
    clúster desechable con initdb (trust local, puerto dedicado), corre los tests gated
    `~Postgres` con TEST_POSTGRES_CONNECTION apuntando a ese clúster y lo detiene al final.
    No toca el servidor PostgreSQL instalado ni sus credenciales.

.PARAMETER Port
    Puerto del clúster temporal. Default 55432.

.PARAMETER DataDir
    Directorio de datos del clúster. Default: %TEMP%\commandcenter-pg-lane.

.PARAMETER Cleanup
    Elimina el directorio de datos al terminar (por defecto se conserva para inspeccionar logs).

.EXAMPLE
    pwsh scripts/test-postgres-lane.ps1
    pwsh scripts/test-postgres-lane.ps1 -Port 55433 -Cleanup
#>
[CmdletBinding()]
param(
    [int]$Port = 55432,
    [string]$DataDir = (Join-Path $env:TEMP 'commandcenter-pg-lane'),
    [switch]$Cleanup
)

$ErrorActionPreference = 'Stop'

function Resolve-PgBin {
    if ($env:PG_BIN -and (Test-Path (Join-Path $env:PG_BIN 'initdb.exe'))) {
        return $env:PG_BIN
    }

    $candidates = Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Name -replace '\D', '') } -Descending

    foreach ($candidate in $candidates) {
        $bin = Join-Path $candidate.FullName 'bin'
        if (Test-Path (Join-Path $bin 'initdb.exe')) {
            return $bin
        }
    }

    throw "No se encontró PostgreSQL (initdb.exe). Instálelo o exporte PG_BIN."
}

$pgBin = Resolve-PgBin
$initdb = Join-Path $pgBin 'initdb.exe'
$pgCtl = Join-Path $pgBin 'pg_ctl.exe'
$pgIsReady = Join-Path $pgBin 'pg_isready.exe'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'CommandCenter.Tests\CommandCenter.Tests.csproj'
$startedHere = $false
$exitCode = 0

function Test-IsReady {
    & $pgIsReady -h localhost -p $Port -U postgres *> $null
    return $LASTEXITCODE -eq 0
}

function Wait-UntilReady([int]$TimeoutSeconds = 90) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-IsReady) { return $true }
        Start-Sleep -Milliseconds 700
    }
    return $false
}

try {
    if (-not (Test-Path (Join-Path $DataDir 'PG_VERSION'))) {
        if (Test-Path $DataDir) {
            Remove-Item $DataDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $DataDir | Out-Null
        Write-Host "[pg-lane] initdb en $DataDir"
        & $initdb -D $DataDir -U postgres --auth=trust --no-locale -E UTF8 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "initdb falló con código $LASTEXITCODE." }
    }

    if (-not (Test-IsReady)) {
        Write-Host "[pg-lane] iniciando PostgreSQL aislado en el puerto $Port"
        $inner = "& '$pgCtl' -D '$DataDir' -l '$DataDir\server.log' -o '-p $Port -c listen_addresses=localhost' start"
        Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile', '-Command', $inner) -WindowStyle Hidden
        $startedHere = $true

        if (-not (Wait-UntilReady)) {
            $log = Join-Path $DataDir 'server.log'
            throw "El clúster no respondió en 90s (¿puerto $Port ocupado?). Revise $log."
        }
    } else {
        Write-Host "[pg-lane] reutilizando el clúster aislado ya activo en el puerto $Port"
    }

    $env:TEST_POSTGRES_CONNECTION = "Host=localhost;Port=$Port;Database=postgres;Username=postgres"
    Write-Host "[pg-lane] TEST_POSTGRES_CONNECTION=Host=localhost;Port=$Port;Database=postgres;Username=postgres"
    Write-Host "[pg-lane] ejecutando carril ~Postgres"

    & dotnet test $testProject -c Release --filter "FullyQualifiedName~Postgres" --nologo
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item Env:\TEST_POSTGRES_CONNECTION -ErrorAction SilentlyContinue

    if ($startedHere -and (Test-Path (Join-Path $DataDir 'postmaster.pid'))) {
        Write-Host "[pg-lane] deteniendo el clúster aislado"
        & $pgCtl -D $DataDir stop -m fast | Out-Null
    }

    if ($Cleanup -and $startedHere -and (Test-Path $DataDir)) {
        Start-Sleep -Seconds 1
        Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit $exitCode
