# Facturas de Proveedor — Revisión simplificada: número de factura OCR sin preview + columnas de margen retiradas

Objetivo: en Facturas Proveedores (Desktop) retirar la visualización de la factura (previews con zoom/navegación) y mostrar en su lugar el número de factura detectado por OCR desde la imagen/PDF; y eliminar las columnas de margen de ganancia (detal y mayor) de la grilla de revisión. Branch: V0.15. Entrega: work units en V0.15 (cadena stacked-to-main cacheada de la cadena vigente). SDD: `openspec/changes/2026-10-10-supplier-invoice-review-simplification/`. RDD: off (clone-local). Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoice|FullyQualifiedName~Ocr"` · build `dotnet build CommandCenter.slnx -c Release`.

## Specs

Fuente: pedido del usuario, verbatim (L1). Supuestos marcados.

- **S1 (Retiro de la visualización)**: "En Facturas Proveedores (Desktop) Elimina la visualizacion de la factura" → alcance: el panel de previews (imagen base64, zoom 0.5–4.0, navegación por página) se retira de `SupplierInvoiceView`; la maquinaria de estado en el VM (zoom, páginas, converter, wheel del code-behind) se elimina.
- **S2 (Número de factura en su lugar)**: "en su lugar agrega el numero de la factura que contiene el archivo / imagen de la factura" → panel compacto donde estaba el preview, visible solo con origen OCR, con el número detectado best-effort desde la imagen/PDF (ej. `FACTURA: *0238317` → `0238317`); placeholder "No detectado". (supuesto: solo visualización — no se persiste ni viaja al staging/confirm; la ruta tabular xlsx/csv/xml no cambia.)
- **S3 (Sin columnas de margen)**: "Elimina las clumnas de margen de ganancia tanto al detal y mayor" → se quitan de la grilla las columnas "Margen detal %" y "Margen mayor %"; se conservan "Precio detal sugerido USD" / "Precio mayor sugerido USD"; el prefill del margen por línea y el contrato de confirm no cambian (alcance Desktop).
- **S4 (Derivado de S1)**: sin consumidor del preview, el endpoint `ocr-extract` deja de generar y devolver `PreviewPagesBase64`; `OcrExtractionResultDto` la reemplaza por `DetectedInvoiceNumber` best-effort. Contrato interno Desktop⇄API (co-desplegado).
- **S5 (Proceso)**: deltas de specs (`supplier-invoice-ocr-extraction`, `supplier-invoice-ocr-review`, `supplier-invoice-staging`), build 0/0, suite 100%, cobertura en umbral, ANEXO 8.160.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| Plan | S1-S5 | inline | Tracker + artefactos SDD + commit de planificación | hecho — commit de planificación (hash se registra al cerrar T1) |
| T1 | S2, S4 | delegated (writer) | Backend: `DetectInvoiceNumber` best-effort + retiro de previews (DTO/servicio/comentario de frontera) + tests | pendiente |
| T2 | S1, S2, S3 | delegated (writer) | Desktop: panel "N.º de factura", retiro de previews/zoom/converter, columnas de margen fuera + tests | pendiente |
| T3 | S5 | inline (+ acciones delegadas) | Verificación final (suite completa + cobertura), verify-report, ANEXO 8.160, cierre | pendiente |

Estrategia de entrega: `ask-on-risk` → cadena stacked-to-main cacheada (política vigente de la cadena V0.15). Forecast ~350–550 líneas autoradas.

## Log

- L1 (2026-10-10) — Pedido del usuario, verbatim:

> En Facturas Proveedores (Desktop) Elimina la visualizacion de la factura y en su lugar agrega el numero de la factura que contiene el archivo / imagen de la factura, Elimina las clumnas de margen de ganancia tanto al detal y mayor

- L2 (2026-10-10) — Decisiones/supuestos del tramo (exploración previa al plan):
  - S2 es solo visualización: el número detectado no se persiste ni afecta staging/confirm (supuesto; si el mantenedor pide trazabilidad, es un tramo propio con migración).
  - Previews fuera de punta a punta (S4): tras retirar la UI ningún consumidor usa `PreviewPagesBase64` (solo VM + converter + tests); mantener el payload base64 por escaneo sería desperdicio de red/CPU (MBs por foto).
  - S3 con alcance Desktop: solo se retiran las columnas; el prefill del margen (producto) y su uso en confirm/apply quedan intactos → sin migraciones.
  - La ruta tabular (xlsx/csv/xml) no tiene número de factura en el archivo → sin panel para origen no-OCR; la ruta no cambia.
  - La spec 8.147-S5/S6 (revisión lado a lado con imagen) queda parcialmente reemplazada por decisión del mantenedor: el panel de número la sustituye.
