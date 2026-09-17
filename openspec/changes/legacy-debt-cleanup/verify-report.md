```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:7d6e5d256f77029562f28d006cdf96ac48c562555f0d6693db73fc3390b02668
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 3/3
scenarios: 8/8
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
test_exit_code: 0
test_output_hash: sha256:fb567ce5693818bffeccadc10456755d3621b7ff0e662058131ae5db8d39a902
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:8436cc96bcd180532cf20de12832475d9fd80e7f1d66763382956201efe865c0
```

## Verification Report

**Change**: legacy-debt-cleanup
**Version**: N/A (delta specs, no version headers)
**Mode**: Standard (Strict TDD inactive: `openspec/config.yaml` -> `strict_tdd: false`, `testing.strict_tdd_mode: disabled`)
**Scope of this report**: **Slices S1 and S2** — S1 (zero-trust close, items 26/39) and S2 (error contract + dead fields, items 2/18/25/12/35). S3-S5c are not implemented; the change-level verdict remains pending. The machine-readable envelope above is deliberately left as the admitted **S1** envelope: its counts (`requirements: 3/3`, `scenarios: 8/8`) and `evidence_revision` are scoped to the S1 delta spec `payment-method-currency-classification` (3 requirements / 8 scenarios by document order), while the S2 verdict and its own fresh evidence live in the "Slice S2" section below.
**Verified revision**: `c6c767f` (remediation commit; `HEAD`). Working tree carries one uncommitted documentation-only change: a 14-line `apply-progress.md` append (GGA hook exception note). No production or test file is modified against `c6c767f`.
**Prior verdict**: `fail` (commit `00adc45`), 1 blocker / 1 critical finding. This revision supersedes it for S1.
**evidence_revision** is the SHA-256 of the colon-joined per-file SHA-256 digests of the thirteen S1 production/test files listed under "S1 Changed Files".
**Hash definition**: `build_output_hash` / `test_output_hash` are the SHA-256 of the byte-exact combined stdout+stderr captured for the command execution reported above (UTF-8, LF-joined, `Set-Content -NoNewline`), computed with `Get-FileHash -Algorithm SHA256`.

### Completeness

| Metric | Value |
|--------|-------|
| Slice S1 tasks total (`tasks.md` Phase 1) | 10 |
| Slice S1 tasks complete | 10 |
| Slice S1 tasks incomplete | 0 |
| Change tasks complete (all phases) | 10 / 66 |
| Change phases implemented | 1 of 8 (S1) |

All ten Phase 1 tasks (1.1-1.10) are checked in `tasks.md`. Phases 2-5c are unchecked, so a change-level (`pass`) verification is not yet admissible; this report verifies S1 in isolation and leaves the change-level verdict pending.

### Re-executed Evidence (verbatim)

All commands claimed in `apply-progress.md` (remediation section) were re-executed independently. Results are reported verbatim; one divergence from the claim is recorded under RESIDUAL-01.

**1. Build** - `dotnet build CommandCenter.slnx -c Release` - exit `0` - matches claim

```text
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

**2. Backend tests** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` - exit `0` - matches claim on retry

```text
Correctas! - Con error:     0, Superado:  1149, Omitido:     0, Total:  1149, Duración:     7 s - CommandCenter.Tests.dll (net10.0)
```

Stability note: of five full-suite executions performed for this verification, four reported `1149/1149` and one (`--no-build`) reported `1147/1149` with two failures in `CommandCenter.Tests.CheckoutUxTests` (`UpdateCustomer_PreservesExistingPaymentsAndRecalculatesCustody`, `CanFinalize_OverrideSale_PartialPayment_ReturnsTrue_WithRegistrarAbonoLabel`). The same two tests pass 9/9 when run in isolation, and neither file is touched by S1 or by `c6c767f`. See RESIDUAL-01.

**3. Frontend tests** - `npm test` (Web.Frontend) - exit `0` - matches claim

```text
ℹ tests 271
ℹ suites 58
ℹ pass 271
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
```

**4. Frontend lint** - `npm run lint` (Web.Frontend, oxlint) - exit `0`, no findings - matches claim

```text
> web-frontend@0.0.0 lint
> oxlint
```

**5. S1 focused filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~CloseShift"` - exit `0` - matches claim

```text
Correctas! - Con error:     0, Superado:    12, Omitido:     0, Total:    12, Duración:     2 s - CommandCenter.Tests.dll (net10.0)
```

**6. S1 web test alone** - `node --import ./test/esbuild-jsx-loader.mjs --test "src/pages/RegisterClosePage.currency-classification.test.js"` - exit `0` - matches claim

```text
✔ does not use name/substring heuristic for currency classification (2.8979ms)
✔ getMethodCurrency reads only from server-provided method.currency (0.8423ms)
✔ payload omits the currency key from declaredAmounts (0.4713ms)
ℹ tests 3
ℹ pass 3
ℹ fail 0
```

**7. Coverage gate** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` then `python scripts/check-coverage.py CommandCenter.Tests/TestResults/7b6b3903-5965-4a33-b8aa-70f45791940a/coverage.cobertura.xml` - exit `0` - gate passes; measured rates differ from the prior snapshot (see RESIDUAL-02)

```text
Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8096 min=0.7000 gap_a_70%=0.0000 [OK]
  Sales.Module       rate=0.8525 min=0.8000 gap_a_70%=0.0000 [OK]
  Inventory.Module   rate=0.7937 min=0.7200 gap_a_70%=0.0000 [OK]
