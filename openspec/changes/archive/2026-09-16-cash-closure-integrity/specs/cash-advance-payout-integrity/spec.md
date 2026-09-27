# cash-advance-payout-integrity Specification

## Purpose

Guarantees that a cash advance never pays physical cash without its accounting sale and
never silently substitutes a default commission, preserving drawer reconciliation and
fiscal traceability.

## Requirements

### Requirement: Atomic Payout and Accounting Sale

A cash advance MUST create its accounting sale and its physical drawer movements
atomically; if either fails, neither MUST persist. The system MUST NOT log-and-continue
when the accounting sale cannot be created.

#### Scenario: Sale creation succeeds

- GIVEN a valid advance request and a resolvable commission
- WHEN the advance is processed
- THEN the accounting sale is created first
- AND the drawer expense and commission income are recorded
- AND the operation commits as one unit

#### Scenario: Accounting sale fails

- GIVEN the accounting sale creation throws
- WHEN the advance is processed
- THEN no physical payout is recorded
- AND the operation fails with an error

### Requirement: Commission Resolved from System Settings

The commission MUST be resolved from a constructor-injected `ISystemSettingsService`.
The system MUST NOT fall back to a hardcoded 10%/7% commission.

#### Scenario: Configured commission is applied

- GIVEN a configured advance commission
- WHEN the advance is processed
- THEN the commission amount MUST use the configured value

### Requirement: Fail-Closed on Unresolvable Commission

When the configured commission is missing or invalid, the advance MUST be rejected with a
clear error and no cash MAY leave the drawer.

#### Scenario: Missing commission rejects the advance

- GIVEN no valid commission setting can be resolved
- WHEN the advance is processed
- THEN the operation is rejected with a clear error
- AND no payout or accounting sale is persisted

### Requirement: Ordering and Rate Anchoring Preserved

The accounting sale MUST be created before the drawer movements, and its `AppliedRate`
MUST anchor the drawer movements.

#### Scenario: Drawer uses the sale's anchored rate

- GIVEN the accounting sale is created with an `AppliedRate`
- WHEN the drawer movements are recorded
- THEN they MUST use that `AppliedRate`

### Requirement: Financial Snapshots Preserved

The advance MUST persist `AppliedRate`, `TotalUSD`, `TotalBsS` and `FinalPaidAmountBsS`,
and MUST leave `RoundingAdjustment` at its default.

#### Scenario: Snapshots are written as before

- GIVEN a completed advance
- WHEN its accounting sale is inspected
- THEN the snapshot fields match the pre-change values
- AND `RoundingAdjustment` remains at its default

### Requirement: No Service Locator

The orchestration MUST be owned by an injected coordinator; `CashDrawerService` MUST NOT
depend on `IServiceProvider` or `ISalesService` (no service locator).

#### Scenario: Drawer has no locator

- GIVEN the resolved dependency graph
- WHEN `CashDrawerService` is inspected
- THEN it MUST NOT reference `IServiceProvider` or `ISalesService`
- AND the coordinator MUST receive `ISalesService` and `ISystemSettingsService` by constructor
