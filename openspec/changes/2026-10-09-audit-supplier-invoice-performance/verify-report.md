# Verify Report: Performance de facturas de proveedor (8.155)

## Veredicto

**PASS WITH WARNINGS** — 6/6 requisitos COMPLIANT (REQ-SIB-01/02/03, REQ-AIO-01/02/03). W1 (el caché de precarga retenía la instancia detachada tras un retry xmin) fue detectado por el verificador independiente y **cerrado por T2b con reproducción real contra PostgreSQL** (RED: falla del attach-graph sobre el producto detachado → GREEN con el fix). W2 (retry xmin gated no ejercitado localmente) quedó saldado en el cierre: los tests gated corrieron contra Postgres local.

## Gates de cierre (post-T2b)

- Build: `dotnet build CommandCenter.slnx -c Release` → **0 advertencias, 0 errores**.
- Suite completa **CON PostgreSQL real** (pos_test local recreada fresca, patrón CI): **2172/2172**, 0 fallas, 0 omitidas (base 2162 + 3 T1 + 6 T2 + 1 gated T2b).
- Cobertura con Postgres (`--collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` + `python scripts/check-coverage.py`, exit 0): Core **0.8886** / Sales.Module **0.9057** / Inventory.Module **0.8621**.
- Reproducción W1: gated `ConfirmAsync_AfterXminRetryWithSharedProduct_AppliesAllLinesOnFreshInstance` — RED sin el fix (EntityGraphAttacher sobre el producto detachado), GREEN con el fix; ambos extremos observados por el padre contra Postgres real con xmin real.
- Spot-check del padre: diffs T1/T2/T2b revisados.

## Veredicto por requisito (verificador independiente read-only)

| Requisito | Veredicto | Evidencia clave |
|---|---|---|
| REQ-SIB-01 | COMPLIANT | `LoadMatchLookupsAsync` (2 consultas; primer-por-Id; claves normalizadas), `TryResolveExactMatch` en memoria, fuzzy solo para no-emparejadas con nombre; 32 regresiones verdes; lecturas **4/4 constantes vs 4/11 lineales**. |
| REQ-SIB-02 | COMPLIANT (estático) | `Take(100)` tras `(sim desc, Id asc)`; pg_trgm es Postgres-only — inspección sin test local. |
| REQ-SIB-03 | COMPLIANT | SQLite + `DbCommandInterceptor` (SELECT por prefijo; EnsureCreated fuera del conteo; pin de no-linealidad sólido). |
| REQ-AIO-01 | COMPLIANT | Precarga trackeada de productos/alias; mismo `KeyNotFoundException`; retry con re-lectura fresca; SELECTs **4/4/4**. |
| REQ-AIO-02 | COMPLIANT | Plan de alias last-write-wins + guardado final único (sin `SaveChanges` nuevo; **N+1 ≤ N+2** vs 2N+1); `UpsertSupplierProductCodeAsync` de creación intacto; rollback sin alias. |
| REQ-AIO-03 | COMPLIANT | Contadores auditados (métrica de saves = `SaveChangesAsync`, roundtrips; los statements SQL emitidos no cambian — divulgado; Writes 3N+1 pre/post); gated de xmin ejercitado en el cierre. |

## Hallazgos (honestos)

- **W1 — CERRADA por T2b (`f4ede27`)**: tras un retry xmin, `productsById` retenía la instancia detachada; una línea posterior con el MISMO producto fallaba en el attach-graph (reproducido contra Postgres real). Fix: `ApplyApprovedLineAsync` devuelve el producto final y el loop refresca el caché (`return product` + guarda de bucle irrecuperable). El intento previo de test no-gated con `DbUpdateConcurrencyException` fabricada se descartó y documentó: la API pública de EF no permite construir `IUpdateEntry` (y `EntityEntry` no implementa esa interfaz); la reproducción fiel es el test gated.
- **W2 — saldado en el cierre**: el gated existente de xmin y el nuevo corrieron contra Postgres local (pos_test recreada fresca; sin `TEST_POSTGRES_CONNECTION` hacen early-return, patrón del repo para CI).
- **INFOs**: métrica de saves a nivel `SaveChangesAsync` y no statements SQL (divulgado en el test); RED de T1/T2 no re-verificable read-only (aritmética consistente 2162→2165→2171→2172); `Take` sin test local; pos_test local recreada para runs gated.

## Límite de PERF-02 (D2)

El "único `SaveChanges`" literal de la auditoría violaría la spec canónica `supplier-invoice-apply` (L64-73: retry `xmin` sobre entidad fresca dentro de la transacción) sin un rediseño con retry de operación completa; se entregó la mejora compatible (**2N+2 → 4 lecturas; 2N+1 → N+1 saves**) y queda G1 para la decisión del mantenedor.

## Artefactos / commits

Change `2026-10-09-audit-supplier-invoice-performance`; tracker `odd/tasks/auditoria-facturas-performance.md`; ANEXO 8.155. Commits: `5f206c3` (plan) + `5267c0e` (T1) + `83190b3` (T2) + `f4ede27` (T2b) + commit de cierre.
