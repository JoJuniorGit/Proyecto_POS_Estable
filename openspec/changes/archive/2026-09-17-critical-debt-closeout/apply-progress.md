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

---

# Apply Progress — critical-debt-closeout — S2

## Slice S2: Advance commission contract + source mapping + H-13 (REQ-CAP-02/03, REQ-ADB-05; AD-2/3/6)

**Work unit**: `cdc-s2-commission`
**Mode**: Standard (`strict_tdd: false` in `openspec/config.yaml`)
**Store**: openspec (change artifacts under `openspec/changes/critical-debt-closeout/`)
**Branch**: V0.15 — no branches, no PRs
**Commit**: `fix(8.141): comision server-sourced + source mapping (S2, item 41 + S4b-04 + H-13) - ANEXO 8.141`

### Completed Tasks

- [x] S2-01 (RED, threat matrix) — New-route authorization suite in `CommandCenter.Tests/Unit/CashAdvanceCommissionEndpointTests.cs`: the action carries `[HttpGet("advance-commission")]` + `[Authorize(Roles="Admin,Manager,Cashier")]`, no `AllowAnonymous`, class-level `[Authorize]` (anonymous → 401), `Driver` excluded; Cashier resolves 200 with the configured pct; unconfigured → 422 and the body carries neither `percentage` nor `isTransfer`.
- [x] S2-02 (RED) — Coordinator/parity/fail-closed tests: `TryGetCommissionPercentageAsync` → null for `null`/`""`/`"abc"`/`"0"`/`"-5"`; `ProcessAsync` still throws the verbatim message including the key; previewed P == charged P; WPF VM shows 5.5 (not 7/10), follows the selected channel (12.25) and blocks `CanConfirm` when null. Extended `CommandCenter.Tests/CashAdvanceTests.cs` and the `MockClientCashDrawerService` double.
- [x] S2-03 (GREEN) — `Sales.Module/Services/CashAdvanceCoordinator.cs`: one private `ResolveCommissionCoreAsync` core; new public `TryGetCommissionPercentageAsync(bool, CancellationToken = default)` → `decimal?`; `ProcessAsync` keeps its throwing adapter with the message verbatim.
- [x] S2-04 (GREEN) — `Backend.API/Controllers/CashDrawerController.cs`: `[HttpGet("advance-commission")]` + `[Authorize(Roles = "Admin,Manager,Cashier")]` returning `CashAdvanceCommissionDto`; unresolvable → `ApiUnprocessableEntity` 422 ProblemDetails. Endpoint lives in `CashDrawerController` (not the settings controller).
- [x] S2-05 — `Desktop.Client.Core/Services/{ICashDrawerService,CashDrawerService}.cs`: `AdvanceCommissionClientDto` + `GetAdvanceCommissionAsync(bool isTransfer, CancellationToken)` calling the new route (422 → null).
- [x] S2-06 — `Desktop.Client.Core/ViewModels/CashAdvanceRegisterViewModel.cs`: literal deleted; `CommissionPercentage` (`decimal?`) comes from the server; `CanConfirm` requires a valid percentage; ctor stays source-compatible `(methods, availableCashLocal, exchangeRate = 1.0m, ICashDrawerService? cashDrawer = null)`.
- [x] S2-07 — `Desktop.Client/Services/WpfDialogService.Modals.cs` + `WpfDialogService.cs`: drawer service + exchange rate passed into the VM; the dialog awaits the initial read before opening.
- [x] S2-08 (RED, web) — `Web.Frontend/src/constants/cashTransactionSource.test.js`, `Web.Frontend/src/pages/RegisterPage.sourceFilter.test.js`, `Web.Frontend/src/components/register/CashAdvanceModal.commission.test.js` (all new): advance filter includes 2 / excludes 4; `getSourceLabel(2) === 'Adelanto Efectivo'`; no bare source ordinal in `RegisterPage.jsx`; modal reads the server pct, blocks submit on non-2xx; no 7/10 literal.
- [x] S2-09 (GREEN, web) — `Web.Frontend/src/constants/cashTransactionSource.js` created (frozen enum + one `SOURCE_DEFINITIONS` table + `getSourceLabel`/`getSourceIdByFilterKey`/`matchesSourceFilter`); `RegisterPage.jsx` filter and labels re-pointed to the table; the inverted switch and the bare ordinal literals are gone.
- [x] S2-10 (GREEN, web) — `Web.Frontend/src/components/register/CashAdvanceModal.jsx`: `:45` literal and `:166` labels removed; per-channel server read; null/non-2xx → no percentage, fixed blocked message, submit disabled.
- [x] S2-11 — H-13 (AD-6): `[Range(typeof(decimal), "0", "79228162514264337593543950335", ...)]` on the 8 decimal properties of `ProductDialogViewModel.Pricing.cs` and the 4 of `AddProductViewModel.cs`; new `CommandCenter.Tests/Unit/ProductDecimalRangeValidationTests.cs`.

### Files Changed

