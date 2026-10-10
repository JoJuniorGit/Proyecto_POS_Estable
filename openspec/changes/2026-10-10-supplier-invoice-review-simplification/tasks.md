# Tasks: Revisión simplificada de Facturas de Proveedor (8.160)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~350-550 |
| Budget risk | Medium |
| Chained PRs | Continúa la cadena V0.15 (slices por unidad) |
| Split | 2 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. Backend número de factura + sin previews (T1) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~Ocr"` | Revertir DTO + OcrExtractionService + tests |
| 2. Desktop panel + sin márgenes (T2) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoice"` | Revertir XAML/VM/converter + tests |
| 3. Verificación + cierre (T3) | suite completa + `scripts/check-coverage.py` | Revertir docs |

## T1 (S2, S4) — Backend: detección de número de factura + retiro de previews

- [ ] 1.1 `OcrExtractionResultDto`: `PreviewPagesBase64` → `DetectedInvoiceNumber` (`string?`); comentario del DTO y frontera `IOcrExtractionService` alineados.
- [ ] 1.2 `OcrExtractionService`: retiro del armado de previews; `InvoiceNumberPattern` + `DetectInvoiceNumber` best-effort (mismo criterio que RIF).
- [ ] 1.3 Tests: RED (detección + ausencia de previews en servicio y endpoint) → GREEN; focused `~Ocr` + build 0/0; commit.

## T2 (S1, S2, S3) — Desktop: panel de número de factura, sin previews, sin columnas de margen

- [ ] 2.1 VM OCR: retiro de estado/comandos de preview/zoom/páginas; `OcrDetectedInvoiceNumber` + wiring en `ApplyOcrReviewState`/`ClearOcrReviewState`.
- [ ] 2.2 XAML: retiro del card de preview (y converter/recurso + handler wheel del code-behind); panel compacto "N.º de factura (OCR)" visible con `IsOcrSource`; columnas "Margen detal %"/"Margen mayor %" retiradas (precios sugeridos quedan).
- [ ] 2.3 Tests: RED (estado OCR nuevo + higiene de retiros) → GREEN; focused `~SupplierInvoice` + build 0/0; commit.

## T3 (S5) — Verificación + cierre

- [ ] 3.1 Suite completa + cobertura en umbral.
- [ ] 3.2 `verify-report.md` (por requisito) + estado del change + ANEXO 8.160 + tracker.
- [ ] 3.3 Commit de cierre `docs(8.160)`.

## Notes

- Tracker: `odd/tasks/facturas-proveedor-simplificacion-revision.md`.
- Los writers NO commitean; el padre commitea por unidad.
- Sin migraciones; sin cambios en confirm/apply; ruta tabular intacta.
