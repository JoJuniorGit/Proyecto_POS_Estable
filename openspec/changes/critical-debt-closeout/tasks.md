# Tasks: critical-debt-closeout

## Review Workload Forecast

| Field | Value | Rationale |
|-------|-------|-----------|
| Chained PRs recommended | Yes | Total ≈730–800 authored lines is above the 400-line budget, and S3b's 18 semantic re-points are the dominant unknown. |
| 400-line budget risk | High | Design budget inputs: S1 ≈120 + S2 ≈220 + S3a ≈80 + tests + S3b ≈300–350; S3b alone can break 400 in one pass. |
| Estimated changed lines | ~730–800 (S1 ~120 · S2 ~220 · S3a ~80 + tests · S3b ~300–350) | Authored additions + deletions across four slices; S3b's semantic test rewrites may exceed its design estimate. |
| Decision needed before apply | Yes | Delivery strategy is ask-on-risk: the user must pick the chain strategy before sdd-apply starts (design AD-7 expects S2/S3 chaining). |

```text
Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High
```

Suggested split: **PR1 S1 → PR2 S2 (chain backend → clients only if the diff exceeds 400) → PR3 S3a → PR4 S3b**.
If the user selects feature-branch-chain: PR1 targets the tracker branch, each later PR targets the immediate previous PR branch (base boundary must be retargeted so no previous slice appears in a child diff).

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | S1 closure-line currency (REQ-PMC-06) | PR1 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ClosureReportLineCurrency"` | Seed an undeclared USD method with non-zero expected → read the closure → USD-only line, status ≠ Balanced | `DailyClosureService.Rules.cs` + the two call sites in `DailyClosureService.cs` + S1 tests; persisted closures untouched |
| 2 | S2 backend commission contract (REQ-CAP-02/03) | PR2 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~CashAdvanceCommission"` | Configure 5.5% → `GET api/cashdrawer/advance-commission?isTransfer=true` → 200 {5.5}; unset → 422; POST cash-advance → charged 5.5 | `CashAdvanceCoordinator.cs` + `CashDrawerController.cs` + endpoint tests |
| 3 | S2 advance clients together (REQ-CAP-02/03) | PR2 (chain child if >400) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~CashAdvance"` + `cd Web.Frontend && npm test` | Open the WPF and Web previews with 5.5 → both show 5.5; channel switch → the other value; non-2xx → submit blocked | WPF drawer service + VM + dialog wiring and `CashAdvanceModal.jsx` — revert both clients together (contract pair) |
| 4 | S2 web source mapping (REQ-ADB-05) | PR2 | `cd Web.Frontend && npm test` | `advance` filter shows `CashAdvance` (2) rows and hides `Closing` (4) rows; labels 2/3/4 resolve correctly | `cashTransactionSource.js` + `RegisterPage.jsx` |
| 5 | S2 H-13 decimal ranges (AD-6, no spec) | PR2 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ProductDecimalRangeValidation"` | WPF product dialog accepts `decimal.MaxValue` without overflow | `ProductDialogViewModel.Pricing.cs` + `AddProductViewModel.cs` + test file |
| 6 | S3a service split (REQ-COC-06/03) | PR3 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"` | N/A — pure move; no runtime behavior to exercise; structural measures + mutation check below | The three `DailyClosureService*` files; revert to the monolith |
| 7 | S3b legacy removal + 18 re-points (REQ-COC-05/03) | PR4 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` (full) | Closure created through the command path after delete; 18 re-pointed tests green; coverage gate ≥ 0.80 | Guard move + 3 deletions + 18 re-points + structural tests revert together (single commit) |

### Work-Unit Commit Structure

- **S1 (PR1)** — one commit: RED tests + `DailyClosureService.Rules.cs` + both call sites. Tests ride the same commit.
- **S2 (PR2)** — one commit per work unit, ordered: (1) backend core + route + endpoint tests; (2) advance clients together (WPF service/VM/wiring and Web modal) + their tests — never split the two clients; (3) Web source mapping + tests; (4) H-13 + validation tests. Tests ride the behavior commit.
- **S3a (PR3)** — one commit; pure move, existing tests stay green; structural measurements recorded in the same commit.
- **S3b (PR4)** — one commit containing: recorded baseline coverage, duplicate-guard move, the 18 re-points, the 3 deletions, structural tests, and the restored coverage evidence. Delete and re-points MUST NOT be split across commits.

## Phase S1 — Closure-line currency (WARNING-04, REQ-PMC-06; AD-1)

- [x] **S1-01 (RED)** — Discriminating tests for merged lines: merged USD line is single-currency; non-zero per-currency diff → `Surplus`/`Shortage` (never `Balanced`); |diff| < 0.05m → `Balanced`. Files: `CommandCenter.Tests/Unit/ClosureReportLineCurrencyTests.cs` (new). Proof: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ClosureReportLineCurrency"` fails on the current hardcode (`DailyClosureService.cs:470`) and mixed Bs.S/USD diff (`:463-469`). Rollback: delete the test file.
- [x] **S1-02** — Create `Sales.Module/Services/DailyClosureService.Rules.cs` with one `BuildReportDetail(paymentMethodId, methodName, declaredNative, expectedBsS, rate)`: currency via `PaymentMethodCurrencyResolver.Resolve`; system = USD ? `PricingCalculator.ToUSD(expectedBsS, rate)` : `expectedBsS`; diff = declaredNative − system; status = |diff| < 0.05m ? Balanced : (diff > 0 ? Surplus : Shortage). Proof: compiles; S1-01 assertions pass against the helper. Rollback: delete the file.
- [x] **S1-03** — Re-point `BuildDeclaredDetails` (`Sales.Module/Services/DailyClosureService.cs:315-359`) to `BuildReportDetail`; declared-path semantics and tolerance unchanged. Proof: existing declared-path closure tests stay green. Rollback: revert the call site only.
- [x] **S1-04** — Re-point `MergeMissingMethodsWithReport` (`:434-473`): convert the Bs.S actual to the method's native currency before the diff, call `BuildReportDetail`, delete the hardcoded `ClosureStatus.Balanced` (`:470`) and the duplicate `new ShiftReportDetailResult`; persisted `ClosureDetail` block (`:449-456`) untouched (response-only). Proof: S1-01 green + IL/source scan: exactly one `new ShiftReportDetailResult` in `Sales.Module`. Rollback: revert the file.
- [x] **S1-05 (GREEN)** — Persisted-snapshot regression: read back a pre-change closure and assert persisted Bs.S fields (`DifferenceBsS`, `TotalDifferenceBsS`) unchanged after the change. Files: `ClosureReportLineCurrencyTests.cs`. Proof: test green + closure filter green. Rollback: delete the test.

