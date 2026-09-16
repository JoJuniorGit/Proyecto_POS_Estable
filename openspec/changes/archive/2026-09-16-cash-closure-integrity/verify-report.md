```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:1a3ce6421bf17b909ba7bb83abfec6e73df872b02c5b9e961295fb22666d4d8b
verdict: pass
blockers: 0
critical_findings: 0
requirements: 14/14
scenarios: 18/18
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
test_exit_code: 0
test_output_hash: sha256:f65e68c1fc321b88386be484135f461e9eb88d3cbe0b5c175bcfbc2da8d1283e
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:a1ae51175af1a1cc42f231b57dca3f6d8fe9402a4a0123b474397501dce0c958
```

## Verification Report

**Change**: cash-closure-integrity
**Version**: N/A (delta specs, no version headers)
**Mode**: Standard (Strict TDD inactive: `openspec/config.yaml` → `strict_tdd: false`, `testing.strict_tdd_mode: disabled`)
**Revision**: 2 (re-verification). Supersedes revision 1 (`fail`, CRITICAL-01). The fix added one test; no production file changed since revision 1.

### Completeness

| Metric | Value |
|--------|-------|
| Tasks total | 20 |
| Tasks complete | 20 |
| Tasks incomplete | 0 |

All 20 tasks in `tasks.md` are checked.

### Build & Tests Execution

**Build**: ✅ Passed (0 warnings / 0 errors)

```text
$ dotnet build CommandCenter.slnx -c Release
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

Supplementary forced full rebuild (proves the whole tree compiles from scratch, not an incremental no-op): `dotnet build CommandCenter.slnx -c Release --no-incremental` → exit 0, 0 warnings / 0 errors, output `sha256:5e522c66aed96df6f1f40e1ddf6412e214b879d50a933a0c49b7e22d73ac67e9`.

**Tests**: ✅ 1137 passed / ❌ 0 failed / ⚠️ 0 skipped (Δ +1 vs revision 1's 1136 — exactly the new CRITICAL-01 test; no other test was added, renamed or removed)

```text
$ dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
Correctas! - Con error: 0, Superado: 1137, Omitido: 0, Total: 1137, Duración: 9 s
```

**Coverage** (gate: `python scripts/check-coverage.py <coverage.cobertura.xml>`, exit 0)

```text
Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8364 min=0.7000 [OK]
  Sales.Module       rate=0.8879 min=0.8000 [OK]
  Inventory.Module   rate=0.8251 min=0.7200 [OK]
