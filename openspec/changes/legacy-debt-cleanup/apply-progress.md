# Apply Progress — legacy-debt-cleanup — S1

## Slice S1: Zero-Trust Close

### Completed Tasks

- [x] 1.1 **RED**: Added `CloseShiftResolverClassificationTests.cs` — tests asserting CloseShift classifies via `PaymentMethodCurrencyResolver`, ignores request.Currency; unknown method id → 400
- [x] 1.2 `ShiftReportMapper.cs` — pre-existing, verified in place (AD-4)
- [x] 1.3 `DailyClosureService.CreateClosureFromCommandAsync` — pre-existing, classifies via resolver (AD-1/3), ExpectedAmountBsS verbatim, ActualAmountBsS = declaredNative × rate
- [x] 1.4 `CreateClosureCommand.cs` and `DeclaredPaymentAmount.cs` — pre-existing (AD-5)
- [x] 1.5 `IDailyClosureService` — pre-existing, has `CreateClosureFromCommandAsync` signature (AD-5)
- [x] 1.6 `ShiftsController.CloseShift` — pre-existing, builds command and delegates (AD-1/5); added CancellationToken parameter and ArgumentException catch
- [x] 1.7 `RegisterClosePage.jsx` — removed `currency` from payload; getMethodCurrency reads server `method.currency` only (AD-2)
- [x] 1.8 **GREEN**: Added report↔receipt agreement tests in `CloseShiftResolverClassificationTests.cs` (ShiftReportMapper + receipt content use same resolver)
- [x] 1.9 **GREEN**: Added `RegisterClosePage.currency-classification.test.js` — no name/substring heuristic; payload has no currency key
- [x] 1.10 Re-pointed `SecurityHardeningSprint2Tests`, `Phase7ClosureWithoutRateTests`, `ResidualRemediationLote26Tests` to use `CreateClosureCommand` without Currency and correct mock setups

### Pre-existing Fixes (discovered during S1 verification)

- Fixed `DailyClosureService.GetExpectedTotalsByPaymentMethodAsync` — added `CancellationToken` parameter to match interface contract
- Fixed `DailyClosureService.ResolveEffectiveRateAsync` — `ExchangeRateAtOpen` → `OpeningExchangeRate` (pre-existing property name mismatch)
- Moved `ShiftReportDetailDto` from `Backend.API.Controllers` to `Sales.Module.Services` — eliminates cross-layer reference from Sales.Module mapper to Backend.API
- Made `ShiftReportMapper` public — test assembly needs access
- Added `CancellationToken` to `ShiftsController.CloseShift` action signature

### Files Changed

| File | Action | What Was Done |
|------|--------|---------------|
| `Backend.API/Controllers/ShiftsController.cs` | Modified | Added `CancellationToken` param, `ArgumentException` catch, removed duplicate `ShiftReportDetailDto` |
| `Sales.Module/Services/DailyClosureService.cs` | Modified | Added `CancellationToken` to `GetExpectedTotalsByPaymentMethodAsync`, forwarded token to EF queries, fixed `OpeningExchangeRate` |
| `Sales.Module/Services/ShiftReportMapper.cs` | Modified | Made public |
| `Sales.Module/Services/ShiftReportDetailDto.cs` | Created | DTO extracted from controller (layering fix) |
| `Web.Frontend/src/pages/RegisterClosePage.jsx` | Modified | Removed `currency` from payload |
| `CommandCenter.Tests/Unit/CloseShiftResolverClassificationTests.cs` | Created | S1 tests: resolver classification, report↔receipt agreement |
| `Web.Frontend/src/pages/RegisterClosePage.currency-classification.test.js` | Created | S1 web tests: no heuristic, no currency in payload |
| `CommandCenter.Tests/SecurityHardeningSprint2Tests.cs` | Modified | Removed Currency from DeclaredAmountDto, fixed mock |
| `CommandCenter.Tests/Unit/Phase7ClosureWithoutRateTests.cs` | Modified | Fixed mock to throw from CreateClosureFromCommandAsync, added CancellationToken |
| `CommandCenter.Tests/Unit/ResidualRemediationLote26Tests.cs` | Modified | Removed Currency from DeclaredAmountDto, fixed mock verification |

### Verification Results

- `dotnet build CommandCenter.slnx -c Release`: **0 errors, 0 warnings**
- `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`: **1144 passed, 0 failed**
- `npm test` (Web.Frontend): **271 passed, 0 failed**
- `npm run lint` (Web.Frontend): **clean**

### Work Unit Evidence

| Evidence | Value |
|----------|-------|
| Focused test command | `dotnet test --filter "FullyQualifiedName~CloseShift"`: 7 passed, 0 failed |
| Runtime harness | Close shift with mismatched currency → arqueo uses resolver; verified via mock assertion in `CloseShift_DeclaresBothCurrencies_ClassifiesViaResolverAndIgnoresRequestCurrency` |
| Rollback boundary | `ShiftsController.cs`, `DailyClosureService.cs`, `ShiftReportMapper.cs`, `ShiftReportDetailDto.cs`, `RegisterClosePage.jsx`, test files |