## Phase S2 — Advance commission contract + source mapping + H-13 (REQ-CAP-02/03, REQ-ADB-05; AD-2/3/6)

- [x] **S2-01 (RED, threat matrix)** — New-route authorization suite: Cashier → 200 with the configured pct; unconfigured → 422 and body carries no default pct; anonymous and Driver → 401/403 with no business data. Files: `CommandCenter.Tests/Unit/CashAdvanceCommissionEndpointTests.cs` (new). Proof: RED — the route does not exist yet (404). Rollback: delete the test file.
- [x] **S2-02 (RED)** — Coordinator/parity/fail-closed tests: `TryGetCommissionPercentageAsync` → null when unset/invalid; `ProcessAsync` still throws the verbatim message; previewed P == charged P; WPF VM shows 5.5 (not 7/10), follows the selected channel, and blocks `CanConfirm` when null. Files: `CashAdvanceCommissionEndpointTests.cs`, `CommandCenter.Tests/CashAdvanceTests.cs` (extend VM tests). Proof: RED on current literals. Rollback: revert the test additions.
- [x] **S2-03 (GREEN)** — `Sales.Module/Services/CashAdvanceCoordinator.cs`: extract one private core from `ResolveCommissionPercentageAsync` (`:180-199`); add public `TryGetCommissionPercentageAsync(bool isTransfer, CancellationToken cancellationToken = default)` → `decimal?`; `ProcessAsync` keeps its throwing adapter with the message verbatim. Proof: S2-01/S2-02 green. Rollback: revert the file.
- [x] **S2-04 (GREEN)** — `Backend.API/Controllers/CashDrawerController.cs`: add `[HttpGet("advance-commission")]` + `[Authorize(Roles = "Admin,Manager,Cashier")]` returning `CashAdvanceCommissionDto(bool IsTransfer, decimal Percentage)`; unresolvable → `ApiProblemResults.ApiUnprocessableEntity` 422 ProblemDetails. Endpoint lives in `CashDrawerController.cs` — deliberately not the settings controller (`CashDrawerController.cs:251` already serves Cashier on `cash-advance`). Proof: S2-01 green. Rollback: revert the file.
- [x] **S2-05** — `Desktop.Client.Core/Services/ICashDrawerService.cs` and `Desktop.Client.Core/Services/CashDrawerService.cs`: add `GetAdvanceCommissionAsync(bool isTransfer, CancellationToken)` calling the new route. Proof: client contract covered by S2-02 (mock handler). Rollback: revert both files.
- [x] **S2-06** — `Desktop.Client.Core/ViewModels/CashAdvanceRegisterViewModel.cs`: delete the `:58` literal; commission comes from the server, is nullable while unread/failed, and `CanConfirm == false` until a valid percentage exists; ctor stays source-compatible with optional `decimal exchangeRate = 1.0m, ICashDrawerService? cashDrawer = null`. Proof: S2-02 VM tests. Rollback: revert the file.
- [x] **S2-07** — `Desktop.Client/Services/WpfDialogService.Modals.cs` (`:216`) and `Desktop.Client/Services/WpfDialogService.cs`: pass the drawer service and exchange rate into the VM. Proof: build 0/0 + dialog construction test. Rollback: revert both files.
- [x] **S2-08 (RED, web)** — `Web.Frontend/src/constants/cashTransactionSource.test.js`, `Web.Frontend/src/pages/RegisterPage.sourceFilter.test.js`, `Web.Frontend/src/components/register/CashAdvanceModal.commission.test.js` (all new): `advance` filter includes source 2 / excludes 4; `getSourceLabel(2) === 'Adelanto Efectivo'`; source scan — no bare source ordinal in `RegisterPage.jsx`; modal shows 5.5 and blocks submit on non-2xx; no 7/10 literal. Proof: `cd Web.Frontend && npm test` RED. Rollback: delete the three files.
- [x] **S2-09 (GREEN, web)** — Create `Web.Frontend/src/constants/cashTransactionSource.js` (frozen `CashTransactionSource` enum + one `SOURCE_DEFINITIONS` table + `getSourceLabel`; `constants/` is a new directory); re-point `Web.Frontend/src/pages/RegisterPage.jsx` `:123` filter and `:135-162` labels to that table (`advance` → 2; 2 → Adelanto, 3 → Ajuste, 4 → Cierre). Proof: S2-08 mapping tests. Rollback: revert `RegisterPage.jsx` and delete the constants file.
- [x] **S2-10 (GREEN, web)** — `Web.Frontend/src/components/register/CashAdvanceModal.jsx`: remove the `:45` literal and the `:166` hardcoded labels; read the server percentage per selected channel; on null/non-2xx hide the percentage and disable submit. Proof: S2-08 modal tests. Rollback: revert the file.
- [x] **S2-11** — H-13 (AD-6): replace `[Range(0, double.MaxValue)]` with `[Range(typeof(decimal), "0", "79228162514264337593543950335")]` on the 8 decimal properties in `Desktop.Client.Core/ViewModels/ProductDialogViewModel.Pricing.cs` and the 4 in `Desktop.Client.Core/ViewModels/AddProductViewModel.cs`; add `CommandCenter.Tests/Unit/ProductDecimalRangeValidationTests.cs` asserting `decimal.MaxValue` validates without overflow. Proof: new test + full suite green. Rollback: revert the two VM files + delete the test. (Hygiene only — no spec requirement.)

