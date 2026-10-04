# OCR de Facturas de Proveedor — Digitalización y Carga Automática

Objetivo: cargar facturas de proveedor por imagen/PDF/cámara usando un motor OCR open source (Tesseract) con preprocesamiento de imagen (OpenCV), extracción heurística de filas (plantilla del proveedor + fallback genérico) en backend, y revisión lado a lado en WPF (imagen con zoom + grilla editable con resaltado por confianza) antes de aprobar. Branch: V0.15. Entrega: work units en V0.15 (chain stacked-to-main cacheada de 8.144/8.145/8.146). SDD: `openspec/changes/2026-10-04-supplier-invoice-ocr/`. RDD: off (clone-local). Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoice|FullyQualifiedName~Ocr"` · build `dotnet build CommandCenter.slnx -c Release`.

## Specs

Fuente: pedido del usuario, verbatim (L1). Decisiones de diseño en L2 (supuestos marcados).

- **S1 (Motor OCR Open Source)**: "El sistema integrará una librería de código abierto líder en la industria (ej. Tesseract OCR) para la extracción de texto."
  - Tesseract 5.x (Apache-2.0) detrás de `IOcrEngine` (tests determinísticos con TSV/stub; smoke nativo gated). Estado del engine aislado por interfaz.
- **S2 (Preprocesamiento de Imagen)**: "Antes de pasar por el OCR, la imagen debe ser procesada (usando herramientas como OpenCV o Magick) para mejorar la precisión: escalado a escala de grises, binarización, aumento de contraste y corrección de inclinación (deskew)."
  - OpenCvSharp4: escala de grises → contraste (CLAHE) → binarización (Otsu con fallback adaptativo) → deskew (ángulo por Hough/minAreaRect). Pasos como funciones puras testeables con imágenes sintéticas.
- **S3 (Extracción y Plantillas (Parsing))**: "El backend debe utilizar expresiones regulares (Regex) o reglas heurísticas basadas en la 'Planilla de Mapeo del Proveedor' (previamente definida) para identificar dónde están las columnas de cantidad, descripción y precio dentro del bloque de texto escaneado."
  - Parser sobre word-boxes (TSV de Tesseract): fila por clustering Y, columnas por alineación X contra keywords de la plantilla (`SupplierColumnMapping` enviada por el cliente cuando hay proveedor) con fallback genérico (Descripción/Nombre/Producto · Cant/Cantidad · Precio/Costo/Unitario/Importe · Código/SKU/Ref · Barras/EAN); regex numérico culture-tolerant (coma/punto); confianza por campo = agregación de las palabras que lo componen.
- **S4 (Captura de Documento)**: "La interfaz debe permitir adjuntar un archivo (PNG/JPG/PDF) o utilizar la cámara del dispositivo para tomar la foto."
  - WPF: botón "Escanear factura (OCR)" (png/jpg/jpeg/pdf, ≤20 MB, PDF ≤5 páginas) + diálogo de cámara (OpenCvSharp `VideoCapture`, degradación elegante si no hay cámara). La ruta tabular existente (xlsx/csv/xml) no cambia.
- **S5 (Validación Lado a Lado)**: "la pantalla de 'Staging' (pre-visualización) debe dividirse en dos: Izquierda: La imagen original escaneada (con zoom). Derecha: La tabla de datos extraídos editable."
  - Split view en `SupplierInvoiceView` cuando la factura staged proviene de OCR: izquierda previews PNG renderizadas por el backend (navegación por página, zoom por rueda/botones); derecha la grilla existente (editable, già con márgenes y aprobación).
- **S6 (Corrección de Errores)**: "Cualquier campo donde el OCR tenga un nivel de confianza bajo (Low Confidence Score) debe resaltarse en amarillo o rojo para obligar al cajero a verificarlo visualmente contra la imagen antes de aprobar la carga."
  - Confianzas por campo persistidas en la línea (`OcrNameConfidence`, `OcrQuantityConfidence`, `OcrUnitCostConfidence`, numeric(5,2) nullable, porcentaje 0–100); resaltado de celdas: amarillo 60–85, rojo <60 (constantes `Core.Common.OcrConfidence`); solo líneas OCR.
