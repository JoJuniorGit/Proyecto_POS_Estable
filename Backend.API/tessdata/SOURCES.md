# tessdata vendido — origen y verificación (8.147-T2/D14)

Datos de idioma de Tesseract para el motor OCR del backend. Se descartan los paquetes NuGet de
datos (`Tesseract.Data.Spanish` no existe en NuGet; `Tesseract.Data.English` solo publica la
versión 4.0.0 en inglés), por lo que se vendieron ambos idiomas desde el repositorio oficial
`tesseract-ocr/tessdata_fast` (Apache-2.0), que es el dataset rápido recomendado para producción.

- Commit de origen: `87416418657359cb625c412a48b6e1d6d41c29bd` (rama `main`)
- URL base: https://github.com/tesseract-ocr/tessdata_fast

| Archivo | Origen (raw) | Tamaño (bytes) | SHA-256 |
|---|---|---|---|
| `eng.traineddata` | `https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/87416418657359cb625c412a48b6e1d6d41c29bd/eng.traineddata` | 4 113 088 | `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2` |
| `spa.traineddata` | `https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/87416418657359cb625c412a48b6e1d6d41c29bd/spa.traineddata` | 2 294 433 | `6f2e04d02774a18f01bed44b1111f2cd7f3ba7ac9dc4373cd3f898a40ea6b464` |

Los archivos se copian al output y a la publicación vía `Backend.API/Backend.API.csproj`
(`Content Include="tessdata\*.traineddata"`); `TesseractOcrEngine` los resuelve desde
`AppContext.BaseDirectory/tessdata` (o `TESSDATA_PREFIX`/constructor si se sobreescribe).