## Phase S3a — DailyClosureService split (REQ-COC-06, REQ-COC-03; AD-4/8)

- [ ] **S3a-01** — Create `Sales.Module/Services/DailyClosureService.Receipts.cs`: move `GenerateReceiptContent` (`DailyClosureService.cs:493-563`) and `WriteClosedClosureReceiptsAsync` (`:565-650`) verbatim; delete them from the main file. Proof: closure filter green; pure move, no behavior diff. Rollback: move the members back (single-commit revert).
- [ ] **S3a-02** — Extend `Sales.Module/Services/DailyClosureService.Rules.cs` with the statics now in `DailyClosureService.cs` (`ValidateDeclaredMethods`, `BuildDeclaredDetails`, `MergeMissingMethodsWithReport`, `RecalculateTotals`; `BuildReportDetail` already lives there); the main file keeps orchestration + queries only. Proof: closure filter green; each responsibility single-owned, no duplicate rule text. Rollback: move the members back.
- [ ] **S3a-03** — Structural (REQ-COC-06): measure and record the line count of all three `DailyClosureService*` files; every file MUST be ≤ 500 lines. Proof: recorded command output in apply notes. Rollback: none (measurement).
- [ ] **S3a-04** — Complexity + mutation (REQ-COC-03, AD-8): measure and record McCabe < 10 for every method of the three partials and for `DailyClosureController.CreateClosure`; mutation check — temporarily re-introduce a duplicated rule locally, confirm a closure test fails, then revert the mutation. Proof: recorded measurements + mutation output. Rollback: none (throwaway mutation reverted).

