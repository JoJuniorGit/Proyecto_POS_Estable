# supplier-invoice-apply Specification

## Purpose

Atomic confirm of approved supplier-invoice lines applying cost, retail/wholesale margins, derived prices and stock with StockMovement audit, under zero-trust re-validation.

## Requirements

### Requirement: Single Atomic Confirm Endpoint

The system MUST expose one confirm operation (`POST /api/supplier-invoices/{id}/confirm`) that applies only lines marked approved, and MUST NOT apply any rejected or `[CONFLICT]` line.

#### Scenario: Only approved lines applied

- GIVEN a draft invoice with approved and rejected lines
- WHEN confirm is called
- THEN only approved lines mutate `Product`
- AND rejected and `[CONFLICT]` lines are left untouched

### Requirement: Cost And Margin Update

For each approved line the system MUST set `Product.CostPriceUSD` and update both margins, where `ProfitMarginRetail` is primary and drives `ProfitMarginWholesale` unless an independent wholesale margin exists; all money MUST be `decimal` and derived prices MUST use `PricingCalculator.RoundPriceUp`.

#### Scenario: Both margins updated

- GIVEN an approved line with no independent wholesale margin
- WHEN confirm runs
- THEN `CostPriceUSD` and both margins are updated together
- AND `PriceRetailUSD`/`PriceWholesaleUSD` are recomputed with `RoundPriceUp`

### Requirement: Margin Override Audit

The system MUST persist the per-line margin override for audit and MUST apply the product default within the same transaction.

#### Scenario: Override recorded

- GIVEN a line whose override differs from the product's historical margin
- WHEN confirm commits
- THEN the override value is persisted on the line for audit
- AND the product's margin reflects the same value

### Requirement: Stock Increment With Audit

Each approved line MUST increment `Product.StockQuantity` and MUST create a `StockMovement` audit row within the same transaction.

#### Scenario: Stock and audit written together

- GIVEN an approved line with quantity Q
- WHEN confirm commits
- THEN `StockQuantity` increases by Q
- AND a `StockMovement` row records the reason and the acting user

### Requirement: Zero-Trust Server Re-validation

The backend MUST re-validate every approved line server-side and MUST NOT trust the client `IsValid` flag or client-sent prices.

#### Scenario: Forged client validation rejected

- GIVEN a line submitted as valid but failing server validation
- WHEN confirm runs
- THEN the system MUST reject it and roll back
- AND MUST NOT persist its cost, margin, or stock change

### Requirement: All-Or-Nothing Transaction With Concurrency Retry

Confirm MUST run inside a single transaction via `CreateExecutionStrategy`, and MUST retry transient `xmin` optimistic-concurrency conflicts; any unrecoverable failure MUST roll back the whole confirm.

#### Scenario: Concurrency conflict retried

- GIVEN a product modified concurrently during confirm
- WHEN the `xmin` token mismatches
- THEN the operation retries on a fresh entity
- AND commits atomically or rolls back entirely

### Requirement: RBAC Gating

The confirm endpoint MUST be restricted to `Admin` and `Manager`; `Cashier` and `Driver` MUST be blocked.

#### Scenario: Cashier blocked

- GIVEN an authenticated `Cashier`
- WHEN the confirm endpoint is called
- THEN the request is rejected with 403
- AND no cost, margin, or stock change occurs

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
