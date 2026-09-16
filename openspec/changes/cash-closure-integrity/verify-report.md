```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:9f067a22f7e4e9c839ea259f8cd0c9b220716118a030858cf8ba0119b516a8ed
verdict: fail
blockers: 0
critical_findings: 1
requirements: 14/14
scenarios: 16/18
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
test_exit_code: 0
test_output_hash: sha256:269e3aaf91ed9a5a625b47e3b78b0701faf9e230cc0b897b7c79a0be04a0f582
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:7a22bd1293d01c2a3d91fe223bc796e6e5dc47d96941db54490a3a7da09a4386
```

## Verification Report

**Change**: cash-closure-integrity
**Version**: N/A (delta specs, no version headers)
**Mode**: Standard (Strict TDD inactive: `openspec/config.yaml` → `strict_tdd: false`, `testing.strict_tdd_mode: disabled`)

### Completeness

| Metric | Value |
|--------|-------|
| Tasks total | 20 |
| Tasks complete | 20 |
| Tasks incomplete | 0 |

All 20 tasks in `tasks.md` are checked. Phase 4 (verification) tasks 4.1–4.4 were re-executed independently in this pass and reproduced.

### Build & Tests Execution

**Build**: ✅ Passed (0 warnings / 0 errors)

```text
$ dotnet build CommandCenter.slnx -c Release
Compilación correcta.
    0 Advertencia(s)
    0 Errores
Tiempo transcurrido 00:00:36.84
```

**Tests**: ✅ 1136 passed / ❌ 0 failed / ⚠️ 0 skipped

```text
$ dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
Correctas! - Con error: 0, Superado: 1136, Omitido: 0, Total: 1136, Duración: 12 s
```

**Coverage** (gate: `python scripts/check-coverage.py <coverage.cobertura.xml>`, exit 0)

```text
Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8364 min=0.7000 [OK]
  Sales.Module       rate=0.8879 min=0.8000 [OK]
  Inventory.Module   rate=0.8251 min=0.7200 [OK]
```

Report: `CommandCenter.Tests/TestResults/20613f97-d89f-4756-b4ce-82f927fc8323/coverage.cobertura.xml`.
All three gates pass; Sales.Module (0.8879) is the gated layer for this change (design gate: Sales.Module ≥ 0.80).

**Environment note**: `TEST_POSTGRES_CONNECTION` is not set in this environment (`GITHUB_ACTIONS` also unset). The three Postgres-gated tests in `CashAdvanceEnvelopeTests` therefore early-return via `SkipIfNoPostgres()`/`if (!IsPostgresAvailable) return;`. They are counted as *passed* by xUnit (not *skipped*), so the green 1136/1136 suite contains 3 vacuous passes. See WARNING-01.

### Spec Compliance Matrix

Actual counts extracted from the spec headings: **14 requirements / 18 scenarios**
(`cash-closure-arqueo-window` 5/8, `cash-advance-payout-integrity` 6/7, `payment-method-currency-classification` 3/3).