## Phase S3b — Legacy entry removal + 18 re-points (REQ-COC-05, REQ-COC-03; AD-5/8)

- [ ] **S3b-01** — Baseline before any delete/re-point: run the coverage command and record Core / Sales.Module / Inventory rates plus the backend test count. Files: none (command evidence). Proof: Sales.Module gate ≥ 0.80 `[OK]`. Rollback: none (measurement).
- [ ] **S3b-02** — Move the duplicate-declaration guard (`DailyClosureService.cs:125-134`) into `ValidateDeclaredMethods` in `Sales.Module/Services/DailyClosureService.Rules.cs` so the command path keeps throwing "duplicados"; the legacy copy stays until S3b-06 deletes it. Proof: guard test still throws `ArgumentException`. Rollback: revert the guard move.
- [ ] **S3b-03** — Re-point the 5 validation-only sites (`CommandCenter.Tests/Unit/DailyClosureServiceUnitTests.cs:79,294,343`; `CommandCenter.Tests/Unit/ResidualRemediationLote26Tests.cs:245`; `CommandCenter.Tests/SecurityHardeningSprint2Tests.cs:155`) to `CreateClosureCommand` with declarations; same `ArgumentException` from `RecalculateTotals`/`ValidateDeclaredMethods`; no persistence asserted. Proof: those tests green. Rollback: revert the re-points.
- [ ] **S3b-04** — Re-point the 11 persist-and-assert sites (`CommandCenter.Tests/Unit/DailyClosureServiceUnitTests.cs:54,183,314,363`; `CommandCenter.Tests/CheckoutAndPaymentTests.cs:339,377,408,442,460`; `CommandCenter.Tests/CashDrawerClosureTests.cs:189`; `CommandCenter.Tests/Unit/Phase2FinancialAndIntegrityTests.cs:88`): seed known sales, declare actual amounts, assert expected/totals from the DB-derived `ExpectedTotalDto` — not the entity's declared expected. Proof: those tests green. Rollback: revert the re-points.
- [ ] **S3b-05** — Re-point the 2 integration sites (`CommandCenter.Tests/Integration/DailyClosureFlowIntegrationTests.cs:58`; `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs:72`) to `CreateClosureFromCommandAsync`; `DailyClosureTestHelper.CreateMocks` already returns rate 50m; no test may assert on receipt files (receipts stay fail-open, logged). Proof: integration tests green. Rollback: revert the re-points.
- [ ] **S3b-06** — Delete `CreateClosureAsync(DailyClosure)` (`DailyClosureService.cs:112`), `ExecuteClosureCoreAsync` (`:123`) and `MergeMissingMethodsIntoClosure` (`:409`); the command path (`CreateClosureFromCommandAsync`) is the single implementation; no public entity-returning entry remains. Proof: build 0/0; no reference to the deleted members; S3b-02 guard survives. Rollback: restore the three methods (same commit as the re-points).
- [ ] **S3b-07** — Structural absence tests: `CommandCenter.Tests/Unit/ClosureLegacyEntryRemovalTests.cs` (new) — reflection: no public method accepts or returns `DailyClosure` to create a closure; `ExecuteClosureCoreAsync`/`MergeMissingMethodsIntoClosure` absent; duplicate guard still throws "duplicados". Proof: test green. Rollback: delete the test file.
- [ ] **S3b-08** — Restore coverage in the same commit: re-run the coverage command after re-points + delete; record before/after rates; confirm Sales.Module ≥ 0.80 and the full backend suite green; commit all S3b tasks as one work unit. Proof: coverage gate exit 0 + recorded evidence. Rollback: revert the single S3b commit.

## Traceability