```

Report: `CommandCenter.Tests/TestResults/80dc48af-710a-4c2c-b70c-d1bfc109e489/coverage.cobertura.xml`. All three gates pass and are unchanged from revision 1; `Sales.Module` (0.8879) is the gated layer for this change (design gate: Sales.Module ≥ 0.80).

**Environment note**: `TEST_POSTGRES_CONNECTION` and `GITHUB_ACTIONS` are both unset in this environment, and the local PostgreSQL 18 instance rejects the credentials available in the repo (`appsettings.Development.json`, `ci.yml`) — authentication could not be established without guessing secrets, which was not attempted. The Postgres-gated tests therefore early-return. See WARNING-01.

### Spec Compliance Matrix

Actual counts extracted from the spec headings: **14 requirements / 18 scenarios**
(`cash-closure-arqueo-window` 5/8, `cash-advance-payout-integrity` 6/7, `payment-method-currency-classification` 3/3). Counts are unchanged from revision 1; every test named below was re-confirmed to exist in the current tree.

| # | Requirement | Scenario | Test | Result |
|---|-------------|----------|------|--------|
| 1 | Session-Anchored Window Precedence | Open session anchors the window | `ClosureWindowResolverTests.Resolve_WhenSessionOpen_SessionOpensAtAnchorsWindow:28`, `..._WhenNightShiftCrossingMidnight_SalesFromPreviousDayAreIncluded:102`, `DailyClosureServiceWindowTests.GetExpectedTotals_SalesBeforeSessionAreExcluded:38` | ✅ COMPLIANT |
| 2 | Session-Anchored Window Precedence | No session falls back to last closure, then start of day | `ClosureWindowResolverTests.Resolve_WhenNoSession_FallsBackToLastClosure:41`, `..._WhenNoSessionNoClosure_FallsBackToStartOfDay:53` | ✅ COMPLIANT |
| 3 | Half-Open Interval Boundaries | Row exactly at the window start | `DailyClosureServiceWindowTests.GetExpectedTotals_IncludesSaleExactlyAtSessionOpenedAt:102`, `..._CashTransactionExactlyAtStart_Included:249` | ✅ COMPLIANT |
| 4 | Half-Open Interval Boundaries | Row exactly at the right bound | `DailyClosureServiceWindowTests.GetExpectedTotals_ExcludesSaleExactlyAtEndExclusiveUtc:148`, `..._CashTransactionExactlyAtEndExclusive_Excluded:289` | ✅ COMPLIANT |
| 5 | Backdated Closure Fallback | Backdated closure behind the current session | `ClosureWindowResolverTests.Resolve_WhenBackdatedClosureBehindSession_FallsBackToLastClosure:77`, `..._WhenBackdatedClosureNoLastClosure_FallsBackToStartOfDay:90` | ✅ COMPLIANT |
| 6 | Pure Resolver and Immutable Snapshots | Resolver is unit-testable without a database | `ClosureWindowResolverTests` (7 tests, pure static `ClosureWindowResolver.Resolve`, no `DbContext`) | ✅ COMPLIANT |
| 7 | Pure Resolver and Immutable Snapshots | A persisted closure is never rewritten | `DailyClosureServiceWindowTests.PersistedClosure_IsNeverRewrittenByLaterArqueo:196` | ✅ COMPLIANT (preview path only; see SUGGESTION-01) |
| 8 | Preview Requires an Explicit Date | Preview rejects a missing date | `DailyClosureControllerTests.GetExpectedTotals_WhenDefaultDate_Returns400ProblemDetails:46`, `..._WhenValidDate_ReturnsOk:58` | ✅ COMPLIANT |
| 9 | Atomic Payout and Accounting Sale | Sale creation succeeds | `CashAdvanceCoordinatorTests.ProcessAsync_ConfiguredCommissionApplied:52`, `ProcessAsync_PhysicalDrawerBalanceDeductsOnlyRequested:179`, `CashAdvanceTests.ProcessCashAdvance_Transfer_UsesConfiguredCommissionFromSystemSettings:273` | ✅ COMPLIANT |
| 10 | Atomic Payout and Accounting Sale | Accounting sale fails | `Integration/CashAdvanceEnvelopeTests.Envelope_SaleFailure_NoDrawerTransactionPersists:100` — covering test exists and passes, but its body is Postgres-gated and therefore **vacuous in this environment** | ✅ COMPLIANT (suite-covered; unexecuted here → WARNING-01) |
| 11 | Commission Resolved from System Settings | Configured commission is applied | `CashAdvanceCoordinatorTests.ProcessAsync_ConfiguredCommissionApplied:52`, `CashAdvanceTests.ProcessCashAdvance_Transfer_UsesConfiguredCommissionFromSystemSettings:273` (5.5% ≠ default 7%) | ✅ COMPLIANT |
| 12 | Fail-Closed on Unresolvable Commission | Missing commission rejects the advance | `CashAdvanceCoordinatorTests.ProcessAsync_MissingCommissionRejectsWithoutPayoutOrSale:79`, `CashAdvanceTests.ProcessCashAdvance_Cash_MissingCommissionRejectsWithoutPayoutOrSale` | ✅ COMPLIANT |
| 13 | Ordering and Rate Anchoring Preserved | Drawer uses the sale's anchored rate | `CashAdvanceCoordinatorTests.ProcessAsync_AppliedRateAnchorsDrawerMovements:108` (mock returns 60.0 vs input 50.0; asserts both drawer txs use 60.0) | ✅ COMPLIANT |
| 14 | Financial Snapshots Preserved | Snapshots are written as before | `CashAdvanceCoordinatorTests.ProcessAsync_SnapshotsPreservedWithDefaultRoundingAdjustment:150` (`TotalUSD=21.4`, `TotalBsS=1070`, `FinalPaidAmountBsS=1070`, `RoundingAdjustment=0`) | ✅ COMPLIANT |
| 15 | No Service Locator | Drawer has no locator | Source: `CashDrawerService.cs:17` (`CashDrawerService(SalesDbContext)`), zero `IServiceProvider`/`ISalesService` references; every coordinator test constructs the graph via constructor injection | ✅ COMPLIANT (static + compile-enforced) |
| 16 | Single Classifier Source of Truth | Report uses the shared classifier | `PaymentMethodCurrencyClassificationTests.GetReportById_Dolares_ClasificaBsSPorResolver:42` | ✅ COMPLIANT |
| 17 | Conversions via PricingCalculator.ToUSD | Bs.S total is converted with the shared helper | `PaymentMethodCurrencyClassificationTests.ToUSD_RoundsAwayFromZero_DiffersFromRawDivision:225` (`ToUSD(100,6)=16.67 ≠ 16.666…`, `ToUSD(1,8)=0.13`); report assert `3.00m` at `:114` | ✅ COMPLIANT |
| 18 | Report and Receipt Agreement | Report matches the stored receipt | `PaymentMethodCurrencyClassificationTests.GetReportById_And_ClosureReceipt_ClasificanIgual_MismoCierre:118` (**NEW**) | ✅ COMPLIANT (CRITICAL-01 closed; residual assertion weakness → WARNING-03) |

**Compliance summary**: 18/18 scenarios compliant, 0 critical findings, 2 open warnings (WARNING-01 environment, WARNING-03 assertion strength).

#### CRITICAL-01 closure analysis — does the new test actually exercise both paths and discriminate?

**It executes both real code paths on the same closure** (`PaymentMethodCurrencyClassificationTests.cs`):

| Step | Evidence |
|------|----------|
| One persisted closure with two details, `"Dolares"` and `"Divisas (USD)"` | `:123-153` |
| Report path: real controller, real `SalesDbContext` | `:158-178` → `ShiftsController.GetReportById(42)` |
| Production line exercised by the report path | `Backend.API/Controllers/ShiftsController.cs:283` (`PaymentMethodCurrencyResolver.Resolve`) |
| Receipt path: real stored-receipt generator | `:182` → `DailyClosureService.GenerateReceiptContent(closure, isBlind: false)` |
| Production line exercised by the receipt path | `Sales.Module/Services/DailyClosureService.cs:249` (non-blind) — same resolver call as `:225` (blind) and `ClosurePdfGenerator.cs:72` |
| Ground truth | `:188` `PaymentMethodCurrencyResolver.Resolve(detail.PaymentMethodName)` |
| Report-side assertion | `:192` `Assert.Equal(expectedCurrency, reportByMethod[name].Currency)` |
| Receipt-side assertion | `:194-206` receipt must contain a line carrying both the method name and the resolved currency |

**Discriminating power — proven against the actual historical defect.** The pre-fix report path classified with a local heuristic: `Backend.API/Controllers/ShiftsController.cs@b016c5d:281` → `...ToLower().Contains("usd") || ...Contains("dolar") || ...Contains("$")`. For `"Dolares"` that yields `USD`, while the resolver yields `Bs.S`. The new test computes `expectedCurrency = Resolve("Dolares") = "Bs.S"` at `:188` and asserts equality with the report value at `:192`, so **on the pre-fix code the test fails**; on the current code (`ShiftsController.cs:283-284`) it passes. The test is therefore a genuine regression guard for CRITICAL-01, not a tautology. The `"Dolares"` row supplies the same discrimination on the receipt side (`:199`), since that method name does not contain the literal `USD` — a receipt path regressing to the old heuristic would print `USD` and fail the assertion.

**Residual weakness (not a blocker, registered separately)**: for any method the resolver classifies as `USD`, the name necessarily contains the literal `"USD"` (`PaymentMethodCurrencyResolver.cs:24-27`), so the receipt-side `line.Contains(expectedCurrency)` check at `:199` is satisfied by the method-name column alone. See WARNING-03.

### Correctness (Static Evidence)

| Requirement | Status | Notes |
|------------|--------|-------|
| Session-Anchored Window Precedence | ✅ Implemented | `ClosureWindowResolver.cs:20` `activeSessionOpenedAtUtc ?? lastClosureDateUtc ?? startOfDayUtc`; `DailyClosureService.cs:37` feeds the open session |
| Half-Open Interval Boundaries | ✅ Implemented | `DailyClosureService.cs:46-47` and `:57-58` both use `>= startUtc && < endUtc`; `EndExclusiveUtc = startOfDayUtc.AddDays(1)` (`ClosureWindowResolver.cs:18`) |
| Backdated Closure Fallback | ✅ Implemented | `ClosureWindowResolver.cs:22-28` re-clamps only when `anchor >= endExclusiveUtc`, bounded to the business day |
| Pure Resolver and Immutable Snapshots | ✅ Implemented | `ClosureWindowResolver.cs:6-8` static class with a tuple return and no `DbContext`; `CreateClosureAsync` never rewrites persisted `ClosureDetail` rows |
| Preview Requires an Explicit Date | ✅ Implemented | `DailyClosureController.cs:56-59` returns `Problem(...)` when `dateUtc == default`; no fallback to current day |
| Atomic Payout and Accounting Sale | ✅ Implemented | `CashAdvanceCoordinator.cs:67` execution-strategy envelope; `:99` transaction; `:151` commit; `:167-174` rollback-and-rethrow (no log-and-continue) |
| Commission Resolved from System Settings | ✅ Implemented | `CashAdvanceCoordinator.cs:64` → `ResolveCommissionPercentageAsync` (`:178`); reads `SettingKeys.CashAdvance*CommissionPct` |
| Fail-Closed on Unresolvable Commission | ✅ Implemented | `CashAdvanceCoordinator.cs:190-191` logs and throws `InvalidOperationException` before any sale/drawer write (guarded earlier at `:67`-time, i.e. before the transaction at `:99`) |
| Ordering and Rate Anchoring Preserved | ✅ Implemented | `CashAdvanceCoordinator.cs:107` seeds `anchoredRate`, `:120-122` overrides it from `createdSale.AppliedRate`; drawer movements at `:130-143` use `anchoredRate` |
| Financial Snapshots Preserved | ✅ Implemented | Snapshots produced by `ISalesService.CreateCashAdvanceSaleAsync` and returned unchanged; `RoundingAdjustment` never set |
| No Service Locator | ✅ Implemented | `CashDrawerService.cs:15-20` single `SalesDbContext` dependency; `ICashDrawerService.cs` no longer declares `ProcessCashAdvanceAsync` |
| Single Classifier Source of Truth | ✅ Implemented | `ShiftsController.cs:283` `PaymentMethodCurrencyResolver.Resolve(d.PaymentMethodName)`; no local heuristic remains |
| Conversions via PricingCalculator.ToUSD | ✅ Implemented | `ShiftsController.cs:285-286` `PricingCalculator.ToUSD(...)`; `Desktop.Client.Core/Helpers/PricingHelper.cs:20` is a pure delegation; `Backend.API.csproj` has no `Desktop.*` reference |
| Report and Receipt Agreement | ✅ Implemented + verified at runtime | Same resolver on all four call sites: `ShiftsController.cs:283`, `DailyClosureService.cs:225` (blind receipt), `:249` (audit receipt), `ClosurePdfGenerator.cs:72` (PDF). Now asserted by the new test. |

### Coherence (Design)

| Decision | Followed? | Notes |
|----------|-----------|-------|
| AD-1 Static `ClosureWindowResolver` returning `(StartUtc, EndExclusiveUtc)` | ✅ Yes | `ClosureWindowResolver.cs:6-8` |
| AD-2 Read `CashDrawerSessions` off the scoped `SalesDbContext`; ctor unchanged | ✅ Yes | `DailyClosureService.cs:19` single-param ctor; session read `:31-35`; all 12 call sites un-churned |
| AD-3 Backdated guard = legacy clamped rule bounded to the day | ✅ Yes | `ClosureWindowResolver.cs:22-28`; guarantees `StartUtc < EndExclusiveUtc` |
| AD-4 Envelope owned by the coordinator (4 ctor deps) | ✅ Yes | `CashAdvanceCoordinator.cs:21-31` |
| AD-5 `AppLogger.LogWarn` + throw, no `ILogger<>` | ✅ Yes | `CashAdvanceCoordinator.cs:190` |
| AD-6 `InvalidOperationException` fail-closed | ✅ Yes | `CashAdvanceCoordinator.cs:191` (mapped to 409 by the existing middleware) |
| AD-7 `PricingCalculator.ToUSD`, not `PricingHelper` | ✅ Yes | `ShiftsController.cs:285-286`; `PricingHelper.cs:20` delegates |
| AD-8 Validate preview `dateUtc` | ✅ Yes | `DailyClosureController.cs:56-63` |

Additional design-stated invariants verified: default isolation (not Serializable) — `CashAdvanceCoordinator.cs:99` `BeginTransactionAsync()` with no isolation level; InMemory transaction skip preserved for unit tests.

### Rules Compliance Audit

- **Financial integrity / decimal-only money** ✅ — no `float`/`double` in any changed path; commission and rounding use `MidpointRounding.AwayFromZero`.
- **History immutability** ✅ — no persisted closure/sale snapshot is recomputed; `ClosureWindowResolver` is pure and writes nothing.
- **No `async void`** ✅ — all changed async members return `Task`/`Task<T>`; the new test is `async Task`.
- **DTO/data boundary** ✅ for the new code — the coordinator returns `CashAdvanceResultDto`; pre-existing EF-entity leakage in `CashDrawerController`/`DailyClosureService` is registered legacy debt.
- **Zero-Trust / RBAC** ✅ — no role checks were weakened.
- **Layering** ✅ — `Backend.API` does not reference `Desktop.*`; no cyclic module dependency introduced.
- **Naming/Async suffix** ✅ for changed members.
- **CancellationToken** ✅ for new code — the coordinator accepts and propagates `CancellationToken` (`:64`, `:99`, `:151`, `:171`, `:180`).
- **Size limits** ✅ — `CashDrawerService.cs` 449 lines (< 500); the new test file is 235 lines.
- **0 warnings / 0 errors** ✅ — confirmed twice (incremental and forced full rebuild).

### Issues Found

**Change-attributable**

- **CRITICAL-01 — ✅ CLOSED.** `payment-method-currency-classification` REQ-3 "Report matches the stored receipt" is no longer untested. Evidence: `PaymentMethodCurrencyClassificationTests.GetReportById_And_ClosureReceipt_ClasificanIgual_MismoCierre:118` (+95 lines, uncommitted working-tree change), executing `ShiftsController.GetReportById` (`ShiftsController.cs:283`) and `DailyClosureService.GenerateReceiptContent` (`DailyClosureService.cs:249`) over the same persisted closure, with per-method label equality assertions. Discriminating power proven against the pre-fix blob (`ShiftsController.cs@b016c5d:281`): the test fails on the old heuristic and passes on the new resolver call. Suite moved 1136 → 1137 with 0 failures.

**WARNING**

- **WARNING-01 (OPEN — residual, environment-derived)** — Scenario #10 ("Accounting sale fails") rests on `CashAdvanceEnvelopeTests.Envelope_SaleFailure_NoDrawerTransactionPersists`, whose body early-returns when `TEST_POSTGRES_CONNECTION` is unset (`CashAdvanceEnvelopeTests.cs:102-103`; same pattern at `:72-73` and `:143-144`). xUnit counts those three tests as *passed*, so the green 1137/1137 contains 3 vacuous passes. This is **not specific to this change**: it is the documented repo-wide "silent-pass" pattern of the `PostgresRealCollection` classes (`WebApplicationFactorySmokeTests.cs` comments it explicitly; `HistoryImmutabilityTests.cs:30-36` and `PostgresRealIntegrationTests.cs:20-33` repeat it), deliberately enforced in CI by throwing when `GITHUB_ACTIONS` is set without a database. This run could not produce real evidence: no `TEST_POSTGRES_CONNECTION`, and the live local PostgreSQL 18 instance rejected every credential available in the repo. Recommendation (pre-archive if the operator has a reachable database): run `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` with `TEST_POSTGRES_CONNECTION` pointing at an isolated database (the bootstrap only issues `CREATE TABLE` when the marker tables are missing — `TestSchemaBootstrap.cs:88-95` — so an isolated DB is safe). This warning does not block archive: the covering test exists, is part of the suite, and CI enforces its real execution.
- **WARNING-02 (REGISTERED — code divergence remains, pre-existing)** — `Desktop.Client.Core/ViewModels/CashAdvanceRegisterViewModel.cs:58` hardcodes `IsTransfer ? 7.0m : 10.0m`, diverging from the server, which resolves the commission from `SystemSettings` (verified: configured 5.5%). The advance preview shown to the operator can disagree with the amount actually charged. The server remains authoritative and fail-closed, so this is not a payout-integrity break. **Pre-existing** (initial commit `7b382ee`, untouched by this change) and **now registered** as item 41 of `docs/deuda-legacy-gga-2026-09-16.md:73`, with the fix direction recorded. Registration was not present at revision 1; the reformulation of revision 1's recommendation is therefore complete, while the underlying client-side divergence stays open as legacy debt.
- **WARNING-03 (NEW — assertion strength, narrow)** — The receipt half of the new test is tautological for every USD-classified method: `PaymentMethodCurrencyResolver.Resolve` returns `USD` only when the name contains the literal `"USD"` (`PaymentMethodCurrencyResolver.cs:24-27`), so `line.Contains(expectedCurrency)` at `PaymentMethodCurrencyClassificationTests.cs:199` is satisfied by the method-name column alone, whatever the `MONEDA` column prints. A receipt-path regression affecting only USD-named methods (`"Divisas (USD)"` rendered as `Bs.S`) would escape detection. The `"Dolares"` row *is* discriminating and covers the historical defect, and the report-side assertion at `:192` is exact for both rows, so the spec scenario is met. Recommendation: make the receipt assertion positional (assert the rendered `"{name} {currency}"` cell pair, or parse the fixed-width columns) instead of relying on substring containment.

**SUGGESTION**

- **SUGGESTION-01** — Scenario #7 ("A persisted closure is never rewritten") is asserted only through the preview path; the spec's WHEN also covers "a later arqueo". `CreateClosureAsync` does not rewrite details, but no test runs a closure after a persisted one.
- **SUGGESTION-02** — `ClosureWindowResolverTests` duplicates the Venezuela UTC conversion helpers instead of asserting against `TimeZoneHelper.GetUtcRange`; low risk, but a shared helper would prevent drift if the day-boundary rule changes.
- **SUGGESTION-03** — The new test asserts label agreement only; the scenario's wording also covers amount agreement. A literal report↔receipt amount equality is impossible by design (the report states amounts in the method's currency, the receipt always prints Bs.S), so the intent is covered by classification equality plus the report-side amount assertions at `PaymentMethodCurrencyClassificationTests.cs:110,114`. An explicit comment or an assertion tying the receipt's Bs.S figure to `PricingCalculator.ToUSD` for the USD row would make that intent auditable.
- **SUGGESTION-04** — The new test adds `using System;` (`:14`) while every `System` type is fully qualified (`:128`, `:28`); the directive is unused. Harmless (0 warnings), but the repo forbids introducing new technical debt.

**Registered legacy debt (PRE-EXISTING — out of scope, do NOT attribute to this change)**
All 41 items in `docs/deuda-legacy-gga-2026-09-16.md` were cross-checked; none is introduced or worsened by slices 1–3. The most load-bearing for this change's files: `CloseShift` trusting `request.Currency` as a second currency source of truth (item 26, deferred to H-10/C3), Web `RegisterClosePage.jsx:61` client-side classifier fallback (item 39), the client-side commission hardcode now registered as item 41 (see WARNING-02), EF-entity leakage in `CashDrawerController`/`DailyClosureService` (items 3, 8, 15, 19), the repo-wide `CancellationToken` sweep (items 4, 9, 16, 21, 27), and `ProblemDetails` adoption (items 2, 18, 25). Group A–I of that document is the sane sanitation backlog; nothing there blocks this change.

### Verdict

**PASS**

Build (0 warnings / 0 errors, incremental and forced full rebuild), tests (1137/1137, 0 skipped), and all three coverage gates pass; 20/20 tasks are complete, 14/14 requirements are implemented, and design AD-1..AD-8 are faithfully realized. The single revision-1 blocker, CRITICAL-01, is closed with a discriminating runtime test that exercises both the report path (`ShiftsController.cs:283`) and the stored-receipt path (`DailyClosureService.cs:249`) over the same closure and provably fails on the pre-fix heuristic. No change-attributable critical finding remains. Archive is admissible; WARNING-01 (Postgres-gated scenario #10 unexecuted in this environment) should be closed by a Postgres-backed suite run when a database is reachable, and WARNING-03 records a narrow assertion-strength improvement for a future pass.

---

### Method & Evidence

| Item | Value |
|------|-------|
| Worktree | `V0.1` @ `5565d12` (revision 1's report commit), with a **dirty working tree**: `CommandCenter.Tests/Unit/PaymentMethodCurrencyClassificationTests.cs` (+95) and `openspec/changes/cash-closure-integrity/apply-progress.md` (+4) uncommitted. The CRITICAL-01 fix is **not yet committed**. |
| Slice commits | `a94c536` (H-01), `a34584c` + `b016c5d` (H-02), `711710b` (classifier), `93d3739` (legacy-debt registration), `5565d12` (revision-1 report) |
| Pre-fix blob used for the discrimination proof | `Backend.API/Controllers/ShiftsController.cs@b016c5d:281` |
| Build output | `sha256:a1ae51175af1a1cc42f231b57dca3f6d8fe9402a4a0123b474397501dce0c958` (canonical command) · forced rebuild `sha256:5e522c66aed96df6f1f40e1ddf6412e214b879d50a933a0c49b7e22d73ac67e9` |
| Test output | `sha256:f65e68c1fc321b88386be484135f461e9eb88d3cbe0b5c175bcfbc2da8d1283e` |
| Coverage run output | `sha256:a86decb360d9a7bad68cc01229ae5f0a3eea39c6caa84c5cc586c5882ab1bd4e` |
| `evidence_revision` derivation | `sha256` over the LF-joined envelope fields, in order: `verdict`, `test_command`, `test_exit_code`, `test_output_hash`, `build_command`, `build_exit_code`, `build_output_hash` (no trailing newline). Reproducible from the yaml block above. |
| Admission check | `gentle-ai sdd-verify-validate --input <report> --requirements 14 --scenarios 18` → `valid: true`, `verdict: pass`, before persisting these exact bytes. |
| RDD/independent review | Clone RDD disabled by an OpenCode runtime limitation (`docs/reporte.txt` ANEXOS 8.137/8.139 G1); slice-level independent verification was executed via the fallback path and is recorded in `apply-progress.md`. Review state is informational and was not treated as a verification prerequisite. |
| Code touched | None. This verification is read-only apart from `verify-report.md`. |