| # | Requirement | Scenario | Test | Result |
|---|-------------|----------|------|--------|
| 1 | Session-Anchored Window Precedence | Open session anchors the window | `ClosureWindowResolverTests.Resolve_WhenSessionOpen_SessionOpensAtAnchorsWindow`, `..._WhenNightShiftCrossingMidnight_SalesFromPreviousDayAreIncluded`, `DailyClosureServiceWindowTests.GetExpectedTotals_SalesBeforeSessionAreExcluded` | ✅ COMPLIANT |
| 2 | Session-Anchored Window Precedence | No session falls back to last closure, then start of day | `ClosureWindowResolverTests.Resolve_WhenNoSession_FallsBackToLastClosure`, `..._WhenNoSessionNoClosure_FallsBackToStartOfDay` | ✅ COMPLIANT |
| 3 | Half-Open Interval Boundaries | Row exactly at the window start | `DailyClosureServiceWindowTests.GetExpectedTotals_IncludesSaleExactlyAtSessionOpenedAt`, `..._CashTransactionExactlyAtStart_Included` | ✅ COMPLIANT |
| 4 | Half-Open Interval Boundaries | Row exactly at the right bound | `DailyClosureServiceWindowTests.GetExpectedTotals_ExcludesSaleExactlyAtEndExclusiveUtc`, `..._CashTransactionExactlyAtEndExclusive_Excluded` | ✅ COMPLIANT |
| 5 | Backdated Closure Fallback | Backdated closure behind the current session | `ClosureWindowResolverTests.Resolve_WhenBackdatedClosureBehindSession_FallsBackToLastClosure`, `..._WhenBackdatedClosureNoLastClosure_FallsBackToStartOfDay` | ✅ COMPLIANT |
| 6 | Pure Resolver and Immutable Snapshots | Resolver is unit-testable without a database | `ClosureWindowResolverTests` (7 tests, pure static `ClosureWindowResolver.Resolve`, no `DbContext`) | ✅ COMPLIANT |
| 7 | Pure Resolver and Immutable Snapshots | A persisted closure is never rewritten | `DailyClosureServiceWindowTests.PersistedClosure_IsNeverRewrittenByLaterArqueo` | ✅ COMPLIANT (preview path only; see SUGGESTION-01) |
| 8 | Preview Requires an Explicit Date | Preview rejects a missing date | `DailyClosureControllerTests.GetExpectedTotals_WhenDefaultDate_Returns400ProblemDetails`, `..._WhenValidDate_ReturnsOk` | ✅ COMPLIANT |
| 9 | Atomic Payout and Accounting Sale | Sale creation succeeds | `CashAdvanceCoordinatorTests.ProcessAsync_ConfiguredCommissionApplied`, `ProcessAsync_PhysicalDrawerBalanceDeductsOnlyRequested`, `CashAdvanceTests.ProcessCashAdvance_Transfer_Applies7PercentCommission...` | ✅ COMPLIANT |
| 10 | Atomic Payout and Accounting Sale | Accounting sale fails | `Integration/CashAdvanceEnvelopeTests.Envelope_SaleFailure_NoDrawerTransactionPersists` — **not executed** (no `TEST_POSTGRES_CONNECTION`); no InMemory equivalent | ⚠️ PARTIAL |
| 11 | Commission Resolved from System Settings | Configured commission is applied | `CashAdvanceCoordinatorTests.ProcessAsync_ConfiguredCommissionApplied`, `CashAdvanceTests.ProcessCashAdvance_Transfer_UsesConfiguredCommissionFromSystemSettings` (5.5% ≠ default 7%) | ✅ COMPLIANT |
| 12 | Fail-Closed on Unresolvable Commission | Missing commission rejects the advance | `CashAdvanceCoordinatorTests.ProcessAsync_MissingCommissionRejectsWithoutPayoutOrSale`, `CashAdvanceTests.ProcessCashAdvance_Cash_MissingCommissionRejectsWithoutPayoutOrSale` | ✅ COMPLIANT |
| 13 | Ordering and Rate Anchoring Preserved | Drawer uses the sale's anchored rate | `CashAdvanceCoordinatorTests.ProcessAsync_AppliedRateAnchorsDrawerMovements` (mock returns 60.0 vs input 50.0; asserts both drawer txs use 60.0) | ✅ COMPLIANT |
| 14 | Financial Snapshots Preserved | Snapshots are written as before | `CashAdvanceCoordinatorTests.ProcessAsync_SnapshotsPreservedWithDefaultRoundingAdjustment` (`TotalUSD=21.4`, `TotalBsS=1070`, `FinalPaidAmountBsS=1070`, `RoundingAdjustment=0`) | ✅ COMPLIANT |
| 15 | No Service Locator | Drawer has no locator | Source: `CashDrawerService.cs:17` (`CashDrawerService(SalesDbContext)`), zero `IServiceProvider`/`ISalesService` references; every coordinator test constructs the graph via constructor injection | ✅ COMPLIANT (static + compile-enforced) |
| 16 | Single Classifier Source of Truth | Report uses the shared classifier | `PaymentMethodCurrencyClassificationTests.GetReportById_Dolares_ClasificaBsSPorResolver` | ✅ COMPLIANT |
| 17 | Conversions via PricingCalculator.ToUSD | Bs.S total is converted with the shared helper | `PaymentMethodCurrencyClassificationTests.ToUSD_RoundsAwayFromZero_DiffersFromRawDivision` (`ToUSD(100,6)=16.67 ≠ 16.666…`, `ToUSD(1,8)=0.13`); controller assert `3.00m` | ✅ COMPLIANT |
| 18 | Report and Receipt Agreement | Report matches the stored receipt | (none found) | ❌ UNTESTED |

