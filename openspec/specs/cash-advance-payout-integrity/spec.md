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
The system MUST NOT fall back to a hardcoded 10%/7% commission. The prohibition covers the client
advance previews: the percentage shown by `CashAdvanceRegisterViewModel` (WPF) and
`CashAdvanceModal` (Web) MUST be the server-resolved value for the selected channel
(`CashAdvance.TransferCommissionPct` / `CashAdvance.CashCommissionPct`), and neither client MAY
compute a percentage from a local literal.
(Previously: only the server was bound; both clients hardcoded 7% for transfers and 10% for cash.)

#### Scenario: Configured commission is applied

- GIVEN a configured advance commission
- WHEN the advance is processed
- THEN the commission amount MUST use the configured value

#### Scenario: Preview shows the server-resolved percentage

- GIVEN a configured transfer commission of 5.5%
- WHEN the advance preview is opened for a transfer method
- THEN the preview MUST show 5.5%
- AND it MUST NOT show 7 or 10

#### Scenario: Preview follows the selected channel

- GIVEN different configured transfer and cash percentages
- WHEN the operator changes the selected method channel
- THEN the preview MUST show the configured percentage for that channel

#### Scenario: No client literal remains

- GIVEN the source of `CashAdvanceRegisterViewModel` and `CashAdvanceModal`
- WHEN the commission computation is inspected
- THEN no hardcoded 7/10 default MUST remain

### Requirement: Fail-Closed on Unresolvable Commission

When the configured commission is missing or invalid, the advance MUST be rejected with a
clear error and no cash MAY leave the drawer. The client previews MUST fail closed the same way:
when no valid percentage can be obtained from the server, the preview MUST NOT display or apply a
substituted percentage, and submission MUST be blocked so no advance can be sent with a fabricated
commission.
(Previously: only the server rejection was bound; the clients substituted 7/10.)

#### Scenario: Missing commission rejects the advance

- GIVEN no valid commission setting can be resolved
- WHEN the advance is processed
- THEN the operation is rejected with a clear error
- AND no payout or accounting sale is persisted

#### Scenario: Preview without a resolvable percentage blocks submission

- GIVEN the server cannot resolve a valid commission
- WHEN the advance preview is opened
- THEN it MUST NOT show any default percentage
- AND it MUST block submission

#### Scenario: Previewed percentage equals the charged percentage

- GIVEN a preview rendered with percentage P from the server
- WHEN the advance is processed
- THEN the charged commission MUST use P
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
