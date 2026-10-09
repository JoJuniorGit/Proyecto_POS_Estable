# Performance de facturas de proveedor (PERF-01 staging + PERF-02 apply)

Objetivo: tramo 8.155 del roadmap de auditoría — PERF-01 (N+1 en staging/matching: batch prefetch + fuzzy acotado) y PERF-02 (N+1 + saves en el apply: lecturas batch + alias consolidados, conservando el retry xmin spec-mandated). Change: `openspec/changes/2026-10-09-audit-supplier-invoice-performance/` (ANEXO 8.155). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (PERF-01 — staging batch)**: precarga única de barcodes→SKU y supplierCodes→productos (primer-por-Id); loop en memoria; fuzzy solo para no-emparejadas con nombre; `Take` acotado en candidatos trigram; semántica de matching idéntica. Evidencia: SQLite + interceptor (lecturas acotadas, no lineales). Detalle: `specs/staging-batch/spec.md`.
- **S2 (PERF-02 — apply I/O reducido)**: precarga trackeada de productos (1 consulta) + alias existentes (1 consulta) + upsert en memoria con UN save final (≤ N+2 saves); retry xmin por línea INTACTO (spec `supplier-invoice-apply` L64-73); parcial vs la letra de la auditoría (D2/G1). Detalle: `specs/apply-io/spec.md`.
- **S3 (evidencia)**: contadores de comandos con SQLite + `DbCommandInterceptor` (RED antes del fix) + regresiones de semántica.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1+S3 | delegated (writer) | Staging batch + fuzzy acotado + tests de evidencia | pendiente |
| T2 | S2+S3 | delegated (writer) | Apply: lecturas batch + alias consolidados + tests | pendiente |
| T3 | S1-S3 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.155 + cierre | pendiente |

## Log

- L1 (2026-10-09) — Pedido del usuario, verbatim: "Haz 8.155".
- L2 (2026-10-09) — Verificación previa: PERF-01 CONFIRMADO (Staging.cs:40-47 por línea + Matching.cs:210-222 scan trigram sin cota); PERF-02 CONFIRMADO con matiz spec (Apply.cs:82-117 + 148-199: ~2N lecturas + ~2N saves; el savepoint por línea implementa la spec `supplier-invoice-apply` L64-73 "retry xmin sobre entidad fresca" — el save único la rompería salvo rediseño con retry de operación completa → parcial-por-diseño + G1). Decisiones D1 (primer-por-Id en diccionarios), D2 (parcial spec-mandated), D3 (fuzzy por línea, Take acotado). Tests spec'eados con SQLite + interceptor (no hay patrón previo de contador en el repo; SQLite es soportado por el contexto — guard xmin).
- L3 (2026-10-09) — Próximo: T1 delegado a writer (superficies: SupplierInvoiceService.Staging.cs, SupplierInvoiceService.Matching.cs, SupplierInvoiceStagingBatchingTests.cs nuevo + regresiones existentes si hace falta).