**Compliance summary**: 16/18 scenarios compliant (1 PARTIAL, 1 UNTESTED).

Structural support for scenario 18 exists — `Sales.Module/Services/ClosurePdfGenerator.cs:72`, `DailyClosureService.cs:225`, `DailyClosureService.cs:249` and `ShiftsController.cs:283` all call the same `PaymentMethodCurrencyResolver`, so agreement is guaranteed *by construction* — but **no test asserts it at runtime**, and the existing receipt tests (`DailyClosureServiceUnitTests:83,109`) assert only header strings, never the `MONEDA` column or the USD amount.

### Correctness (Static Evidence)

| Requirement | Status | Notes |
|------------|--------|-------|
| Session-Anchored Window Precedence | ✅ Implemented | `ClosureWindowResolver.cs:20` `activeSessionOpenedAtUtc ?? lastClosureDateUtc ?? startOfDayUtc`; `DailyClosureService.cs:31-40` reads the open session and feeds it |
| Half-Open Interval Boundaries | ✅ Implemented | `DailyClosureService.cs:46-47` and `:57-58` both use `>= startUtc && < endUtc`; `EndExclusiveUtc = startOfDayUtc.AddDays(1)` (`ClosureWindowResolver.cs:18`) |
| Backdated Closure Fallback | ✅ Implemented | `ClosureWindowResolver.cs:22-28` re-clamps only when `anchor >= endExclusiveUtc`, bounded to the business day |
| Pure Resolver and Immutable Snapshots | ✅ Implemented | `ClosureWindowResolver` is a static class with a tuple return and no `DbContext`; `CreateClosureAsync` never rewrites persisted `ClosureDetail` rows |
| Preview Requires an Explicit Date | ✅ Implemented | `DailyClosureController.cs:56-63` returns `Problem(statusCode: 400, title: "Parámetro inválido")` when `dateUtc == default`; no fallback to current day |
| Atomic Payout and Accounting Sale | ✅ Implemented | `CashAdvanceCoordinator.cs:67-81` execution-strategy envelope; `:96-100` transaction; `:167-175` rollback-and-rethrow (no log-and-continue) |
| Commission Resolved from System Settings | ✅ Implemented | `CashAdvanceCoordinator.cs:21-31` constructor-injects `ISystemSettingsService`; `:186` reads `SettingKeys.CashAdvance*CommissionPct` |
| Fail-Closed on Unresolvable Commission | ✅ Implemented | `CashAdvanceCoordinator.cs:188-194` throws `InvalidOperationException` before any sale/drawer write |
| Ordering and Rate Anchoring Preserved | ✅ Implemented | `CashAdvanceCoordinator.cs:108-123` sale first, then `anchoredRate = createdSale.AppliedRate`; `:125-147` both drawer movements use `anchoredRate` |
| Financial Snapshots Preserved | ✅ Implemented | Snapshots produced by `ISalesService.CreateCashAdvanceSaleAsync` and returned unchanged; `RoundingAdjustment` never set |
| No Service Locator | ✅ Implemented | `CashDrawerService.cs:15-20` single `SalesDbContext` dependency; `ICashDrawerService.cs` no longer declares `ProcessCashAdvanceAsync` |
| Single Classifier Source of Truth | ✅ Implemented | `ShiftsController.cs:283` `PaymentMethodCurrencyResolver.Resolve(d.PaymentMethodName)` |
| Conversions via PricingCalculator.ToUSD | ✅ Implemented | `ShiftsController.cs:285-286` `PricingCalculator.ToUSD(...)`; `Desktop.Client.Core/Helpers/PricingHelper.cs:20` is a pure delegation; `Backend.API.csproj` has no `Desktop.*` reference |
| Report and Receipt Agreement | ✅ Implemented, ❌ unverified | Same resolver on both paths (see scenario 18) |

