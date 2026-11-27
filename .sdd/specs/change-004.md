# change-004 Specification - 8.212 WU3

## Requirements

### R1. Request contract
`CashAdvanceRequest` adds `DispensedCashPaymentMethodId`, `DispensedAmount`, and
`ReceivedPaymentMethodId`. Legacy `RequestedAmountLocal` and `PaymentMethodId` remain valid:
`DispensedAmount` defaults to `RequestedAmountLocal`; `ReceivedPaymentMethodId` defaults to
`PaymentMethodId`.

### R2. Method validation
The dispensed method must be physical (`IsCash`, active, not deleted) and valid for the active
monetary regime. The received method must be non-physical, active, not deleted, and valid for
the regime. If the dispensed method is omitted and the session has exactly one physical method
valid for the regime, auto-select it; if there are multiple, return a clear 400.

### R3. Balance under lock
After `LockOpenSessionForWriteAsync(sessionId)`, resolve the dispensed method's
`CurrentBalance` from `GetPhysicalMethodBalancesAsync`. Reject when
`CurrentBalance < DispensedAmount` with:
`Existencia insuficiente en [Nombre] ([CurrencyCode]): disponible [símbolo][saldo], requerido [símbolo][monto].`
The check must happen inside the transaction/lock, not only before it.

### R4. Currency and rounding
The physical amount is in the dispensed method currency. Apply VES cash rounding only when the
dispensed currency is VES; USD/COP/EUR use their minor unit. Same-currency principal/commission
uses rate 1. Cross-currency uses the anchored profile rate: convert dispensed amount to USD,
then to the receiver currency.

### R5. Accounting
The physical expense `CashTransaction` must use the dispensed method/currency. The commission
income must be non-physical and use the received method/currency. The sale/payment must use the
received method and the received amount. The result DTO exposes dispensed and received amounts,
currencies, and method IDs without removing legacy fields.

### R6. Idempotency and evidence
The full request (including the dispensed method and amount) participates in the idempotency
payload hash. Focused tests cover legacy compatibility, explicit dispensed method, insufficient
method balance, same-currency, USD→local and local→USD advances, invalid method roles, and
lock-path validation. Build and relevant cash-advance regression suites pass.
