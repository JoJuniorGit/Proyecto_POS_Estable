# Performance de facturas de proveedor (PERF-01 staging + PERF-02 apply)

Objetivo: tramo 8.155 del roadmap de auditoría — PERF-01 (N+1 en staging/matching: batch prefetch + fuzzy acotado) y PERF-02 (N+1 + saves en el apply: lecturas batch + alias consolidados, conservando el retry xmin spec-mandated). Change: `openspec/changes/2026-10-09-audit-supplier-invoice-performance/` (ANEXO 8.155). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (PERF-01 — staging batch)**: precarga única de barcodes→SKU y supplierCodes→productos (primer-por-Id); loop en memoria; fuzzy solo para no-emparejadas con nombre; `Take` acotado en candidatos trigram; semántica de matching idéntica. Evidencia: SQLite + interceptor (lecturas acotadas, no lineales). Detalle: `specs/staging-batch/spec.md`.
- **S2 (PERF-02 — apply I/O reducido)**: precarga trackeada de productos (1 consulta) + alias existentes (1 consulta) + upsert en memoria con UN save final (≤ N+2 saves); retry xmin por línea INTACTO (spec `supplier-invoice-apply` L64-73); parcial vs la letra de la auditoría (D2/G1). Detalle: `specs/apply-io/spec.md`.
- **S3 (evidencia)**: contadores de comandos con SQLite + `DbCommandInterceptor` (RED antes del fix) + regresiones de semántica.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1+S3 | delegated (writer) | Staging batch + fuzzy acotado + tests de evidencia | hecho — RED 4/11 lineales→GREEN 4/4 constantes; focused 32/32; suite 2165/2165; commit 5267c0e |
| T2 | S2+S3 | delegated (writer) | Apply: lecturas batch + alias consolidados + tests | hecho — RED SELECTs 6/10/14 y saves 5/9/13→GREEN 4/4/4 y 3/5/7 (N+1); suite 2171/2171; commit 83190b3 |
| T2b | W1 verificación | inline | Refresco del caché de producto tras el retry xmin + reproducción gated real | hecho — RED attach-graph contra Postgres local→GREEN; suite CON Postgres 2172/2172; commit f4ede27 |
| T3 | S1-S2 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.155 + cierre | hecho — PASS WITH WARNINGS (6/6 COMPLIANT; W1 cerrado por T2b; W2 saldado con Postgres local); cobertura 0.8886/0.9057/0.8621 exit 0; ANEXO 8.155; commit de cierre en L6 |

## Log

- L1 (2026-10-09) — Pedido del usuario, verbatim: "Haz 8.155".
- L2 (2026-10-09) — Verificación previa: PERF-01 CONFIRMADO (Staging.cs:40-47 por línea + Matching.cs:210-222 scan trigram sin cota); PERF-02 CONFIRMADO con matiz spec (Apply.cs:82-117 + 148-199: ~2N lecturas + ~2N saves; el savepoint por línea implementa la spec `supplier-invoice-apply` L64-73 "retry xmin sobre entidad fresca" — el save único la rompería salvo rediseño con retry de operación completa → parcial-por-diseño + G1). Decisiones D1 (primer-por-Id en diccionarios), D2 (parcial spec-mandated), D3 (fuzzy por línea, Take acotado). Tests spec'eados con SQLite + interceptor (no hay patrón previo de contador en el repo; SQLite es soportado por el contexto — guard xmin).
- L3 (2026-10-09) — Próximo: T1 delegado a writer (superficies: SupplierInvoiceService.Staging.cs, SupplierInvoiceService.Matching.cs, SupplierInvoiceStagingBatchingTests.cs nuevo + regresiones existentes si hace falta).
- L4 (2026-10-09) — T1 (5267c0e) y T2 (83190b3) completados por writers (test-first; suites 2162→2165→2171) + verificación independiente read-only PASS WITH WARNINGS: REQ-SIB-01/02/03 y REQ-AIO-01/02/03 COMPLIANT; contadores auditados (métrica de saves = SaveChangesAsync; statements SQL no cambian — divulgado); **W1 detectado**: tras el retry xmin, `productsById` retenía la instancia detachada (falla teórica para líneas posteriores con el mismo producto); INFOs registrados. Cobertura del verificador sin Postgres: Core 0.8639 / Sales 0.8718 / Inventory 0.8323 (exit 0).
- L5 (2026-10-09) — W1 CERRADA por T2b (f4ede27): fix = `ApplyApprovedLineAsync` devuelve el producto final y el loop refresca el caché; reproducción REAL contra PostgreSQL local (pos_test recreada fresca, patrón CI): test gated nuevo con 2 líneas al mismo producto + conflicto xmin → RED (falla del attach-graph sobre el producto detachado) → GREEN con fix; suite completa CON Postgres: 2172/2172; cobertura CON Postgres Core 0.8886 / Sales 0.9057 / Inventory 0.8621 (exit 0). El test no-gated con excepción fabricada se descartó (EF no expone construcción de IUpdateEntry; documentado en el verify-report).
- L6 (2026-10-09) — Cierre: verify-report.md + ANEXO 8.155 + state/tasks actualizados + commit de cierre (hash en git log). Entrega pendiente: push/PR/merge (decisión del mantenedor; cadena stacked-to-main cacheada). G1 abierto: PERF-02 parcial-por-diseño (save único exigiría retry de operación completa). Próximo tramo: 8.156 (PERF-04/05 + CLEAN-05) cuando se autorice.
