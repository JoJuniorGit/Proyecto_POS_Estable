# Apply Progress: cash-closure-integrity

## Slice 1 — Completed Tasks
- [x] 1.1 Create `Sales.Module/Services/ClosureWindowResolver.cs`
- [x] 1.2 Create `CommandCenter.Tests/Unit/ClosureWindowResolverTests.cs`
- [x] 1.3 Modify `Sales.Module/Services/DailyClosureService.cs` — session read + resolver call + predicates
- [x] 1.4 Modify `Backend.API/Controllers/DailyClosureController.cs` — preview dateUtc validation
- [x] 1.5 Create `CommandCenter.Tests/Unit/DailyClosureServiceWindowTests.cs`

### Slice 1 Post-verification fixes (1b)
- [x] 1b.1 DailyClosureService.cs: dead code removal (venDate/tz/startOfDayUtc/endOfDayUtc unused) + short-form enum CashDrawerStatus.Open
- [x] 1b.2 ClosureWindowResolverTests.cs: renamed misnamed tests (RowExactlyAt*), applied QA convention Metodo_Escenario_ResultadoEsperado to all 7 tests
- [x] 1b.3 DailyClosureServiceWindowTests.cs: added 2 edge-case tests for CashTransactions predicate (at Start included, at EndExclusive excluded)
- [x] 1b.4 DailyClosureControllerTests.cs: new file with 2 tests (GetExpectedTotals default→400 ProblemDetails, valid→Ok)
- [x] 1b.5 Consolidacion pre-commit (post-GGA): 3 tests redundantes de null-anchors fusionados en 1 (-2 tests); 400 del preview migrado a ProblemDetails

### Slice 1 Files Changed
| File | Action | Lines |
|------|--------|-------|
| `Sales.Module/Services/ClosureWindowResolver.cs` | Created | ~30 |
| `CommandCenter.Tests/Unit/ClosureWindowResolverTests.cs` | Modified | ~117 (7 tests, renamed/consolidated) |
| `Sales.Module/Services/DailyClosureService.cs` | Modified | ~15 changed + 7 dead lines removed |
| `Backend.API/Controllers/DailyClosureController.cs` | Modified | ~5 added |
| `CommandCenter.Tests/Unit/DailyClosureServiceWindowTests.cs` | Modified | +50 (2 new CashTransactions tests) |
| `CommandCenter.Tests/Unit/DailyClosureControllerTests.cs` | Created | ~70 |

## Slice 2 — Completed Tasks
- [x] 2.1 Create `Sales.Module/Services/CashAdvanceCoordinator.cs`
- [x] 2.2 Create `CommandCenter.Tests/Unit/CashAdvanceCoordinatorTests.cs`
- [x] 2.3 Create `CommandCenter.Tests/Integration/CashAdvanceEnvelopeTests.cs`
- [x] 2.4 Modify `Sales.Module/Services/CashDrawerService.cs` — remove ProcessCashAdvanceAsync, IServiceProvider, GetSalesService, GetSettingsService, GetCommissionPercentageAsync
- [x] 2.5 Modify `Sales.Module/Interfaces/ICashDrawerService.cs` — remove ProcessCashAdvanceAsync signature
- [x] 2.6 Re-point `CommandCenter.Tests/CashAdvanceTests.cs` — 9 call sites → CashAdvanceCoordinator.ProcessAsync
- [x] 2.7 Re-point `CommandCenter.Tests/Unit/CashDrawerServiceUnitTests.cs` — remove 3 tests of removed method; `CashDrawerClosureTests.cs` — remove 1 test of removed method; `Phase3ConcurrencyAndReservationTests.cs` — fix constructor calls
- [x] 2.8 Modify `Backend.API/Controllers/CashDrawerController.cs` — inject CashAdvanceCoordinator, replace ProcessCashAdvanceAsync call
- [x] 2.9 Modify `Backend.API/Startup/ServiceCollectionExtensions.cs` — register CashAdvanceCoordinator as scoped

