# Spec (delta): Orden determinista al aplicar facturas de proveedor — SRE-01 (8.154)

Fuente: auditoría integral re-emitida, SRE-01 (parte abierta en facturas): "Ordenar determinísticamente las líneas de la factura por `ResolvedProductId` antes de mutar".

## ADDED Requirements

### Requirement: REQ-SIA-01 — El loop de aplicación procesa en orden determinista por producto

`SupplierInvoiceService.ConfirmAsync` MUST procesar las líneas en orden ascendente por `ResolvedProductId` (nulls primero según el comparador por defecto de `int?`), con tie-break ascendente por `Id`, vía un helper puro reutilizable `SupplierInvoiceApplyOrdering.OrderForApply(IEnumerable<SupplierInvoiceLine>)` (clase pública estática en archivo propio; precedente `StockDeductionConsolidator` de 8.149). El conjunto de efectos (productos mutados, movimientos, alias, estado de la factura) MUST ser idéntico al actual — solo cambia el orden de adquisición de bloqueos de fila de `Products` (deadlocks AB-BA entre facturas con los mismos productos en orden invertido).

#### Scenario: Entrada desordenada

- GIVEN líneas con ResolvedProductId [7, 3, 5]
- WHEN `OrderForApply`
- THEN [3, 5, 7].

#### Scenario: Nulls y empates

- GIVEN líneas con ResolvedProductId [null, 5 (Id 9), 5 (Id 4)]
- WHEN `OrderForApply`
- THEN [null, 5(Id 4), 5(Id 9)] (nulls primero; ties por Id).

#### Scenario: Determinismo con entrada invertida

- GIVEN los mismos elementos en orden invertido
- WHEN `OrderForApply`
- THEN el resultado es idéntico al de la entrada original.

#### Scenario: Regresión de efectos

- GIVEN una factura confirmable con líneas desordenadas
- WHEN `ConfirmAsync`
- THEN mismas mutaciones de productos, movimientos y estado que antes del cambio.

### Requirement: REQ-SIA-02 — Cobertura de pruebas

- Tests puros del helper (orden, nulls, ties, determinismo) en archivo nuevo autorizado.
- Regresión de `ConfirmAsync` con líneas desordenadas (semántica intacta).
