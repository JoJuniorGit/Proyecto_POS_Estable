# Delta for supplier-invoice-apply

## ADDED Requirements

### Requirement: Supplier Code Learning on Apply

For every approved line applied by confirm that carries a non-empty supplier code and a resolved product, the system MUST upsert the `SupplierProductCode` mapping inside the same transaction: insert when the supplier+code has no mapping, update the product when it already exists (last-write-wins toward the human-confirmed product). A failed confirm MUST leave no alias change.

#### Scenario: Alias learned on confirm

- GIVEN an approved line with supplier code `XYZ-9` resolved to product `P`
- WHEN confirm applies the invoice
- THEN `SupplierProductCode(SupplierId, "XYZ-9")` maps to `P`

#### Scenario: Existing mapping is corrected by the confirmed product

- GIVEN an existing alias `XYZ-9 → Q`
- WHEN a confirmed line with supplier code `XYZ-9` applies to product `P`
- THEN the alias is updated to `P`

#### Scenario: Rollback leaves no alias change

- GIVEN a confirm that fails after applying earlier lines
- WHEN the transaction rolls back
- THEN no `SupplierProductCode` row is inserted or updated by that confirm

#### Scenario: Lines without supplier code are ignored

- GIVEN an applied line with an empty supplier code
- WHEN confirm commits
- THEN no alias row is written