### Slice 2 Files Changed
| File | Action | Lines |
|------|--------|-------|
| `Sales.Module/Services/CashAdvanceCoordinator.cs` | Created | ~150 |
| `CommandCenter.Tests/Unit/CashAdvanceCoordinatorTests.cs` | Created | ~200 |
| `CommandCenter.Tests/Integration/CashAdvanceEnvelopeTests.cs` | Created | ~120 |
| `Sales.Module/Services/CashDrawerService.cs` | Modified | -167 (removed locator + ProcessCashAdvanceAsync; 449 lines total, under 500) |
| `Sales.Module/Interfaces/ICashDrawerService.cs` | Modified | -9 (removed ProcessCashAdvanceAsync signature) |
| `CommandCenter.Tests/CashAdvanceTests.cs` | Modified | rewritten (9 call sites re-pointed) |
| `CommandCenter.Tests/Unit/CashDrawerServiceUnitTests.cs` | Modified | -60 (removed 3 tests of deleted method) |
| `CommandCenter.Tests/CashDrawerClosureTests.cs` | Modified | -12 (removed 1 test of deleted method) |
| `CommandCenter.Tests/Unit/Phase3ConcurrencyAndReservationTests.cs` | Modified | ~4 (constructor call fix) |
| `Backend.API/Controllers/CashDrawerController.cs` | Modified | +5 (constructor + field + call) |
| `Backend.API/Startup/ServiceCollectionExtensions.cs` | Modified | +1 (register coordinator) |
| `CommandCenter.Tests/Unit/ExchangeRateReferenceBoundaryTests.cs` | Modified | +4 (controller ctor fix) |
| `CommandCenter.Tests/Unit\SecurityTests.cs` | Modified | +4 (controller ctor fix) |

## Work Unit Evidence

| Evidence | Value |
|----------|-------|
| Focused test command | `dotnet test --filter "CashAdvanceCoordinator"` → 5/5 unit tests passed (integration envelope tests require TEST_POSTGRES_CONNECTION; not matched by this filter) |
| Runtime harness | N/A — EF InMemory for unit tests; TEST_POSTGRES_CONNECTION-gated for envelope tests |
| Rollback boundary | `CashAdvanceCoordinator.cs` (new), `CashDrawerService.cs` locator removal, `CashDrawerController.cs` wiring, `ServiceCollectionExtensions.cs` registration |

### Slice 2 Post-verification fixes (2b)
- [x] 2b.1 CashAdvanceCoordinatorTests.cs: snapshot test — exact assertions (`Assert.Equal(21.4m, sale.TotalUSD)`, `Assert.Equal(0m, sale.RoundingAdjustment)`) replacing weak `Assert.True(sale.TotalUSD > 0m)`
- [x] 2b.2 CashAdvanceCoordinatorTests.cs: rate anchoring test — inline coordinator with `inventoryMock` returning official rate 60.0m vs input 50.0m; asserts `AppliedRate`/drawer txs = 60.0m (discriminates BCV rate from raw input)
- [x] 2b.3 CashAdvanceEnvelopeTests.cs: added `[Collection(PostgresRealCollection.Name)]` (DisableParallelization); `SkipIfNoPostgres()` helper replacing the 3 silent-return guards; scoped delta assertion in `Envelope_DrawerFailure_RollsBackSaleAndTransactions` (capture count before, assert unchanged after — avoids false positives on shared DB); new atomicity test (fake `ICashDrawerService` that throws → assert no sale/tx persisted)
- [x] 2b.4 CashAdvanceCoordinator.cs: `Core.Logging.AppLogger.LogWarn(...)` before commission throw (context: transfer vs cash, setting key and value)

## Verification
- `dotnet build CommandCenter.slnx -c Release` → 0 errors / 0 warnings
- `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` → 1132/1132 (0 failures)
- `dotnet test --filter "CashAdvanceCoordinator"` → 5/5 unit tests (integration envelope tests require Postgres; not matched by filter)

## Status
Slice 1 complete (committed). Slice 2 complete + post-verification fixes (2b) applied. Ready for SDD verify.
