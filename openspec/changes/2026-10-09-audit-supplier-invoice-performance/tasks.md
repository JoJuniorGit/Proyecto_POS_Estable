# Tasks: Performance de facturas de proveedor (8.155)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~500-650 |
| Budget risk | Medium-High |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 2 unidades + T2b + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. PERF-01 staging batch (T1) | `--filter "FullyQualifiedName~SupplierInvoiceStaging\|FullyQualifiedName~SupplierInvoiceMatching"` | Revertir Staging/Matching + tests |
| 2. PERF-02 apply I/O (T2) | `--filter "FullyQualifiedName~SupplierInvoiceApply"` | Revertir Apply + tests |
| 3. Verificación + cierre (T3) | suite completa (con Postgres) + cobertura | Revertir docs |

## T1 (S1) — PERF-01: staging en lote

- [x] 1.1 Precarga batch (2 consultas, primer-por-Id, claves normalizadas); loop en memoria; fuzzy solo para no-emparejadas con nombre.
- [x] 1.2 `Take(100)` acotado en candidatos trigram (orden preservado).
- [x] 1.3 Tests: 3 nuevos (SQLite + interceptor; fuzzy Times.Once/Never; empates por Id) + 32 regresiones verdes sin cambios. RED 4→11 lineales → GREEN 4/4.
- [x] 1.4 Build 0/0 + focused + suite 2165/2165; commit `5267c0e`.

## T2 (S2) — PERF-02: apply con lecturas batch y saves consolidados

- [x] 2.1 Precarga trackeada de productos (1 consulta; misma excepción) + retry con re-lectura fresca.
- [x] 2.2 Alias: precarga (1 consulta) + plan en memoria + guardado final único (sin SaveChanges nuevo); creación intacta.
- [x] 2.3 Tests: 6 nuevos (SELECTs 6/10/14→4/4/4; saves 5/9/13→3/5/7; LWW; rollback; borrado) + regresiones verdes.
- [x] 2.4 Build 0/0 + focused 37/37 + suite 2171/2171; commit `83190b3`.

## T2b (W1 de verificación) — Refresco del caché tras el retry xmin

- [x] 2b.1 Fix: `ApplyApprovedLineAsync` devuelve el producto final; el loop refresca `productsById` (retenía la instancia detachada).
- [x] 2b.2 Reproducción real: test gated nuevo (2 líneas al mismo producto + conflicto xmin). RED sin fix (attach-graph sobre el producto detachado) → GREEN con fix, contra PostgreSQL local. Test no-gated fabricado descartado (EF no expone `IUpdateEntry`; documentado).
- [x] 2b.3 Suite CON Postgres real: 2172/2172; build 0/0; commit `f4ede27`.

## T3 (S1-S2) — Verificación + cierre

- [x] 3.1 Verificador independiente: 6/6 COMPLIANT; contadores auditados; W1 detectado (cerrado por T2b); W2 saldado en el cierre (gated con Postgres local).
- [x] 3.2 Suite 2172/2172 CON Postgres + cobertura Core 0.8886 / Sales 0.9057 / Inventory 0.8621 (exit 0) + build 0/0.
- [x] 3.3 `verify-report.md` + ANEXO 8.155 + cierre del tracker + commit `docs(8.155)`.

## Notes

- Tracker: `odd/tasks/auditoria-facturas-performance.md`.
- D2/G1: PERF-02 parcial-por-diseño (spec `supplier-invoice-apply` L64-73); el save único exige rediseño con retry de operación completa (decisión del mantenedor).
- Los writers NO commitean; el padre commitea por unidad.
