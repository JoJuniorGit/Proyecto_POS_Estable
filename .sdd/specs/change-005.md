# change-005 Specification - 8.212 WU4

## Requirements

### R1. Client contract
`CashDrawerSessionDto` exposes nullable `PhysicalMethodBalances`. The client service exposes
`GetPhysicalMethodBalancesAsync(sessionId)`, an open overload accepting `MethodBalances`, and a
method-aware cash-advance overload accepting `DispensedCashPaymentMethodId`, `DispensedAmount`, and
`ReceivedPaymentMethodId`. Default interface implementations preserve legacy test doubles.

### R2. Drawer visibility
The WPF cash drawer shows one card per physical method with `Name`, `CurrencyCode`,
`CurrencySymbol`, and `CurrentBalance`. Cards for methods with `CanDispense == false` are visibly
de-emphasized. The view refreshes after load, open, close, cash in/out, sales, petty cash, and cash
advances.

### R3. Cash-advance selector
The dialog lists physical methods. With exactly one method it auto-selects it; with multiple methods
it selects the first dispensable method and lets the operator choose. It shows
`Existencia actual: X -> Existencia tras el avance: Y` and the exact insufficient message:
`Existencia insuficiente en [Nombre] ([CurrencyCode]): disponible [símbolo][saldo], requerido [símbolo][monto].`

### R4. Confirmation rules
Confirmation is disabled when no physical method is selected (when methods exist), the method is
inactive, the requested amount exceeds its balance, the electronic receiver is missing, or the
commission is unresolved. The server remains the final balance authority.

### R5. Opening breakdown
When active physical methods are available, the opening overlay shows one editable amount per
method. It derives `OpeningBalanceLocal` and `OpeningBalanceUsd` from the rows and sends the explicit
`MethodBalances` list. Without methods, the legacy local/USD fields remain available.

### R6. Compatibility
Legacy requests without the new fields keep the old backend path. Existing local/USD aggregate fields
remain in the session DTO. No historical amounts are recalculated.

### R7. Evidence
Focused ViewModel tests cover auto-selection, projection, insufficient balance, inactive method, and
session balance population. Build and cash-drawer/cash-advance regressions pass.