| File | Action | Authored lines | What Was Done |
|------|--------|----------------|---------------|
| `Sales.Module/Services/CashAdvanceCoordinator.cs` | Modified | +21 / −2 | Private core + public `TryGetCommissionPercentageAsync`; throwing adapter unchanged in message |
| `Sales.Module/DTOs/CashAdvanceCommissionDto.cs` | Created | +3 | `record (bool IsTransfer, decimal Percentage)` |
| `Backend.API/Controllers/CashDrawerController.cs` | Modified | +14 | `GET advance-commission` with Admin/Manager/Cashier; 200 pct / 422 ProblemDetails |
| `Desktop.Client.Core/Services/ICashDrawerService.cs` | Modified | +7 | `AdvanceCommissionClientDto` + `GetAdvanceCommissionAsync` contract |
| `Desktop.Client.Core/Services/CashDrawerService.cs` | Modified | +15 | New-route read; 422 → null; 200 → pct (>0) or null |
| `Desktop.Client.Core/ViewModels/CashAdvanceRegisterViewModel.cs` | Modified | +56 / −6 | Server-sourced nullable percentage, nullable totals, `RefreshCommissionAsync`, `CanConfirm` gate, ctor + `cashDrawer` |
| `Desktop.Client/Services/WpfDialogService.cs` | Modified | +4 / −1 | Optional `ICashDrawerService` dependency |
| `Desktop.Client/Services/WpfDialogService.Modals.cs` | Modified | +8 / −5 | Passes drawer service + rate, awaits the read before `ShowDialog` |
| `Web.Frontend/src/constants/cashTransactionSource.js` | Created | +57 | Frozen contract enum + single `{id,key,label}` table + filter/label resolvers |
| `Web.Frontend/src/pages/RegisterPage.jsx` | Modified | +4 / −36 | Filter and labels resolve from the table; bare source ordinals removed |
| `Web.Frontend/src/components/register/CashAdvanceModal.jsx` | Modified | +64 / −10 | Server read per channel, fail-closed preview and submit; literals removed |
| `Desktop.Client.Core/ViewModels/ProductDialogViewModel.Pricing.cs` | Modified | +8 / −8 | 8 decimal-only `[Range]` bounds |
| `Desktop.Client.Core/ViewModels/AddProductViewModel.cs` | Modified | +4 / −4 | 4 decimal-only `[Range]` bounds |
| `CommandCenter.Tests/Unit/CashAdvanceCommissionEndpointTests.cs` | Created | +265 | Endpoint RBAC/200/422, coordinator try/parity/verbatim, WPF client mock-handler contract |
| `CommandCenter.Tests/Unit/ProductDecimalRangeValidationTests.cs` | Created | +80 | Decimal-only `OperandType`, `decimal.MaxValue` valid, negative rejected, validator path |
| `CommandCenter.Tests/CashAdvanceTests.cs` | Modified | +69 / −11 | VM preview server-sourced, channel follow, fail-closed (replaces the literal test) |
| `CommandCenter.Tests/CashDrawerClosureTests.cs` | Modified | +5 | Test double implements the new client member |
| `Web.Frontend/src/constants/cashTransactionSource.test.js` | Created | +64 | Contract ordinals, table shape, labels, advance filter, cross-filter resolution |
| `Web.Frontend/src/pages/RegisterPage.sourceFilter.test.js` | Created | +54 | Source scan: single mapping, no bare source ordinal, opening via enum |
| `Web.Frontend/src/components/register/CashAdvanceModal.commission.test.js` | Created | +94 | Pure resolver/summary, route + disabled scan, no 7/10 literal, real mount |
| `openspec/changes/critical-debt-closeout/tasks.md` | Modified | +11 / −11 | S2-01..S2-11 marked `[x]` |

Authored total: 907 additions + 94 deletions = 1001 changed lines (docs excluded ≈ 979). Over the 400-line review budget; recorded as `size:exception` accepted by the dispatch (D4 in the ANEXO).

### Discriminating Test Evidence (RED → GREEN)

RED run (tests written first, production absent) — verbatim S2 backend filter:

```
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~CashAdvanceCommission|FullyQualifiedName~ProductDecimalRangeValidation|FullyQualifiedName~CashAdvanceRegisterViewModel"
```

Observed: build failed with CS0117/CS1061 naming exactly the missing APIs — `CashDrawerController.GetAdvanceCommission`, `CashAdvanceCommissionDto`, `CashAdvanceCoordinator.TryGetCommissionPercentageAsync`, `ICashDrawerService.GetAdvanceCommissionAsync`, the `cashDrawer` ctor parameter and `CashAdvanceRegisterViewModel.RefreshCommissionAsync`.

RED run (web):

```
node --import ./test/esbuild-jsx-loader.mjs --test src/constants/cashTransactionSource.test.js src/pages/RegisterPage.sourceFilter.test.js src/components/register/CashAdvanceModal.commission.test.js
```

Observed: `ERR_MODULE_NOT_FOUND` for `src/constants/cashTransactionSource.js`, the RegisterPage scan failures (bare `source !== 4` filter and the local inverted `getSourceLabel` switch), and the modal helper imports missing.

GREEN runs: S2 backend filter → 21 passed / 0 failed; `FullyQualifiedName~CashAdvance` → 55 passed / 0 failed; web three files → 14 passed / 0 failed.

H-13 mutation check: with `[Range(0, double.MaxValue)]` temporarily re-introduced on one property, `DecimalMoneyProperties_UseDecimalOperandRange_NotDouble` failed (1 error / 2 passed); restored → 3/3 green. Empirically, on .NET 10 the double-based attribute does not overflow on `decimal.MaxValue` (value converts to double and compares), so the discriminating assertion is the decimal-only `OperandType`; the test pins the contract and the boundary.

