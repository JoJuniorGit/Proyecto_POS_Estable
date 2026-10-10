# Proposal: Performance de facturas de proveedor (PERF-01 staging + PERF-02 apply) — ANEXO 8.155

## Contexto

Tramo 3 autorizado ("Haz 8.155"). Alcance: **PERF-01** (N+1 en staging/matching de facturas) y **PERF-02** (N+1 + múltiples SaveChanges en el apply), con un hallazgo de diseño: la spec canónica `openspec/specs/supplier-invoice-apply/spec.md` (REQ "All-Or-Nothing Transaction With Concurrency Retry", L64-73) exige reintentar conflictos `xmin` **sobre entidad fresca** dentro de la transacción; el `SaveChanges` por línea con savepoint (comentario 8.144) es el mecanismo que lo hace posible. La demanda literal de PERF-02 ("un solo SaveChanges atómico") lo rompería salvo un rediseño con retry de operación completa (ver D2).

Verificación previa (estática, de este plan):

| Hallazgo | Veredicto | Evidencia |
|---|---|---|
| PERF-01 | CONFIRMADO | `Staging.cs:40-47` llama `MatchProductAsync` por línea (1-2 consultas exactas + fuzzy con scan trigram sin cota en `Matching.cs:210-222`); el coste crece lineal con líneas. |
| PERF-02 | CONFIRMADO (con matiz spec) | `Apply.cs:82-117` + `ApplyApprovedLineAsync:148-199`: por línea un `FirstOrDefaultAsync` + `SaveChanges` (savepoint/retry xmin deliberado) y, con código de proveedor, `UpsertSupplierProductCodeAsync` (otra lectura) + **segundo** `SaveChanges`. ~2N lecturas + ~2N saves para N líneas. El "save único" choca con la spec de retry (D2). |

Fuera de alcance: PERF-03/04/05, CLEAN-01/03/04/05, SEC-07 y el residual de céntimos.

## Alcance

1. **S1 (PERF-01 — staging batch)**: precarga en lote UNA vez por stage: (a) productos por barcode→SKU; (b) productos por (SupplierId, SupplierCode) vía join; el loop queda en memoria para los caminos exactos y solo cae a fuzzy (`FindCandidatesAsync`) para líneas sin match exacto. Semántica intacta (prioridad barcode > supplierCode > fuzzy, primeros por `Id`, umbral configurable leído una vez, statuses NEW/UPDATE/UNCHANGED). Endurecimiento acotado del fuzzy: `Take` en la consulta de candidatos (el roadmap previo lo anotó como "trigram sin LIMIT").
2. **S2 (PERF-02 — apply I/O)**: precarga trackeada de los productos resueltos en UNA consulta (mismos `KeyNotFoundException`/filtros), precarga de los alias existentes en UNA consulta, upsert de alias **en memoria** con un único guardado al final (mismos last-write-wins y rollback sin cambios de alias), y conservación EXACTA del savepoint/retry xmin por línea (spec `supplier-invoice-apply` L64-73). Resultado: ~2N lecturas + 2N+1 saves → 2 lecturas + N+2 saves. **Parcial vs la letra de la auditoría (D2), por mandato de la spec propia del proyecto.**
3. **S3 (evidencia)**: tests de semántica (regresiones existentes) + tests de batching con contador de comandos (SQLite en memoria + `DbCommandInterceptor`): el número de lecturas deja de crecer linealmente con las líneas.

## Decisiones de diseño

- D1 (PERF-01): los diccionarios preservan "primer producto por `Id`" (agrupando en memoria tras ordenar por `Id`), porque los índices únicos filtrados de producción no se aplican en InMemory/tests.
- D2 (PERF-02, parcial por diseño): la spec canónica exige retry `xmin` sobre entidad fresca **dentro** de la transacción; un único `SaveChanges` masivo deja los estados del tracker sin recuperación confiable tras el rollback del savepoint y no puede "reintentar sobre entidad fresca" sin re-ejecutar todo el apply. Se ejecuta la mejora compatible (batch de lecturas + consolidación de saves) y se registra la desviación con rationale y recomendación (G1: si el mantenedor quiere el save único, es un rediseño con retry de operación completa — decisión aparte).
- D3: el fuzzy sigue siendo por línea (spec `supplier-product-matching`); solo se acota por `Take` la lista de candidatos ya ordenada (el ganador no cambia).

## Entrega y riesgos

- Estrategia: `ask-on-risk` → `stacked-to-main` (cacheada). Forecast ~500-650 líneas (2 módulos + tests de evidencia).
- Riesgos: equivalencia de semántica del matching (cubierta por `SupplierInvoiceStagingTests`/`SupplierInvoiceMatchingTests` existentes + nuevas regresiones); el retry del apply debe quedar intacto (test existente de conflicto gated + regresión no-gated con InMemory no ejercita xmin).
- RDD: off (clone-local) → self-verification de writers + verificador independiente + suite/cobertura.

## Tramos siguientes

- 8.156: PERF-04/05 + CLEAN-05 (WPF/clientes). 8.157: SEC-07. 8.158: SRE-03 + CLEAN-03. 8.159: CLEAN-01/04 + PERF-03.
