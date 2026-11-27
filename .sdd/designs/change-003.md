# change-003 Design - 8.212 WU2

## Decision
Move the per-method read model into `CashDrawerBalanceService`; keep `CashDrawerService` as
the orchestration facade. The balance service owns the aggregation and profile/symbol lookup;
the facade delegates and maps the session DTO.

## Data flow
1. Load `CashDrawerSessionMethodBalance` rows for the session.
2. Aggregate physical `CashTransactions` by `(PaymentMethodId, Type)`.
3. Resolve current `PaymentMethod` rows for `IsActive` and names/currencies.
4. Resolve the active monetary profile once for `CurrencySymbol` and regime validity.
5. Project `PhysicalMethodBalanceDto` per session row.
6. `CashDrawerSessionResponseDto.PhysicalMethodBalances` is populated by the active-session
   read path; `MapSession` remains a pure legacy mapper.

## Files
- `Sales.Module/DTOs/PhysicalMethodBalanceDto.cs` (new).
- `Sales.Module/DTOs/CashDrawerSessionResponseDto.cs` (additive collection).
- `Sales.Module/Services/CashDrawerBalanceService.cs` (new public/internal query).
- `Sales.Module/Services/CashDrawerService.cs` (delegate + active-session mapping).
- `Sales.Module/Interfaces/ICashDrawerService.cs` (new read method).
- `Backend.API/Controllers/CashDrawerController.cs` (dedicated endpoint).

## Compatibility
Legacy aggregates are computed by summing the method projections. Do not change the
`PettyCashPhysicalBalancesResponseDto` contract.

## Testing
Use InMemory for deterministic method-balance arithmetic and one PostgreSQL-independent
endpoint/controller test. The PostgreSQL migration lane is already covered by WU1.
