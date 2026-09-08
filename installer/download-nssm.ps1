# =====================================================================
# Script auxiliar para descargar nssm.exe (Opcional)
# =====================================================================

$nssmUrl = "https://nssm.cc/release/nssm-2.24.zip"
$zipFile = "$PSScriptRoot\nssm-2.24.zip"
$extractDir = "$PSScriptRoot\nssm-temp"

# 8.16-I06: pin de integridad. Verifica que el binario descargado coincida con el nssm.exe
# versionado en el repositorio (anti supply-chain). Si difiere, se aborta el reemplazo.
$expectedSha256 = "F689EE9AF94B00E9E3F0BB072B34CAAF207F32DCB4F5782FC9CA351DF9A06C97"

Write-Host "Descargando NSSM desde $nssmUrl..." -ForegroundColor Cyan
try {
    Invoke-WebRequest -Uri $nssmUrl -OutFile $zipFile
    Expand-Archive -Path $zipFile -DestinationPath $extractDir -Force

    $downloadedNssm = "$extractDir\nssm-2.24\win64\nssm.exe"
    $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $downloadedNssm).Hash
    if ($actualSha256 -ne $expectedSha256) {
        throw "Integridad no verificada: SHA-256 esperado '$expectedSha256' pero se obtuvo '$actualSha256'. Se omite el reemplazo de nssm.exe."
    }

    Copy-Item -Path $downloadedNssm -Destination "$PSScriptRoot\nssm.exe" -Force

    Remove-Item $zipFile -Force
    Remove-Item $extractDir -Recurse -Force

    Write-Host "nssm.exe descargado y verificado exitosamente en $PSScriptRoot\nssm.exe" -ForegroundColor Green
}
catch {
    Write-Host "No se pudo descargar/verificar nssm.exe automáticamente. Inno Setup utilizará el fallback nativo de Windows (sc.exe)." -ForegroundColor Yellow
}