- **S7 (Decisiones de diseño / supuestos)**:
  - OCR + preprocesamiento + parsing corren en **backend** (criterios "Procesamiento y Backend"); el cliente captura, sube y revisa. (supuesto: revisión = grilla staged existente + panel de imagen, no un wizard nuevo.)
  - Líneas OCR stagean **sin plantilla**: `StageSupplierInvoiceRequestDto.OcrSourced = true` relaja el requisito de mapping de primera importación (la persistencia de la plantilla de archivos no cambia).
  - Previews las sirve el backend (PNG base64 en la respuesta) → el cliente no agrega lector PDF; para imágenes la preview muestra lo que el OCR vio.
  - Cámara del cliente vía OpenCvSharp4 (evita bump de TFM a WinRT; trade-off ~45 MB de nativos, documentado).
  - tessdata español+inglés: paquete de datos NuGet si existe; si no, vendored bajo `Backend.API/tessdata` (documentado, copiado a output).
  - Límites v1: 20 MB de subida, 5 páginas de PDF; extracción best-effort de RIF/nombre de proveedor para prefill (nunca auto-commit).
  - Web.Frontend fuera de alcance (el módulo es WPF-only).

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1-S7 | inline (orchestrator) | Doc ODD + artefactos SDD + commit de planificación | hecho — commit de planificación (hash se registra en T2) |
| T2 | S1-S2 | delegated (writer) | Backend OCR core: paquetes + `IOcrEngine`/Tesseract + preprocesamiento OpenCV + raster PDF + tessdata + tests | hecho — verificado PASS WITH WARNINGS (build 0/0; OCR 24/24 [22 propios]; suite 1673/1673; smoke nativo OK; sin vulnerabilidades); commit 6dafd32 |
| T3 | S3 | delegated (writer) | Parser heurístico word-boxes → filas/columnas/confianza + tests canned TSV | hecho — verificado PASS WITH WARNINGS (build 0/0; parser 19/19; suite 1692/1692); commit 0580da6 |
| T4 | S3, S5 | delegated (writer) | Endpoint `POST /api/supplier-invoices/ocr-extract` + DTOs + DI + límites/RBAC/previews/RIF + tests | hecho — verificado PASS WITH WARNINGS (build 0/0; 15/15; suite 1707/1707); commit 1df554b |
| T5 | S6 | delegated (writer) | Persistencia de confianzas (entidad/DTO/migración) + `OcrSourced` en staging + tests | hecho — verificado PASS WITH WARNINGS (build 0/0; 108/108; suite 1712/1712; migración 20261004174140); commit en L9 |
| T6 | S4-S6 | delegated (writer) | Cliente: servicios upload + VM flujo OCR (extracción→stage→preview/zoom/cámara) + tests headless | pendiente |
| T7 | S4-S6 | delegated (writer) | UI WPF: split view + zoom + resaltado por confianza + diálogo de cámara + wiring | pendiente |
| T8 | S1-S6 | delegated (verify) + inline | Verificación independiente por slice + final + ANEXO 8.147 | pendiente |

Estrategia de entrega: `ask-on-risk` → cadena **stacked-to-main** cacheada (misma política de la cadena V0.15 vigente).

## Log

- L1 (2026-10-04) — Pedido del usuario, verbatim:

> Implementación de OCR Open Source para Digitalización y Carga Automática de Facturas
>
> Objetivo:
> Habilitar la carga de facturas de proveedores a través de imágenes (fotos o escaneos). El sistema utilizará un motor OCR de código abierto para extraer los datos estructurados (Proveedor, RIF, Códigos, Cantidades y Costos) y pre-llenar la tabla de revisión (Staging), minimizando la entrada manual de datos.
>
> Criterios de Aceptación (Procesamiento y Backend):
>
> Motor OCR Open Source: El sistema integrará una librería de código abierto líder en la industria (ej. Tesseract OCR) para la extracción de texto.
>
> Preprocesamiento de Imagen: Antes de pasar por el OCR, la imagen debe ser procesada (usando herramientas como OpenCV o Magick) para mejorar la precisión: escalado a escala de grises, binarización, aumento de contraste y corrección de inclinación (deskew).
>
> Extracción y Plantillas (Parsing): El backend debe utilizar expresiones regulares (Regex) o reglas heurísticas basadas en la "Planilla de Mapeo del Proveedor" (previamente definida) para identificar dónde están las columnas de cantidad, descripción y precio dentro del bloque de texto escaneado.
>
> Criterios de Aceptación (Interfaz de Usuario / UI):
> 4. Captura de Documento: La interfaz debe permitir adjuntar un archivo (PNG/JPG/PDF) o utilizar la cámara del dispositivo para tomar la foto.
> 5. Validación Lado a Lado (Side-by-Side): Dado que el OCR nunca es 100% preciso, la pantalla de "Staging" (pre-visualización) debe dividirse en dos:
>
> Izquierda: La imagen original escaneada (con zoom).
>
> Derecha: La tabla de datos extraídos editable.
>
> Corrección de Errores (Human-in-the-loop): Cualquier campo donde el OCR tenga un nivel de confianza bajo (Low Confidence Score) debe resaltarse en amarillo o rojo para obligar al cajero a verificarlo visualmente contra la imagen antes de aprobar la carga.