```

All three thresholds in the `tasks.md` Verification section (Core >= 0.70, Sales.Module >= 0.80, Inventory.Module >= 0.72) pass. `Sales.Module` (0.8525), the layer gated for this change, is above its 0.80 baseline.

**Environment note**: `TEST_POSTGRES_CONNECTION` is unset, so the Postgres-gated classes continue to early-return as vacuous passes; they do not cover any S1 path.

### S1 Finding-to-Closure Matrix

Every finding registered against the prior S1 verdict is re-checked below with runtime evidence from `c6c767f`.

| Finding | Prior severity | Closure | Fresh evidence |
|---------|----------------|---------|----------------|
| CRITICAL-01 - REQ-PMC-04 "Unknown declared payment method" 400 is not RFC 7807 | Critical / blocker | **CLOSED** (residual RESIDUAL-03) | `ShiftsController.cs:157-160` now returns `Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest)`. `ControllerBase.Problem(...)` yields an `ObjectResult` whose `Value` **is** a `ProblemDetails` (not a nested `ObjectResult`), so `ObjectResultExecutor.InferContentTypes` promotes the media type to `application/problem+json`. Covering tests: `CloseShiftResolverClassificationTests.CloseShift_UnknownPaymentMethodId_ReturnsProblemDetails` (line 182) asserts `ObjectResult`, `StatusCode == 400`, `ProblemDetails` body, `Status == 400` and `Detail` contains `999` (lines 201-205); `SecurityHardeningSprint2Tests.ShiftsController_UnknownDeclaredPaymentMethodId_ReturnsBadRequestWithoutCreatingClosure` (line 230) asserts `ObjectResult` + `ProblemDetails` + `Status == 400` and `RolloverSessionAfterClosureAsync` `Times.Never` (lines 283-288). Payload **is** asserted; content type **is not** asserted by any test. |
| WARNING-01 - close-path classification and diverging-name scenario have no covering test | Warning / high | **CLOSED** | Five real-path tests now execute the production `DailyClosureService` against an InMemory `SalesDbContext` with no service-behavior mock: `CreateClosureFromCommandAsync_RealService_UsdMethodClassifiedAsUsd` (line 330), `..._BsSMethodClassifiedAsBsS` (line 372), `..._DivergingName_UsesResolverClassification` (line 412), `..._UnknownMethodId_ThrowsArgumentException` (line 452), `..._ReqPmc04_ClientUsdForLocalMethod_UsesResolverClassification` (line 483). Each service is constructed as `new DailyClosureService(salesCtx)` (lines 344, 386, 426, 465, 497) and asserts the persisted `ClosureDetail.ActualAmountBsS` read back from the context (lines 363-368, 404-408, 444-448, 515-520). |
| WARNING-02 - REQ-PMC-04 "client declares USD for a local-currency method" untested | Warning | **CLOSED** (residual RESIDUAL-04) | `..._ReqPmc04_ClientUsdForLocalMethod_UsesResolverClassification` (line 483) drives a Bs.S-classified method (`"Efectivo Bs.S"`, seeded at line 486) through the real service with `Amount = 5000` and `ExchangeRate = 50`, then asserts the resolver classification governs (`LocalCurrency`, line 512), the declared amount is untouched (`5000`, line 513) and the **persisted** closure records the resolver classification (`ActualAmountBsS == 5000`, i.e. no `x rate` conversion, and `ExchangeRate == 50`, lines 519-520). |
| WARNING-03 - unauthorized exchange-rate source swap in the close path | Warning / financial | **CLOSED** | The swap is fully reverted. `DailyClosureService.ResolveEffectiveRateAsync` is deleted by `c6c767f`; the service consumes `command.ExchangeRate` only (`DailyClosureService.cs:213`). `CreateClosureCommand` gained the `ExchangeRate` parameter (`CreateClosureCommand.cs`) and `ShiftsController.CloseShift` resolves the rate with the pre-S1 call `GetTodayExchangeRateAsync()` -> `ExchangeRateResolver.ReadEffectiveTodayRateAsync` (`ShiftsController.cs:111`, `:53-56`) with the pre-S1 `exchangeRate <= 0` guard (`:112-117`). Rate semantics are therefore identical to pre-S1 (`ExchangeRateResolver`: BCV today -> last BCV history -> active-session opening rate -> 0; `ExchangeRateWriteService.cs:88-118`), the persisted `DailyClosure.ExchangeRate` snapshot is written from `command.ExchangeRate` (`DailyClosureService.cs:322`) exactly as before, and `DailyClosureController` still uses the same resolver (`DailyClosureController.cs:47-49, 140`) so the two close paths agree again. No S3 preemption: `ITodayExchangeRateProvider` (AD-6) exists only in `design.md`/`tasks.md` and in no compiled file; AD-5 (service owns transaction/persistence/rollover) is untouched and `tasks.md` 3.2/3.3/3.4/3.6/3.7 remain valid. |
| WARNING-06 - S1-added explanatory comments in production code | Warning / rule | **CLOSED** (residual RESIDUAL-05) | `git diff ef6efe4 c6c767f -- "*.cs"` adds **zero** comment lines. The remediation removed the XML `<summary>` blocks from `ShiftReportMapper.cs`, `ShiftReportDetailDto.cs`, `CreateClosureCommand.cs`, `IDailyClosureService.cs` and the inline `AD-*` rationale from `ShiftsController.cs` and `DailyClosureService.cs`. Surviving S1-authored comments are enumerated in RESIDUAL-05. Apply-progress labels for tasks 1.2, 1.3, 1.4 and 1.5 were corrected from "pre-existing" to "created/modified by S1" (`apply-progress.md` lines 8-11). |
| WARNING-04 - merged undeclared methods: hardcoded status and mixed units | Warning | **NOT FIXED - recorded as pending for S3** | Still present at `DailyClosureService.cs:301-313`: `"Balanced"` is hardcoded at line 313 while `DifferenceBsS` at line 310-312 can be non-zero (line 290 sets `actualAmount = 0` for cash), and for a USD method `DeclaredAmount` carries `exp.ExpectedAmountBsS` (Bs.S) while `SystemAmount` carries `PricingCalculator.ToUSD(...)`. `apply-progress.md` lines 129/94 (S3) and `tasks.md` 3.4 (AD-8 `MergeMissingMethods`/`RecalculateTotals` extraction) record it. Not silently fixed. |
| WARNING-05 - `apply-progress.md` misattributes created files as "pre-existing" | Warning / evidence trail | **CLOSED** | `apply-progress.md` lines 8, 10, 11 now state "created by S1"/"modified by S1" with an explicit "NOTE: prior apply-progress incorrectly listed ... as 'pre-existing'; corrected in remediation section." |
| WARNING-07 - `DailyClosureService.cs` exceeds the class-size ceiling | Warning / rule | **NOT FIXED - recorded as pending for S3** | File length is **504** lines (`Get-Content ... | .Count`), above the 300-500 ceiling in `docs/coding-guidelines-core.md`. `tasks.md` 3.4 (AD-8 extraction) and `apply-progress.md` line 130 record it. Not silently fixed. See RESIDUAL-06 for the label/number defect in that record. |

### Diverging-name test discrimination (WARNING-01/02 follow-up)

The requested justification that `CreateClosureFromCommandAsync_RealService_DivergingName_UsesResolverClassification` is non-tautological:

1. The method under test is the **production** service (`new DailyClosureService(salesCtx)`, line 426); no `Mock<IDailyClosureService>` participates, so the assertion cannot read back an injected constant.
2. The payment-method name `"Dólares"` is seeded into the InMemory `PaymentMethods` table (line 418) and reaches the classifier only through `GetExpectedTotalsByPaymentMethodAsync`, which reads `PaymentMethods` (`DailyClosureService.cs:66-71`) and projects `PaymentMethodName = method.Name` (line 99).
3. `PaymentMethodCurrencyResolver.Resolve` returns `USD` only when the name contains the literal `USD` (`PaymentMethodCurrencyResolver.cs:20-29`). `"Dólares"` does not, so the classification is `Bs.S`.
4. **Discrimination**: if a client-currency read or a naive name heuristic (`dolar`, `$`, `divisa`) influenced classification, the method would be classified `USD` and `actualAmountBsS` would become `declared.Amount × exchangeRate = 100 × 50 = 5000` (`DailyClosureService.cs:245-247`). The test asserts `Assert.Equal(100m, persistedDetail.ActualAmountBsS)` (line 448) after re-reading the row from the context (lines 444-447), so the regression turns the test red. The `UsdMethodClassifiedAsUsd` test (line 330) asserts the mirror case (`5000`, line 368), so a heuristic that classified everything as Bs.S is also caught.
5. A client-currency read cannot even compile on this path: `DeclaredPaymentAmount` exposes only `(PaymentMethodId, Amount)` (`CreateClosureCommand.cs`) - the currency member was deleted from the DTO and every read site.

### S1 Scenario Evidence Matrix

Actual counts taken from the delta spec headings: **3 requirements / 8 scenarios** (`openspec/changes/legacy-debt-cleanup/specs/payment-method-currency-classification/spec.md`).

| Requirement | Scenario | Covering test | Result |
|-------------|----------|---------------|--------|
| REQ-PMC-01 | Report uses the shared classifier | `CloseShiftResolverClassificationTests.ShiftReportMapper_ProducesConsistentLabels_WithResolverClassification` (line 259) | COMPLIANT - `GetReportById` delegates to `ShiftReportMapper.MapDetails` (`ShiftsController.cs:221`), which calls `PaymentMethodCurrencyResolver.Resolve`. Test passes. |
| REQ-PMC-01 | Close classifies every declared method via the resolver | `CreateClosureFromCommandAsync_RealService_UsdMethodClassifiedAsUsd` (line 330) + `..._BsSMethodClassifiedAsBsS` (line 372) | COMPLIANT - the real service classifies each declared method via the resolver (`DailyClosureService.cs:243`) and both currencies are covered end-to-end against a real context. |
| REQ-PMC-01 | A diverging method name does not change the close classification | `CreateClosureFromCommandAsync_RealService_DivergingName_UsesResolverClassification` (line 412) | COMPLIANT - `"Dólares"` classifies as `Bs.S` and the persisted amount is not rate-converted (line 448). |
| REQ-PMC-04 | Client declares USD for a local-currency method | `CreateClosureFromCommandAsync_RealService_ReqPmc04_ClientUsdForLocalMethod_UsesResolverClassification` (line 483) | COMPLIANT - resolver classification governs the amounts and the persisted closure (`ActualAmountBsS == 5000`, `ExchangeRate == 50`). Residual: the payload cannot literally carry `currency:"USD"` (see RESIDUAL-04). |
| REQ-PMC-04 | Currency omitted from the declaration | `CloseShift_EmptyDeclaredAmounts_CreatesCommandAndDelegatesToService` (line 212) | COMPLIANT - the declaration DTO carries no currency member and the close succeeds; test passes. |
| REQ-PMC-04 | Unknown declared payment method | `CloseShift_UnknownPaymentMethodId_ReturnsProblemDetails` (line 182) + `SecurityHardeningSprint2Tests.ShiftsController_UnknownDeclaredPaymentMethodId_ReturnsBadRequestWithoutCreatingClosure` (line 230) + `CreateClosureFromCommandAsync_RealService_UnknownMethodId_ThrowsArgumentException` (line 452) | COMPLIANT - HTTP 400 with an RFC 7807 `ProblemDetails` body (asserted), and the real service throws before `_context.DailyClosures.Add` (`DailyClosureService.cs:224-235` precedes `:194`) so nothing is persisted; rollover is asserted `Times.Never`. |
| REQ-PMC-05 | No local heuristic remains | `RegisterClosePage.currency-classification.test.js` -> 3 tests, all pass | COMPLIANT - the `usd`/`dolar`/`$`/`divisa` fallback is absent and no Web source sends a `currency` key. |
| REQ-PMC-05 | Diverging method name is labelled by the server value | `RegisterClosePage.currency-classification.test.js` -> "getMethodCurrency reads only from server-provided method.currency" | COMPLIANT - `getMethodCurrency` returns `'USD'` only when the server-supplied `method.currency === 'USD'`; the test asserts the helper never reads `method.name` and never calls `.includes(`. |

**Compliance summary**: 8/8 scenarios compliant, 0 UNTESTED, 0 FAILING. Requirement-level completeness: 3/3 (REQ-PMC-01, REQ-PMC-04 and REQ-PMC-05 all satisfied).

### S1 Changed Files

| File | Action | Role in S1 |
|------|--------|------------|
| `Backend.API/Controllers/ShiftsController.cs` | Modified | Delegates the closure to the service; no `request.Currency` read remains; RFC 7807 400 paths at lines 114-117 and 159; rate resolved via `GetTodayExchangeRateAsync()` |
| `Sales.Module/Services/DailyClosureService.cs` | Modified | `CreateClosureFromCommandAsync` (209-344) consumes `command.ExchangeRate`; `ResolveEffectiveRateAsync` deleted |
| `Sales.Module/Interfaces/CreateClosureCommand.cs` | Modified | `CreateClosureCommand` + `DeclaredPaymentAmount`; now carries `ExchangeRate` |
| `Sales.Module/Interfaces/IDailyClosureService.cs` | Modified | `CreateClosureFromCommandAsync`, `CloseShiftResult`, `ShiftReportDetailResult` |
| `Sales.Module/Services/ShiftReportMapper.cs` | Unchanged (comment only) | Single projection used by the report path (AD-4) |
| `Sales.Module/Services/ShiftReportDetailDto.cs` | Unchanged (comment only) | DTO moved out of the controller to fix the Sales.Module -> Backend.API layering inversion |
| `Web.Frontend/src/pages/RegisterClosePage.jsx` | Unchanged | Heuristic removed; payload no longer sends `currency` |
| `Web.Frontend/src/services/shiftApi.js` | Unchanged | JSDoc typedef only |
| `CommandCenter.Tests/Unit/CloseShiftResolverClassificationTests.cs` | Modified | 12 S1 tests: 5 real-path service tests + 7 controller/projection tests |
| `Web.Frontend/src/pages/RegisterClosePage.currency-classification.test.js` | Unchanged | 3 S1 web tests |
| `CommandCenter.Tests/SecurityHardeningSprint2Tests.cs` | Modified | Re-pointed to `CreateClosureFromCommandAsync`; unknown-method assertion upgraded to `ProblemDetails` |
| `CommandCenter.Tests/Unit/Phase7ClosureWithoutRateTests.cs` | Modified | Re-pointed; no-rate assertion upgraded to a 400 `ProblemDetails` |
| `CommandCenter.Tests/Unit/ResidualRemediationLote26Tests.cs` | Unchanged | Re-pointed in S1, `Currency` removed |

No S2+ production file appears in the S1 diff. The only S5a-shaped surface remains the `CancellationToken` on `GetExpectedTotalsByPaymentMethodAsync` (AD-12), consumed by the new service method.

### S1 Rules Compliance Audit

Checked against `docs/coding-guidelines-core.md` (the system invariants) and `openspec/config.yaml`.

| Invariant | Status | Evidence |
|-----------|--------|----------|
| Money in `decimal` only, never `float`/`double` | Compliant | `declared.Amount * exchangeRate`, `expectedAmountBsS`, `diffBsS` are all `decimal` (`DailyClosureService.cs:245-249`) |
| No recalculation of historical values with the current rate | Compliant | The closure persists its own `ExchangeRate` snapshot from the command (`DailyClosureService.cs:322`); persisted closures are never recomputed |
| `AsNoTracking` on read-only paths | Compliant | New reads use `AsNoTracking` (`DailyClosureService.cs:29, 34, 45, 55, 67, 281`; `ExchangeRateWriteService.cs:94, 100`) |
| No `async void` introduced | Compliant | All new members are `async Task` |
| RBAC / Zero-Trust unchanged | Compliant | `[Authorize(Roles = "Admin,Manager,Cashier")]` and the `Driver` guard on `CloseShift` are unchanged (`ShiftsController.cs:58-65`) |
| Errors surfaced as `ProblemDetails` | Compliant for the S1 path (residual RESIDUAL-07) | CRITICAL-01 is fixed: the unknown-method 400 and the no-rate 400 both emit `ProblemDetails`. `CloseShift` still returns an anonymous-object 400 for duplicate method ids (`ShiftsController.cs:75`), scoped to S2 task 2.2 |
| No unexplained comments | Compliant for the remediation (residual RESIDUAL-05) | `c6c767f` adds no comment line to any `.cs` file; one S1-authored explanatory comment survives |
| Class file <= 300-500 lines | **Violated** | WARNING-07: `DailyClosureService.cs` is 504 lines; deferred to S3 (AD-8) |
| Entities never returned to clients | Compliant for the S1 path | The close response is `ShiftReportDto`/`ShiftReportDetailResult`; no entity appears in the S1 response |

**Audit statement**: the implementation respects the financial-arithmetic, snapshot-immutability, async and error-contract invariants. Two rule deviations remain open and both are explicitly deferred by `tasks.md` to S3 (class size) and S2 (remaining anonymous error object), not regressions introduced by the remediation.

### S1 Design Coherence

| Decision | Followed? | Notes |
|----------|-----------|-------|
| AD-1 (close classification, delete `DeclaredAmountDto.Currency` and all reads, response currency is the resolver value) | Yes | `DeclaredAmountDto` has no `Currency` member (`ShiftsController.cs:249-254`) and no `request.Currency` read remains; the response currency comes from the resolver |
| AD-2 (page reads `method.currency` only; payload stops sending `currency`) | Yes | `RegisterClosePage.jsx` and the payload match the decision; asserted by the 3 web tests |
| AD-3 (`ExpectedAmountBsS` verbatim; `ActualAmountBsS = declaredNative x rate`) | Yes | `DailyClosureService.cs:245-248`; the `/rate*rate` round-trip is gone |
| AD-4 (one projection shared by report and close) | Yes | `ShiftReportMapper.MapDetails` is used by the report path; the close path duplicates the arithmetic rather than calling the mapper, so agreement is by construction of the same formula, not by a single code path (unchanged from S1) |
| AD-6 (rate via `ITodayExchangeRateProvider` -> `ExchangeRateResolver`) | Restored to the pre-S1 status quo, S3 target intact | WARNING-03 is closed by removing the unauthorized resolver; AD-6's provider is still assigned to S3 (tasks 3.2/3.3/3.4/3.7) and does not exist in compiled code |
| AD-15 (S4 client coupling; not S1) | N/A | Out of slice |

### Residual Warnings (non-blocking for S1)

- **RESIDUAL-01 (test stability, pre-existing)** - the backend suite is not deterministically green: 1 of 5 full executions reported `1147/1149` with two `CommandCenter.Tests.CheckoutUxTests` failures that pass in isolation and are unrelated to S1. Cause is test isolation/state leakage outside this change's files. Owner: not S1.
- **RESIDUAL-02 (coverage snapshot drift)** - the gate passes, but the measured rates differ from the prior report's snapshot: Core 0.8364 -> 0.8096, Sales.Module 0.8616 -> 0.8525, Inventory.Module 0.8251 -> 0.7937. The prior report's absolute values did not reproduce; all three remain above threshold.
- **RESIDUAL-03 (missing assertion)** - no test asserts the `application/problem+json` content type that CRITICAL-01's fix produces; the payload (`ProblemDetails` + `Status` + `Detail`) is asserted. The promotion is guaranteed by `ObjectResultExecutor` because `Value` is now a `ProblemDetails`, but the clause "RFC 7807" is proven by inspection, not by an assertion.
- **RESIDUAL-04 (scenario fidelity)** - the REQ-PMC-04 end-to-end test cannot literally be GIVEN `currency:"USD"`: the member no longer exists on `DeclaredAmountDto`, and no test binds a JSON payload containing an unknown `currency` member (a repository-wide search of `CommandCenter.Tests` finds no such literal). The compliant conclusion rests on structural impossibility plus the real-service assertions.
- **RESIDUAL-05 (comments)** - two S1-authored comments survive: `DailyClosureService.cs:223` (`// Validate declared method ids`, purely explanatory, no `8.x-*` marker) and `ShiftsController.cs:78` (`// Identity from JWT claims (H-API-2)`, a rewording of a pre-existing Spanish comment into a non-`8.x` traceability marker). `ShiftsController.cs:152-153` carries an allowed `8.7-B5:` marker.
- **RESIDUAL-06 (evidence record defects)** - `apply-progress.md` line 130 states the class is "537 lines" (actual: 504) and labels the class-size pending item "WARNING-05/07"; the prior report's WARNING-05 was the apply-progress misattribution (now fixed) and the class size was WARNING-07. The label mismatch makes the S3 hand-off ambiguous.
- **RESIDUAL-07 (out-of-slice, S2)** - `ShiftsController.cs:75` still returns `BadRequest(new { message = ... })` (non-RFC 7807) for duplicate declared method ids; `tasks.md` 2.2 targets exactly this site.
- **RESIDUAL-08 (test naming)** - `Phase7ClosureWithoutRateTests.ShiftsClose_WhenNoTodayBcvRateAndNoActiveSessionRate_ThrowsInvalidOperationException` now asserts a 400 `ProblemDetails` (the controller's rate guard fires first, so the mocked service throw is never reached - asserted `Times.Never` at line 182). The name no longer describes the behaviour.
- **RESIDUAL-09 (dead branch)** - `DailyClosureService.cs:214-218` still throws `InvalidOperationException` on `exchangeRate <= 0`, but the controller guards first, so the branch is unreachable from `POST /api/shifts/close`; it remains valid as defensive validation for other callers.
- **SUGGESTION (dead field)** - `DeclaredAmountDto.PaymentMethodName` (`ShiftsController.cs:252`) is still bound from the client and never read (the service uses the DB name); `CloseShiftRequest.CashierName`/`CashierCedula` remain dead too (S2 task 2.6).
- **SUGGESTION (tautological test)** - `CloseShift_DivergingMethodName_UsesResolverNotName` (line 147) still evaluates its divergence assertion inside the mock setup (lines 154-155) and asserts the mock's own output (line 178); it is now redundant given the real-path diverging test and should be deleted or strengthened.

### Remaining Slices (Pending)

| Slice | Status | Verification |
|-------|--------|--------------|
| S1 - Zero-trust close (items 26/39) | Implemented + remediated | **PASS_WITH_WARNINGS** (this section) |
| S2 - Error contract + dead fields | Implemented + remediated + re-verified | **PASS_WITH_WARNINGS** (Slice S2 section below) |
| S3 - Closure orchestration consolidation | Implemented + verified (commit 200cdaa) | **FAIL** - 1 CRITICAL (CRITICAL-S3-01, Serializable transaction missing); see "Slice S3" |
| S4a - Closure DTO boundary | Not implemented | Pending - all Phase 4a tasks unchecked |
| S4b - Drawer DTO boundary | Not implemented | Pending - all Phase 4b tasks unchecked |
| S5a - CancellationToken propagation | Not implemented | Pending - all Phase 5a tasks unchecked |
| S5b - EF tuning + guards/naming/comments | Not implemented | Pending - all Phase 5b tasks unchecked |
| S5c - J findings (H-05/H-06/H-08/H-14) | Not implemented | Pending - all Phase 5c tasks unchecked |

## Slice S2 — Error Contract + Dead Fields

**Verdict: PASS_WITH_WARNINGS** - 4/4 requirements and 9/9 scenarios compliant; 0 blockers, 0 critical findings; 8 non-blocking residual evidence/bookkeeping gaps (`RESIDUAL-S2-01`..`RESIDUAL-S2-08`).

**Verified revision**: `77b2d16` (S2 remediation; `HEAD`). **Prior S2 verification**: failed on `fe1b60b`.
**Slice delta**: `c6c767f` -> `77b2d16` (S2 = `fe1b60b` original + `77b2d16` remediation): 14 code/test files plus 2 documentation files.

**Artifact-trail note**: at the start of this re-verification, `verify-report.md` at `HEAD` contained **no** Slice S2 section. `git log --all -- <path>` returns only `00adc45` and `b36f8e6`, and the file at `HEAD` was the S1-only report. The S2 findings re-checked below are therefore the set supplied by the orchestrator's re-verification brief for `fe1b60b` (CRITICAL-01, WARNING-01..WARNING-05, bookkeeping). This is recorded because the repository artifact trail did not contain the prior S2 verdict text.
**Envelope note**: the YAML envelope at the top of this file remains the admitted **S1** envelope (3 requirements / 8 scenarios, evidence at `c6c767f`) and is deliberately not rewritten; the S2 verdict and its own fresh evidence live in this section. The change-level envelope stays deferred until S3-S5c land.

### S2 Re-executed Evidence (verbatim)

Every command below was re-executed independently on `77b2d16` after a clean `Release` build; stderr was merged into the captured stream (`2>&1`).

**1. Build** - `dotnet build CommandCenter.slnx -c Release` - exit `0` - matches the S2 claim

```text
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

captured-output hash: `sha256:987e2414f909210bf96f3391681bc0257571bcd6da5c38d12644b29fd8275515`

**2. Backend tests (full)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` - exit `0` - matches the S2 claim

```text
Correctas! - Con error:     0, Superado:  1170, Omitido:     0, Total:  1170, Duración: 11 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:411108a37becbbf2efff40e96e50bab662dd0f3d642d78daf26fcf0978af46bc`

Stability note: the known residual flake `SalesServiceUnitTests.SalesHistoryViewModel_WhenSelectedSaleChanges_PreloadsImmediately_ThenLoadsFullDetails` (`SalesServiceUnitTests.cs:516`) did **not** fail in this execution, so no isolated re-run was required and S1's RESIDUAL-01 was not observed.

**3. S2 focused filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~ErrorContract"` - exit `0` - matches the S2 claim

```text
Correctas! - Con error:     0, Superado:    21, Omitido:     0, Total:    21, Duración: 3 s - CommandCenter.Tests.dll (net10.0)
```

**4. Frontend tests** - `npm test` (Web.Frontend) - exit `0` - matches the S2 claim

```text
ℹ tests 271
ℹ suites 58
ℹ pass 271
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
```

**5. Frontend lint** - `npm run lint` (Web.Frontend, oxlint) - exit `0`, no findings - matches the S2 claim

```text
> web-frontend@0.0.0 lint
> oxlint
```

**6. Coverage gate** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` (1170/1170, exit `0`), then `python scripts/check-coverage.py CommandCenter.Tests/TestResults/e2b31d7e-f417-4b4f-8c3c-a14627763833/coverage.cobertura.xml` - exit `0`

```text
Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8096 min=0.7000 gap_a_70%=0.0000 [OK]
  Sales.Module       rate=0.8653 min=0.8000 gap_a_70%=0.0000 [OK]
  Inventory.Module   rate=0.7937 min=0.7200 gap_a_70%=0.0000 [OK]
```

All three thresholds in the `tasks.md` Verification section pass (Core >= 0.70, Sales.Module >= 0.80, Inventory.Module >= 0.72). `Sales.Module` - the layer S2 touches - moved 0.8525 (S1 run) -> 0.8653.

**Hash definition (this section)**: `sha256` is the SHA-256 over the captured combined stdout+stderr (merged with `2>&1` and captured by `Tee-Object`), normalized to UTF-8 without BOM, lines LF-joined, no trailing newline. The S1 envelope's documented recipe ("SHA-256 of the colon-joined per-file digests") could not be reproduced from its recorded file list, so the S1 `evidence_revision` was left untouched rather than recomputed under an unverifiable recipe (see RESIDUAL-S2-07).

**Environment note**: `TEST_POSTGRES_CONNECTION` is unset, so Postgres-gated classes early-return as vacuous passes; no S2 path depends on them.

### S2 Finding-to-Closure Matrix

| Finding | Prior severity | Closure | Fresh evidence |
|---------|----------------|---------|----------------|
| CRITICAL-01 - receipt-writer retry blocks the thread (`Thread.Sleep`) | Critical / blocker | **CLOSED in production; test clause NOT MET** (RESIDUAL-S2-01) | `DailyClosureService.cs:469-501`: both helpers are `private static async Task` and wait with `await Task.Delay(200, cancellationToken)` at lines 481 and 498; the blocking sleeps are gone. Repo-wide `Thread.Sleep` count is **1**, at `UpdaterService/Program.cs:228` (out of scope, non-async path); the receipt path contains **0**. Interface is async (`IDailyClosureService.cs:42`: `Task WriteClosedClosureReceiptsAsync(DailyClosure closure, CancellationToken cancellationToken = default)`) and its only caller awaits it (`DailyClosureController.cs:190`). **However**, no test discriminates the await from a blocking wait: `WriteClosedClosureReceipts_IsFailOpen_DoesNotThrowOnWriteFailure` (700) and `WriteClosedClosureReceipts_RetryLogsOnFailure` (723) both pass unchanged if `Thread.Sleep(200)` replaced the await. The brief's "would fail if a blocking sleep returned" clause is unmet. |
| WARNING-01 - Driver guards return a bodyless `Forbid()` | Warning / high | **CLOSED** | `ShiftsController.cs:64` -> `return this.ApiForbidden("El rol Driver no tiene permisos para cerrar turnos.");`; `DailyClosureController.cs:75` -> `return this.ApiForbidden("El rol Driver no tiene permisos para registrar cierres diarios.");`. `ApiForbidden` returns an `ObjectResult` wrapping a genuine `ProblemDetails` with `Status = 403` and non-null `Detail` (`ApiProblemResults.cs:18-22, 30-46`). Tests `ErrorContractTests.ShiftsController_DriverRole_ReturnsProblemDetails403` (176) and `DailyClosureController_DriverRole_ReturnsProblemDetails403` (470) assert `ObjectResult`, `StatusCode == 403`, `ProblemDetails`, `Status == 403`, `Detail != null`; both pass (21/21 filter run, item 3). |
| WARNING-02 - three non-discriminating repaired tests | Warning | **CLOSED** | (a) former `:172-194` -> `ShiftsController_DriverRole_ReturnsProblemDetails403` (176-200): the `Assert.IsType<ForbidResult>` + tautological `Assert.Equal(403, 403)` pair is replaced by `ProblemDetails` + `problemDetails.Status == 403` + non-null `Detail`; a revert to `Forbid()` now fails at `Assert.IsAssignableFrom<ObjectResult>` (195). (b) former `:463-486` -> `DailyClosureController_DriverRole_ReturnsProblemDetails403` (470-494): identical repair (489-493). (c) former `:556-583` -> `DailyClosureController_UnknownMethodIds_ReturnsProblemDetails400` (565-621): seeds a BCV rate into the InMemory `InventoryDbContext` (581-587) so the `exchangeRate <= 0` guard at `DailyClosureController.cs:143` no longer short-circuits, then asserts `problemDetails.Status == 400` and `Assert.Contains("999", problemDetails.Detail)` (620). The literal `999` exists only in the unknown-id `ApiBadRequest` message (`DailyClosureController.cs:162`), so the assertion cannot be satisfied by the rate guard. |
| WARNING-03 - legacy-sender test did not bind JSON | Warning | **CLOSED (serializer level)** (RESIDUAL-S2-04) | `ErrorContractTests.ShiftsController_LegacySenderExtraFields_StillSucceeds` (659-697) builds raw JSON carrying `cashierName`/`cashierCedula` (685-689) and deserializes it with `System.Text.Json.JsonSerializer.Deserialize<CloseShiftRequest>(..., new JsonSerializerOptions { PropertyNameCaseInsensitive = true })` (690-691), then drives `CloseShift` and asserts `OkObjectResult` (695-696). The removed fields no longer exist (`CloseShiftRequest_HasNoCashierNameOrCedula`, 644-649), so the extra members are genuinely unknown. It exercises the same serializer and options the MVC formatter uses, but not the MVC `[FromBody]` pipeline over HTTP. |
| WARNING-04 - no logging-on-failure test for the retry helpers | Warning | **CLOSED (coarse, environment-dependent)** (RESIDUAL-S2-02/03) | `WriteClosedClosureReceipts_RetryLogsOnFailure` (723-775) applies an ACL `Deny Write` to `%ProgramData%\CommandCenterPOS\Closures` (741-760), invokes the real service (765) and asserts `AppLogger.WarnLogPath` grew (763, 767, 774), restoring the ACL afterwards (769-772). It is regression-sensitive: deleting either `AppLogger.LogWarn` in the catches (480, 497) makes it fail. Limits: the deny is applied only when the directory already exists (747), it asserts any warn-growth rather than the specific `path + exception` entry, and it is Windows-only. The companion `WriteClosedClosureReceipts_IsFailOpen_DoesNotThrowOnWriteFailure` (700-720) asserts the fail-open contract but does not force a failure. Both catches log (no empty `catch`): `DailyClosureService.cs:478-482, 495-499`. |
| WARNING-05 - `ApiProblemResults` emitted a `Dictionary`, not `ProblemDetails` (maintainer chose UPGRADE) | Warning / contract | **CLOSED** (RESIDUAL-S2-05/06) | `ApiProblemResults.cs` now returns genuine `ProblemDetails` (`:30-46`) with `Status`, `Title`, `Detail = detail ?? message`, `Type = https://httpstatuses.com/{status}`, `Instance = Request.Path`, and `Extensions["message"]`/`["traceId"]`; `ApiForbidden`/`ApiUnprocessableEntity` set `ObjectResult.StatusCode` (18-28). Statuses preserved: 400/403/404/409/422. All 21 S2 tests assert `Assert.IsType<ProblemDetails>`; zero `Dictionary<string, object?>` assertions remain repo-wide. `REQ-AEC-01`'s "leave the preview 400 on `Problem(...)`" is honoured (`DailyClosureController.cs:58`; `DailyClosureController_PreviewDate400_KeepsProblemSemantics`, 624-639). Consumers still green: Web `api.js:336-345` reads `message` -> `detail` -> `title`; WPF `ApiErrorParser.cs:17` probes `message|detail|title|errors`; none reads the dropped `error` member. Fidelity gaps: the `error` member still emitted by `GlobalExceptionHandlerMiddleware.WriteProblemDetailsAsync` (`GlobalExceptionHandlerMiddleware.cs:316-326`) is dropped, `type` moved from `https://tools.ietf.org/html/rfc7231#...` to `https://httpstatuses.com/{status}`, and the 409 title changed from "Conflicto de Operacion" to "Conflict" (RESIDUAL-S2-05). No test asserts `application/problem+json` (RESIDUAL-S2-06). |
| Bookkeeping - apply-progress mislabels | Warning / evidence trail | **PARTIALLY CLOSED** (RESIDUAL-S2-07) | The two named items are corrected: `apply-progress.md:239` ("ShiftsController had 4 anonymous error objects (not 5 as previously stated)") and `:240` ("DailyClosureService.cs is 505 lines (not 537 as previously stated)"). Residuals: the S2 Files-Changed table still says "Replaced 5 anonymous error objects" (`:167`) contradicting `:153` ("4 (not 5)"); the line count is given as 505 in three places (`:95, :130, :240`) while the file measures **502**; and `tasks.md:48` (task 2.2) still lists five stale pre-S2 line numbers (74,106,239,266,278). |

### S1 Carry-Overs Closed by S2

- S1 **RESIDUAL-07** (out-of-slice S2 item: `ShiftsController.cs:75` returned `BadRequest(new { message = ... })` for duplicate declared method ids) - **CLOSED**: the site now returns `this.ApiBadRequest(...)` (`ShiftsController.cs:75`), asserted by `ErrorContractTests.ShiftsController_DuplicateMethodId_ReturnsProblemDetails400` (149-173).
- S1 dead-field SUGGESTION (`CloseShiftRequest.CashierName`/`CashierCedula`, `DeclaredAmountDto.PaymentMethodName`) - **CLOSED**: all three members deleted (AD-11) and covered by reflection tests `CloseShiftRequest_HasNoCashierNameOrCedula` (644) and `DeclaredAmountDto_HasNoPaymentMethodName` (652); the Web sender stopped sending them (`shiftApi.js`).
- S1 **RESIDUAL-05** (explanatory comment at `ShiftsController.cs:78`) - **CLOSED**: the comment is gone; the surviving comments in that file are `8.x-*`/`H-*` traceability markers only (lines 52, 148, 151-152, 180-183).

### S2 Scenario Evidence Matrix

Actual counts taken from the delta spec headings: **4 requirements / 9 scenarios** (`openspec/changes/legacy-debt-cleanup/specs/api-error-contract/spec.md`).

| Requirement | Scenario | Covering test | Result |
|-------------|----------|---------------|--------|
| REQ-AEC-01 | Error response is ProblemDetails | 19 payload-shape tests in `ErrorContractTests`, e.g. `ShiftsController_DuplicateMethodId_ReturnsProblemDetails400` (149), `CashDrawerController_AddTransaction_ZeroAmount_ReturnsProblemDetails400` (416), `DailyClosureController_UnknownMethodIds_ReturnsProblemDetails400` (565) | COMPLIANT - each asserts `ObjectResult` + `ProblemDetails` + `Status` equal to the HTTP status. Residual: content type unasserted (RESIDUAL-S2-06). |
| REQ-AEC-01 | No anonymous error object remains | Inspection of the three touched controllers: zero `BadRequest(new` / `NotFound(new` / `new {` error sites remain; each formerly-anonymous site is now a helper call whose payload-shape test fails on a revert | COMPLIANT - verified by inspection plus the shape tests; no standalone structural test exists. |
| REQ-AEC-02 | RBAC denial maps to forbidden | `ShiftsController_DriverRole_ReturnsProblemDetails403` (176), `DailyClosureController_DriverRole_ReturnsProblemDetails403` (470), `CashDrawerController_AddTransaction_CashierRole_ReturnsProblemDetails403` (386) | COMPLIANT - all assert HTTP 403 with a `ProblemDetails` body produced by `ApiForbidden`. |
| REQ-AEC-02 | Invalid input maps to bad request | 400 tests across the three controllers (e.g. `DailyClosureController_EmptyDetails_...` 497, `..._DuplicateMethods_...` 520, `..._NullRequest_...` 547, `CashDrawerController_AddTransaction_ZeroExchangeRate_...` 442) | COMPLIANT - all assert HTTP 400 with a `ProblemDetails` body produced by `ApiBadRequest`. |
| REQ-AEC-03 | Write failure is logged, not swallowed | `WriteClosedClosureReceipts_RetryLogsOnFailure` (723) + source: both catches call `AppLogger.LogWarn` with path + exception (`DailyClosureService.cs:480, 497`) | COMPLIANT (residual RESIDUAL-S2-02). |
| REQ-AEC-03 | Retry wait does not block the thread | Source inspection: `await Task.Delay(200, cancellationToken)` (`DailyClosureService.cs:481, 498`); `Thread.Sleep` absent from the receipt path and from all backend/service projects | COMPLIANT BY INSPECTION ONLY - no discriminating test (RESIDUAL-S2-01). |
| REQ-AEC-04 | Close request contract has no discarded fields | `CloseShiftRequest_HasNoCashierNameOrCedula` (644) | COMPLIANT - reflection asserts neither property exists. |
| REQ-AEC-04 | Legacy sender does not break the close | `ShiftsController_LegacySenderExtraFields_StillSucceeds` (659) | COMPLIANT - raw JSON with the two extra members deserializes and the close returns `Ok`. Residual: serializer-level, not MVC binding (RESIDUAL-S2-04). |
| REQ-AEC-04 | Closure ownership is derived server-side | Inspection: `ShiftsController.cs:81-95` derives identity from `_currentUserService.UserId` / `User.Identity.Name` (`ShiftsController.cs:82, 92-95`); the deleted DTO members are never consulted | COMPLIANT BY INSPECTION ONLY - no test asserts the derivation (RESIDUAL-S2-08). |

**Compliance summary**: 9/9 scenarios compliant, 0 UNTESTED, 0 FAILING. Requirement-level completeness: 4/4 (REQ-AEC-01..04 all satisfied). Two of the nine scenarios rest on inspection rather than assertion (RESIDUAL-S2-01, RESIDUAL-S2-08).

### S2 Changed Files (slice delta `c6c767f` -> `77b2d16`)

| File | Action | Role in S2 |
|------|--------|------------|
| `Backend.API/Controllers/ApiProblemResults.cs` | Modified | Helpers now return genuine `ProblemDetails` (WARNING-05 upgrade); `error` member dropped |
| `Backend.API/Controllers/ShiftsController.cs` | Modified | 4 anonymous error sites -> helpers; Driver guard payload; dead DTO members deleted |
| `Backend.API/Controllers/CashDrawerController.cs` | Modified | 4 anonymous error sites -> `ApiBadRequest`/`ApiForbidden` |
| `Backend.API/Controllers/DailyClosureController.cs` | Modified | 5 legacy sites -> helpers; Driver guard payload; caller now awaits `WriteClosedClosureReceiptsAsync` (190) |
| `Sales.Module/Interfaces/IDailyClosureService.cs` | Modified | `void WriteClosedClosureReceipts` -> `Task WriteClosedClosureReceiptsAsync(..., CancellationToken)` |
| `Sales.Module/Services/DailyClosureService.cs` | Modified | Receipt writers async (`await Task.Delay`), retry failures logged; 502 lines |
| `CommandCenter.Tests/Unit/ErrorContractTests.cs` | Created (S2) | 21 tests: payload shape, Driver guards, dead fields, legacy sender, receipt retry logging/fail-open |
| `CommandCenter.Tests/SecurityHardeningSprint2Tests.cs` | Modified | `DeclaredAmountDto.PaymentMethodName` removed from test DTO usage |
| `CommandCenter.Tests/Unit/CloseShiftResolverClassificationTests.cs` | Modified | `PaymentMethodName` removed from test DTO usage |
| `CommandCenter.Tests/Unit/Phase7ClosureWithoutRateTests.cs` | Modified | `CashierName` removed from `CloseShiftRequest` usage |
| `CommandCenter.Tests/Unit/ResidualRemediationLote26Tests.cs` | Modified | `PaymentMethodName` removed from test DTO usage |
| `Web.Frontend/src/pages/RegisterClosePage.jsx` | Modified | `closeShift` call aligned to the reduced signature |
| `Web.Frontend/src/services/shiftApi.js` | Modified | Stops sending `cashierName`/`cashierCedula` |

Plus the documentation files `apply-progress.md` and `tasks.md`.

### S2 Rules Compliance Audit

Checked against `docs/coding-guidelines-core.md` and `openspec/config.yaml`.

| Invariant | Status | Evidence |
|-----------|--------|----------|
| No `Thread.Sleep` on an async path | Compliant | Receipt path uses `await Task.Delay` (`DailyClosureService.cs:481, 498`); the only repo `Thread.Sleep` is `UpdaterService/Program.cs:228`, out of scope |
| No empty `catch` in the receipt writers | Compliant | Both catches log `path + exception` (`DailyClosureService.cs:480, 497`) |
| Errors surfaced as `ProblemDetails` | Compliant for the S2 sites (residual RESIDUAL-S2-05) | All touched sites are helper-backed and payload-tested; member parity with `GlobalExceptionHandlerMiddleware` is not exact |
| No `async void` introduced | Compliant | The receipt API is `Task`; `WriteClosedClosureReceiptsAsync` is awaited by its caller |
| RBAC / Zero-Trust unchanged | Compliant | Driver guards preserved and now carry a payload; role attributes untouched |
| Entities never returned to clients | N/A | S2 changes no success-body shape |
| Money in `decimal` only | N/A | S2 touches no monetary arithmetic |
| No unexplained comments | Compliant | The non-marker comment at the old `ShiftsController.cs:78` was removed; survivors are `8.x-*`/`H-*` markers |
| Class file <= 300-500 lines | **Violated** | `DailyClosureService.cs` measures **502** lines - WARNING-07, deferred to S3/AD-8 |

### S2 Residual Warnings (non-blocking)

- **RESIDUAL-S2-01 (missing discriminating test, REQ-AEC-03)** - no test distinguishes `await Task.Delay(200, ct)` from a blocking wait; the "Retry wait does not block the thread" scenario is proven by source inspection only. The production defect is fixed; the requested test clause is not met.
- **RESIDUAL-S2-02 (environment-dependent test)** - `WriteClosedClosureReceipts_RetryLogsOnFailure` forces a failure only when `%ProgramData%\CommandCenterPOS\Closures` already exists (line 747); on a host without that directory the deny is skipped, the writes succeed, and the assertion fails. It also asserts warn-file *growth* rather than the specific log entry, and is Windows-only.
- **RESIDUAL-S2-03 (test name over-promises)** - `WriteClosedClosureReceipts_IsFailOpen_DoesNotThrowOnWriteFailure` does not force a write failure; it writes to the real directory and asserts no exception, so fail-open is not exercised deterministically.
- **RESIDUAL-S2-04 (binding level)** - the legacy-sender test binds with `System.Text.Json.JsonSerializer` + `PropertyNameCaseInsensitive` instead of exercising the MVC `[FromBody]` input formatter over HTTP. Behaviorally equivalent for unknown members; not the real pipeline.
- **RESIDUAL-S2-05 (payload parity)** - the upgrade to genuine `ProblemDetails` drops the `error` member that `GlobalExceptionHandlerMiddleware.WriteProblemDetailsAsync` still emits (`GlobalExceptionHandlerMiddleware.cs:316-326`), moves `type` from `tools.ietf.org/html/rfc7231#...` to `https://httpstatuses.com/{status}`, and renames the 409 title from "Conflicto de Operacion" to "Conflict". REQ-AEC-01's "member naming MUST match" holds for casing; member *parity* with the middleware no longer holds. No repo consumer reads `error`, so nothing breaks; reconcile when the error paths are consolidated.
- **RESIDUAL-S2-06 (content type unasserted)** - no test asserts `application/problem+json`; promotion rests on `ObjectResultExecutor` behaviour given a `ProblemDetails` value. Same class as S1 RESIDUAL-03.
- **RESIDUAL-S2-07 (bookkeeping)** - the named corrections landed, but the S2 Files-Changed table still claims "5 anonymous error objects" (`apply-progress.md:167`), the `DailyClosureService.cs` line count is 505 in three places while the file measures 502, and `tasks.md:48` (2.2) keeps five stale line numbers. The S1 envelope's `evidence_revision` recipe also does not reproduce from its recorded file list.
- **RESIDUAL-S2-08 (scenario covered by inspection only)** - REQ-AEC-04 "Closure ownership is derived server-side" has no test; the derivation is read from `ShiftsController.cs:81-95`.

## Slice S3 — Closure Orchestration Consolidation

**Verdict: FAIL** — 0 blockers, **1 CRITICAL finding** (`CRITICAL-S3-01`). 4 requirements / 8 scenarios in the delta spec `closure-orchestration-consolidation`; **7/8 scenarios compliant**, and the single unmet scenario is the REQ-COC-01 transaction scenario.

**Verified revision**: `200cdaa` (`HEAD`). Working tree clean (`git status --short` empty): no production, test, or documentation file is modified against `200cdaa`.
**Slice delta**: `77b2d16` -> `200cdaa` — 27 files (7 production, 18 test, 2 documentation), `+632 / -509` lines.
**Spec under verification**: `openspec/changes/legacy-debt-cleanup/specs/closure-orchestration-consolidation/spec.md` — **4 requirements / 8 scenarios** (counted from the `### Requirement:` and `#### Scenario:` headings; REQ-COC-01..04, two scenarios each).
**Prior slices**: S1 and S2 are `pass_with_warnings` (sections above, unchanged). The change-level verdict remains **pending** for S4a-S5c.
**Envelope note**: the YAML envelope at the top of this file is still the admitted **S1** envelope (3 requirements / 8 scenarios, evidence at `c6c767f`) and is deliberately not rewritten; the S3 verdict and its own fresh evidence live in this section, exactly as the Slice S2 section already documents. The change-level envelope stays deferred until S3-S5c settle.

### S3 Re-executed Evidence (verbatim)

Every command below was re-executed independently on `200cdaa`. `stderr` was merged into the captured stream (`*> file`). No command was taken from `apply-progress.md`.

**1. Build** - `dotnet build CommandCenter.slnx -c Release` - exit `0` - matches the S3 claim (0/0)

```text
Compilación correcta.
    0 Advertencia(s)
    0 Errores

Tiempo transcurrido 00:00:29.59
```

captured-output hash: `sha256:32e404d414b64ba9e95f418e06d03bb9fe7fa9a70146ac379edb4970526fd724`

**2. Backend tests (full)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` - exit `0` - matches the S3 claim (1175/1175)

```text
Correctas! - Con error:     0, Superado:  1175, Omitido:     0, Total:  1175, Duración: 12 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:f9166c1d186f48d47f4b1d593cca985aafd918d60c441bae59dd1e0679567e36`

**3. S3 focused filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"` - exit `0` - matches the S3 claim (77/77)

```text
Correctas! - Con error:     0, Superado:    77, Omitido:     0, Total:    77, Duración: 3 s - CommandCenter.Tests.dll (net10.0)
```

**4. New-test class filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~DailyClosureControllerTests"` - exit `0` - matches the S3 claim (7/7)

```text
Correctas! - Con error:     0, Superado:     7, Omitido:     0, Total:     7, Duración: 467 ms - CommandCenter.Tests.dll (net10.0)
```

**5. Frontend tests** - `npm test` (Web.Frontend) - exit `0` on retry - matches the S3 claim (271/271)

```text
ℹ tests 271
ℹ suites 58
ℹ pass 271
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
```

Stability note (environmental): the **first** execution of this exact command failed with `EXIT=1` and `233` discovered tests / `8` failures, every failure being `FATAL ERROR: Zone Allocation failed - process out of memory` (V8 OOM in the Node test runner, not an assertion failure). The mandated single retry reproduced the claimed `271/271` green run. See `RESIDUAL-S3-10`.

**6. Frontend lint** - `npm run lint` (Web.Frontend, oxlint) - exit `0`, no findings - matches the S3 claim

```text
> web-frontend@0.0.0 lint
> oxlint
```

**7. Coverage gate** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` (exit `0`), then `python scripts/check-coverage.py CommandCenter.Tests/TestResults/8e4161bc-5175-4f2b-b008-7e50102686aa/coverage.cobertura.xml` - exit `0`

```text
Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8364 min=0.7000 gap_a_70%=0.0000 [OK]
  Sales.Module       rate=0.8934 min=0.8000 gap_a_70%=0.0000 [OK]
  Inventory.Module   rate=0.8251 min=0.7200 gap_a_70%=0.0000 [OK]
```

All three thresholds in the `tasks.md` Verification section pass (Core >= 0.70, Sales.Module >= 0.80, Inventory.Module >= 0.72). `Sales.Module` — the layer S3 refactors — moved 0.8653 (S2 run) -> **0.8934**. S3 removed no test: the 82-line reduction in `ErrorContractTests.cs` and the 10-line reduction in `PaymentMethodCurrencyClassificationTests.cs` are constructor-argument/scaffolding collapses (verified by diff: only `-` mock/`DbContext` setup and `+` re-pointed constructor calls, zero deleted `[Fact]`).

**Hash definition (this section)**: `sha256` is the SHA-256 over the captured combined stdout+stderr (`*>` redirection), normalized to UTF-8 without BOM, `CRLF` -> `LF`, trailing newlines trimmed. Same class of recipe as the Slice S2 section.

**Environment note**: `TEST_POSTGRES_CONNECTION` is unset, so the Postgres-gated classes (`DailyClosureRetryIntegrationTests`, `DailyClosureFlowIntegrationTests`) early-return as vacuous passes. The only test that exercises a real `Serializable` transaction around the closure run is skipped in this environment.

### S3 Requirement Evidence Matrix

| Requirement | Scenario | Covering test / evidence | Result |
|-------------|----------|--------------------------|--------|
| REQ-COC-01 | Controller delegates the closure run (no persistence in the controller) | `DailyClosureControllerTests.CreateClosure_DelegatesToService_AndPersistsNothingDirectly` (line 83): asserts `OkObjectResult` and `CreateClosureFromCommandAsync` invoked `Times.Once` with the mapped command. Controller source (`DailyClosureController.cs:49-104`) calls the service at line 92 and has no `DbContext` member at all, so no persistence surface exists | **COMPLIANT** |
| REQ-COC-01 | Service owns the transaction (rollback on a mid-way failure; no partial closure) | **No covering test; not implemented.** `DailyClosureService.cs:268-283` runs `_context.Database.CreateExecutionStrategy().ExecuteAsync(...)` but the file contains **no** `BeginTransactionAsync` — the only transaction references are ambient reads at `:112` and `:269`. `PersistClosureCoreAsync` (`:320-325`) does `Add` + `SaveChangesAsync` (self-committed); `RolloverSessionAfterClosureAsync` (`:272`, `:280`) then opens **its own** transactions (`CashDrawerService.cs:132-215`). The pre-S3 `DailyClosureController` opened `using var dbTransaction = await _salesContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable)` and committed after the rollover (`git show 200cdaa -- Backend.API/Controllers/DailyClosureController.cs`, removed lines) | **NOT COMPLIANT — `CRITICAL-S3-01`** |
| REQ-COC-02 | No `DbContext` in touched controllers (constructors and fields) | `DailyClosureControllerTests`: `DailyClosureController_HasNoDbContextInConstructor` (130), `ShiftsController_HasNoDbContextInConstructor` (142), `DailyClosureController_HasNoDbContextFields` (154), `ShiftsController_HasNoDbContextFields` (162) — all pass. Constructors now take only `IDailyClosureService` + `ICurrentUserService` (`DailyClosureController.cs:22-28`, `ShiftsController.cs:23-29`); the `Microsoft.EntityFrameworkCore` / `Sales.Module.Data` / `Inventory.Module.Data` / `Backend.API.Services` usings are gone from both controller files | **COMPLIANT** |
| REQ-COC-02 | Authorization still gates the closure (403, nothing persisted) | `[Authorize]` remains on both controllers; the Driver guard returns `ApiForbidden` 403 (`DailyClosureController.cs:51-54`, `ShiftsController.cs:35-38`); `[RequireSecurityStampValidation]` retained; backdating (future / >24 h) stays in the controller in `ResolveClosureDate` (`:115-146`) behind `isAdmin`; `GetReportById` ownership check (`ShiftsController.cs:118-134`) retained. Covered by S2's `ErrorContractTests` Driver tests (still green) | **COMPLIANT** |
| REQ-COC-03 | `CreateClosure` is under the ceiling | Claimed `7`. Recount under the convention the apply-progress itself uses for `MergeMissingMethodsWithReport` (it counts `&&`) gives **11** — see the recount table. No compiler/analyzer metric exists in the repo, so the requirement is *unproven by tooling* and *failing under standard enumeration* | **NOT PROVEN / AT RISK — `WARNING-S3-02`** |
| REQ-COC-03 | Extracted methods are under the ceiling | Recount: `ValidateDeclaredMethods` 2, `MergeMissingMethodsIntoClosure` 5, `MergeMissingMethodsWithReport` 7, `RecalculateTotals` 3, `ResolveUserDetailsAsync` 3, `PersistClosureCoreAsync` 1, `ExecuteClosureCoreAsync` 4 — all < 10. `CreateClosureFromCommandAsync` recount is 8 (claimed 9; the claim double-counts two decisions that live in `ResolveUserDetailsAsync`) | **COMPLIANT** |
| REQ-COC-04 | Same inputs, same stored closure (amounts, status, response) | The five real-path tests in `CloseShiftResolverClassificationTests.cs:322-509` execute the production service against a real InMemory `SalesDbContext` (no service mock) and assert the persisted `ClosureDetail.ActualAmountBsS` / `DailyClosure.ExchangeRate` read back from the context. Plus the full suite is 1175/1175. `GetReportById` now reads the persisted snapshot `closure.ExchangeRate` (`ShiftsController.cs:141, 156`) instead of falling back to today's rate | **COMPLIANT** (happy path) |
| REQ-COC-04 | Preview rejection is not regressed (`dateUtc` omitted/default -> 400, no totals) | `DailyClosureControllerTests.GetExpectedTotals_WhenDefaultDate_Returns400ProblemDetails` (56): asserts `ObjectResult`, `StatusCode == 400`, `ProblemDetails`. Source `DailyClosureController.cs:34-41` still returns `Problem(...)` (deliberately not routed through `ApiBadRequest`, per AD-9/REQ-AEC-01) | **COMPLIANT** |

**Compliance summary**: 7/8 scenarios compliant; 1 UNTESTED-AND-FAILING (REQ-COC-01 transaction). Requirement level: REQ-COC-02, REQ-COC-04 satisfied; REQ-COC-01 **not satisfied**; REQ-COC-03 evidenced only by a recount that contradicts the claim.

### REQ-COC-03 — McCabe Recount (method stated)

**Counting method used**: count every branch-introducing construct (`if`, `foreach`, `while`, `for`, `case`, `catch`, `&&`, `||`, `??`, `?:`); complexity = decision points + 1. This is the convention `apply-progress.md` itself applies to `MergeMissingMethodsWithReport`, where it explicitly lists `&&` as a decision point.

| Method | apply-progress claim | My recount (points -> McCabe) | < 10? |
|--------|----------------------|-------------------------------|-------|
| `DailyClosureController.CreateClosure` (49-104) | 7 | `if(Driver)` 1 + `if(request==null \|\| Details==null \|\| !Any)` 1+2 + `if(duplicated)` 1 + `??` 2 + `?:` 1 + `catch` 2 = **10 -> 11** | **NO** |
| `DailyClosureController.ResolveClosureDate` (115-146) | 6 | `if(default)` 1 + `?:` 1 + `if(!isAdmin)` 1 + `if(future)` 1 + `if(>24h)` 1 = 5 -> **6** | yes |
| `DailyClosureService.CreateClosureFromCommandAsync` (192-295) | 9 | `if(rate<=0)` 1 + `foreach` 1 + `?:` 2 + `?:` 2 + `if(CurrentTransaction)` 1 = 7 -> **8** | yes |
| `ValidateDeclaredMethods` (327-343) | 2 | `if(count>0)` 1 -> **2** | yes |
| `MergeMissingMethodsIntoClosure` (345-368) | 5 | `foreach` 1 + `if(!Contains)` 1 + `&&` 1 + `?:` 1 = 4 -> **5** | yes |
| `MergeMissingMethodsWithReport` (370-409) | 7 | `foreach` 1 + `if(!Contains)` 1 + `&&` 1 + `?:` 3 = 6 -> **7** | yes |
| `RecalculateTotals` (411-427) | 3 | `foreach` 1 + `if(<0)` 1 -> **3** | yes |
| `ResolveUserDetailsAsync` (297-318) | 3 | `if(TryParse)` 1 + `if(user!=null)` 1 -> **3** | yes |
| `PersistClosureCoreAsync` (320-325) | not listed | 0 -> **1** | yes |
| `ExecuteClosureCoreAsync` (121-166) | not listed | `if(dup)` 1 + `if(unknown)` 1 + `foreach` 1 -> **4** | yes |

Two evidence defects are proven, not inferred:

1. **`CreateClosure` over the ceiling under the applied convention.** The apply-progress row omits the two short-circuit operators inside the guard at line 56 and the two `??` operators at lines 74-76. Applying the same convention it applies to `MergeMissingMethodsWithReport` yields 11, which violates REQ-COC-03's "MUST measure below 10". If the maintainer's intended metric is statement-level-only (no `&&`/`||`/`??`), the value is 7 and compliant — **the metric convention is undocumented in the repo and must be fixed**. Either way the apply-progress table is internally inconsistent.
2. **`CreateClosureFromCommandAsync` double-counts.** The claimed 9 lists `if(TryParse)` and `if(user!=null)`, which are statements of `ResolveUserDetailsAsync`, not of this method. The true value is 8. (Under 10 either way, so no requirement risk — evidence quality only.)

### Discrimination Review (the S1 CRITICAL-01 lesson)

**The 5 new `DailyClosureControllerTests` tests are discriminating.**

- `CreateClosure_DelegatesToService_AndPersistsNothingDirectly` (line 83) — fails if the controller stops delegating or passes a wrong command (`mockClosure.Verify(..., Times.Once)` + predicate on `UserId`/`Declarations`). The "persists nothing directly" clause is enforced structurally, not by assertion: the controller type has no `DbContext` member, so the regression cannot compile.
- The 4 structural tests assert the *actual* `Type` through reflection, so re-introducing `SalesDbContext`, `InventoryDbContext`, or any `*DbContext` constructor parameter or field turns them red. Not tautological.

**Tautological / non-discriminating assertions were found in four re-pointed tests (regression of the same class the S1 verdict already caught once):**

| Site | Assertion | Why it is vacuous |
|------|-----------|-------------------|
| `SecurityHardeningSprint2Tests.cs:271` | `mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(...), Times.Never)` | `mockCashDrawer` is no longer injected into `ShiftsController` (line 239-241 passes only `mockDailyClosure` + `mockUser`); the controller cannot reach it, so `Times.Never` holds for every possible implementation |
| `ResidualRemediationLote26Tests.cs:283` | `mockClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Never)` | It targets the **legacy** `CreateClosureAsync(DailyClosure)` entry point; the controller now calls `CreateClosureFromCommandAsync`, so the verified method is unreachable and the assertion can never fail |
| `ResidualRemediationLote26Tests.cs:284` | `mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(...), Times.Never)` | `mockCashDrawer` is not injected (line 263-268) |
| `ResidualRemediationLote26Tests.cs:320` | `mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(...), Times.Never)` | `mockCashDrawer` is not injected (line 299-304) |

Also present: `SecurityHardeningSprint2Tests.DailyClosureController_UnknownPaymentMethodId_ReturnsBadRequestWithoutCreatingClosure` (176) now mocks `CreateClosureFromCommandAsync` to throw and then asserts only the controller's `catch` mapping — the name's "WithoutCreatingClosure" clause is not exercised (nothing can be created through a mock). And the re-pointed files retain dead scaffolding (`mockPaymentMethod`, `mockSettings`, unused `salesDb`/`inventoryDb`).

**What the re-pointing did preserve**: no `[Fact]` was deleted in S3 (verified by diffing `ErrorContractTests.cs` and `PaymentMethodCurrencyClassificationTests.cs`, the two files with the largest line reductions — only constructor arguments and unused mock setup were collapsed). Suite-level count rose 1149 (S1) -> 1170 (S2) -> **1175** (S3).

### Scope Check

No S4+ creep. The `200cdaa` file list contains only S3 targets: `ITodayExchangeRateProvider`, `TodayExchangeRateProvider`, `ServiceCollectionExtensions`, `DailyClosureService`, `IDailyClosureService`, `CreateClosureCommand`, both close controllers, and test files. `CashDrawerService.cs` (S4b/S5b), `AuthController.cs` (S5c), `MainWindow.xaml.cs` and the WPF view models (S5c), `RegisterPage.jsx` (S5c), and the DTO folders (S4a/S4b) are untouched. `tasks.md` Phase 4a-5c remain unchecked.

Two in-slice behavior deltas beyond the listed scenarios are recorded as warnings, not as creep: the `DbUpdateException` 409 mapping was dropped from both close controllers (`RESIDUAL-S3-06`), and `GetReportById` now uses the persisted `ExchangeRate` snapshot with no today's-rate fallback (`RESIDUAL-S3-07`, aligned with the snapshot-immutability invariant).

**Risk evaluation requested by the orchestrator**

- **`DailyClosureService.cs` size**: **586 lines** measured (`Get-Content | .Count`), against the 300-500 ceiling in `docs/coding-guidelines-core.md`. This is **worse than the S2 measurement (502) and than the 578 claimed in `apply-progress.md`/ANEXO 8.140**. The AD-8 extraction moved code but added `CreateClosureFromCommandAsync` (103 lines) and produced two near-duplicate merge helpers (`MergeMissingMethodsIntoClosure` vs `MergeMissingMethodsWithReport`, `:345-409`). **Register as a WARNING for S4/S5**; WARNING-07 is not closed — it is enlarged. The declared S3 fold-in ("505 -> 578") is also numerically wrong.
- **`ExecuteClosureCoreAsync` legacy entry point**: retained as the body of the still-public `IDailyClosureService.CreateClosureAsync(DailyClosure)` (`:110-119`, `:121-166`). It has **no production caller** — the two controllers delegate to `CreateClosureFromCommandAsync`; its only callers are tests (`CashDrawerClosureTests`, `CheckoutAndPaymentTests`, `DailyClosureServiceUnitTests`, `Phase7ClosureWithoutRateTests`, `SecurityHardeningSprint2Tests`, `ResidualRemediationLote26Tests`, `DailyClosureFlowIntegrationTests`, `DailyClosureRetryIntegrationTests`) and the WPF-facing path is HTTP, not this service. It carries a second, divergent copy of the closure rules (its own duplicate/unknown guards and its own `MergeMissingMethodsIntoClosure`/`RecalculateTotals` calls) and no `Serializable` transaction of its own either. **Register as a WARNING for S4/S5** with a recommended disposition: delete it together with its test references, or make it a private adapter that delegates to the command path.

### S3 Residual Warnings (non-blocking, except where noted)

- **CRITICAL-S3-01 (transaction ownership — blocks the slice)** — `CreateClosureFromCommandAsync` does not begin a transaction. The `Serializable` isolation that the pre-S3 `DailyClosureController` applied to *totals read -> closure persist -> rollover* is gone, so a mid-way failure (e.g. `OpenSessionAsync` throwing after `CloseSessionAsync` succeeded) now leaves a persisted closure and a half-rolled drawer session; the registration of the retrying execution strategy over a non-transactional, non-idempotent delegate also changes retry semantics. `apply-progress.md:279` and ANEXO 8.140 `B3` both claim "Serializable tx", and REQ-COC-01's scenario text is an unconditional MUST. This is the failing check that sets the slice verdict to `fail`.
- **RESIDUAL-S3-01 (stale test narrative)** — `DailyClosureRetryIntegrationTests.cs:13-19` still documents "El flujo de produccion (DailyClosureController/ShiftsController) abre una transaccion Serializable dentro de una strategy externa". That flow no longer exists; the test itself opens the transaction manually, so it passes against a production shape that was deleted. Postgres-gated, therefore skipped locally.
- **RESIDUAL-S3-02 (McCabe evidence defective)** — see the recount table: `CreateClosure` 11 vs claimed 7; `CreateClosureFromCommandAsync` 8 vs claimed 9 (double-count). The measurement convention is undocumented, so REQ-COC-03 cannot be closed without a stated metric.
- **RESIDUAL-S3-03 (tautological assertions)** — the four sites tabulated in the discrimination review; each should assert against an injected collaborator or be deleted.
- **RESIDUAL-S3-04 (rate-ownership coverage)** — `GetEffectiveTodayRateAsync` appears in exactly one test file, `DailyClosureTestHelper.cs:13`, always fixed at `50m`. No test drives the real `CreateClosureFromCommandAsync` through `exchangeRate <= 0` (`DailyClosureService.cs:197-201`). S1's `Phase7ClosureWithoutRateTests` used to exercise the real guard on the controller; after S3 the same file mocks the service to throw (`:64-65`, `:139-141`), so REQ-COC-01's rate-ownership clause is verified by inspection only.
- **RESIDUAL-S3-05 (no transaction assertion)** — no test asserts a transaction exists around the closure run outside the Postgres-gated skip; the scenario is verified by reading `git show 200cdaa` and a repo-wide `IsolationLevel` grep.
- **RESIDUAL-S3-06 (409 mapping removed)** — both close controllers dropped `catch (DbUpdateException) -> ApiConflict(...)`. A concurrent closure that previously produced HTTP 409 "Conflicto de concurrencia..." now falls through to the global handler. REQ-COC-04's listed scenarios do not freeze this path, but it is an observable status change; confirm the intended contract.
- **RESIDUAL-S3-07 (snapshot read change)** — `ShiftsController.GetReportById` replaced `closure != null && closure.ExchangeRate > 0 ? closure.ExchangeRate : GetTodayExchangeRateAsync()` with `closure.ExchangeRate`. A persisted closure with `ExchangeRate == 0` now reports `0` instead of today's rate. This aligns with "never recompute history with the current rate"; record it as intended.
- **RESIDUAL-S3-08 (post-commit comment wrong on one branch)** — `DailyClosureService.cs:285` says receipts are written "DESPUÉS del commit, fuera de la transacción Serializable", but in the ambient-transaction branch (`:269-273`) the commit belongs to the caller, so receipts are written before that commit. No production caller opens an ambient transaction today, so the exposure is latent.
- **RESIDUAL-S3-09 (class size)** — `DailyClosureService.cs` is 586 lines (>500 ceiling, `WARNING-07` enlarged); recommend a partial-class split or a handler sub-service in S4/S5.
- **RESIDUAL-S3-10 (test-runner OOM, environmental)** — first `npm test` run died with `FATAL ERROR: Zone Allocation failed - process out of memory` and reported 233/8-fail; the single mandated retry was green at 271/271 with lint clean. Not attributable to S3's diff (no Web file is touched by `200cdaa`).
- **RESIDUAL-S3-11 (dead test scaffolding)** — re-pointed tests retain `mockPaymentMethod`, `mockSettings`, and unused `salesDb`/`inventoryDb` locals; hygiene only.

### S3 Verdict

**FAIL** — S3 correctly consolidates orchestration ownership, the rate provider layering, and controller delegation (REQ-COC-02 and REQ-COC-04 are met, and 7 of 8 scenarios are compliant), but REQ-COC-01's "Service owns the transaction" scenario is not implemented: no `Serializable` transaction (indeed no transaction at all) surrounds the closure persist + session rollover, contradicting the spec scenario, `design.md` AD-5/Data Flow, `apply-progress.md:279` and ANEXO 8.140 `B3`. The change does not carry the cross-DB atomicity it had before S3. Add the transaction inside `CreateClosureFromCommandAsync` (execution strategy + `BeginTransactionAsync(IsolationLevel.Serializable)` + commit), add a regression-sensitive test for it, correct the McCabe evidence and the class-size number, and replace the four tautological assertions. S3 cannot be chained into S4 until then.

### Change-Level Verdict

**Pending**. The change cannot receive a change-level verdict while S4a-S5c are unimplemented. S1 and S2 are verified `pass_with_warnings`; **S3 is verified `fail`** (1 CRITICAL: `CRITICAL-S3-01`, the `Serializable` transaction was dropped from the closure run - see the "Slice S3" section). S3 did **not** close WARNING-04 (the merged-undeclared-method lines remain, now duplicated across `MergeMissingMethodsIntoClosure`/`MergeMissingMethodsWithReport`) and did **not** close WARNING-07 (class size grew 502 -> 586 lines); both escalate to S4/S5. S1 RESIDUAL-07 (duplicate-id error object) is **CLOSED** by S2/AD-9.

### Verdict

**PASS_WITH_WARNINGS (Slice S1)** - the remediation commit `c6c767f` closes all five findings registered against the prior verdict: CRITICAL-01 (the 400 path now emits an RFC 7807 `ProblemDetails` payload), WARNING-01 (five real-path tests execute the production `CreateClosureFromCommandAsync` against an InMemory `SalesDbContext`), WARNING-02 (the REQ-PMC-04 end-to-end scenario is covered and the diverging-name test is discriminating), WARNING-03 (the unauthorized rate source is fully reverted to the pre-S1 `ExchangeRateResolver` semantics, with `ITodayExchangeRateProvider` and AD-5/AD-6 left intact for S3) and WARNING-06 (the remediation adds no comment line and the apply-progress labels are corrected). All eight S1 scenarios and all three S1 requirements are compliant, so the verdict moves from `fail` to `pass_with_warnings`. The change carries no blockers and no critical findings; the residual warnings (RESIDUAL-01..09) are non-blocking, and WARNING-04/WARNING-07 remain deliberately deferred to S3 as recorded. S1 may be chained into S2.
