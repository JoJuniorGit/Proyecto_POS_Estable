# Tasks: Revisión simplificada de Facturas de Proveedor (8.160)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~350-550 |
| Budget risk | Medium |
| Chained PRs | Continúa la cadena V0.15 (slices por unidad) |
| Split | 1 unidad de implementación + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

Nota de partición: `OcrExtractionResultDto` es contrato compartido backend⇄Desktop; dividir el retiro de `PreviewPagesBase64` dejaría el build rojo entre slices → una sola unidad con commit atómico.

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. Implementación completa (T1) | `dotnet build CommandCenter.slnx -c Release` + `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoice|FullyQualifiedName~Ocr"` | Revertir DTO/servicio/VM/XAML/converter/tests |
| 2. Verificación + cierre (T2) | suite completa + `scripts/check-coverage.py` | Revertir docs |

## T1 (S1-S4) — Implementación: número de factura OCR, sin previews, sin columnas de margen

- [ ] 1.1 Backend: `OcrExtractionResultDto` (`PreviewPagesBase64` → `DetectedInvoiceNumber`), `InvoiceNumberPattern` + `DetectInvoiceNumber` best-effort, retiro del armado de previews, comentarios de frontera; tests de servicio y endpoint (RED → GREEN).
- [ ] 1.2 Desktop: VM OCR (retiro estado/comandos preview/zoom/páginas; `OcrDetectedInvoiceNumber` + wiring), XAML (card de preview fuera; panel "N.º de factura (OCR)" con `IsOcrSource`; columnas "Margen detal %"/"Margen mayor %" fuera), converter eliminado, code-behind sin handler wheel; tests de cliente/review (RED → GREEN).
- [ ] 1.3 Build 0/0 + focused verdes; commit `feat(8.160)`.

## T2 (S5) — Verificación + cierre

- [ ] 2.1 Suite completa + cobertura en umbral.
- [ ] 2.2 `verify-report.md` (por requisito) + estado del change + ANEXO 8.160 + tracker.
- [ ] 2.3 Commit de cierre `docs(8.160)`.

## Notes

- Tracker: `odd/tasks/facturas-proveedor-simplificacion-revision.md`.
- Los writers NO commitean; el padre commitea.
- Sin migraciones; sin cambios en confirm/apply; ruta tabular intacta.