### Work Unit Evidence

| Evidence | Value |
|----------|-------|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~CashAdvance"` → 55 passed / 0 failed / 55 total; `cd Web.Frontend && npm test` → 287 passed / 0 failed (14 new) |
| Runtime harness command/scenario and exact result | Real controller action with coordinator + settings mock: configured 5.5 → `OkObjectResult` 200 `{isTransfer:true, percentage:5.5}`; unconfigured → `ObjectResult` 422 ProblemDetails with no `percentage`/`isTransfer` key. Real `ProcessAsync` on InMemory `SalesDbContext` (real drawer session) with 5.5 → charged 5.5 = previewed. WPF `CashDrawerService.GetAdvanceCommissionAsync` through `MockHttpMessageHandler`: path `/api/cashdrawer/advance-commission?isTransfer=true` → 5.5; 422 → null |
| Rollback boundary | Backend: `CashAdvanceCoordinator.cs` + `CashDrawerController.cs` + `CashAdvanceCommissionDto.cs`; clients together (contract pair): `Desktop.Client.Core` service/VM + `Desktop.Client` dialog wiring + `CashAdvanceModal.jsx` + `cashTransactionSource.js` + `RegisterPage.jsx`; H-13: the two VM files + the test; tests revert with their commit. No schema, no persisted data touched |
| Structural check | `RegisterPage.jsx` scan: no bare source ordinal, filter/labels resolve from `constants/cashTransactionSource.js`; `CashAdvanceRegisterViewModel.cs` / `CashAdvanceModal.jsx`: no 7/10 commission literal (VM test + web scan) |

### Verification Results (tasks.md ## Verification)

```
dotnet build CommandCenter.slnx -c Release
→ 0 errors, 0 warnings

dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
→ 1254 passed, 0 failed, 0 skipped (baseline floor 1227; +20 vs S1 1234)

cd Web.Frontend && npm test
→ 287 passed, 0 failed (baseline floor 273; +14)

cd Web.Frontend && npm run lint
→ exit 0, clean

dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings
python scripts/check-coverage.py CommandCenter.Tests/TestResults/a5036cc3-234f-4b30-9131-8920141811e2/coverage.cobertura.xml
→ Core 0.8364 [OK] · Sales.Module 0.9087 [OK] · Inventory.Module 0.8251 [OK] — exit 0
```

### Deviations from Design

- D2: no WPF dialog-window construction test. The repo has no headless WPF window infrastructure (null `Application.Current`, `App.xaml` converters) and no existing test constructs a window; the S2-07 proof is build 0/0 plus the VM contract tests (which exercise the exact ctor wiring the dialog uses). Documented, not silently skipped.
- D3: tasks.md suggested 4 work-unit commits for S2; the orchestrator dispatched `cdc-s2-commission` as one work unit with a single commit message and no PRs. Consolidated into one commit — clients stay together (never split WPF/Web), tests ride the behavior, and the contract-pair rollback stays atomic.
- D4: authored 1001 changed lines (≈979 excluding docs) is above the 400-line review budget. It cannot be split without breaking the "clients together" rule or the dispatched single commit; recorded as `size:exception` accepted by the dispatch. No code, tests, or docs were compressed to chase the number.

### GGA (punctual --no-verify)

`gga run` (v2.10.1, provider opencode, rules AGENTS.md) = STATUS FAILED. Every finding is pre-existing and out of slice scope; none is introduced by S2 (verified line by line; the diff adds zero comments to production files — only two `/**` traceability headers in the new web tests, matching the repo convention).

- Read query without `AsNoTracking` (`CashDrawerController.cs:171-174`, `ResolveAnchoredRateAsync`): pre-existing, untouched by S2.
- Explanatory comments: all cited lines are pre-existing (`AddProductViewModel:93`, `CashAdvanceRegisterViewModel:48`, `ProductDialogViewModel.Pricing.cs:12/16`, `WpfDialogService.cs:20-21/89-90/99-102/169-170`, `WpfDialogService.Modals.cs:268-269/305-306/340/377`, `ICashDrawerService.cs:9/93/108`).
- Async methods without `CancellationToken` in `Desktop.Client.Core/Services/CashDrawerService.cs`: pre-existing; the NEW `GetAdvanceCommissionAsync` does accept and propagate a token.
- Controller direct `DbContext` access + thrown validation instead of ProblemDetails: pre-existing (`ResolveAnchoredRateAsync`); the new endpoint returns 422 ProblemDetails via `ApiUnprocessableEntity` and touches no data.
- Duplicate `open`/`open-session` route: pre-existing.

Commit uses a punctual documented `--no-verify` because all blocking findings are pre-existing and out of slice.

---

# Apply Progress — critical-debt-closeout — S3a

## Slice S3a: DailyClosureService split (REQ-COC-06, REQ-COC-03; AD-4/8)

**Work unit**: `cdc-s3a-split`
**Mode**: Standard (`strict_tdd: false` in `openspec/config.yaml`)
**Store**: openspec (change artifacts under `openspec/changes/critical-debt-closeout/`)
**Branch**: V0.15 — no branches, no PRs
**Commit**: `refactor(8.141): split de DailyClosureService en parciales cohesivos (S3a, S3-06) - ANEXO 8.141`

### Completed Tasks

