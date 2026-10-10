# Spec (delta): Staging de facturas en lote — PERF-01 (8.155)

Fuente: auditoría re-emitida, PERF-01: "Carga previa en lote (Batch Fetching) en memoria… Bucle en memoria puro (0 consultas SQL en la iteración principal)". Endurecimiento adicional del roadmap 8.149: "trigram sin LIMIT".

## ADDED Requirements

### Requirement: REQ-SIB-01 — Los caminos exactos del matching no consultan por línea

`StageAsync` MUST precargar UNA sola vez por stage: (a) los productos por `Barcode`→`Product.SKU` (`!IsDeleted`); (b) los productos por `(SupplierId, SupplierCode)` vía join. En el loop, una línea con barcode o supplierCode emparejado MUST resolverse en memoria sin consultas adicionales; el fuzzy (`ISupplierProductSimilaritySearch.FindCandidatesAsync`) MUST invocarse SOLO para líneas sin match exacto con `Name` no vacío (misma regla que hoy). La semántica del matching MUST ser idéntica (spec `supplier-product-matching`): prioridad barcode > supplierCode > fuzzy, "primer producto por `Id`" para duplicados, umbral configurable leído una vez, `MatchMethod` y statuses `NEW/UPDATE/UNCHANGED` sin cambios.

#### Scenario: Líneas con barcode/supplierCode no escalan consultas

- GIVEN N líneas con barcode o supplierCode resueltos
- WHEN `StageAsync`
- THEN las consultas de lectura de productos no crecen con N (precarga constante) y el resultado por línea es idéntico al matching por línea previo.

#### Scenario: Fuzzy solo para no emparejadas

- GIVEN líneas emparejadas por código y una línea sin match exacto con nombre
- WHEN `StageAsync`
- THEN `FindCandidatesAsync` se invoca exactamente para la última.

#### Scenario: Empates preservados

- GIVEN dos productos que comparten SKU o code (solo posible en arneses sin índice)
- THEN se elige el de menor `Id` (mismo criterio que la consulta por línea).

### Requirement: REQ-SIB-02 — Candidatos fuzzy acotados

`PostgresSupplierProductSimilaritySearch.FindCandidatesAsync` MUST acotar la lista ordenada de candidatos con un `Take` (p. ej. 100) preservando el orden `(similitud desc, Id asc)`; el ganador de la selección no cambia.

#### Scenario: Cota

- GIVEN más candidatos que la cota por encima del umbral
- THEN la lista devuelta está acotada y su primer elemento (ganador) es el mismo que sin cota.

### Requirement: REQ-SIB-03 — Evidencia de batching

- Test nuevo con SQLite en memoria + `DbCommandInterceptor` que cuente lecturas: el total para N líneas emparejadas MUST ser acotado y NO crecer linealmente con N (RED antes del fix).
- Las regresiones de semántica existentes (`SupplierInvoiceStagingTests`, `SupplierInvoiceMatchingTests`) MUST quedar verdes sin cambios salvo necesidad real y reportada.
