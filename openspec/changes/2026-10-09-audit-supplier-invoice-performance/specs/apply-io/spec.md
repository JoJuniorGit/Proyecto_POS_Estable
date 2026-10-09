# Spec (delta): Apply de facturas con I/O reducido — PERF-02 (8.155)

Fuente: auditoría re-emitida, PERF-02: "Consultar los productos en batch antes del bucle, mutar los estados y crear los movimientos de inventario en memoria, y ejecutar un único SaveChangesAsync() inmediatamente antes de CommitAsync()". **Conflicto**: la spec canónica `openspec/specs/supplier-invoice-apply/spec.md` (REQ "All-Or-Nothing Transaction With Concurrency Retry", L64-73 y su scenario "Concurrency conflict retried") exige reintentar conflictos `xmin` sobre entidad fresca dentro de la transacción; el `SaveChanges` por línea con savepoint es el mecanismo vigente que lo habilita. Este delta implementa la mejora compatible y documenta la parte que queda fuera por mandato de la spec (D2 del proposal).

## ADDED Requirements

### Requirement: REQ-AIO-01 — Lecturas batch en el apply

`ConfirmAsync` MUST precargar en UNA consulta trackeada los productos resueltos de las líneas aprobadas (mismos filtros `!IsDeleted` y el mismo `KeyNotFoundException("Product {id} was not found or has been deleted.")` cuando falte), y en UNA consulta los `SupplierProductCode` existentes del proveedor para los códigos de la factura. El loop MUST usar las precargas para el intento inicial; el retry `xmin` MUST conservar su re-lectura individual sobre entidad fresca (semántica 8.144 intacta).

#### Scenario: N+1 de lecturas eliminado

- GIVEN una factura con N líneas aprobadas y resueltas
- WHEN `ConfirmAsync`
- THEN las lecturas a `Products` no crecen con N (1 precarga + 1 por conflicto real) y las lecturas a `SupplierProductCodes` no crecen con N.

#### Scenario: Producto faltante conserva el error

- GIVEN una línea resuelta cuyo producto no existe o está borrado
- THEN `KeyNotFoundException` con el mismo mensaje que hoy y rollback total.

#### Scenario: Retry xmin intacto

- GIVEN un conflicto de concurrencia en una línea
- THEN el retry se hace sobre la entidad fresca (re-lectura individual) y la operación sigue siendo atómica (spec `supplier-invoice-apply`).

### Requirement: REQ-AIO-02 — Saves consolidados sin perder granularidad

El aprendizaje de alias MUST aplicarse en memoria (last-write-wins hacia el producto confirmado) y persistirse con UN solo `SaveChanges` adicional al final del loop (solo si hubo cambios), dentro de la misma transacción; un fallo posterior MUST seguir dejando cero cambios de alias. El guardado por línea de producto+movimiento (savepoint/retry) MUST conservarse. Total de saves por confirm: ≤ N + 2 (vs 2N + 1 previos).

#### Scenario: Alias aprendidos en una sola escritura

- GIVEN N líneas aplicadas con códigos de proveedor
- WHEN `ConfirmAsync` termina
- THEN todos los alias quedan persistidos con una única escritura adicional y el resultado final es idéntico al de hoy (incluido last-write-wins y líneas sin código).

#### Scenario: Rollback sin cambios de alias

- GIVEN un fallo tras aplicar líneas con alias en memoria
- THEN la transacción revierte todo y no queda ningún `SupplierProductCode` insertado/actualizado.

### Requirement: REQ-AIO-03 — Evidencia

- Test de contador (SQLite + interceptor): lecturas y saves acotados por N (RED antes del fix; regresión semántica de apply verde).
- Regresiones existentes (`SupplierInvoiceApplyTests` + ordering + integración gated) verdes sin cambios salvo necesidad real y reportada.
