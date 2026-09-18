# Apply Progress — critical-debt-closeout — S1

## Slice S1: Closure-line currency (WARNING-04, REQ-PMC-06; AD-1)

**Work unit**: `cdc-s1-merge-status`
**Mode**: Standard (`strict_tdd: false` in `openspec/config.yaml`)
**Store**: openspec (change artifacts under `openspec/changes/critical-debt-closeout/`)
**Branch**: V0.15 — no branches, no PRs

### Completed Tasks

- [x] S1-01 (RED) — New `CommandCenter.Tests/Unit/ClosureReportLineCurrencyTests.cs`: merged USD line single-currency, merged non-zero per-currency difference is `Surplus`/`Shortage` (never `Balanced`), within-tolerance stays `Balanced`, structural single-construction-site scan, persisted-snapshot regression.
- [x] S1-02 — Created `Sales.Module/Services/DailyClosureService.Rules.cs` (33 lines) with the single `BuildReportDetail`; the class in `DailyClosureService.cs` is now `partial`.
- [x] S1-03 — Re-pointed `BuildDeclaredDetails` to `BuildReportDetail`; declared-path semantics unchanged (declared amount stays native, system = `PricingCalculator.ToUSD` for USD, `0.05m` tolerance).
- [x] S1-04 — Re-pointed `MergeMissingMethodsWithReport`: the merged Bs.S actual is converted to the method's native currency before the difference; the hardcoded `ClosureStatus.Balanced` and the duplicate `new ShiftReportDetailResult` are deleted. The persisted `ClosureDetail` block is untouched (response-only).
- [x] S1-05 (GREEN) — Persisted-snapshot regression: a pre-change closure is read back (service DTO + raw EF read) after a later closure command; `ExpectedAmountBsS`, `ActualAmountBsS`, `DifferenceBsS`, `TotalExpectedBsS`, `TotalActualBsS`, `TotalDifferenceBsS` are unchanged.

### Files Changed

| File | Action | Authored lines | What Was Done |
|------|--------|----------------|---------------|
| `Sales.Module/Services/DailyClosureService.Rules.cs` | Created | +33 | One `BuildReportDetail(paymentMethodId, methodName, declaredNative, expectedBsS, rate)` — resolver currency, per-currency system amount, per-currency difference, `0.05m`-tolerance derived status |
| `Sales.Module/Services/DailyClosureService.cs` | Modified | +14 / −27 | `partial` class; both call sites re-pointed; hardcoded `Balanced` (`:470` pre-change) and mixed Bs.S/USD diff (`:463-469` pre-change) deleted |
| `CommandCenter.Tests/Unit/ClosureReportLineCurrencyTests.cs` | Created | +257 | 7 discriminating/regression tests (S1-01 + S1-05) |
| `openspec/changes/critical-debt-closeout/tasks.md` | Modified | — | S1-01..S1-05 marked `[x]` |
| `openspec/changes/critical-debt-closeout/apply-progress.md` | Created | — | This artifact |
| `docs/reporte.txt` | Modified | — | ANEXO 8.141 record |

Authored total: 331 additions + 27 deletions = 358 changed lines — under the 400-line review budget.

### Discriminating Test Evidence (RED → GREEN)

RED run (test file only, before the production fix) — verbatim S1 command:

```
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ClosureReportLineCurrency"
```

Observed: 4 failed, 3 passed, 7 total. The four failures and the defect each one detects:

- `MergedUsdLine_ExpressesDeclaredSystemAndDifferenceInUsd` — the merged declared amount was the Bs.S value `2500` instead of the USD `50` (mixed-unit defect, old `:463-469`).
- `MergedUndeclaredCashMethod_NonZeroDifference_IsShortageNeverBalanced` — status was `Balanced` for a `-1000` Bs.S difference (hardcoded status, old `:470`).
- `MergedUsdCashMethod_NonZeroDifference_IsShortageNeverBalanced` — status was `Balanced` for a `-50` USD difference.
- `SalesModule_HasExactlyOneShiftReportDetailResultConstructionSite` — 2 construction sites in `Sales.Module` (duplicate).

GREEN run (after the fix), same command: 7 passed, 0 failed.
Closure regression filter: `FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure` → 100 passed, 0 failed.

### Work Unit Evidence

| Evidence | Value |
|----------|-------|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ClosureReportLineCurrency"` → RED: 4 failed / 3 passed / 7 total; GREEN after fix: 7 passed / 0 failed / 7 total |
| Runtime harness command/scenario and exact result | Real service path through `DailyClosureService.CreateClosureFromCommandAsync` on InMemory `SalesDbContext`: undeclared USD method with expected 2500 Bs.S → line in USD (declared 50, system 50, diff 0); undeclared cash method with expected 1000 Bs.S → `Shortage` with diff -1000; undeclared USD cash method with expected 2500 Bs.S → `Shortage` with diff -50; declared USD 60 vs system 50 → `Surplus`. All asserted in the new test file; 7/7 green |
| Rollback boundary | `Sales.Module/Services/DailyClosureService.Rules.cs` (delete) + the two call sites and the `partial` keyword in `Sales.Module/Services/DailyClosureService.cs` (revert the file) + `CommandCenter.Tests/Unit/ClosureReportLineCurrencyTests.cs` (delete). Persisted closures were never touched |
| Structural check | Source scan: exactly one `new ShiftReportDetailResult` in `Sales.Module` (`DailyClosureService.Rules.cs:24`), enforced by `SalesModule_HasExactlyOneShiftReportDetailResultConstructionSite` |

### Verification Results (tasks.md ## Verification)

```
dotnet build CommandCenter.slnx -c Release
→ 0 errors, 0 warnings

dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
→ 1234 passed, 0 failed, 0 skipped (baseline floor 1227; +7 new tests)

cd Web.Frontend && npm test
→ 273 passed, 0 failed

cd Web.Frontend && npm run lint
→ exit 0, clean
```

Coverage gate is not part of S1 (tasks.md reserves it for S3b baseline/restore).

### Deviations from Design

None — implementation matches AD-1 (helper signature, currency conversion, `0.05m` tolerance, response-only persisted `ClosureDetail`).

### GGA (punctual --no-verify)

`gga run` (v2.10.1, provider opencode, rules AGENTS.md) = STATUS FAILED. Every remaining finding is pre-existing and out of slice scope; none is introduced by S1. The one in-slice finding from the first run (missing `CancellationToken` in the new test helpers) was fixed and is gone in the second run.

- Explanatory comment `DailyClosureService.cs:287` (`// 8.7-B5: …`): introduced by 8.7-B5; AD-18 keeps 8.x traceability markers; out of S1 scope.
- `DailyClosureService.cs` 637 lines > 500 ceiling: pre-existing legacy debt, explicitly the scope of S3a-01/S3a-02 of this same change; S1 is forbidden from starting S2+ or the split.
- `ResolveUserDetailsAsync` `FindAsync` without `AsNoTracking` (minor, non-blocking): pre-existing, untouched by S1.
- Legacy `CreateClosureAsync` returning the `DailyClosure` entity (note, non-blocking): S3-07/S4a-R1, deleted in S3b.
- Test naming 2-segment vs 3-segment (advisory, non-blocking): matches repo-wide convention (e.g. `PersistedClosure_IsNeverRewrittenByLaterArqueo`).

Commit uses a punctual documented `--no-verify` because the two blocking findings are pre-existing and out of slice, and the in-slice finding was resolved.