- [x] S3a-01 — Created `Sales.Module/Services/DailyClosureService.Receipts.cs` (165 lines): `GenerateReceiptContent` (`:8-78`) and `WriteClosedClosureReceiptsAsync` (`:80-130`) moved verbatim; their only callees `TryWriteFileWithRetryAsync` (`:132-147`) and `TryWriteTextWithRetryAsync` (`:149-164`) moved with them so the file owns the receipts responsibility (AD-4 maps the partial to the full tail `:493-649`). Deleted from the main file.
- [x] S3a-02 — Extended `Sales.Module/Services/DailyClosureService.Rules.cs` (144 lines) with the four statics: `ValidateDeclaredMethods` (`:35-51`), `BuildDeclaredDetails` (`:53-86`), `MergeMissingMethodsWithReport` (`:88-125`), `RecalculateTotals` (`:127-143`); `BuildReportDetail` (`:9-33`) stays. The main file keeps orchestration + queries + the legacy `MergeMissingMethodsIntoClosure` seam that S3b-06 deletes by its own line map; the now-unused `using Core.Helpers;` was dropped from main.
- [x] S3a-03 — Every `DailyClosureService*` file ≤ 500 lines: main 368, Rules 144, Receipts 165 (command output in Structural Evidence).
- [x] S3a-04 — McCabe < 10 for every method of the three partials and `DailyClosureController.CreateClosure` (table + convention below); mutation check reproduced and reverted.

### Files Changed

| File | Action | Lines | What Was Done |
|------|--------|-------|---------------|
| `Sales.Module/Services/DailyClosureService.cs` | Modified | 637 → 368 | Orchestration + queries; the four rule statics and the receipts tail removed verbatim; unused `using Core.Helpers;` dropped |
| `Sales.Module/Services/DailyClosureService.Rules.cs` | Modified | 33 → 144 | Single-owner closure rules: one `BuildReportDetail` + validation, declared-line building, merge-with-report, totals |
| `Sales.Module/Services/DailyClosureService.Receipts.cs` | Created | 165 | Receipt text generation + write-with-retry I/O only; no closure rule |
| `openspec/changes/critical-debt-closeout/tasks.md` | Modified | — | S3a-01..S3a-04 marked `[x]` |
| `openspec/changes/critical-debt-closeout/apply-progress.md` | Modified | — | This artifact |
| `docs/reporte.txt` | Modified | — | ANEXO 8.141 (S3a) |

Authored total (code only): 111 + 165 additions + 269 deletions = 545 changed lines. A pure move double-counts every relocated line (deleted at the source, re-added at the destination); it cannot shrink without abandoning the three-partial design (AD-4) or compressing code (forbidden). Recorded as a `size:exception` recommendation; the dispatch fixed this slice as one work unit/commit (`cdc-s3a-split`).

### Verbatim Move Evidence (pure move, no behavior diff)

Final check (worktree vs `HEAD`): each block was extracted by exact line range from both revisions and SHA-256 compared — identical source → destination, after all edits. The extraction script asserted the same hashes at write time.

| Block | Source (`HEAD` main) | Destination | SHA-256 |
|---|---|---|---|
| `ValidateDeclaredMethods` | `:380-396` | Rules `:35-51` | `0F-78-81-C2-…-FF-78` |
| `BuildDeclaredDetails` | `:315-348` | Rules `:53-86` | `48-1C-8F-B8-…-0D-2A` |
| `MergeMissingMethodsWithReport` | `:423-460` | Rules `:88-125` | `D6-74-DC-C0-…-D7-68-70` |
| `RecalculateTotals` | `:462-478` | Rules `:127-143` | `42-F0-C5-5D-…-1E-B0` |
| Receipts tail (4 members) | `:480-636` | Receipts `:8-164` | `D9-3A-72-22-…-F2-69` |
| `BuildReportDetail` (preserved) | Rules `:8-32` | Rules `:9-33` | VERBATIM |

```
ValidateDeclaredMethods         0F-78-81-C2-27-81-0D-6A-F8-AF-2C-E1-CE-BC-7D-BF-6A-71-A6-12-7E-62-7C-9B-86-A4-92-C0-AA-44-FF-78
BuildDeclaredDetails            48-1C-8F-B8-C7-1C-48-1B-09-A4-46-E9-66-85-C3-B2-EC-4A-C5-4B-7D-4B-DC-D2-D2-68-19-90-FC-FE-0D-2A
MergeMissingMethodsWithReport   D6-74-DC-C0-46-90-E1-7F-BA-32-76-44-75-2A-34-03-FE-F5-5D-BE-42-8C-E2-F5-2A-47-27-8C-62-D7-68-70
RecalculateTotals               42-F0-C5-5D-51-B7-53-F3-4B-93-E3-B2-70-00-7F-79-DD-DD-FB-1F-70-15-3F-16-CB-B1-7B-FB-23-36-1E-B0
Receipts tail 480-636           D9-3A-72-22-2E-D4-CF-8A-0A-03-B6-36-E1-14-F8-B7-99-C1-9C-15-AB-14-14-11-1A-8D-29-96-DF-91-F2-69
```

### Structural Evidence (S3a-03)

```
(Get-Content <file>).Count
DailyClosureService.cs          = 368
DailyClosureService.Rules.cs    = 144
DailyClosureService.Receipts.cs = 165
```

