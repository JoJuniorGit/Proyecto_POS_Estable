# Tasks: Performance de facturas de proveedor (8.155)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~500-650 |
| Budget risk | Medium-High |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 2 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. PERF-01 staging batch (T1) | `--filter "FullyQualifiedName~SupplierInvoiceStaging\|FullyQualifiedName~SupplierInvoiceMatching"` | Revertir Staging/Matching + tests |
| 2. PERF-02 apply I/O (T2) | `--filter "FullyQualifiedName~SupplierInvoiceApply"` | Revertir Apply + tests |
| 3. Verificación + cierre (T3) | suite completa + cobertura | Revertir docs |

## T1 (S1) — PERF-01: staging en lote

- [ ] 1.1 Precarga batch: barcodes→productos (1 consulta) y supplierCodes→productos (1 consulta) con "primer producto por Id"; loop en memoria; fuzzy solo para líneas sin match exacto (y sin fuzzy si `Name` vacío, como hoy).
- [ ] 1.2 `Take` acotado en la consulta de candidatos trigram (orden preservado; el ganador no cambia).
- [ ] 1.3 Tests: regresiones de semántica existentes verdes + pin de "solo fuzzy para no-emparejadas" + evidencia de batching (SQLite + `DbCommandInterceptor`: lecturas acotadas y NO lineales; RED antes del fix).
- [ ] 1.4 Build 0/0 + focused + suite; evidencia RED/GREEN; commit `fix(8.155)` (lo hace el padre).

## T2 (S2) — PERF-02: apply con lecturas batch y saves consolidados

- [ ] 2.1 Precarga trackeada de productos resueltos (1 consulta; mismos filtros/excepción `KeyNotFoundException`); el retry xmin conserva su re-lectura individual sobre entidad fresca.
- [ ] 2.2 Alias: precarga de existentes (1 consulta) + upsert en memoria (last-write-wins) + UN guardado final (solo si hubo cambios); sin cambios en `UpsertSupplierProductCodeAsync` del flujo de creación.
- [ ] 2.3 Tests: regresiones de apply verdes + evidencia de reducción (contador: lecturas acotadas, saves ≤ N+2; RED antes del fix) + retry/semántica intactos.
- [ ] 2.4 Build 0/0 + focused + suite; evidencia RED/GREEN; commit `fix(8.155)` (lo hace el padre).

## T3 (S1-S2) — Verificación + cierre

- [ ] 3.1 Verificador independiente: equivalencia de matching/statuses, batch real, retry intacto, tests de evidencia auditados.
- [ ] 3.2 Suite completa + cobertura + build 0/0.
- [ ] 3.3 `verify-report.md` + ANEXO 8.155 (incl. D2 parcial por spec) + cierre del tracker + commit `docs(8.155)`.

## Notes

- Tracker: `odd/tasks/auditoria-facturas-performance.md`.
- D2: PERF-02 queda parcial-por-diseño (spec `supplier-invoice-apply` L64-73 manda retry xmin por entidad); G1 en el ANEXO si se quiere el save único con rediseño.
- Los writers NO commitean; el padre commitea por unidad.
