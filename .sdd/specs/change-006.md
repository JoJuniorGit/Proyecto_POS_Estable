# change-006 Specification - 8.212 WU5

## Requirements

### R1. Web balance source
The register reads `session.physicalMethodBalances` when present. If the active-session payload is
legacy, it calls `GET /api/cashdrawer/physical-method-balances?sessionId=`. The list is refreshed
after every register operation.

### R2. Balance cards
The register shows one card per physical method with `name`, `currencyCode`, `currencySymbol`,
`currentBalance`, and a de-emphasized state when `canDispense` is false.

### R3. Cash-advance modal
The modal lists physical methods, auto-selects the sole method or the first dispensable method,
shows `Existencia actual: X -> Existencia tras el avance: Y`, and blocks confirmation when the
method is missing, inactive, or has insufficient balance. The exact message shape is:
`Existencia insuficiente en [Nombre] ([CurrencyCode]): disponible [símbolo][saldo], requerido [símbolo][monto].`

### R4. Advance request
The request sends `dispensedCashPaymentMethodId`, `dispensedAmount`, and
`receivedPaymentMethodId` in addition to the legacy fields. Requests without physical methods keep
the legacy shape.

### R5. Opening breakdown
When active physical payment methods exist for the profile, the opening modal shows one editable
amount per method, derives `openingBalanceLocal` and `openingBalanceUsd` from the rows, and sends
`methodBalances`. Without methods it keeps the legacy local/USD inputs.

### R6. Close visibility
The close modal shows the per-method theoretical balances read from the register. The aggregate
count remains the only editable close input because the server owns the per-method closing snapshot.

### R7. Realtime refresh
The server publishes `CashDrawerChanged` with the session id after a successful committed drawer
operation. The web SignalR hub dispatches `onCashDrawerChanged`; the register refreshes without a
manual reload.

### R8. Compatibility and evidence
Legacy session payloads, legacy advance requests, and the existing modal contracts remain valid.
Focused unit tests cover payload builders, modal validation, and the SignalR handler; the web test
suite and build pass.