Every file ≤ 500 lines (REQ-COC-06); the main file dropped from 637 to 368.

### McCabe Evidence (S3a-04, REQ-COC-03)

Convention (the same one the prior S3 verifier applied; no analyzer metric exists in the repo, so this is a documented manual count per repo precedent): count every branch-introducing construct (`if`, `foreach`, `while`, `for`, `case`, `catch`, `&&`, `||`, `??`, `?:`); `McCabe = points + 1`.

| File | Method | Decision points → McCabe | < 10? |
|---|---|---|---|
| main | `.ctor` | 0 → 1 | yes |
| main | `GetExpectedTotalsByPaymentMethodAsync` | `&&` + 2 `if` → 4 | yes |
| main | `CreateClosureAsync` | 1 `if` → 2 | yes |
| main | `ExecuteClosureCoreAsync` | 2 `if` → 3 | yes |
| main | `GetClosureAsync` | 1 `?:` → 2 | yes |
| main | `LoadClosureEntityAsync` | 0 → 1 | yes |
| main | `GetLatestClosureAsync` | 1 `?:` → 2 | yes |
| main | `GetCashierDisplayNameAsync` | 0 → 1 | yes |
| main | `CreateClosureFromCommandAsync` | 2 `if` → 3 | yes |
| main | `OpenSerializableTransactionAsync` | 2 `if` → 3 | yes |
| main | `ExecuteClosureCommandAsync` | `catch` + 3 `if` → 5 | yes |
| main | `ResolveUserDetailsAsync` | 3 `??` + 2 `if` → 6 | yes |
| main | `PersistClosureCoreAsync` | 0 → 1 | yes |
| main | `MergeMissingMethodsIntoClosure` | `foreach` + `if` + `&&` + `?:` → 5 | yes |
| Rules | `BuildReportDetail` | 3 `?:` → 4 | yes |
| Rules | `ValidateDeclaredMethods` | 1 `if` → 2 | yes |
| Rules | `BuildDeclaredDetails` | `foreach` + `?:` → 3 | yes |
| Rules | `MergeMissingMethodsWithReport` | `foreach` + `if` + `&&` + 2 `?:` → 6 | yes |
| Rules | `RecalculateTotals` | `foreach` + `if` → 3 | yes |
| Receipts | `GenerateReceiptContent` | `?:` + `if` + `foreach` + `if` + 2 `?:` + `foreach` + `if` → 9 | yes |
| Receipts | `WriteClosedClosureReceiptsAsync` | `if` + `foreach` + `if` + 2 `catch` → 6 | yes |
| Receipts | `TryWriteFileWithRetryAsync` | `for` + `catch` + `if` → 4 | yes |
| Receipts | `TryWriteTextWithRetryAsync` | `for` + `catch` + `if` → 4 | yes |
| Controller | `DailyClosureController.CreateClosure` | 2 `if` → 3 | yes |

Maximum = 9 (`GenerateReceiptContent`, verbatim pre-existing body); every method < 10.

### Mutation Check (S3a-04)

Throwaway mutation: replaced the derived status in the moved `BuildReportDetail` with the old duplicated rule `ClosureStatus.Balanced`; ran the S3a filter verbatim:

```
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"
```

Observed: 3 failed / 97 passed — `MergedUndeclaredCashMethod_NonZeroDifference_IsShortageNeverBalanced` (Expected: "Shortage"), `MergedUsdCashMethod_NonZeroDifference_IsShortageNeverBalanced` (Expected: "Shortage"), `DeclaredUsdLine_PerCurrencyDifference_IsSurplus` (Expected: "Surplus"). The mutation breaks both the declared and merged paths because they share the one builder. Reverted; same command = 100 passed / 0 failed.

### Work Unit Evidence

| Evidence | Value |
|---|---|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"` → 100 passed / 0 failed / 100 total (before and after the move); mutated 3 failed / 97 passed |
| Runtime harness command/scenario and exact result | N/A — pure move; no runtime boundary or behavior changed; tasks.md unit 6 declares the harness N/A and the existing closure suite (which executes the real `CreateClosureFromCommandAsync` path) is the discriminator |
| Rollback boundary | The three `DailyClosureService*` files: revert main + Rules to `HEAD` and delete `Receipts.cs` — a single-commit revert restores the monolith. No schema, no persisted data, no constructor or test seam touched |
| Structural check | Verbatim SHA-256 (table above); 368/144/165 lines ≤ 500; McCabe max 9 |

### Verification Results (tasks.md ## Verification)

```
dotnet build CommandCenter.slnx -c Release
→ 0 errors, 0 warnings

dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
→ 1254 passed, 0 failed, 0 skipped (baseline floor 1227; unchanged vs S2 — pure move)

cd Web.Frontend && npm test
→ 287 passed, 0 failed (backend-only slice; baseline floor 273)

cd Web.Frontend && npm run lint
→ exit 0, clean

Coverage BEFORE (TestResults/194b79ed-4668-43bf-9e7d-06537eb612e9)
→ Core 0.8364 [OK] · Sales.Module 0.9087 [OK] · Inventory.Module 0.8251 [OK]

Coverage AFTER (TestResults/ed6c64e3-0dc7-464a-9d6d-e8422a75e62a)
→ Core 0.8378 [OK] · Sales.Module 0.9087 [OK] · Inventory.Module 0.8251 [OK]
```

