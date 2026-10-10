# Proposal: Revisión simplificada de Facturas de Proveedor — número de factura OCR en lugar de preview + retiro de columnas de margen — ANEXO 8.160

## Contexto

Pedido del mantenedor (verbatim):

> En Facturas Proveedores (Desktop) Elimina la visualizacion de la factura y en su lugar agrega el numero de la factura que contiene el archivo / imagen de la factura, Elimina las clumnas de margen de ganancia tanto al detal y mayor

Verificación previa (de este plan):

| Punto | Veredicto | Evidencia |
|---|---|---|
| Preview OCR (imagen/zoom/páginas) | CONFIRMADO en Desktop | `SupplierInvoiceView.xaml` L217-283 (card 460px, `OcrSelectedPreviewBase64`, zoom 0.5–4.0, `OcrPageLabel`) + `Base64PngToImageSourceConverter` + estado en `SupplierInvoiceViewModel.Ocr.cs` |
| Número de factura | NO EXISTE hoy | ni en `OcrExtractionResultDto` ni en el staging; el OCR solo detecta RIF y nombre (`OcrExtractionService.DetectRif/DetectSupplierName`) |
| Columnas de margen | CONFIRMADO en Desktop | `SupplierInvoiceView.xaml` L442-453 ("Margen detal %" → `MarginRetailOverride`) y L455-466 ("Margen mayor %" → `MarginWholesaleOverride`); prefill de producto en `SupplierInvoiceService.Matching.cs` L210-211; uso en confirm `SupplierInvoiceService.Apply.cs` L148/235-242 |
| Consumidores de previews | SOLO Desktop | `PreviewPagesBase64` referenciado únicamente por VM + converter + tests + specs; Web/E2E sin referencias |

## Alcance

1. **S1**: retirar el panel de visualización de la factura (previews, zoom, navegación de páginas) de la vista Desktop y toda su maquinaria asociada (estado del VM, converter, handler wheel del code-behind).
2. **S2**: en su lugar, panel compacto con el **número de factura detectado por OCR** (best-effort) desde la imagen/PDF; placeholder "No detectado". Solo visualización (no persiste).
3. **S3**: eliminar las columnas "Margen detal %" y "Margen mayor %" de la grilla Desktop. Precios sugeridos y contrato de confirm intactos.
4. **S4 (derivado de S1)**: `ocr-extract` deja de devolver `PreviewPagesBase64` (sin consumidor) y agrega `DetectedInvoiceNumber` best-effort.

Fuera de alcance: persistencia del número de factura; cambios en la ruta tabular; cambios en confirm/apply/precios; migraciones.

## Enfoque y decisiones

- **D1 (S4)**: retiro de previews de punta a punta, no solo de la UI: el payload base64 por página (potencialmente MBs por escaneo) no tiene consumidor tras S1; conservarlo sería costo de red/CPU sin uso.
- **D2 (S2)**: detección espejo de RIF/nombre (regex sobre el texto conjunto de las palabras OCR); patrón conservador anclado a la keyword `FACTURA` con ventana acotada de no-dígitos; `null` cuando no hay match. Nunca persistido ni auto-aplicado.
- **D3 (S3)**: alcance Desktop únicamente: el prefill del margen por línea (`Matching`) y su uso en `Apply`/`CreateProductFromLine` quedan intactos; sin migración de datos.
- **D4 (supuesto)**: S2 es informativo (solo vista); si se pide trazabilidad del número, se agenda como tramo propio.

## Entrega y riesgos

- Estrategia: `ask-on-risk` → `stacked-to-main` (cacheada). Forecast ~350–550 líneas. Unidades: backend (T1) / desktop (T2) / cierre (T3).
- Riesgo principal: contrato `OcrExtractionResultDto` (mitigado: Desktop es el único consumidor y co-despliega; tests de endpoint/cliente se actualizan en las mismas unidades).
- RDD: off (clone-local) → verificación por focused + suite completa + cobertura.
