# change-004 Design - 8.212 WU3

## Flow
1. Controller anchors the client rate as today and forwards the new request fields.
2. Coordinator resolves the active monetary profile once and filters active physical methods
   valid for the regime.
3. Coordinator resolves receiver first (explicit or legacy), then dispensed method (explicit,
   single-method auto-selection, or clear ambiguity error).
4. Coordinator computes the dispensed physical amount and currency. VES uses the existing cash
   rounding rule; other currencies use 2dp.
5. Coordinator computes canonical USD principal from the dispensed amount and profile rate.
6. Coordinator computes receiver principal and commission in receiver currency; commission
   percentage remains the configured cash/transfer percentage.
7. `ProcessEnvelopeAsync` acquires `LockOpenSessionForWriteAsync`, then re-reads
   `GetPhysicalMethodBalancesAsync` and validates the exact dispensed method balance.
8. `CreateCashAdvanceSaleAsync` receives received-principal/commission/currency/total-USD
   optional values; legacy callers keep the old computation.
9. Expense uses dispensed method/currency; income uses received method/currency; sale/payment
   use received method.
10. Result DTO carries both sides while preserving legacy `RequestedAmountLocal`,
    `CommissionAmountLocal`, and `TotalChargedLocal`.

## Compatibility
When no dispensed method can be resolved and no WU1 method rows exist, preserve the legacy
single-local-drawer path and its tests. When method rows exist, use the per-method path.

## Files
- `Sales.Module/DTOs/CashDrawerRequestDtos.cs`
- `Sales.Module/Interfaces/ICashDrawerService.cs` (result DTO)
- `Sales.Module/Services/CashAdvanceCoordinator.cs`
- `Sales.Module/Services/SalesService.CashAdvance.cs`
- `Backend.API/Controllers/CashDrawerController.cs`
- `CommandCenter.Tests/Unit/CashAdvanceCoordinatorTests.cs` (or new focused test file)

## Testing
InMemory for amount/accounting arithmetic; PostgreSQL lane for the advisory-lock balance
validation if the existing harness supports it. Preserve all existing cash-advance tests.