No coverage delta outstanding: Sales.Module and Inventory are identical; Core is +0.0014 (run-order variance, not a drop) — nothing to restore per the slice rule.

### Pending Registration (out of S3a scope — not expanded)

- **RESIDUAL-S1-02** — `ShiftReportMapper.MapDetails` (`Sales.Module/Services/ShiftReportMapper.cs:41-66`) is the third aligned copy of the per-currency declared/system/difference + `0.05m` tolerance arithmetic. Phase S3a in `tasks.md` lists only S3a-01..S3a-04 (the residual is not in the slice), so it was NOT addressed. Remains pending for a future slice.

### Deviations from Design

- The receipt partial also carries `TryWriteFileWithRetryAsync` / `TryWriteTextWithRetryAsync` (S3a-01 names the two public members; AD-4 maps Receipts to the full tail `:493-649`). Moving them keeps the main file free of receipt I/O and the receipts file single-responsibility.
- `MergeMissingMethodsIntoClosure` stays in the main file: S3a-02 enumerates exactly four statics, and S3b-06 deletes this legacy seam from the main file by its own line map. Not moved, to avoid starting S3b.

### GGA (punctual --no-verify)

`gga run` (v2.10.1, provider opencode, rules AGENTS.md) = STATUS FAILED. Every finding is PRE-EXISTING and out of S3a scope; S3a is a verbatim move — none of the findings is introduced by this slice (all cited lines are unchanged content whose file position simply changed):

- Explanatory comment `DailyClosureService.cs:286` (`// 8.7-B5: …`): pre-existing (introduced by 8.7-B5; same finding already documented in S1/S2 GGA notes).
- `CreateClosureAsync` / `ExecuteClosureCoreAsync` returning the `DailyClosure` entity (`:111`, `:122`): pre-existing legacy entry point (S3-07/S4a-R1), deleted in S3b; S3a is forbidden from starting S3b.
- `ResolveUserDetailsAsync` `FindAsync` without `AsNoTracking` (`:324`): pre-existing; the method was not moved (main file), only its line number shifted. Already documented in S1/S2.
- Observations (non-blocking): generic `catch (Exception)` in the moved receipts methods, `0.05m` present in Rules/Receipts, and the "métodos no reconocidos" duplicate in main `:138-147` vs Rules `:35-51` — all pre-existing, moved verbatim; the duplicate validation is the legacy divergent copy S3b-06 deletes.

Commit uses a punctual documented `--no-verify` because all findings are pre-existing and out of slice.

---

# Apply Progress — critical-debt-closeout — S3b

## Slice S3b: Legacy entry removal + 18 re-points (REQ-COC-05, REQ-COC-03; AD-5/8)

**Work unit**: `cdc-s3b-legacy`
**Mode**: Standard (`strict_tdd: false` in `openspec/config.yaml`)
**Store**: openspec (change artifacts under `openspec/changes/critical-debt-closeout/`)
**Branch**: V0.15 — no branches, no PRs
**Commit**: `refactor(8.141): eliminacion del entry point legacy + re-point de 18 tests (S3b, S3-07/OQ-1) - ANEXO 8.141`
**OQ-1**: ACCEPTED by the maintainer — the 18 legacy tests are semantic rewrites to the command contract, not a mechanical call swap.

### Completed Tasks

