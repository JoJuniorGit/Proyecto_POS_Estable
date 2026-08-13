# =====================================================================
# Script auxiliar para descargar nssm.exe (Opcional)
# =====================================================================

$nssmUrl = "https://nssm.cc/release/nssm-2.24.zip"
$zipFile = "$PSScriptRoot\nssm-2.24.zip"
$extractDir = "$PSScriptRoot\nssm-temp"

Write-Host "Descargando NSSM desde $nssmUrl..." -ForegroundColor Cyan
try {
    Invoke-WebRequest -Uri $nssmUrl -OutFile $zipFile
    Expand-Archive -Path $zipFile -DestinationPath $extractDir -Force
    Copy-Item -Path "$extractDir\nssm-2.24\win64\nssm.exe" -Destination "$PSScriptRoot\nssm.exe" -Force

    Remove-Item $zipFile -Force
    Remove-Item $extractDir -Recurse -Force

    Write-Host "nssm.exe descargado exitosamente en $PSScriptRoot\nssm.exe" -ForegroundColor Green
}
catch {
    Write-Host "No se pudo descargar nssm.exe automáticamente. Inno Setup utilizará el fallback nativo de Windows (sc.exe)." -ForegroundColor Yellow
}