| Spec | Requirement | Scenarios (delta) | Tasks |
|------|-------------|-------------------|-------|
| payment-method-currency-classification | REQ-PMC-06 | Merged USD line single-currency; Non-zero never Balanced; Within-tolerance Balanced; Persisted snapshot untouched | S1-01, S1-02, S1-03, S1-04, S1-05 |
| cash-advance-payout-integrity | REQ-CAP-02 | Preview shows the server-resolved percentage; Preview follows the channel; No client literal remains | S2-02, S2-03, S2-05, S2-06, S2-07, S2-10 (+ S2-01) |
| cash-advance-payout-integrity | REQ-CAP-03 | Missing commission rejects; Preview without a resolvable percentage blocks submission; Previewed == charged | S2-01, S2-02, S2-03, S2-06, S2-10 |
| api-dto-boundary | REQ-ADB-05 | Advance filter includes advances; Excludes closings; Labels agree; Filter resolves from the mapping | S2-08, S2-09 |
| closure-orchestration-consolidation | REQ-COC-03 | CreateClosure under the ceiling; Closure service methods under the ceiling | S3a-03, S3a-04, S3b-06 |
| closure-orchestration-consolidation | REQ-COC-05 | No public entity entry point; Any legacy seam delegates | S3b-02, S3b-06, S3b-07 |
| closure-orchestration-consolidation | REQ-COC-06 | Each file within the ceiling; Split by responsibility | S3a-01, S3a-02, S3a-03 |
| (no spec — design AD-6 / H-13) | decimal-only `[Range]` | `decimal.MaxValue` valid, no overflow | S2-11 |

## Verification

```powershell
# 1. Build — 0 errors, 0 warnings (TreatWarningsAsErrors)
dotnet build CommandCenter.slnx -c Release

# 2. Backend suite — 100% green; baseline floor 1227 (legacy-debt-cleanup close), >=1227
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release

# 3. Frontend suite + lint — 100% green; baseline floor 273, >=273
cd Web.Frontend
npm test
npm run lint

# 4. Coverage gate — run BEFORE S3b delete/re-point and re-run in the SAME S3b commit
cd ..
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings
python scripts/check-coverage.py CommandCenter.Tests/TestResults/<run-guid>/coverage.cobertura.xml
# thresholds: Core >= 0.70, Sales.Module >= 0.80, Inventory.Module >= 0.72
```

Per-slice focused commands:

```powershell
# S1
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ClosureReportLineCurrency"

# S2 (backend + WPF), then web
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~CashAdvance"
cd Web.Frontend && npm test

# S3a / S3b
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"
```

Structural / mutation checks (AD-8):

- **S1** — IL/source scan: exactly one `new ShiftReportDetailResult` in `Sales.Module`; persisted-snapshot byte-identity test.
- **S2** — Threat-matrix RED tests: Cashier → 200; unconfigured → 422 with no default; anonymous/Driver → 401/403. Source scan: no bare source ordinal in `RegisterPage.jsx`; no 7/10 literal in `CashAdvanceRegisterViewModel.cs` / `CashAdvanceModal.jsx`. Parity test: previewed P == charged P.
- **S3a** — Every `DailyClosureService*` file ≤ 500 lines (measured, recorded); every method McCabe < 10 (measured, recorded); mutation: a duplicated rule temporarily re-introduced locally makes a closure test fail, then the mutation is reverted.
- **S3b** — Reflection absence checks (`ExecuteClosureCoreAsync`, `MergeMissingMethodsIntoClosure`, no public `DailyClosure` entry); "duplicados" guard test; coverage measured before and after inside the same commit; full suite ≥ 1227.

## Open Questions Gating Apply

- **OQ-1 (gates S3b-03..S3b-05)** — Maintainer must accept semantic rewrites of the 18 legacy tests, not a mechanical call swap. Fallback: non-public adapter + `InternalsVisibleTo("CommandCenter.Tests")` (spec-forbidden as a public adapter).
- **OQ-2 (gates S2-04)** — Fail-closed status: 422 `ApiUnprocessableEntity` (chosen) vs 409 `ApiConflict`. Confirm before S2-04.
- **OQ-3 (gates S2-10)** — Web preview error UX: fixed blocked message (chosen) vs retry affordance.
- **OQ-4 (gates S1-02)** — Merged USD declared value uses `PricingCalculator.ToUSD` rounding for symmetry (chosen) vs unrounded.
- **OQ-5 (gates S2-11)** — `decimal` max bound (chosen) vs a business ceiling such as `1_000_000`.
