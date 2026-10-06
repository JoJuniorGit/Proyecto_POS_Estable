# Delta for stock-deduction-concurrency

## ADDED Requirements

### Requirement: Deterministic Deduction Lock Order

`UpdateStockBatchAsync` MUST resolve every request to its target product (parent/shared-stock resolution and unit conversion applied), consolidate requests that share `(target product, reason, sale id)` into a single deduction with the summed quantity, and execute the resulting updates ordered ascending by target product id. The total deducted quantity per target MUST be identical to the legacy behavior for any input order.

#### Scenario: Reversed input order produces the same execution order

- GIVEN two batches with the same items in opposite orders
- WHEN each batch is consolidated
- THEN both produce the same ordered target sequence
- AND the summed quantities per target are identical

#### Scenario: Duplicate targets are consolidated

- GIVEN a batch with two requests for the same target product, reason and sale
- WHEN the batch runs
- THEN a single stock update and a single movement with the summed quantity are produced

#### Scenario: Totals preserved

- GIVEN a batch of N requests across M targets
- WHEN the batch runs
- THEN the per-target stock delta equals the sum of the resolved request deltas
- AND per-target movement quantities match the applied deltas
