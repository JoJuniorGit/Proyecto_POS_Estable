# change-003 Specification - 8.212 WU2

## Requirements

### R1. Per-method balance read model
The active cash drawer session must expose every physical payment method captured at session
opening with:
`PaymentMethodId`, `Name`, `CurrencyCode`, `CurrencySymbol`, `CurrentBalance`, `IsActive`,
`CanDispense`.

### R2. Calculation
`CurrentBalance` must equal:
`OpeningAmount + physical income movements - physical expense movements` for that exact
`PaymentMethodId`, excluding `Source == Opening`. Historical movement currency snapshots must
win over the current payment-method currency.

### R3. Active/CanDispense
`IsActive` reflects the current payment-method row. `CanDispense` is true only when the method
is active, not deleted, belongs to the active monetary regime, and `CurrentBalance > 0`.
An inactive method with a balance remains visible but cannot dispense.

### R4. Compatibility
`GetCurrentBalanceLocalAsync` and `GetPhysicalBalanceAsync` remain source-compatible. Their
values must equal the sum of the corresponding per-method balances by local currency and by
USD. `CashDrawerSessionResponseDto` adds the collection without removing legacy fields.

### R5. Endpoint
The active-session response includes `PhysicalMethodBalances`. A dedicated read-only
`GET /api/cashdrawer/physical-method-balances?sessionId={id}` returns the same collection for
operational refresh. The existing `/physical-balances` endpoint remains available.

### R6. Evidence
Focused tests must cover opening, cash sale, change, petty-cash expense/reversal,
cash-in/out, and a cash-advance physical movement. The full .NET suite and build must pass.