### Coherence (Design)

| Decision | Followed? | Notes |
|----------|-----------|-------|
| AD-1 Static `ClosureWindowResolver` returning `(StartUtc, EndExclusiveUtc)` | ✅ Yes | `ClosureWindowResolver.cs:6-8` |
| AD-2 Read `CashDrawerSessions` off the scoped `SalesDbContext`; ctor unchanged | ✅ Yes | `DailyClosureService.cs:19` single-param ctor; session read `:31-35`; all 12 call sites un-churned |
| AD-3 Backdated guard = legacy clamped rule bounded to the day | ✅ Yes | `ClosureWindowResolver.cs:22-28`; guarantees `StartUtc < EndExclusiveUtc` |
| AD-4 Envelope owned by the coordinator (4 ctor deps) | ✅ Yes | `CashAdvanceCoordinator.cs:21-31` |
| AD-5 `AppLogger.LogWarn` + throw, no `ILogger<>` | ✅ Yes | `CashAdvanceCoordinator.cs:190` |
| AD-6 `InvalidOperationException` fail-closed | ✅ Yes | `CashAdvanceCoordinator.cs:191-193` (mapped to 409 by the existing middleware) |
| AD-7 `PricingCalculator.ToUSD`, not `PricingHelper` | ✅ Yes | `ShiftsController.cs:285-286`; `PricingHelper.cs:20` delegates |
| AD-8 Validate preview `dateUtc` | ✅ Yes | `DailyClosureController.cs:56-63` |

Additional design-stated invariants verified: default isolation (not Serializable) — `CashAdvanceCoordinator.cs:99` `BeginTransactionAsync()` with no isolation level; InMemory transaction skip preserved for unit tests — `:97-100`.

### Rules Compliance Audit

- **Financial integrity / decimal-only money** ✅ — no `float`/`double` in any changed path; commission and rounding use `Math.Round(..., MidpointRounding.AwayFromZero)` (`CashAdvanceCoordinator.cs:104`, `PricingCalculator.ToUSD`).
- **History immutability** ✅ — no persisted closure/sale snapshot is recomputed; `ClosureWindowResolver` is pure and writes nothing.
- **No `async void`** ✅ — all changed async members return `Task`/`Task<T>`.
- **DTO/data boundary** ✅ for the new code — the coordinator returns `CashAdvanceResultDto`; pre-existing EF-entity leakage in `CashDrawerController` is registered legacy debt (see below).
- **Zero-Trust / RBAC** ✅ — no role checks were weakened.
- **Layering** ✅ — `Backend.API` does not reference `Desktop.*`; no cyclic module dependency introduced (`coordinator → ISalesService → ICashDrawerService → SalesDbContext`).
- **Naming/Async suffix** ✅ for changed members (`ProcessAsync`, `ResolveCommissionPercentageAsync`).
- **CancellationToken** ✅ for new code — the coordinator accepts and propagates `CancellationToken` (`:42`, `:64`, `:80-81`, `:99`, `:151`, `:171`, `:180`).
- **Size limits** ✅ — `CashDrawerService.cs` 449 lines (< 500), no `IServiceProvider`; design's 449-line prediction matched exactly.
- **No explanatory comments in new code** ✅ — the coordinator and resolver carry none.

### Issues Found

**Change-attributable**

- **CRITICAL-01** — `payment-method-currency-classification` REQ-3 scenario "Report matches the stored receipt" is **UNTESTED**. No runtime test compares the report's classification/labels and USD amounts against the stored receipt/PDF for the same payment method. Agreement holds only by construction (both call `PaymentMethodCurrencyResolver`), which does not satisfy the spec's MUST at runtime. Minimal fix: one test that builds a `DailyClosure` with e.g. `"Divisas (USD)"` and `"Dolares"` details, generates `DailyClosureService.GenerateReceiptContent`/`ClosurePdfGenerator` output and `ShiftsController.GetReportById`, and asserts label and amount equality per method.

**WARNING**