- L2 (2026-10-04) — Evidencia de exploración (sesión + grep): CI corre en **windows-2025** (nativos de Tesseract/OpenCV/PDFium llegan por NuGet — sin apt); gestión central de paquetes (`Directory.Packages.props`, con cultura de pins de vulnerabilidades y gate `dotnet list package --vulnerable`); el cliente WPF ya trae ZXing/MaterialDesign, TFM `net10.0-windows` (bump a WinRT sería cascadeante → cámara vía OpenCvSharp); **sin** captura de cámara previa en el repo; `UpsertColumnMappingAsync` (`SupplierInvoiceService.Mapping.cs:12`) exige plantilla en la primera importación de un proveedor → el flujo OCR necesita `OcrSourced`; el filtro del file picker es tabular (xlsx/csv/xml) y `IDialogService`/`IFilePickerDialog` ya están abstraídos; la vista de staging y su VM existen con BindingProxy y grilla editable (base para el side-by-side).
- L3 (2026-10-04) — Plan SDD creado (proposal + 3 specs + design + tasks + state) y doc ODD. Próximo: T2 (backend OCR core) delegado a writer con verificación independiente.
- L4 (2026-10-04) — Aclaración del mantenedor, verbatim: "Sin camara se debe cargar el 'Archivo' de la imagen, procede con T2". Interpretación registrada: el camino de archivo (selector PNG/JPG/PDF) es independiente de la cámara; la cámara es opcional y su ausencia no bloquea la carga de la imagen (alineado con S4/S7 y el escenario "No camera available → file path only").
- L5 (2026-10-04) — T2 completado y verificado independientemente (PASS WITH WARNINGS; 0 defectos funcionales). Implementado: paquetes centrales (Tesseract 5.2.0, OpenCvSharp4 + runtime.win 4.13.0.20260627, PDFtoImage 5.4.0), `IOcrEngine` + records, bandas `OcrConfidence` (60/85/0), `ImagePreprocessor` (grises→CLAHE→Otsu/adaptativo→deskew Hough/minAreaRect; convención de signo testeada; PNG codec), `TesseractOcrEngine` (lazy, serializado por semáforo, datapath ctor→`TESSDATA_PREFIX`→base/tessdata), decoders imagen/PDF (cap 5 páginas antes de renderizar; pragma CA1416 localizado y justificado) y dispatcher por extensión, DI singleton, flag CI `TEST_OCR_NATIVE`. tessdata vendido (tessdata_fast spa+eng; SHA256/tamaños re-verificados byte-exact contra SOURCES.md; ~6.4 MB). Verificación: build 0/0; enfocados OCR 24/24 (22 propios + 2 matches incidentales del filtro — usar filtro por clase en los próximos slices); suite completa 1673/1673; `dotnet list --vulnerable` limpio; smoke nativo real ejecutado: `words=4: FACTURA | PROVEEDOR | TOTAL | 12345`. Residuales: `OcrConfidenceTests` no fija 60/85 exactos; caminos sin test (fallo de `EncodePng`, PDF basura, dispatch `.jpg`/`.jpeg`, excepción por traineddata ausente, precedencia de override, disposición bajo excepción) — cubrir en T8 si conviene; Tesseract 5.2.0 (2022); serialización global del engine (v1). Nota de proceso: el primer dispatch del writer murió por error de conexión del proveedor tras escribir un esqueleto parcial; el retry lo completó y validó (sin residuos observables según el verificador). Commit del slice 1: 6dafd32.
- L6 (2026-10-04) — T3 completado (writer; RED 19 fallas → GREEN) + verificación independiente PASS WITH WARNINGS (0 defectos funcionales): parser puro `InvoiceTableParser` (clustering Y 0.6×altura mediana; encabezado con ≥2 columnas — nombres de plantilla consumen palabras antes del fallback genérico; bandas por midpoints con extremo abierto; anchors heredados en páginas de continuación; encabezados repetidos excluidos; filas sobre el encabezado fuera; numérico espejo exacto del parseo tabular del cliente; confianza = mínimo de palabras contribuyentes, 1 decimal; no resuelto = 0) + `OcrExtractedLineDto` (8 miembros). Build 0/0; filtro de clase 19/19; suite 1692/1692 (+19). Commits: T2 = 6dafd32; T3 = hash en L7. Residuales/notas: (a) tokens con punto solo (`"4.250"`) parsean 4.25 por espejo intencional con la ruta tabular — riesgo de forma de datos en facturas es-VE con miles por punto; decisión para T8/mantenedor (cambiarlo divergiría de la ruta xlsx o exigiría tocar ambos caminos); (b) bandas abiertas pueden surfacear filas de totales/pie como líneas de baja confianza (mitigado por la revisión humana); (c) clamp 0–100 y regla ≥2 anchors sin pin de test directo (cubrir en T8 si conviene); (d) parser 524 líneas físicas (458 non-blank), apenas sobre el objetivo 300–500 (clase cohesiva).
- L7 (2026-10-04) — T4 completado (writer; RED 10 fallas → GREEN) + verificación independiente PASS WITH WARNINGS (0 blockers; 7/7): DTOs request/result, `IOcrExtractionService` + `OcrExtractionService` (guardas exactas nombre/vacío/extensión/20 MB; decode → previews = PNG originales en orden de página antes de preprocesar; por página preprocess→engine→parser con mapping opcional; pistas RIF regex y nombre de proveedor best-effort de página 1; sin DB), endpoint multipart `POST /api/supplier-invoices/ocr-extract` (campos planos; mapping solo con name+quantity+unitCost; 200/400/403; RBAC de clase) y DI (`InvoiceTableParser` singleton; `IOcrExtractionService` scoped). Build 0/0; enfocados 15/15; suite 1707/1707 (+15). Commits: T3 = 0580da6; T4 = hash en L8. Residuales: rama de mapping por formulario y guard de archivo faltante sin cobertura HTTP; rechazo >5 páginas/contenido corrupto depende del middleware (inspeccionado, no ejecutado); boot DI de producción no ejercitado local (Postgres gated); previews base64 sin tope de tamaño de respuesta; `SupplierId` viaja pero no se usa server-side (el mapping llega plano); deriva menor de mensaje ("El archivo es obligatorio." vs "El nombre de archivo es obligatorio."); heurística de nombre de proveedor puede capturar logos/títulos (solo pista).
- L8 (2026-10-04) — T5 completado (writer; RED compile-level → GREEN) + verificación independiente PASS WITH WARNINGS (0 defectos): entidad + DbContext + migración aditiva `20261004174140_AddSupplierInvoiceLineOcrConfidence` (3 columnas nullable numeric(5,2); Down acotado; snapshot solo esas propiedades); DTOs (`OcrSourced=false` en request; 3 confianzas nullable con defaults en `StageLineDto`; append en `SupplierInvoiceLineDto` con call sites mecánicos); staging: gate `ColumnMapping is not null || !OcrSourced` (OCR+null omite el upsert; OCR+mapping y tabular sin cambios); confianzas persistidas solo para líneas OCR (tabular fuerza null) y mapeadas en `ToLineDto`. Build 0/0; enfocados `~SupplierInvoice` 108/108; suite 1712/1712 (+5). Commits: T4 = 1df554b; T5 = hash en L9. Residuales: Postgres gated sin ejecutar local (forma de columnas solo por modelo+migración); `OcrSourced` es client-provided (diseño explícito D11; money/apply siguen zero-trust); confianzas sin clamp 0–100 server-side (solo display; candidato a endurecer en T8); el filtro preexistente `IsReadableLine` descarta filas sin code/barcode/name — riesgo de integración para T6/T7: la UI debe bloquear el stage si una fila OCR no tiene identificador legible (evitar descarte silencioso).
