<#
  clear-products.ps1 - Vacía todos los productos y datos de inventario relacionados.
  Elimina StockReservations, StockMovements, StockMovements_Archive y Products.
  Uso:
    .\docs\clear-products.ps1
    .\docs\clear-products.ps1 -ConnectionString "Host=localhost;Database=CommandCenterDb;Username=postgres;Password=123456"
    .\docs\clear-products.ps1 -Force   # omite la confirmación
#>
param(
    [string]$ConnectionString = "Host=localhost;Database=CommandCenterDb;Username=postgres;Password=123456",
    [switch]$Force
)
$ErrorActionPreference = "Stop"

if (-not $Force) {
    Write-Host ""
    Write-Host "=========================================================" -ForegroundColor Red
    Write-Host "  ADVERTENCIA: Se eliminaran TODOS los productos,        " -ForegroundColor Red
    Write-Host "  movimientos de stock, reservas y archivo de movimientos." -ForegroundColor Red
    Write-Host "  Esta accion NO se puede deshacer.                      " -ForegroundColor Red
    Write-Host "=========================================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "Conexion: $ConnectionString" -ForegroundColor Yellow
    Write-Host ""
    $confirm = Read-Host "Escriba 'CONFIRMAR' para continuar"
    if ($confirm -ne "CONFIRMAR") {
        Write-Host "Operacion cancelada." -ForegroundColor Cyan
        exit 0
    }
}

$psql = "psql"
if (-not (Get-Command "psql" -ErrorAction SilentlyContinue)) {
    $found = Get-ChildItem "C:\Program Files\PostgreSQL" -Recurse -Filter "psql.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*\bin" } |
        Select-Object -First 1 -ExpandProperty FullName
    if ($found) {
        $psql = $found
    } else {
        Write-Host "ERROR: psql no encontrado. Instale PostgreSQL o agregue psql al PATH." -ForegroundColor Red
        exit 1
    }
}

$parts = @{}
$ConnectionString -split ";" | ForEach-Object {
    $kv = $_ -split "=", 2
    if ($kv.Length -eq 2) { $parts[$kv[0].Trim()] = $kv[1].Trim() }
}

$env:PGPASSWORD = $parts["Password"]
$pgHost = $parts["Host"]
$db = $parts["Database"]
$user = $parts["Username"]
$port = if ($parts.ContainsKey("Port")) { $parts["Port"] } else { "5432" }

function Invoke-Psql {
    param([string]$SqlFile)
    $output = & $psql -h $pgHost -p $port -U $user -d $db -f $SqlFile 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql error: $output" }
    return $output
}

try {
    Write-Host ""
    Write-Host "Ejecutando limpieza..." -ForegroundColor Yellow

    $tmpCount = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $tmpCount -Value 'SELECT COUNT(*) FROM "Products";' -Encoding UTF8
    $countBefore = (Invoke-Psql $tmpCount | Select-String "\d+" | ForEach-Object { $_.Matches[0].Value } | Select-Object -First 1)
    Remove-Item $tmpCount -ErrorAction SilentlyContinue

    if (-not $countBefore) { $countBefore = "0" }
    Write-Host "  Productos encontrados: $countBefore" -ForegroundColor White

    $sqlFile = [System.IO.Path]::GetTempFileName()
    @'
BEGIN;
DELETE FROM "StockReservations";
DELETE FROM "StockMovements";
DELETE FROM "StockMovements_Archive";
DELETE FROM "Products" WHERE "ParentProductId" IS NOT NULL;
DELETE FROM "Products";
COMMIT;
'@ | Set-Content -Path $sqlFile -Encoding UTF8

    Invoke-Psql $sqlFile | Write-Host
    Remove-Item $sqlFile -ErrorAction SilentlyContinue

    $tmpCount2 = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $tmpCount2 -Value 'SELECT COUNT(*) FROM "Products";' -Encoding UTF8
    $countAfter = (Invoke-Psql $tmpCount2 | Select-String "\d+" | ForEach-Object { $_.Matches[0].Value } | Select-Object -First 1)
    Remove-Item $tmpCount2 -ErrorAction SilentlyContinue

    if (-not $countAfter) { $countAfter = "0" }

    Write-Host ""
    Write-Host "Resultado:" -ForegroundColor Green
    Write-Host "  Productos antes : $countBefore" -ForegroundColor White
    Write-Host "  Productos ahora : $countAfter" -ForegroundColor White
    Write-Host ""
    Write-Host "Limpieza completada." -ForegroundColor Green
}
catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
    exit 1
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}