- [x] S3b-01 — Baseline coverage BEFORE any delete/re-point: `TestResults/4613ff39-3b2a-48c3-8ae8-6765fa3e2a0f` → Core 0.8378 `[OK]` · Sales.Module 0.9087 `[OK]` · Inventory.Module 0.8251 `[OK]`; backend suite 1254/1254.
- [x] S3b-02 — Duplicate-declaration guard moved from the legacy `ExecuteClosureCoreAsync` into `ValidateDeclaredMethods` (`DailyClosureService.Rules.cs`), placed BEFORE the unknown-method check so the command path keeps the legacy ordering and message ("duplicados").
- [x] S3b-03 — 5 validation-only sites re-pointed: `DailyClosureServiceUnitTests` (3), `ResidualRemediationLote26Tests` (duplicate guard, now via command), `SecurityHardeningSprint2Tests` (negative amount; service now built with the helper's rate 50m because the command path requires a positive rate).
- [x] S3b-04 — 11 persist-and-assert sites re-pointed: known sales seeded, native amounts declared (USD methods declare USD; the rest Bs.S), expected/totals read back from the persisted DB-derived entity — not from the removed entity-carried `ExpectedAmountBsS`.
- [x] S3b-05 — 2 integration sites re-pointed (`DailyClosureFlow`, `DailyClosureRetry`); `DailyClosureTestHelper.CreateMocks` already returns rate 50m; no test asserts on receipt files (receipts stay fail-open, logged).
- [x] S3b-06 — Deleted `CreateClosureAsync(DailyClosure)`, `ExecuteClosureCoreAsync` and `MergeMissingMethodsIntoClosure`; `CreateClosureFromCommandAsync` is the single implementation. Build 0/0; no reference to the deleted members anywhere in the repo.
- [x] S3b-07 — New `CommandCenter.Tests/Unit/ClosureLegacyEntryRemovalTests.cs`: reflection absence of any public entity-accepting/returning closure entry, absence of `CreateClosureAsync`/`ExecuteClosureCoreAsync`/`MergeMissingMethodsIntoClosure`, and the duplicate guard still throwing "duplicados" through the command path without persisting.
- [x] S3b-08 — Coverage re-run AFTER (same commit): Core 0.8364 `[OK]` · Sales.Module 0.9091 `[OK]` · Inventory.Module 0.8251 `[OK]`; full suite 1259/1259; coverage gate exit 0.

### Files Changed

| File | Action | Authored lines | What Was Done |
|------|--------|----------------|---------------|
| `Sales.Module/Services/DailyClosureService.cs` | Modified | −83 | The three legacy members deleted; orchestration is command-only (286 lines) |
| `Sales.Module/Services/DailyClosureService.Rules.cs` | Modified | +13 | Duplicate guard relocated into `ValidateDeclaredMethods` before the unknown check |
| `CommandCenter.Tests/Unit/DailyClosureServiceUnitTests.cs` | Modified | +89 / −91 | 7 sites re-pointed (4 persist-and-assert, 3 validation); totals/names read back from the persisted closure |
| `CommandCenter.Tests/CheckoutAndPaymentTests.cs` | Modified | +48 / −51 | 5 sites re-pointed; window-reset tests keep their intent; auto-complete reads persisted details |
| `CommandCenter.Tests/CashDrawerClosureTests.cs` | Modified | +10 / −10 | Closure through the command; session assertions unchanged (mock rollover is a no-op) |
| `CommandCenter.Tests/Unit/Phase2FinancialAndIntegrityTests.cs` | Modified | +12 / −10 | Disabled-method closure through the command; persisted detail asserted |
| `CommandCenter.Tests/Unit/ResidualRemediationLote26Tests.cs` | Modified | +11 / −11 | Duplicate-id test now exercises the relocated guard on the command path |
| `CommandCenter.Tests/SecurityHardeningSprint2Tests.cs` | Modified | +10 / −15 | Negative-amount test through the command; inline `Mock.Of` rate provider replaced by the helper (rate 50m) |
| `CommandCenter.Tests/Integration/DailyClosureFlowIntegrationTests.cs` | Modified | +13 / −12 | Flow re-pointed; persisted totals read back |
| `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs` | Modified | +17 / −16 | Retry/Serializable flow re-pointed; docstring + test name follow the command entry |
| `CommandCenter.Tests/Unit/ClosureLegacyEntryRemovalTests.cs` | Created | +69 | 3 structural/guard tests (5 cases with the Theory) |
| `openspec/changes/critical-debt-closeout/tasks.md` | Modified | — | S3b-01..S3b-08 marked `[x]`; OQ-1 recorded as ACCEPTED |
| `openspec/changes/critical-debt-closeout/apply-progress.md` | Modified | — | This artifact |
| `docs/reporte.txt` | Modified | — | ANEXO 8.141 (S3b) |

Authored total (code + tests, docs excluded): 292 additions + 299 deletions = 591 changed lines. The 18 semantic re-points dominate the diff (each carries its own seeding/read-back); tasks.md mandates ONE commit for S3b and forbids splitting delete from re-points (removal alone does not compile). Recorded as a `size:exception`; the dispatch fixed this slice as one work unit/commit (`cdc-s3b-legacy`).

### Re-point Map (18 sites, 8 files)

| Class | Sites | Command-path semantics |
|-------|-------|------------------------|
| Validation-only | `DailyClosureServiceUnitTests` :79/:294/:343; `ResidualRemediationLote26Tests` :245; `SecurityHardeningSprint2Tests` :155 | Declarations with the invalid payload; same `ArgumentException` from `ValidateDeclaredMethods`/`RecalculateTotals`; no persistence asserted |
| Persist-and-assert | `DailyClosureServiceUnitTests` :54/:183/:314/:363; `CheckoutAndPaymentTests` :339/:377/:408/:442/:460; `CashDrawerClosureTests` :189; `Phase2FinancialAndIntegrityTests` :88 | Known sales seeded; actual amounts declared native; expected/totals read back from the persisted DB-derived entity |
| Integration | `DailyClosureFlowIntegrationTests` :58; `DailyClosureRetryIntegrationTests` :72 | Same call swap under the command path; rate 50m from the helper; no receipt-file assertions |

### Discriminating Test Evidence (RED → GREEN)

This slice is a removal + semantic re-point; there is no new production behavior to drive RED→GREEN in the TDD sense. The discriminating evidence is the compiler + suite double-check:

- Guard-before-commands order matters: with the guard only in the deleted legacy method, `ResidualRemediationLote26Tests` would find duplicate declarations accepted by `ValidateDeclaredMethods` and persisted. S3b-02 was applied before the re-points precisely so that test (and the new structural guard test) discriminates: both fail if the guard is absent from `ValidateDeclaredMethods`.
- Absence is enforced structurally: `LegacyClosureMembers_AreAbsent` fails the moment any legacy member is restored; `PublicSurface_ExposesNoEntityReturningClosureEntryPoint` fails if an entity entry point reappears.
- Compile-time proof of the re-points: before S3b-06 the 8 test files no longer referenced the legacy members (build green after delete = zero dangling references, verified by full-solution build 0/0).

### Work Unit Evidence

| Evidence | Value |
|----------|-------|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"` → 105 passed / 0 failed / 105 total (100 pre-S3b + 5 new structural cases) |
| Runtime harness command/scenario and exact result | Real `CreateClosureFromCommandAsync` executions on InMemory `SalesDbContext` across the 13 persist/integration re-points: closure persisted, session rollover invoked on the double, receipts fail-open; the 11 validation sites assert the command path still rejects invalid payloads before any persistence. `DailyClosureRetryIntegrationTests` requires `TEST_POSTGRES_CONNECTION` (skips locally by design — pre-existing policy). |
| Rollback boundary | Revert `Sales.Module/Services/DailyClosureService.cs` + `DailyClosureService.Rules.cs` + the 8 re-pointed test files + delete `ClosureLegacyEntryRemovalTests.cs` — a single-commit revert restores the legacy entry and its tests together. No schema, no persisted data, no config |
| Structural check | Reflection: no public method accepts/returns `DailyClosure`; `CreateClosureAsync`/`ExecuteClosureCoreAsync`/`MergeMissingMethodsIntoClosure` absent; `CreateClosureFromCommandAsync` present returning `Task<CloseShiftResult>`; duplicate guard throws "duplicados" on the command path |

### Coverage Evidence (S3b-01 baseline / S3b-08 restore)

```
BEFORE (TestResults/4613ff39-3b2a-48c3-8ae8-6765fa3e2a0f) — suite 1254/1254
  Core               rate=0.8378 [OK]
  Sales.Module       rate=0.9087 [OK]
  Inventory.Module   rate=0.8251 [OK]

AFTER (TestResults/4fdcb3cb-e647-4141-a78c-576f8c941ce8) — suite 1259/1259
  Core               rate=0.8364 [OK]
  Sales.Module       rate=0.9091 [OK]
  Inventory.Module   rate=0.8251 [OK]
```

- Sales.Module (the slice's gate) rises +0.0004 — no delta to restore; removing the legacy divergent path did not cost coverage because the command path is fully exercised.
- Core reads 0.8364 vs 0.8378: run-order variance, the same pair of values recorded across the S1/S2/S3a runs; no Core production file is touched by S3b. Nothing to restore; gate ≥ 0.70.
- The 18 re-points keep the same test count and coverage intent (the suite grew only by the 5 new structural cases).

### Verification Results (tasks.md ## Verification)

```
dotnet build CommandCenter.slnx -c Release
→ 0 errors, 0 warnings

dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
→ 1259 passed, 0 failed, 0 skipped (continuity floor 1254 + 5 structural)

dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"
→ 105 passed, 0 failed, 105 total

cd Web.Frontend && npm test
→ 287 passed, 0 failed

cd Web.Frontend && npm run lint
→ exit 0, clean

dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings
python scripts/check-coverage.py CommandCenter.Tests/TestResults/4fdcb3cb-e647-4141-a78c-576f8c941ce8/coverage.cobertura.xml
→ Core 0.8364 [OK] · Sales.Module 0.9091 [OK] · Inventory.Module 0.8251 [OK] — exit 0
```

### Deviations from Design

- The dispatch offered an S3b-a/S3b-b split "guards/removal first, re-points second". That split is infeasible while keeping the repo green: deleting the legacy members breaks the 18 still-unre-pointed call sites at compile time, and `tasks.md` explicitly mandates ONE commit and forbids splitting delete from re-points. The guard move alone (13 lines) is not a meaningful separate work unit. Single commit, `size:exception` recorded.
- `SecurityHardeningSprint2Tests` previously used `Mock.Of<ITodayExchangeRateProvider>()` (rate 0m); the command path validates `rate > 0` and would throw `InvalidOperationException` instead of the asserted `ArgumentException`, so the test now uses `DailyClosureTestHelper.CreateService` (rate 50m), matching every other closure test. The assertion intent (negative amount rejected) is unchanged.
- The `CreateClosureAsync_ClientSuppliedPaymentMethodName_...` legacy test asserted a falsified name supplied through the entity; `DeclaredPaymentAmount` carries no name, so the rewrite keeps the observable contract — the persisted detail name comes from the authoritative catalog. The falsification vector is structurally excluded by the command DTO and by the absence test.

### GGA (punctual --no-verify)

`gga run` (v2.10.1, provider opencode, rules AGENTS.md) = STATUS FAILED. Every finding is PRE-EXISTING and out of S3b scope; the diff introduces no new narrative comments (the cited ones pre-date the slice; only the retry docstring was updated to name the surviving entry point) and does not touch the skip policy of the Docker-gated test.

- Narrative comments in `CheckoutAndPaymentTests`, `DailyClosureFlowIntegrationTests`, `SecurityHardeningSprint2Tests`, `Phase2FinancialAndIntegrityTests`, `DailyClosureServiceUnitTests`, `DailyClosureRetryIntegrationTests`: pre-existing; documented legacy debt (`docs/deuda-legacy-gga-2026-09-16.md` item 38, Grupo D).
- `DailyClosureRetryIntegrationTests` silent `return` without `TEST_POSTGRES_CONNECTION`: pre-existing (documented P2 debt); S3b did not alter that branch.
- `CreateClosureFromCommandAsync` without `ArgumentNullException.ThrowIfNull(command)`: pre-existing; the method was not modified by S3b (only its legacy siblings were deleted).
- `ResolveUserDetailsAsync` `FindAsync` without `AsNoTracking`: pre-existing; same finding recorded in S1/S2/S3a.

Commit uses a punctual documented `--no-verify` because all findings are pre-existing and out of slice.