- **WARNING-01** — The three `CashAdvanceEnvelopeTests` early-return without asserting when `TEST_POSTGRES_CONNECTION` is unset, yet xUnit reports them as *passed* (0 skipped). Scenario "Accounting sale fails" (matrix #10) therefore has no effective runtime evidence in this environment. CI on GitHub Actions throws (by design, `:39-43`), but any non-CI environment (and this verification run) silently gets a vacuous green. Recommendation: run the suite once with a real Postgres before archive, and consider an InMemory-testable seam so the "sale throws ⇒ no drawer movement" path is asserted unconditionally.
- **WARNING-02** — `Desktop.Client.Core/ViewModels/CashAdvanceRegisterViewModel.cs:58` still hardcodes `IsTransfer ? 7.0m : 10.0m`, diverging from the server, which now resolves the commission from `SystemSettings` (verified: configured 5.5% in `ProcessCashAdvance_Transfer_UsesConfiguredCommissionFromSystemSettings`). The advance preview shown to the operator can therefore disagree with the amount actually charged. **Pre-existing** (initial commit `7b382ee`, untouched by this change) and **not registered** in `docs/deuda-legacy-gga-2026-09-16.md`. The server remains authoritative and fail-closed, so this is not a payout-integrity break — but it belongs in the legacy registry (adjacent to item 39's client-side classification fallback).

**SUGGESTION**

- **SUGGESTION-01** — Scenario #7 ("A persisted closure is never rewritten") is asserted only through the preview path; the spec's WHEN also covers "a later arqueo". `CreateClosureAsync` does not rewrite details, but no test runs a closure after a persisted one.
- **SUGGESTION-02** — `ClosureWindowResolverTests` duplicates the Venezuela UTC conversion helpers instead of asserting against `TimeZoneHelper.GetUtcRange`; low risk, but a shared helper would prevent drift if the day-boundary rule changes.

**Registered legacy debt (PRE-EXISTING — out of scope, do NOT attribute to this change)**
All 40 items in `docs/deuda-legacy-gga-2026-09-16.md` were cross-checked and remain valid; none is introduced or worsened by slices 1–3. The most load-bearing for this change's files: `CloseShift` trusting `request.Currency` as a second currency source of truth (item 26, deferred to H-10/C3), Web `RegisterClosePage.jsx:61` client-side classifier fallback (item 39), EF-entity leakage in `CashDrawerController`/`DailyClosureService` (items 3, 8, 15, 19), the repo-wide `CancellationToken` sweep (items 4, 9, 16, 21, 27), and `ProblemDetails` adoption (items 2, 18, 25). Group A–I of that document is the sane sanitation backlog; nothing there blocks this change.

### Verdict

**FAIL**
Build, tests (1136/1136) and all three coverage gates pass, and 20/20 tasks are complete with 14/14 requirements implemented and design AD-1..AD-8 faithfully realized — but one mandatory spec scenario (`payment-method-currency-classification` REQ-3 "Report matches the stored receipt") has no passing covering test, and one atomicity scenario has only vacuum-green evidence without a real Postgres. Archive is not admissible until CRITICAL-01 is closed; WARNING-01 should be closed by a Postgres-backed run.

---

### Method & Evidence

| Item | Value |
|------|-------|
| Worktree | `V0.1` @ `93d3739` (clean, `git status --porcelain` empty) |
| Slice commits | `a94c536` (H-01), `a34584c` + `b016c5d` (H-02), `711710b` (classifier) |
| Build output | `sha256:7a22bd1293d01c2a3d91fe223bc796e6e5dc47d96941db54490a3a7da09a4386` |
| Test output | `sha256:269e3aaf91ed9a5a625b47e3b78b0701faf9e230cc0b897b7c79a0be04a0f582` |
| Coverage run output | `sha256:54108ec63f191c94dc6ea0b8e5f475bcb79a552cc1ceff66b83643090f2c633e` |
| RDD/independent review | Clone RDD disabled by an OpenCode runtime limitation (`docs/reporte.txt` ANEXOS 8.137/8.139 G1); slice-level independent verification was executed via the fallback path and is recorded in `apply-progress.md`. Review state is informational and was not treated as a verification prerequisite. |
| Code touched | None. This verification is read-only apart from `verify-report.md`. |
