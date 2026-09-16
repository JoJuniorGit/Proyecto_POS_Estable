# Apply Progress: cash-closure-integrity — Slice 1

## Completed Tasks
- [x] 1.1 Create `Sales.Module/Services/ClosureWindowResolver.cs`
- [x] 1.2 Create `CommandCenter.Tests/Unit/ClosureWindowResolverTests.cs`
- [x] 1.3 Modify `Sales.Module/Services/DailyClosureService.cs` — session read + resolver call + predicates
- [x] 1.4 Modify `Backend.API/Controllers/DailyClosureController.cs` — preview dateUtc validation
- [x] 1.5 Create `CommandCenter.Tests/Unit/DailyClosureServiceWindowTests.cs`

## Post-verification fixes (1b)
- [x] 1b.1 DailyClosureService.cs: dead code removal (venDate/tz/startOfDayUtc/endOfDayUtc unused) + short-form enum CashDrawerStatus.Open
- [x] 1b.2 ClosureWindowResolverTests.cs: renamed misnamed tests (RowExactlyAt*), applied QA convention Metodo_Escenario_ResultadoEsperado to all 7 tests
- [x] 1b.3 DailyClosureServiceWindowTests.cs: added 2 edge-case tests for CashTransactions predicate (at Start included, at EndExclusive excluded)
- [x] 1b.4 DailyClosureControllerTests.cs: new file with 2 tests (GetExpectedTotals default→400 ProblemDetails, valid→Ok)
- [x] 1b.5 Consolidacion pre-commit (post-GGA): 3 tests redundantes de null-anchors fusionados en 1 (-2 tests); 400 del preview migrado a ProblemDetails

## Files Changed
| File | Action | Lines |
|------|--------|-------|
| `Sales.Module/Services/ClosureWindowResolver.cs` | Created | ~30 |
| `CommandCenter.Tests/Unit/ClosureWindowResolverTests.cs` | Modified | ~117 (7 tests, renamed/consolidated) |
| `Sales.Module/Services/DailyClosureService.cs` | Modified | ~15 changed + 7 dead lines removed |
| `Backend.API/Controllers/DailyClosureController.cs` | Modified | ~5 added |
| `CommandCenter.Tests/Unit/DailyClosureServiceWindowTests.cs` | Modified | +50 (2 new CashTransactions tests) |
| `CommandCenter.Tests/Unit/DailyClosureControllerTests.cs` | Created | ~70 |

## Work Unit Evidence

| Evidence | Value |
|----------|-------|
| Focused test command | `dotnet test --filter "ClosureWindowResolver\|DailyClosureServiceWindow\|DailyClosureController"` → 19 passed (incluye 2 tests preexistentes que matchean el filtro), 0 failed |
| Runtime harness | N/A — pure unit tests + EF InMemory, no runtime boundary |
| Rollback boundary | `ClosureWindowResolver.cs` (new), `DailyClosureService.cs` changes (lines 39-64), `DailyClosureController.cs` lines 54-58 |

## Verification
- `dotnet build CommandCenter.slnx -c Release` → 0 errors / 0 warnings
- `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` → 1126/1126 (0 failures; neto +2 vs base 1124 tras consolidacion pre-commit)

## Status
Slice 1 complete + post-verification fixes (1b) complete. Ready for SDD verify.
