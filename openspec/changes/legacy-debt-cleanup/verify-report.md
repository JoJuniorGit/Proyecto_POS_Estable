```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:2db6c81f19a620c4838c1912971828dae39a6b91fe0af9685e4e618229a87a14
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 3/3
scenarios: 5/5
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
test_exit_code: 0
test_output_hash: sha256:3f8f629158dbd35c075a2d00cf749f84c1a4846d6fe2e8de9a8bd1738e4dd82f
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:3a1d2f6e6c116edfe4694a823e7cd5e800848acabdd3167a1378ce90687f5b70
```

## Verification Report

**Change**: legacy-debt-cleanup
**Version**: N/A (delta specs, no version headers)
**Mode**: Standard (Strict TDD inactive: `openspec/config.yaml` -> `strict_tdd: false`, `testing.strict_tdd_mode: disabled`)
**Scope of this report**: a per-slice verification record for the change `legacy-debt-cleanup`. Slices S1 (zero-trust close, items 26/39), S2 (error contract + dead fields, items 2/18/25/12/35), S3 (closure orchestration consolidation, items 1/30/6/13/14), **S4a (closure DTO boundary, commit `829778c`)** and **S4b (drawer DTO boundary, commit `65e038a`)** are implemented; S5a-S5c are not, so the **change-level verdict remains pending**. The machine-readable envelope at the top of this file describes the **most recently admitted slice — S4b at `65e038a`**. The previous head envelopes are preserved verbatim under "S4a Admitted Envelope (preserved)", "S3 Admitted Envelope (preserved)" and "S1 Admitted Envelope (preserved)"; the S1, S2, S3, S4a and S4b verdicts and their own fresh evidence live in their own sections.
**Envelope counts (S4b)**: `requirements: 3/3` and `scenarios: 5/5` are completed/total for **this slice's declared scope** — REQ-ADB-02, REQ-ADB-03 and the drawer half of REQ-ADB-04 — not for the whole `api-dto-boundary` delta spec, which carries 4 requirements / 7 scenarios. Spec-wide at revision `65e038a`, the entire `api-dto-boundary` spec is now complete: **4/4 requirements** and **7/7 scenarios** — REQ-ADB-01 and the closure half of REQ-ADB-04 were admitted under S4a at `829778c`; S4b completes REQ-ADB-02, REQ-ADB-03 and the drawer half. Change-wide, **15 of the 18 requirements** and **32 of the 38 scenarios** across the five delta specs are complete; only `async-cancellation-propagation` (3 requirements / 6 scenarios) remains, in S5a/S5c.
**Verified revision**: the current `HEAD` is `65e038a` (`refactor(8.140): DTOs inmutables de drawer (slice S4b, items 15/19/20) - ANEXO 8.140`). The working tree is clean (`git status --short` and `git diff --stat HEAD` both empty): no production, test, or documentation file is modified against `65e038a`. Earlier sections record the revision each of them was verified at: S1 at `c6c767f` (which carried one uncommitted documentation-only `apply-progress.md` append at that moment), S2 at `77b2d16`, S3 at `579347b` and S4a at `829778c`.
**Prior verdict**: `fail` (commit `00adc45`), 1 blocker / 1 critical finding — superseded for S1. Per slice: S2 was `fail` on `fe1b60b` and S3 was `fail` on `200cdaa` (1 CRITICAL, `CRITICAL-S3-01`); both were superseded by their re-verifications. S4a was `pass_with_warnings` at `829778c`; S4b has no prior verdict.
**evidence_revision** (head envelope, S4b) is the SHA-256 of the ASCII string produced by joining, with `:`, the lowercase SHA-256 hex digests of the twenty-six S4b production/test files that exist at `65e038a`, listed in order under "S4b Changed Files" (the deleted `Backend.API/DTOs/CashDrawerDtos.cs` is excluded because it no longer exists to hash; the same recipe used for S4a and S3, which were reproducible).
**Hash definition**: `build_output_hash` / `test_output_hash` are the SHA-256 of the captured combined stdout+stderr for the command execution reported above, normalized to UTF-8 without BOM, `CRLF` -> `LF`, trailing newlines trimmed; identical recipe per section below.

### S1 Admitted Envelope (preserved)

The envelope below was the file's admitted machine-readable head from the S1 verification until this S3 re-verification re-pointed the head envelope to the S3 slice. It is preserved byte-for-byte as the S1 evidence record.

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

The S1 `evidence_revision` above is the SHA-256 of the colon-joined per-file SHA-256 digests of the thirteen S1 production/test files listed under "S1 Changed Files" (that recipe did not reproduce from its recorded file list — see RESIDUAL-S2-07). The S1 `build_output_hash` / `test_output_hash` are the SHA-256 of the byte-exact combined stdout+stderr captured for the S1 command execution reported in the S1 section (UTF-8, LF-joined, `Set-Content -NoNewline`).

### S3 Admitted Envelope (preserved)

The envelope below was this file's admitted machine-readable head from the S3 re-verification until the S4a verification re-pointed the head envelope to the S4a slice. It is preserved byte-for-byte as the S3 evidence record.

```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:d6dda3afadbf8d3273651e5b4f74a42e65414bc843cae8dbcb352e3766b045be
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 4/4
scenarios: 8/8
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
test_exit_code: 0
test_output_hash: sha256:fbe42f41f6f4ccb6055708f82c25805131db047bfafddbe9dea3409d43bfc389
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:df799fe601a812014f586cec26862365b16d8d60fa8a2f761631f2f2cf6b7f87
```

### S4a Admitted Envelope (preserved)

The envelope below was this file's admitted machine-readable head from the S4a verification until the S4b verification re-pointed the head envelope to the S4b slice. It is preserved byte-for-byte as the S4a evidence record.

```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:b96d5e3bdd5aef28d55a1968c613b103bd177ce05f1a0c873a2113f9ef338d4b
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 2/2
scenarios: 4/4
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
test_exit_code: 0
test_output_hash: sha256:e0ebbc71fe54232be778743f7dd9bb118c09bd586c44f07b44f206b31870a0d3
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:12942f0e260fa5f766cca6dae5e9dae739767bada4e6ae11e3cd7af5b64fa31d
```

The S4a `evidence_revision` above is the SHA-256 of the colon-joined per-file SHA-256 digests of the fifteen S4a production/test files listed under "S4a Changed Files"; its build/test output hashes are those captured for the S4a command executions reported in the "Slice S4a" section.

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
| S3 - Closure orchestration consolidation | Implemented + remediated + re-verified (remediation commit `579347b`; prior verification `fail` on `200cdaa`) | **PASS_WITH_WARNINGS** - 0 blockers, 0 critical findings, 4/4 requirements and 8/8 scenarios compliant; `CRITICAL-S3-01` and `S3-02..S3-05` closed, `S3-06`/`S3-07` registered for S4/S5; see "Slice S3" |
| S4a - Closure DTO boundary | Implemented + verified (`829778c`) | **PASS_WITH_WARNINGS** - 0 blockers, 0 critical findings; REQ-ADB-01 complete and the closure half of REQ-ADB-04 compliant (4/4 in-scope scenarios); REQ-ADB-02/03 and the drawer half of REQ-ADB-04 pending S4b; see "Slice S4a" |
| S4b - Drawer DTO boundary | Implemented + verified (`65e038a`) | **PASS_WITH_WARNINGS** - 0 blockers, 0 critical findings; REQ-ADB-02, REQ-ADB-03 and the drawer half of REQ-ADB-04 compliant (5/5 in-scope scenarios); `api-dto-boundary` spec-wide now 4/4 and 7/7; see "Slice S4b" |
| S5a - CancellationToken propagation | Not implemented | Pending - all Phase 5a tasks unchecked |
| S5b - EF tuning + guards/naming/comments | Not implemented | Pending - all Phase 5b tasks unchecked |
| S5c - J findings (H-05/H-06/H-08/H-14) | Not implemented | Pending - all Phase 5c tasks unchecked |

## Slice S2 — Error Contract + Dead Fields

**Verdict: PASS_WITH_WARNINGS** - 4/4 requirements and 9/9 scenarios compliant; 0 blockers, 0 critical findings; 8 non-blocking residual evidence/bookkeeping gaps (`RESIDUAL-S2-01`..`RESIDUAL-S2-08`).

**Verified revision**: `77b2d16` (S2 remediation; the `HEAD` at the S2 verification; `HEAD` has since advanced to `579347b`). **Prior S2 verification**: failed on `fe1b60b`.
**Slice delta**: `c6c767f` -> `77b2d16` (S2 = `fe1b60b` original + `77b2d16` remediation): 14 code/test files plus 2 documentation files.

**Artifact-trail note**: at the start of this re-verification, `verify-report.md` at `HEAD` contained **no** Slice S2 section. `git log --all -- <path>` returns only `00adc45` and `b36f8e6`, and the file at `HEAD` was the S1-only report. The S2 findings re-checked below are therefore the set supplied by the orchestrator's re-verification brief for `fe1b60b` (CRITICAL-01, WARNING-01..WARNING-05, bookkeeping). This is recorded because the repository artifact trail did not contain the prior S2 verdict text.
**Envelope note**: this section was written while the file's machine-readable envelope was still the admitted **S1** envelope (3 requirements / 8 scenarios, evidence at `c6c767f`); that envelope is now preserved verbatim under "S1 Admitted Envelope (preserved)" and the head envelope has been re-pointed to the S3 slice at `579347b` by the S3 re-verification. The S2 verdict and its own fresh evidence live in this section. The change-level envelope stays deferred until S4a-S5c land.

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

## Slice S3 — Closure Orchestration Consolidation (re-verified)

**Verdict: PASS_WITH_WARNINGS** — 0 blockers, **0 critical findings**. The delta spec `closure-orchestration-consolidation` carries **4 requirements / 8 scenarios**; **8/8 scenarios compliant**, 0 UNTESTED, 0 FAILING. Two findings remain open by explicit design and are registered for S4/S5: `S3-06` (class size, `WARNING-07` enlarged) and `S3-07` (legacy divergent entry point). The residual warnings are non-blocking and none of them touches the scenarios: `RESIDUAL-S3-01` (stale test narrative), `RESIDUAL-S3-08` (post-commit comment on the ambient branch), `RESIDUAL-S3-12` (McCabe-table arithmetic), `RESIDUAL-S3-13` (drawer enlistment proven by inspection), plus the unchanged `RESIDUAL-S3-07` and the carried `WARNING-04`/`WARNING-07`; prior `RESIDUAL-S3-02`..`-06`, `-10` and `-11` are closed.

**Verified revision**: `579347b` (S3 remediation, `fix(8.140): remediacion S3 (tx Serializable + McCabe + asserts + 409)`; current `HEAD`). Working tree clean at verification time (`git status --short` empty; `git diff --stat` empty): no production, test, or documentation file is modified against `579347b`.
**Prior S3 verdict**: **FAIL** on `200cdaa` — 1 CRITICAL (`CRITICAL-S3-01`: no transaction at all surrounded the closure run), a defective McCabe evidence table, four tautological assertions, no covering test for the real `exchangeRate <= 0` guard, and the `DbUpdateException` -> 409 mapping dropped from both close controllers. This re-verification supersedes that verdict.
**Slice deltas**: original S3 `77b2d16` -> `200cdaa` (27 files, `+632 / -509`); **S3 remediation `200cdaa` -> `579347b`** — 11 files, `+850 / -123`: 8 production/test files (`DailyClosureService.cs`, `DailyClosureController.cs`, `ShiftsController.cs`, `DailyClosureTransactionTests.cs`, `DailyClosureControllerTests.cs`, `CloseShiftResolverClassificationTests.cs`, `SecurityHardeningSprint2Tests.cs`, `ResidualRemediationLote26Tests.cs`) plus `docs/reporte.txt`, `apply-progress.md` and this report.
**Spec under verification**: `openspec/changes/legacy-debt-cleanup/specs/closure-orchestration-consolidation/spec.md` — **4 requirements / 8 scenarios** counted from the `### Requirement:` and `#### Scenario:` headings (REQ-COC-01..04, two scenarios each).
**Prior slices**: S1 and S2 are `pass_with_warnings` (sections above, unchanged). The change-level verdict remains **pending** for S4a-S5c.
**Envelope note**: the head YAML envelope described **this S3 slice** (`requirements: 4/4`, `scenarios: 8/8`) with evidence at `579347b` until the S4a verification re-pointed the head to `829778c`; the S3 envelope is now preserved verbatim under "S3 Admitted Envelope (preserved)" and `evidence_revision` covers the eight S3 production/test files listed under "S3 Changed Files". The former admitted S1 envelope is preserved verbatim under "S1 Admitted Envelope (preserved)". The change-level envelope stays deferred until S4b-S5c land.

### S3 Re-executed Evidence (verbatim)

Every command below was re-executed independently on `579347b` after the working tree was confirmed clean. `stderr` was merged into the captured stream (`*>` redirection). No result was taken from `apply-progress.md`.

**1. Build** - `dotnet build CommandCenter.slnx -c Release` - exit `0` - matches the claim (0/0)

```text
Compilación correcta.
    0 Advertencia(s)
    0 Errores

Tiempo transcurrido 00:00:04.54
```

captured-output hash: `sha256:df799fe601a812014f586cec26862365b16d8d60fa8a2f761631f2f2cf6b7f87`

**2. Backend tests (full)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` - exit `0` - matches the claim (1180/1180)

```text
Correctas! - Con error:     0, Superado:  1180, Omitido:     0, Total:  1180, Duración: 13 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:fbe42f41f6f4ccb6055708f82c25805131db047bfafddbe9dea3409d43bfc389`

The S3 remediation raised the suite 1175 -> 1180 (+2 `DailyClosureTransactionTests`, +2 409 tests, +1 real rate-guard test; one duplicate/superseded controller assertion consolidated). No `[Fact]` was deleted in the remediation (verified by diffing every touched test file).

**3. Transaction-test filter (new class)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~DailyClosureTransactionTests"` - exit `0` - matches the claim (2/2)

```text
Correctas! - Con error:     0, Superado:     2, Omitido:     0, Total:     2, Duración: 3 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:38eb0c82f32b89a664c95167109e3307caad22d07dbba341aaf35a4a5df899d6`

**4. S3 focused filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"` - exit `0` - matches the claim (82/82)

```text
Correctas! - Con error:     0, Superado:    82, Omitido:     0, Total:    82, Duración: 3 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:a1b903f2d30997b1581bd3a0e44b4858a3f7358a5da84158f09662eb160a3acc`

**5. Frontend tests** - `npm test` (Web.Frontend) - exit `0` on the **first** execution - matches the claim (271/271)

```text
ℹ tests 271
ℹ suites 58
ℹ pass 271
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
```

captured-output hash: `sha256:bbdb0586f5a484ff44f6b78dd59e7b910597fe4055aec23d7ceb1bca51362480`

Stability note: the prior `RESIDUAL-S3-10` V8 `Zone Allocation failed - process out of memory` failure did **not** reproduce; the first execution was green (271/271), so no retry was needed.

**6. Frontend lint** - `npm run lint` (Web.Frontend, oxlint) - exit `0`, no findings - matches the claim

```text
> web-frontend@0.0.0 lint
> oxlint
```

captured-output hash: `sha256:1472f392035e28478ac827e6e5301e8adde36ba5a0a27bc61cdf6e42453bc7d3`

**7. Coverage gate** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` (1180/1180, exit `0`), then `python scripts/check-coverage.py CommandCenter.Tests/TestResults/b7b93ec6-3995-417f-bb3a-e82d3b9e50be/coverage.cobertura.xml` - exit `0`

```text
Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8364 min=0.7000 gap_a_70%=0.0000 [OK]
  Sales.Module       rate=0.9006 min=0.8000 gap_a_70%=0.0000 [OK]
  Inventory.Module   rate=0.8251 min=0.7200 gap_a_70%=0.0000 [OK]
```

The gate reproduces the remediation claim **exactly** (Core 0.8364 / Sales.Module 0.9006 / Inventory.Module 0.8251) and all three `tasks.md` thresholds pass (Core >= 0.70, Sales.Module >= 0.80, Inventory.Module >= 0.72). `Sales.Module`, the layer S3 refactors, sits at 0.9006 vs 0.8934 on the prior S3 revision.

**8. Independent discrimination mutation (verifier-owned; reverted)** - exit `1` under mutation - the only command below that does **not** run on the unmodified revision

The implementer's discrimination claim was re-derived independently rather than accepted. A **temporary, verifier-owned mutation** was applied to `Sales.Module/Services/DailyClosureService.cs:213-226`: `OpenSerializableTransactionAsync` was forced to return `null` on SQLite by extending the provider check to `|| ProviderName?.Contains("Sqlite", …) == true`. The two `DailyClosureTransactionTests` were then re-run against the mutated tree:

```text
Con error! - Con error:     2, Superado:     0, Omitido:     0, Total:     2, Duración: 3 s - CommandCenter.Tests.dll (net10.0)
  Assert.True() Failure
Expected: True
Actual:   False
  Assert.Equal() Failure: Values differ
Expected: 0
Actual:   1
     at CommandCenter.Tests.Unit.DailyClosureTransactionTests.CreateClosureFromCommandAsync_WhenRolloverFails_RollsBackThePersistedClosure() in …\CommandCenter.Tests\Unit\DailyClosureTransactionTests.cs:line 114
```

This reproduces the implementer's recorded failure modes exactly (`Assert.True` at `DailyClosureTransactionTests.cs:82` -> `Expected: True / Actual: False`; `Assert.Equal(0, persistedClosures)` at `:114` -> `Expected: 0 / Actual: 1`), i.e. **without the transaction the persisted closure survives the rollover failure**. The mutation was then reverted from a byte-exact backup: the restored file hashes to `5C6EA3FC723614A0781F215E0E7D96D9413930B029CE25E0737513CEC3CB449A`, identical to the pre-mutation hash, and `git status --short` / `git diff --stat` are both empty. The mutation capture is recorded at `sha256:912838e204ddd68f7945f94dc469ec4cb5791729f06e6c6e2d40ddc9ce2e86c2`.

**Hash definition (this section)**: `sha256` is the SHA-256 over the captured combined stdout+stderr (`*>` redirection), normalized to UTF-8 without BOM, `CRLF` -> `LF`, trailing newlines trimmed.

**Environment note**: `TEST_POSTGRES_CONNECTION` is unset, so the Postgres-gated classes (`DailyClosureRetryIntegrationTests`, `DailyClosureFlowIntegrationTests`) early-return as vacuous passes. The transaction is now covered locally by the SQLite `DailyClosureTransactionTests` instead of by a skipped Postgres test.

### S3 Finding-to-Closure Matrix

Each finding registered against the prior S3 verdict is re-checked below with runtime evidence from `579347b`.

| Finding | Prior severity | Closure | Fresh evidence |
|---------|----------------|---------|----------------|
| **CRITICAL-S3-01** — no `Serializable` transaction around the closure run | Critical / blocker | **CLOSED** | `DailyClosureService.cs:204-211` (`CreateClosureFromCommandAsync`) keeps an ambient-transaction short-circuit and otherwise wraps the run in `_context.Database.CreateExecutionStrategy().ExecuteAsync(...)`; `ExecuteClosureCommandAsync` (`:228-301`) opens `OpenSerializableTransactionAsync` (`:213-226`) -> `BeginTransactionAsync(IsolationLevel.Serializable, ct)` at `:225` **before** the totals read (`:237`), wraps assembly, `PersistClosureCoreAsync` (`:267`) and `_cashDrawerService.RolloverSessionAfterClosureAsync` (`:268`), commits at `:270-273` (after the rollover) and rolls back in the `catch` (`:286-293`), disposing in `finally` (`:294-300`). Receipt writing stays after the commit (`:275-276`). The two new `DailyClosureTransactionTests` are **discriminating** (see the mutation evidence above): both fail when the transaction is forced absent. 2/2 green unmutated. |
| **S3-02** — `DailyClosureController.CreateClosure` McCabe not genuinely < 10 | Warning | **CLOSED** | `CreateClosure` (`DailyClosureController.cs:50-64`) now has exactly two decision points — `if (User.IsInRole("Driver"))` (`:52`) and `if (validationError is not null)` (`:58`) — i.e. **McCabe 3** under the convention stated in `apply-progress.md` (points = `if`/`foreach`/`catch`/`&&`/`\|\|`/`??`/`?:`; `McCabe = points + 1`). The validation was extracted to `ValidateClosureRequest` (`:110-126`), which recounts to 4. Both are < 10. |
| **S3-03** — former tautological assertions | Warning | **CLOSED** | The four vacuous `Verify(..., Times.Never)` sites are gone. `SecurityHardeningSprint2Tests.cs:219-276` now seeds the InMemory `PaymentMethods` table, builds the **real** `DailyClosureService` via `DailyClosureTestHelper.CreateService(salesDb, rateProvider, cashDrawer)` with the `cashDrawer` **injected**, drives the real unknown-method guard, and asserts `Assert.Contains("999", problemDetails.Detail)` + `Assert.Empty(await salesDb.DailyClosures.AsNoTracking().ToListAsync())` + a now-reachable `cashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(...), Times.Never)`. `ResidualRemediationLote26Tests.cs:253-346`: same shape for both duplicate-id tests, and the legacy-entry-point verification `mockClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Never)` was **deleted**. Each test now fails on a real regression (persisted rows or a reached rollover), not only on an unreachable mock. |
| **S3-04** — no test drives the real `exchangeRate <= 0` guard | Warning | **CLOSED** | `CloseShiftResolverClassificationTests.cs:511-545` (`CreateClosureFromCommandAsync_RealService_WhenEffectiveRateIsNotPositive_ThrowsBeforePersisting`) mocks only `ITodayExchangeRateProvider` (`GetEffectiveTodayRateAsync -> 0m`), runs the **production** service over an InMemory `SalesDbContext`, and asserts `InvalidOperationException` containing `"tasa BCV"`, `Assert.Empty(DailyClosures)` and rollover `Times.Never`. The exercised guard is the production one at `DailyClosureService.cs:197-202`; removing it makes the test red. |
| **S3-05** — `DbUpdateException` -> 409 dropped | Warning | **CLOSED** | `DailyClosureController.cs:93-96` and `ShiftsController.cs:97-100` both `catch (DbUpdateException)` -> `this.ApiConflict(...)`, which returns a `ConflictObjectResult` wrapping a genuine `ProblemDetails` with `Status = 409` (`ApiProblemResults.cs:15-16`). Covered by `DailyClosureControllerTests.CreateClosure_WhenServiceThrowsDbUpdateException_Returns409ProblemDetails` and `CloseShift_WhenServiceThrowsDbUpdateException_Returns409ProblemDetails`, each asserting `ConflictObjectResult` + `StatusCodes.Status409Conflict` + `ProblemDetails` + `Status == 409`. Both pass in the 82/82 closure filter. |
| **S3-06** — `DailyClosureService.cs` exceeds the 300-500 line ceiling | Warning (registered, not fixed) | **REGISTERED for S4/S5 — confirmed** | Verifier-measured 636 lines (`(Get-Content …).Count`), matching the claim. Registered at `apply-progress.md:445` and `docs/reporte.txt:9363` (ANEXO "SLICE S3 REMEDIACION", `D1`). Not silently fixed; `WARNING-07` remains enlarged (502 at S2 -> 586 at `200cdaa` -> 636). |
| **S3-07** — legacy `ExecuteClosureCoreAsync` keeps a divergent, transaction-free copy of the closure rules | Warning (registered, not fixed) | **REGISTERED for S4/S5 — confirmed** | Registered at `apply-progress.md:446` and `docs/reporte.txt:9367` (`D2`) with a recommended disposition (delete with its test references, or reduce to a private adapter delegating to the command path). Unchanged by the remediation. |
| **Bookkeeping** — false "Serializable tx" claims in `apply-progress.md` and ANEXO 8.140 `B3` | Warning / evidence trail | **CLOSED** | `apply-progress.md:279` (task 3.4) now carries an explicit `**CORRECTION (\`lcs-s3-remediation\`)**: this entry originally claimed "Serializable tx". Commit \`200cdaa\` opened **no transaction at all** …`. The ANEXO 8.140 `B3` block is annotated at `docs/reporte.txt:9229-9231` ("`CORRECCION: la afirmacion "Serializable tx" era FALSA en el commit 200cdaa`"). The McCabe table in `apply-progress.md` is marked `**SUPERSEDED**` with the reason recorded, and task 3.11 points to the corrected recount. |

**Closure summary**: 6 of the 7 briefed items closed with fresh runtime evidence; `S3-06` and `S3-07` are **confirmed as registered pending items** for S4/S5 (not fixed, not silently). 0 blockers, 0 critical findings remain against S3.

### S3 Requirement Evidence Matrix (revised)

| Requirement | Scenario | Covering test / evidence | Result |
|-------------|----------|--------------------------|--------|
| REQ-COC-01 | Controller delegates the closure run (no persistence in the controller) | `DailyClosureControllerTests.CreateClosure_DelegatesToService_AndPersistsNothingDirectly` asserts `CreateClosureFromCommandAsync` invoked `Times.Once` with the mapped command; `DailyClosureController.cs:50-64` calls `ExecuteCreateClosureAsync` (`:66`) which delegates at `:81`, and the type has no `DbContext` member. The 4 reflection structural tests enforce the absence of `*DbContext` constructor parameters and fields | **COMPLIANT** |
| REQ-COC-01 | Service owns the transaction (rollback on a mid-way failure; no partial closure) | `CreateClosureFromCommandAsync` (`:204-211`) + `ExecuteClosureCommandAsync` (`:228-301`) + `OpenSerializableTransactionAsync` (`:213-226`): `BeginTransactionAsync(IsolationLevel.Serializable)` at `:225` precedes the totals read at `:237` and covers persist + rollover; `CommitAsync` at `:272`; `RollbackAsync` at `:290`. Covering tests: `DailyClosureTransactionTests` 2/2 (SQLite relational, transaction observed live **inside** the rollover callback at `:73-74`, `Assert.Equal(IsolationLevel.Serializable, isolationLevelDuringRollover)` at `:83`; row count 0 after a rollover failure at `:114`). **Discriminating**: independently confirmed by the verifier-owned mutation (section above) | **COMPLIANT** |
| REQ-COC-02 | No `DbContext` in touched controllers (constructors and fields) | `DailyClosureControllerTests`: `DailyClosureController_HasNoDbContextInConstructor`, `ShiftsController_HasNoDbContextInConstructor`, `DailyClosureController_HasNoDbContextFields`, `ShiftsController_HasNoDbContextFields` — all green in the 82/82 filter. Constructors take only `IDailyClosureService` + `ICurrentUserService` (`DailyClosureController.cs:23-29`, `ShiftsController.cs:24-30`), and no `Microsoft.EntityFrameworkCore` / `*.Data` using remains in either file | **COMPLIANT** |
| REQ-COC-02 | Authorization still gates the closure (403, nothing persisted) | `[Authorize]` on both controllers; Driver guard returns `ApiForbidden` 403 (`DailyClosureController.cs:52-55`, `ShiftsController.cs:36-39`); `[RequireSecurityStampValidation]` retained; backdating (future / >24 h) stays in `ResolveClosureDate` (`DailyClosureController.cs:137-168`) behind `isAdmin`; the `GetReportById` ownership check is retained (`ShiftsController.cs:121-139`). Covered by S2's `ErrorContractTests` Driver tests (still green) | **COMPLIANT** |
| REQ-COC-03 | `CreateClosure` is under the ceiling | Verifier recount: `DailyClosureController.CreateClosure` = 2 points -> **McCabe 3** (`DailyClosureController.cs:52`, `:58`). The convention is now documented in `apply-progress.md`, closing the prior "metric undocumented" objection. No analyzer metric exists in the repo, so this remains a documented manual count | **COMPLIANT** |
| REQ-COC-03 | Extracted methods are under the ceiling | Verifier recount of all fifteen listed methods: maximum **7** (`MergeMissingMethodsWithReport`), everything < 10 (table below). One arithmetic slip remains in the apply-progress table (`BuildDeclaredDetails` 6, not 5) — still far below the ceiling (RESIDUAL-S3-12) | **COMPLIANT** |
| REQ-COC-04 | Same inputs, same stored closure (amounts, status, response) | The five real-path tests in `CloseShiftResolverClassificationTests.cs` execute the production service against a real InMemory `SalesDbContext` and assert persisted `ClosureDetail.ActualAmountBsS` / `DailyClosure.ExchangeRate`; the full suite is 1180/1180 and `Sales.Module` coverage is 0.9006. `GetReportById` reads the persisted snapshot `closure.ExchangeRate` (`ShiftsController.cs:161`) | **COMPLIANT** (happy path) |
| REQ-COC-04 | Preview rejection is not regressed (`dateUtc` omitted/default -> 400, no totals) | `DailyClosureControllerTests.GetExpectedTotals_WhenDefaultDate_Returns400ProblemDetails` asserts `ObjectResult` + `StatusCode == 400` + `ProblemDetails`; the source still returns `Problem(...)` (`DailyClosureController.cs:35-42`), deliberately not routed through `ApiBadRequest` (AD-9/REQ-AEC-01) | **COMPLIANT** |

**Compliance summary**: **8/8 scenarios compliant**, 0 UNTESTED, 0 FAILING. Requirement level: REQ-COC-01, REQ-COC-02, REQ-COC-03 and REQ-COC-04 all satisfied. The only previously failing scenario (REQ-COC-01 "service owns the transaction") is now covered by a discriminating test.

### REQ-COC-03 — McCabe Recount (verifier-owned)

**Counting convention** (as stated in `apply-progress.md`, and the one applied here): count every branch-introducing construct (`if`, `foreach`, `while`, `for`, `case`, `catch`, `&&`, `||`, `??`, `?:`); `McCabe = points + 1`.

| Method | apply-progress claim | Verifier recount (points -> McCabe) | < 10? |
|--------|----------------------|-------------------------------------|-------|
| `DailyClosureController.CreateClosure` | 3 | `if(Driver)` 1 + `if(validationError)` 1 = 2 -> **3** | yes |
| `DailyClosureController.ValidateClosureRequest` | 4 | `if` 1 + `\|\|` 1 + `?:` 1 = 3 -> **4** | yes |
| `DailyClosureController.ExecuteCreateClosureAsync` | 4 | `catch` x3 = 3 -> **4** | yes |
| `DailyClosureController.ResolveUserId` | 4 | `??` x2 + `?:` 1 = 3 -> **4** | yes |
| `DailyClosureController.ResolveClosureDate` | 6 | 4x`if` + `?:` = 5 -> **6** | yes |
| `DailyClosureService.CreateClosureFromCommandAsync` | 3 | `if(rate<=0)` 1 + `if(CurrentTransaction)` 1 = 2 -> **3** | yes |
| `DailyClosureService.OpenSerializableTransactionAsync` | 3 | `if(CurrentTransaction)` 1 + `if(ProviderName)` 1 = 2 -> **3** | yes |
| `DailyClosureService.ExecuteClosureCommandAsync` | 5 | `catch` 1 + `if(transaction is not null)` x3 = 4 -> **5** | yes |
| `DailyClosureService.BuildDeclaredDetails` | 5 | `foreach` 1 + 4x`?:` (incl. the **nested** `?:` at `:334`) = 5 -> **6** | yes (claim off by one) |
| `DailyClosureService.ResolveUserDetailsAsync` | 3 | `if(TryParse)` 1 + `if(user!=null)` 1 = 2 -> **3** | yes |
| `DailyClosureService.PersistClosureCoreAsync` | 1 | — -> **1** | yes |
| `DailyClosureService.ValidateDeclaredMethods` | 2 | `if(count>0)` 1 -> **2** | yes |
| `DailyClosureService.MergeMissingMethodsIntoClosure` | 5 | `foreach` + `if(!Contains)` + `&&` + `?:` = 4 -> **5** | yes |
| `DailyClosureService.MergeMissingMethodsWithReport` | 7 | `foreach` + `if(!Contains)` + `&&` + 3x`?:` = 6 -> **7** | yes |
| `DailyClosureService.RecalculateTotals` | 3 | `foreach` + `if(<0)` = 2 -> **3** | yes |

Every listed method is genuinely below 10; the maximum is 7. Prior `RESIDUAL-S3-02` (defective table: `CreateClosure` counted 11 while claimed 7, `CreateClosureFromCommandAsync` double-counted) is **closed** — the extraction reduced `CreateClosure` to 3 and the recount no longer double-counts. The single remaining arithmetic defect (`BuildDeclaredDetails`, nested ternary omitted) is recorded as RESIDUAL-S3-12; it does not affect any requirement.

### Discrimination Review

**The two `DailyClosureTransactionTests` are discriminating — independently confirmed by mutation.** The SQLite relational context is required because EF InMemory cannot `BeginTransaction`; the drawer is mocked, so the callback observes `context.Database.CurrentTransaction` at the exact instant the transaction must be open. Forcing `OpenSerializableTransactionAsync` to return `null` on SQLite turned both tests red with `Expected: True / Actual: False` (`:82`) and `Expected: 0 / Actual: 1` (`:114`). The mutation was reverted byte-identically.

**The repaired S3-03 sites are discriminating.** They now execute the production service over a real context and assert observable state (`Assert.Empty(DailyClosures)`, the `"999"` detail, a reachable rollover `Times.Never`) instead of only verifying a dependency the code under test could not reach. The deleted assertion targeted the legacy `CreateClosureAsync(DailyClosure)` entry point, which no controller calls.

**The S3-04 test is discriminating.** Removing the `exchangeRate <= 0` guard at `DailyClosureService.cs:197-202` makes the production service persist a closure; the test asserts `ThrowsAsync<InvalidOperationException>` and an empty closure set, so it cannot pass without the guard.

**The S3-05 tests are discriminating.** They assert `ConflictObjectResult` + `ProblemDetails.Status == 409`; reverting either controller to a non-409 path fails them.

**The 4 structural controller tests remain discriminating.** They assert the actual reflected `Type`, so re-introducing a `DbContext` constructor parameter or field turns them red.

### Scope Check

The `200cdaa` -> `579347b` remediation diff contains only S3 targets plus bookkeeping: `DailyClosureService.cs`, `DailyClosureController.cs`, `ShiftsController.cs`, the four re-pointed test files, the new `DailyClosureTransactionTests.cs`, `docs/reporte.txt`, `apply-progress.md` and this report. No S4+ creep: `CashDrawerService.cs` (S4b/S5b), `AuthController.cs` (S5c), the WPF view models / `MainWindow.xaml.cs` (S5c), `RegisterPage.jsx` (S5c) and the DTO folders (S4a/S4b) are untouched; `tasks.md` Phase 4a-5c remain unchecked.

**Atomicity design note (inspection)**: the outer `Serializable` transaction and the rollover share the same scoped `SalesDbContext` in DI, and `CashDrawerService.CloseSessionAsync` explicitly detects an ambient transaction (`CashDrawerService.cs:131-133`: `ownsTransaction = !isInMemory && ambientTransaction == null`) so it enlists in the outer transaction instead of opening a nested one. The closure persist, the session close and the session reopen therefore commit or roll back together, which is the cross-DB atomicity the pre-S3 controller provided. Because the SQLite test mocks `ICashDrawerService`, this enlistment is proven by source inspection plus the `CurrentTransaction`-non-null assertion at rollover time, not by a test that writes drawer rows through the real drawer service (RESIDUAL-S3-13).

**Retry semantics**: opening the transaction **inside** the `CreateExecutionStrategy()` delegate (rather than around it) is the correct Npgsql pattern; each retry re-enters `ExecuteClosureCommandAsync` and opens a fresh transaction, disposing it in `finally`. The prior `CRITICAL-S3-01` concern about "a retrying strategy over a non-transactional, non-idempotent delegate" is therefore resolved.

### S3 Residual Warnings (non-blocking for S3)

- **RESIDUAL-S3-01 (stale test narrative)** — `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs:13-19` still attributes the `Serializable` transaction to "El flujo de produccion (DailyClosureController/ShiftsController)". After the remediation the transaction is opened by `DailyClosureService` (inside the execution strategy), not by the controllers; the substantive claim (a `Serializable` transaction inside an external strategy on the production close flow) now holds again, so this is a documentation-attribution nuance, not a defect. Postgres-gated, skipped locally.
- **RESIDUAL-S3-08 (post-commit comment, ambient branch)** — `DailyClosureService.cs:275` states receipts are written "DESPUÉS del commit, fuera de la transacción Serializable". This is exact for the service-owned transaction; in the ambient-transaction branch (`:204-207`) the commit belongs to the caller and this service writes receipts without committing. No production caller opens an ambient transaction today, so the exposure is latent.
- **RESIDUAL-S3-12 (evidence arithmetic)** — the apply-progress McCabe table lists `BuildDeclaredDetails` as 4 points -> 5; the verifier recount is 5 points -> 6, because the nested `?:` at `DailyClosureService.cs:334` was not counted. Still well below 10; evidence-quality only.
- **RESIDUAL-S3-13 (enlistment proven by inspection)** — `DailyClosureTransactionTests` mocks `ICashDrawerService`, so the fact that the real drawer's `CloseSessionAsync`/`OpenSessionAsync` writes enlist in the outer transaction is established by reading `CashDrawerService.cs:131-133` rather than by a relational end-to-end test. Recommend a Postgres/SQLite test with the real `CashDrawerService` when S4b touches the drawer.
- **WARNING-04 (carry-over, still open)** — the merged undeclared payment-method lines still hardcode `"Balanced"` while `DifferenceBsS` can be non-zero, and still mix units (Bs.S declared vs USD-converted system amount) — now duplicated across `MergeMissingMethodsIntoClosure` (`DailyClosureService.cs:395-418`, hardcoded status implicit) and `MergeMissingMethodsWithReport` (`:420-459`, `"Balanced"` at `:456`). S3/AD-8 extracted the methods but did not correct the semantics. Deferred to S4/S5.
- **WARNING-07 / S3-06 (carry-over, enlarged)** — `DailyClosureService.cs` measures **636** lines (verifier-confirmed), over the 300-500 ceiling in `docs/coding-guidelines-core.md`.
- **RESIDUAL-S3-07 (snapshot read change, unchanged)** — `ShiftsController.GetReportById` still returns the persisted `closure.ExchangeRate` with no today's-rate fallback (`ShiftsController.cs:161`). A persisted closure with `ExchangeRate == 0` reports `0` instead of today's rate. Aligned with the snapshot-immutability invariant; recorded as intended.
- **RESIDUAL-S3-11 (dead scaffolding)** — **CLOSED** for the two named files: no `mockPaymentMethod` / `mockSettings` / `new Mock<IPaymentMethodService>` / `new Mock<ISystemSettingsService>` remains in `SecurityHardeningSprint2Tests.cs` or `ResidualRemediationLote26Tests.cs`.
- **RESIDUAL-S3-10 (test-runner OOM, environmental)** — not reproduced in this re-verification; `npm test` was green on the first execution.
- **RESIDUAL-S3-03 / -04 / -05 / -06 (prior residuals)** — **CLOSED**: the tautological assertions were repaired (S3-03), the real rate guard has a covering test (S3-04), a transaction assertion now exists outside the Postgres-gated skip (S3-05), and the `DbUpdateException` -> 409 mapping was restored (S3-06).
- **RESIDUAL-S3-14 (out-of-slice `Problem` vs `ApiBadRequest`)** — `ShiftsController.CloseShift` still returns `Problem(detail, 400)` for `ArgumentException`/`InvalidOperationException` (`:89-96`) while `DailyClosureController` routes both through `ApiBadRequest` (`:85-92`). Both emit `ProblemDetails` with status 400, so the S3 error contract holds; the two close paths differ only in the `type`/`extensions` richness. Hygiene, not a defect.

### S3 Changed Files (remediation delta `200cdaa` -> `579347b`)

| File | Action | Role in S3 remediation |
|------|--------|-----------------------|
| `Sales.Module/Services/DailyClosureService.cs` | Modified | `OpenSerializableTransactionAsync` + `ExecuteClosureCommandAsync` wrap totals read -> assembly -> persist -> rollover in `Serializable`, with `CommitAsync` after the rollover and `RollbackAsync` on failure (CRITICAL-S3-01) |
| `Backend.API/Controllers/DailyClosureController.cs` | Modified | `ValidateClosureRequest` extraction -> `CreateClosure` McCabe 3 (S3-02); `DbUpdateException` -> `ApiConflict` 409 (S3-05) |
| `Backend.API/Controllers/ShiftsController.cs` | Modified | `DbUpdateException` -> `ApiConflict` 409 (S3-05) |
| `CommandCenter.Tests/Unit/DailyClosureTransactionTests.cs` | Created | 2 discriminating SQLite tests: `Serializable` transaction present during rollover; rollback of the persisted closure on a rollover failure |
| `CommandCenter.Tests/Unit/DailyClosureControllerTests.cs` | Modified | 2x 409 tests (S3-05) |
| `CommandCenter.Tests/Unit/CloseShiftResolverClassificationTests.cs` | Modified | Real-production `exchangeRate <= 0` guard test (S3-04) |
| `CommandCenter.Tests/SecurityHardeningSprint2Tests.cs` | Modified | Real service + injected `cashDrawer`; non-tautological assertions (S3-03) |
| `CommandCenter.Tests/Unit/ResidualRemediationLote26Tests.cs` | Modified | Real service + injected `cashDrawer`; legacy mock verify deleted (S3-03) |
| `docs/reporte.txt` | Modified | ANEXO "SLICE S3 REMEDIACION" + `B3` correction |
| `openspec/changes/legacy-debt-cleanup/apply-progress.md` | Modified | S3 remediation section; task 3.4 / 3.11 corrections; S3-06/S3-07 registered |

The eight production/test files above, in this order, are the input to the head envelope's `evidence_revision`.

### S3 Verdict

**PASS_WITH_WARNINGS** — the S3 remediation at `579347b` closes every finding that set the prior verdict to `fail`. `CRITICAL-S3-01` is genuinely fixed: the closure run is wrapped in an explicit `BeginTransactionAsync(IsolationLevel.Serializable)` that commits after the rollover and rolls back on failure, and the new SQLite tests are discriminating (independently reproduced by the verifier's own mutation). `CreateClosure` now measures McCabe 3 with the counting convention documented; the four tautological assertions were replaced with real-service assertions; the production `exchangeRate <= 0` guard has a covering test; and the `DbUpdateException` -> 409 `ProblemDetails` mapping is restored in both close controllers. 8/8 scenarios and 4/4 requirements of the S3 delta spec are compliant, the build is 0/0, the backend suite is 1180/1180, the closure filter 82/82, the frontend 271/271 with clean lint, and the coverage gate reproduces the claim exactly. The residual items are non-blocking: `S3-06`/`S3-07` and the carried `WARNING-04`/`WARNING-07` are registered for S4/S5, and the remaining residuals are evidence-quality or inspection-based notes (RESIDUAL-S3-08, -12, -13).

## Slice S4a — Closure DTO Boundary

**Verdict: PASS_WITH_WARNINGS** — 0 blockers, **0 critical findings**. The slice delta spec is `api-dto-boundary` (**4 requirements / 7 scenarios**, counted from its `### Requirement:` / `#### Scenario:` headings). S4a owns the closure half: **REQ-ADB-01 complete** (2/2 scenarios) and **REQ-ADB-04 compliant for the closure DTOs** (2/2 scenarios); REQ-ADB-02 (drawer session DTO / cash-advance result) and REQ-ADB-03 (history projected, not hand-built) are drawer-only and belong to the explicitly-not-started S4b, so they are **pending and not claimed**. In-scope: **4/4 scenarios compliant**, 0 UNTESTED, 0 FAILING. Residuals are non-blocking and recorded, not hidden: `S4a-R1` (public concrete entity-returning legacy entry point), `S4a-R2` (class size 645 lines), `RESIDUAL-S4a-01` (parity evidence is serializer-level, not MVC-pipeline), `RESIDUAL-S4a-02` (commit-message item misattribution), `RESIDUAL-S4a-03` (checked task vs. changed-file mismatch) and `SIZE-S4a` (commit exceeds the 400-line review budget).

**Verified revision**: `829778c` (`refactor(8.140): DTOs inmutables de cierre (slice S4a, items 3/15/19/20) - ANEXO 8.140`; current `HEAD`). Working tree clean at verification time (`git status --short` and `git diff --stat HEAD` both empty).
**Slice delta**: `0f5a8c3` -> `829778c` — 18 files, `+643 / -62`: 15 production/test files (2 new DTOs, 6 modified production files, 1 new test file, 6 re-pointed test files) plus `docs/reporte.txt`, `apply-progress.md` and `tasks.md`.

### S4a Re-executed Evidence (verbatim)

Every command below was re-executed independently on `829778c` after the working tree was confirmed clean. `stderr` was merged into the captured stream. No result was taken from `apply-progress.md`.

**1. Build** - `dotnet build CommandCenter.slnx -c Release` - exit `0` - matches the claim (0/0)

```text
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

captured-output hash: `sha256:12942f0e260fa5f766cca6dae5e9dae739767bada4e6ae11e3cd7af5b64fa31d`

**2. Backend tests (full)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` - exit `0` - matches the claim (1185/1185)

```text
Correctas! - Con error:     0, Superado:  1185, Omitido:     0, Total:  1185, Duración: 12 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:e0ebbc71fe54232be778743f7dd9bb118c09bd586c44f07b44f206b31870a0d3`

**3. S4a focused filter (as declared in `tasks.md`)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~Dto"` - exit `0` - matches the claim (71/71)

```text
Correctas! - Con error:     0, Superado:    71, Omitido:     0, Total:    71, Duración: 1 s - CommandCenter.Tests.dll (net10.0)
```

**4. New test class alone** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~ClosureDtoBoundaryTests"` - exit `0` - the five S4a tests execute; `0` skipped, so none is Postgres-gated

```text
Correctas! - Con error:     0, Superado:     5, Omitido:     0, Total:     5, Duración: 229 ms - CommandCenter.Tests.dll (net10.0)
```

**5. Frontend tests** - `npm test` (Web.Frontend) - exit `0` on the first execution - matches the claim (271/271)

```text
ℹ tests 271
ℹ pass 271
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
```

captured-output hash: `sha256:03b6525ddf4adc1ecbe25e57a4a91d147943fb4e570b6980ecb972f8bec43daa`

**6. Frontend lint** - `npm run lint` (Web.Frontend, oxlint) - exit `0`, no findings - matches the claim

```text
> web-frontend@0.0.0 lint
> oxlint
```

captured-output hash: `sha256:1472f392035e28478ac827e6e5301e8adde36ba5a0a27bc61cdf6e42453bc7d3`

**7. Coverage gate** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` (1185/1185, exit `0`), then `python scripts/check-coverage.py CommandCenter.Tests/TestResults/4c0b6220-4a4a-452b-8d82-a7d33a990510/coverage.cobertura.xml` - exit `0`

```text
Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8364 min=0.7000 gap_a_70%=0.0000 [OK]
  Sales.Module       rate=0.9011 min=0.8000 gap_a_70%=0.0000 [OK]
  Inventory.Module   rate=0.8251 min=0.7200 gap_a_70%=0.0000 [OK]
```

The gate reproduces the S4a claim **exactly** (Core 0.8364 / Sales.Module 0.9011 / Inventory.Module 0.8251) and all three `tasks.md` thresholds pass (Core >= 0.70, Sales.Module >= 0.80, Inventory.Module >= 0.72). `Sales.Module`, the layer S4a changes, sits at 0.9011 vs 0.9006 at S3.

**Environment note**: `TEST_POSTGRES_CONNECTION` is unset, so the Postgres-gated classes continue to early-return as vacuous passes. No S4a test is Postgres-gated: the five `ClosureDtoBoundaryTests` are pure reflection/serialization tests and all five executed (`Omitido: 0`).

### S4a Boundary Evidence (structural + grep)

| Check | Method | Result |
|-------|--------|--------|
| No EF entity import or type on the touched boundary | `grep` for `Sales.Module.Entities` and for bare `DailyClosure` / `ClosureDetail` across `DailyClosureController.cs`, `IDailyClosureService.cs`, `ShiftsController.cs` and both new DTOs | **0 matches** |
| Service interface returns DTOs | `IDailyClosureService.cs:37-38` declare `Task<DailyClosureResponseDto?>`; `:43` takes `DailyClosureResponseDto`; the entity `Task<DailyClosure> CreateClosureAsync(DailyClosure)` member is gone from the interface and the file's only `Sales.Module` using is `Sales.Module.DTOs` | **CONFIRMED** |
| Controller returns the DTO | `DailyClosureController.GetClosure` is `Task<ActionResult<DailyClosureResponseDto>>` (`:130-135`); the file's only `Sales.Module` using is `Sales.Module.DTOs` (`:4`) | **CONFIRMED** |
| Entity access confined to the projection edge | `DailyClosureService.LoadClosureEntityAsync` (private) plus `ShiftReportMapper.MapClosure`/`MapDetail` are the only entity touch points; `ShiftsController.cs:145` consumes `closure.Details` as DTOs | **CONFIRMED** |
| DTO immutability | `DailyClosureResponseDto.cs` (12 lines) and `ClosureDetailResponseDto.cs` (10 lines) are `public sealed record` with positional parameters only; **0** `set;` occurrences in either file | **CONFIRMED** |
| Golden-JSON options mirror production | `ClosureDtoBoundaryTests.ApiJsonOptions` = CamelCase + `ReferenceHandler.IgnoreCycles`; production `ServiceCollectionExtensions.cs:157-158` sets exactly those two options | **CONFIRMED** |

The structural test `ClosureServiceAndControllerSignatures_DoNotExposeSalesModuleEntities` reflects `IDailyClosureService` (every public method, return + parameters, generics and arrays flattened) and `DailyClosureController` public instance methods, and additionally pins the generic argument of `GetClosureAsync`/`GetLatestClosureAsync` to `DailyClosureResponseDto`. It does **not** reflect the concrete `DailyClosureService` class — see `S4a-R1`.

### S4a Field-Parity Assessment

**Golden payload (touched endpoint `GET /api/dailyclosure/{id}`)**

- `Assert.Equal` against the full-body literal pins the DTO output byte-for-byte; the test also serializes the entity with the production options and compares every client-bound member of both bodies.
- All eight closure scalars survive with the same JSON name and value: `id`, `closureDate`, `userId`, `exchangeRate`, `totalExpectedBsS`, `totalActualBsS`, `totalDifferenceBsS`, `observation`.
- All six detail members survive: `id`, `dailyClosureId`, `paymentMethodId`, `paymentMethodName`, `expectedAmountBsS`, `actualAmountBsS`, `differenceBsS`.
- The EF navigation members `dailyClosure` and `paymentMethod` (previously serialized as `null`) are absent from the DTO body and asserted absent (`ContainsKey` false, plus a recursive property-name scan).

**Consumer search (bindings to the removed members)**

- WPF `ClosureDetailDto` (`Desktop.Client.Core/Services/IDailyClosureClientService.cs:26-34`) declares only `Id`, `PaymentMethodId`, `PaymentMethodName`, `ExpectedAmountBsS`, `ActualAmountBsS`, `DifferenceBsS` — it binds **no** navigation member, so their removal cannot break a binding.
- WPF `DailyClosureDto` (`:14-24`) declares `Id`, `ClosureDate`, `UserId`, `TotalExpectedBsS`, `TotalActualBsS`, `TotalDifferenceBsS`, `Observation`, `Details`.
- The WPF closure client calls only `GET api/dailyclosure/expected-totals` and `POST api/dailyclosure` (`DailyClosureClientService.cs:20, 27`); it never calls `GET api/dailyclosure/{id}`. Its `CreateClosureAsync` result is discarded by the view model (`DailyClosureViewModel.cs:346`), so the POST shape is not bound at runtime either — this independently confirms deviation D2.
- **Zero** references to the `dailyclosure` endpoint anywhere in `Web.Frontend/src` (`*.js` / `*.jsx` search: count `0`).
- The DTO's `exchangeRate` member has no counterpart in the WPF `DailyClosureDto` on the GET path; it is additive, and `System.Text.Json` ignores unknown members by default, so nothing breaks.

**Assessment**: field parity holds for every member any client binds, and the only removed members are EF navigation properties that no consumer reads. Caveat `RESIDUAL-S4a-01`: the "before" body is a re-serialization of the entity under the production options, not a captured pre-S4a HTTP response, and no test drives the MVC pipeline for this endpoint; parity is established at the serializer-contract level.

### S4a Requirement Evidence Matrix

| Requirement | Scenario | Covering test / evidence | Result |
|-------------|----------|--------------------------|--------|
| REQ-ADB-01 | Closure response carries no EF entity | `ClosureDto_GoldenJson_PreservesLegacyBoundFields_AndDropsEntityNavigationMembers` (golden literal + nav absence) + `GetClosure_ReturnsDeclaredDto_AndPreservesNotFoundSemantics` (200 DTO, 404 preserved) + the grep/reflection boundary evidence above | **COMPLIANT** |
| REQ-ADB-01 | Service returns a DTO | `ClosureServiceAndControllerSignatures_DoNotExposeSalesModuleEntities` asserts the `GetClosureAsync`/`GetLatestClosureAsync` generic argument equals `DailyClosureResponseDto`; `IDailyClosureService.cs:37-38` | **COMPLIANT** |
| REQ-ADB-04 | DTOs expose no public setter | `ClosureDtos_ExposeNoPublicSetter` — every reflected public setter must carry the `IsExternalInit` modreq; `ClosureResponseDtos_AreDeclaredInSalesModuleDtosNamespace` also pins `sealed` | **COMPLIANT** (closure DTOs) |
| REQ-ADB-04 | Existing client fields survive the swap | `ClosureDto_GoldenJson_...` per-member comparison + consumer search (no binding to the removed navs) | **COMPLIANT** (closure DTOs) |
| REQ-ADB-02 | Drawer session response is a DTO / cash advance exposes DTOs | — | **PENDING — S4b** (drawer-only; not started) |
| REQ-ADB-03 | History is projected, not hand-built | — | **PENDING — S4b** (drawer-only; not started) |

**Compliance summary**: **REQ-ADB-01 complete**; **REQ-ADB-04 satisfied for the closure DTOs** (both of its scenarios) and pending for the drawer DTOs. In-scope scenarios: **4/4 compliant, 0 UNTESTED, 0 FAILING**. Spec-wide at `829778c`: **1/4 requirements** and **4/7 scenarios** complete, with the remainder being S4b's drawer work, which this slice explicitly must not start.

### S4a Discrimination Review

The five S4a tests were inspected for the tautology failure mode found in earlier slices (an assertion that restates a mock's own setup, or a `Times.Never` guarding a path the code under test cannot reach).

- **`ClosureDto_GoldenJson_...` is discriminating.** `Assert.Equal(GoldenClosureJson, dtoJson)` is a strict whole-body equality against a literal: any added, renamed, reordered or dropped DTO member turns it red. The member-by-member loop compares two independently produced serializations (entity vs. DTO), not a constant restated from the DTO, and the nav-absence assertions are pinned to the production record shape.
- **`ClosureDtos_ExposeNoPublicSetter` is discriminating.** It reflects the real types and rejects any setter lacking `IsExternalInit`, so replacing a positional record with a mutable `{ get; set; }` class fails it.
- **`ClosureServiceAndControllerSignatures_DoNotExposeSalesModuleEntities` is discriminating.** It reflects real method signatures; re-introducing a `Sales.Module.Entities` type in any return or parameter (including inside generics and arrays) fails it. Its scope gap (the concrete class is not reflected) is recorded as `S4a-R1`.
- **`GetClosure_ReturnsDeclaredDto_AndPreservesNotFoundSemantics` is discriminating but light.** It drives the real controller with a mocked service and asserts the 200 body type, the serialized absence of the navs and the preserved 404. It cannot detect a service-side entity leak (the mock supplies the DTO) — that is what the reflection test covers.
- **`ClosureResponseDtos_AreDeclaredInSalesModuleDtosNamespace`** is a low-value but discriminating structural pin (namespace + `sealed`).
- **No tautology found** in this file: no assertion verifies a mock the code under test never reaches, and no `Times.Never` guards an unreachable path.
- **Method caveat**: discrimination is established by inspection only. The verification brief forbids code writes, so no verifier-owned mutation was executed here (the S3 verification's mutate-and-revert precedent was not available). Execution evidence: 5/5 tests passed with `Omitido: 0`, confirming that none is skipped or gated.

### S4a Scope Check

- **No S4b creep**: the commit touches no `CashDrawerService.cs`, `CashDrawerController.cs` or `CashDrawerDtos.cs`, and creates no drawer DTO. The `tasks.md` diff flips exactly the eight Phase 4a checkboxes and nothing else — `checked = 39` (10 + 10 + 11 + 8), `unchecked = 33` (Phase 4b-5c).
- **No S5 creep**: no `AuthController.cs`, no `MainWindow.xaml.cs`, no WPF view models, no `RegisterPage.jsx`, and no `AsNoTracking`/guard/`CancellationToken` production work.
- **Size signal (`SIZE-S4a`)**: 18 files changed, **705 changed lines (643 inserted / 62 deleted)** — above the 400-line review budget declared in the session preflight. Excluding the 248 documentation-only lines (`docs/reporte.txt` 140, `apply-progress.md` 100, `tasks.md` 16) and the 295-line new test file, the production diff is ~176 lines (the two new DTO files = 22 lines, plus ~154 modified lines). The budget exceedance is therefore driven by the new test file and the docs, not by the production surface.
- **Commit-message item mismatch (`RESIDUAL-S4a-02`)**: the subject says `(slice S4a, items 3/15/19/20)`. Independently confirmed against `docs/deuda-legacy-gga-2026-09-16.md`: item **3** (`GetClosure`/`CreateClosure` serialize `DailyClosure`/`ClosureDetail`) and item **8** (`GetClosureAsync`/`CreateClosureAsync` return the entity) are the closure findings; items **15** (`GetActiveSession`/`OpenSession`/`CloseSession`/`AddTransaction` return `CashDrawerSession`/`CashTransaction`), **19** (`ICashDrawerService.GetHistoryAsync` returns `List<CashTransaction>`; `CashAdvanceResultDto` exposes `CashTransaction`) and **20** (`GetHistoryAsync` materializes `new CashTransaction { Sale = ... }`) are the drawer findings S4b owns. The commit subject therefore under-claims item 8 and over-claims 15/19/20. `apply-progress.md` D5 records the correct mapping; the commit subject does not. A commit message is immutable without a git write (forbidden here), so this is registered rather than fixed.
- **Checked task vs. changed-file mismatch (`RESIDUAL-S4a-03`)**: (a) 4a.5 says `CreateClosure` must return a DTO, but `POST /api/dailyclosure` still returns `CloseShiftResult` — a `sealed record` DTO with no entity member — documented as deviation D1, so REQ-ADB-01 holds while the literal task text is only partially met; (b) 4a.6 says `Desktop.Client.Core/Services/DailyClosureClientService.cs` must bind to DTO field names, but the file is **not in the commit** and is unchanged, documented as deviation D2 (nothing to re-bind, because no client reads the closure GET). Both boxes are `[x]`, so `tasks.md` slightly over-reports the changed-file set. Neither point affects a requirement.
- **`S4a-R1` (confirmed)**: `Sales.Module/Services/DailyClosureService.cs:112` still declares `public async Task<DailyClosure> CreateClosureAsync(DailyClosure closure)` — a public, entity-returning create entry point on the concrete class. It is no longer on `IDailyClosureService`, has no production caller (`S3-07`), and the structural test reflects only the interface plus the controller, so REQ-ADB-01 is not violated; but the class still exposes an entity-returning create method. Registered by `apply-progress.md` (S4a-R1); candidate for deletion together with its test references.
- **`S4a-R2` / `WARNING-07` / `S3-06` (confirmed, enlarged)**: `Sales.Module/Services/DailyClosureService.cs` measures **645** lines (verifier-measured `(Get-Content).Count`), above the 300-500 ceiling in `docs/coding-guidelines-core.md`; S4a added 9 lines (636 -> 645).
- **Carried and untouched**: `WARNING-04` (hardcoded `"Balanced"` status and mixed units in the merged undeclared-method lines) and `S3-07` remain open for S4b/S5, exactly as registered by the S3 verdict. S4a neither fixes nor worsens them.

### S4a Changed Files

The fifteen production/test files below, in this order, are the input to the head envelope's `evidence_revision`.

| File | Action | Role in S4a |
|------|--------|-------------|
| `Sales.Module/DTOs/DailyClosureResponseDto.cs` | Created | Immutable closure response contract (AD-14) |
| `Sales.Module/DTOs/ClosureDetailResponseDto.cs` | Created | Immutable closure detail contract (AD-14) |
| `Sales.Module/Services/ShiftReportMapper.cs` | Modified | `MapClosure(DailyClosure) -> DailyClosureResponseDto`; `MapDetails` now consumes `IReadOnlyList<ClosureDetailResponseDto>` |
| `Sales.Module/Interfaces/IDailyClosureService.cs` | Modified | `GetClosureAsync`/`GetLatestClosureAsync` return DTOs; `WriteClosedClosureReceiptsAsync` takes a DTO; the entity create member is removed from the interface |
| `Sales.Module/Services/DailyClosureService.cs` | Modified | Read/create paths project through `ShiftReportMapper`; private `LoadClosureEntityAsync`; receipt generator and writer take the DTO |
| `Sales.Module/Services/ClosurePdfGenerator.cs` | Modified | `GeneratePdf` takes `DailyClosureResponseDto` |
| `Backend.API/Controllers/DailyClosureController.cs` | Modified | `GetClosure` returns the declared DTO; entity `using` removed |
| `Backend.API/Controllers/ShiftsController.cs` | Modified | Report path consumes DTO details; unused entity `using` removed |
| `CommandCenter.Tests/Unit/ClosureDtoBoundaryTests.cs` | Created | The five S4a tests: golden-JSON parity, init-only reflection, no-entity signature reflection, namespace/seal, controller GET + NotFound |
| `CommandCenter.Tests/Unit/ErrorContractTests.cs` | Modified | Re-pointed 3 `GetClosureAsync` mocks and 2 receipt-writer calls to the DTO |
| `CommandCenter.Tests/Unit/PaymentMethodCurrencyClassificationTests.cs` | Modified | Re-pointed 2 `GetClosureAsync` mocks and 1 receipt call to the DTO |
| `CommandCenter.Tests/Unit/CloseShiftResolverClassificationTests.cs` | Modified | `MapDetails` callers re-pointed to DTO details |
| `CommandCenter.Tests/Unit/DailyClosureServiceUnitTests.cs` | Modified | Receipt generator callers re-pointed to the DTO |
| `CommandCenter.Tests/CheckoutAndPaymentTests.cs` | Modified | Receipt/PDF generator callers re-pointed to the DTO |
| `CommandCenter.Tests/CheckoutUxTests.cs` | Modified | Receipt generator callers re-pointed to the DTO |

Plus the documentation files `docs/reporte.txt` (ANEXO 8.140), `apply-progress.md` (S4a section) and `tasks.md` (Phase 4a checkboxes), which are **not** part of `evidence_revision`.

### S4a Verdict

**PASS_WITH_WARNINGS** — the closure DTO boundary is genuinely established at `829778c`. The two new DTOs are `sealed record`s with no mutable setter; `IDailyClosureService` and `DailyClosureController` expose no `Sales.Module.Entities` type (0 grep matches, plus a reflecting structural test); the service projects entities into DTOs through `ShiftReportMapper`; and the golden-JSON test pins the response body byte-for-byte while proving that every client-bound member survives and the two EF navigations disappear. Every claim was re-executed: build 0/0, backend suite 1185/1185, the `Dto` filter 71/71, the new class 5/5, frontend 271/271 with clean lint, and the coverage gate reproduces the claim exactly. No S4b/S5 creep and no tautological test were found. The residual items are non-blocking and explicitly recorded: `S4a-R1` (concrete entity-returning legacy entry point), `S4a-R2` (645-line class), `RESIDUAL-S4a-01` (serializer-level parity evidence), `RESIDUAL-S4a-02` (commit item misattribution), `RESIDUAL-S4a-03` (task-checkbox vs. changed-file mismatch) and `SIZE-S4a` (705 changed lines vs. the 400-line budget). S4a may be chained into S4b.

## Slice S4b — Drawer DTO Boundary

**Verdict: PASS_WITH_WARNINGS** — 0 blockers, **0 critical findings**. The drawer half of `api-dto-boundary` is established at `65e038a`: both new DTOs are `sealed record`s with init-only members, `ICashDrawerService` and `CashDrawerController` expose no `Sales.Module.Entities` class type (only the three value-type enums), and every public drawer method returns DTOs. In-scope scenarios: **5/5 compliant**, 0 UNTESTED, 0 FAILING — REQ-ADB-02 (2/2), REQ-ADB-03 (1/1) and the drawer half of REQ-ADB-04 (2/2). Spec-wide at `65e038a`, the whole `api-dto-boundary` delta spec is complete (4/4 requirements, 7/7 scenarios; the closure half was admitted under S4a at `829778c`). Residuals are non-blocking and recorded, not hidden: `RESIDUAL-S4b-01` (parity evidence is serializer-level, not MVC-pipeline), `RESIDUAL-S4b-02` (task 4b.8 over-checked — no WPF file changed), `RESIDUAL-S4b-03` (weak assertion components in the history test), `RESIDUAL-S4b-04` (out-of-scope pre-existing Web `advance` filter bug found during the consumer search), `S4b-R1` (drawer read tuning deferred to S5b/AD-16) and `SIZE-S4b` (1053 changed lines vs the 400-line review budget).

**Verified revision**: `65e038a` (`refactor(8.140): DTOs inmutables de drawer (slice S4b, items 15/19/20) - ANEXO 8.140`; current `HEAD`). Working tree clean at verification time (`git status --short` and `git diff --stat HEAD` both empty).
**Slice delta**: `829778c` -> `65e038a` — 30 files, `+887 / -166` (1053 changed lines): 27 production/test files (2 new DTOs, 1 deleted API DTO, 3 modified production files, 1 modified Web page, 1 new test file, 19 re-pointed test files) plus `docs/reporte.txt` (+146), `apply-progress.md` (+108) and `tasks.md` (`+12 / -10`, exactly the ten Phase 4b checkboxes plus the S4a bookkeeping fold-in note).

### S4b Re-executed Evidence (verbatim)

Every command below was re-executed independently on `65e038a` after the working tree was confirmed clean. `stderr` was merged into the captured stream. No result was taken from `apply-progress.md`.

**1. Build** - `dotnet build CommandCenter.slnx -c Release` - exit `0` - matches the claim (0/0)

```text
Compilación correcta.
    0 Advertencia(s)
    0 Errores

Tiempo transcurrido 00:00:35.48
```

captured-output hash: `sha256:3a1d2f6e6c116edfe4694a823e7cd5e800848acabdd3167a1378ce90687f5b70`

**2. Backend tests (full)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` - exit `0` - matches the claim (1193/1193)

```text
Correctas! - Con error:     0, Superado:  1193, Omitido:     0, Total:  1193, Duración: 12 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:3f8f629158dbd35c075a2d00cf749f84c1a4846d6fe2e8de9a8bd1738e4dd82f`

**3. S4b focused filter (as declared in `tasks.md`)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~Dto"` - exit `0` - matches the claim (79/79; S4a had 71)

```text
Correctas! - Con error:     0, Superado:    79, Omitido:     0, Total:    79, Duración: 1 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:d38b64e6868e48b6e49ce0c51798b89543838a284955ebb5abd0c30c9d2dc6fe`

**4. New test class alone** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~DrawerDtoBoundaryTests"` - exit `0` - matches the claim (8/8); `Omitido: 0`, so none is Postgres-gated

```text
Correctas! - Con error:     0, Superado:     8, Omitido:     0, Total:     8, Duración: 2 s - CommandCenter.Tests.dll (net10.0)
```

captured-output hash: `sha256:5417bb970d47397e3dbae58c06891e1320896ae421f1fbc10abeb9c6ab33b19b`

**5. Frontend tests** - `npm test` (Web.Frontend) - exit `0` on the first execution - matches the claim (271/271)

```text
ℹ tests 271
ℹ suites 58
ℹ pass 271
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
```

captured-output hash: `sha256:c73d53d44a9ae4cd06befbe0227ad4139fc7738892cbbf6f33fd56268e46ec04`

**6. Frontend lint** - `npm run lint` (Web.Frontend, oxlint) - exit `0`, no findings - matches the claim

```text
> web-frontend@0.0.0 lint
> oxlint
```

captured-output hash: `sha256:1472f392035e28478ac827e6e5301e8adde36ba5a0a27bc61cdf6e42453bc7d3`

**7. Coverage gate** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` (1193/1193, exit `0`), then `python scripts/check-coverage.py CommandCenter.Tests/TestResults/62bbe479-7f29-40bd-badd-64c3be1b4cf2/coverage.cobertura.xml` - exit `0`

```text
Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8364 min=0.7000 gap_a_70%=0.0000 [OK]
  Sales.Module       rate=0.9047 min=0.8000 gap_a_70%=0.0000 [OK]
  Inventory.Module   rate=0.8251 min=0.7200 gap_a_70%=0.0000 [OK]
```

The gate reproduces the S4b claim **exactly** (Core 0.8364 / Sales.Module 0.9047 / Inventory.Module 0.8251) and all three `tasks.md` thresholds pass (Core >= 0.70, Sales.Module >= 0.80, Inventory.Module >= 0.72). `Sales.Module`, the layer S4b changes, sits at 0.9047 vs 0.9011 at S4a.

**Hash definition (this section)**: `sha256` is the SHA-256 over the captured combined stdout+stderr (`*>` redirection), normalized to UTF-8 without BOM, `CRLF` -> `LF`, trailing newlines trimmed.

**Environment note**: `TEST_POSTGRES_CONNECTION` is unset, so the Postgres-gated classes continue to early-return as vacuous passes. No S4b test is Postgres-gated: the eight `DrawerDtoBoundaryTests` are reflection/serialization/InMemory tests and all eight executed (`Omitido: 0`).

### S4b Boundary Evidence (structural + grep)

| Check | Method | Result |
|-------|--------|--------|
| No EF entity class on the drawer boundary | grep for bare `CashDrawerSession` / `CashTransaction` class usage across `ICashDrawerService.cs`, `CashDrawerController.cs` and both new DTOs | **0 matches**. The four files keep a `using Sales.Module.Entities;` (lines 2/5/1/1), which resolves only the three **value-type enums** (`CashDrawerStatus`, `CashTransactionType`, `CashTransactionSource` — declared `enum` at `CashDrawerSession.cs:8`, `CashTransaction.cs:7,13`) plus `CashTransactionType`/`CashTransactionSource` in parameters and request models. No entity class appears in any return or parameter type. |
| Interface returns DTOs | `ICashDrawerService.cs:21-75`: `GetActiveSessionAsync`/`GetActiveSessionWithTransactionsAsync` -> `CashDrawerSessionResponseDto?`; `GetOrCreateActiveSessionAsync`/`OpenSessionAsync`/`CloseSessionAsync` -> `CashDrawerSessionResponseDto`; `AddTransactionAsync`/`RecordSaleChangeAsync` -> `CashTransactionResponseDto`; `GetHistoryAsync` -> `List<CashTransactionResponseDto>`; `CashAdvanceResultDto.ExpenseTransaction`/`IncomeTransaction` -> `CashTransactionResponseDto` | **CONFIRMED** |
| Controller returns DTOs | `CashDrawerController.cs`: `GetActiveSession` -> `ActionResult<CashDrawerSessionResponseDto?>` (`:46`); `GetHistory` -> `ActionResult<IEnumerable<CashTransactionResponseDto>>` (`:65`); `OpenSession`/`CloseSession` -> `ActionResult<CashDrawerSessionResponseDto>` (`:80, :89`); `AddTransaction` -> `ActionResult<CashTransactionResponseDto>` (`:106`); `ProcessCashAdvance` -> `ActionResult<CashAdvanceResultDto>` (`:255`). The response mutators now build new DTOs with `with` instead of mutating entities (`MapLocalTimesAsync` `:224-237`, `MapLocalTime` `:239-250`, cash-advance `:283-284`) | **CONFIRMED** |
| Old API-local DTO deleted | `Backend.API/DTOs/CashDrawerDtos.cs` removed (`Test-Path` false); no compiled reference to a `Backend.API.DTOs.CashTransactionDto` remains (the surviving `using Backend.API.DTOs` sites belong to payment/product/reservation contracts) | **CONFIRMED** |
| History projects instead of materializing | `GetHistoryAsync` (`CashDrawerService.cs:441-466`) is a single LINQ `Select(t => new CashTransactionResponseDto { ... InvoiceNumber = t.Sale != null ? t.Sale.InvoiceNumber : null ... })`; no `new CashTransaction` / `new Sale` remains anywhere in the service — the old code projected `new CashTransaction { Sale = new Sale { ... } }` | **CONFIRMED** (REQ-ADB-03) |
| DTO immutability | `CashDrawerSessionResponseDto.cs` (18 lines: 10 scalar `{ get; init; }` members + `Transactions`) and `CashTransactionResponseDto.cs` (22 lines: 15 `{ get; init; }` members) are `public sealed record`; **0** `set;` occurrences in either file | **CONFIRMED** |
| Structural test reflects real types | `DrawerDtoBoundaryTests.CashDrawerServiceAndControllerSignatures_DoNotExposeSalesModuleEntities` walks every `ICashDrawerService` method and every public instance method of `CashDrawerController` (generics/arrays flattened) and fails on any `Sales.Module.Entities` **class**; `CashDrawerController_Actions_DeclareDrawerResponseDtos` pins all six action body types, including the `CashTransactionResponseDto` element of `GetHistory` | **CONFIRMED** (8/8 green) |
| Scope clarification — `CashAdvanceResultDto` wrapper | The wrapper class itself is pre-existing and keeps mutable setters (`ICashDrawerService.cs:7-17`); REQ-ADB-02 only requires its two transaction members to be DTOs, which they now are. The wrapper's own mutability is pre-existing debt registered for S5b/AD-17, not a regression | **CONFIRMED (scoped)** |

### S4b Field-Parity Assessment

**Golden payload (touched endpoint `GET /api/cashdrawer/active-session`)**

- `Assert.Equal(GoldenSessionJson, dtoJson)` pins the DTO output byte-for-byte against a literal; the test additionally serializes the legacy entity graph with the production JSON options and compares every client-bound member of both bodies.
- Before (legacy body): the session scalars plus a `transactions` array whose entries carried `session` (cycle), `sale` (the included `Sale`) and `paymentMethod` navigation members — all three asserted present in the legacy JSON (`DrawerDtoBoundaryTests.cs:165-167`).
- After (S4b DTO): all ten session scalars survive with the same JSON name and value (`id`, `openedAt`, `openedAtLocal`, `closedAt`, `closedAtLocal`, `status`, `openingBalanceLocal`, `openingExchangeRate`, `closingBalanceLocal`, `closingExchangeRate`); all fourteen previously bound transaction scalars survive (`id`, `sessionId`, `transactionTime`, `transactionTimeLocal`, `type`, `source`, `amountUsd`, `exchangeRate`, `amountLocal`, `description`, `referenceId`, `saleId`, `isPhysicalCash`, `paymentMethodId`); the `session`/`sale`/`paymentMethod` navigations are gone (asserted `ContainsKey` false on the transaction and absent from the recursive property-name scan).
- The removed `sale` navigation is replaced by the **flattened `invoiceNumber`**: `Assert.Equal(legacyPhysical["sale"]["invoiceNumber"], dtoTransaction["invoiceNumber"])` (`:163`) proves value equality between the old nested path and the new top-level member; `MapTransaction` sets it from `transaction.Sale?.InvoiceNumber` (`CashDrawerService.cs:497`).
- Non-physical transactions: the legacy GET already filtered `IsPhysicalCash` and ordered by `TransactionTime` descending in the controller; S4b moved that filter/order into the service (`CashDrawerService.cs:45-49`). The golden seed's non-physical transaction (id 32) is asserted absent from the DTO body (`:190`).

**Removed-navigation consumer search (bindings)**

- WPF `CashTransactionDto` (`Desktop.Client.Core/Services/ICashDrawerService.cs:55-88`) declares only `Id`, `TransactionTimeLocal`, `Description`, `InvoiceNumber`, `AmountUsd`, `AmountLocal`, `ExchangeRate`, `Type`, `Source`, `IsPhysicalCash`, `PaymentMethodId` — **no** `session`/`sale`/`paymentMethod` member exists to break. `CashDrawerView.xaml` binds `FormattedInvoiceNumber` (`:183, :303`), `TransactionTimeLocal` (`:193, :245, :249`) and `AmountBsS` (`:200, :313`); `FormattedInvoiceNumber` reads `InvoiceNumber` and falls back to `Description` (`:81-84`), both preserved.
- WPF `CashDrawerSessionDto` (`:90-103`) declares the session scalars plus `Transactions` — every DTO member is a superset; `System.Text.Json` ignores the added members (`ReadFromJsonAsync` with default options, `Desktop.Client.Core/Services/CashDrawerService.cs:26, :34, :42, :56, :73`).
- Web: the only `tx.sale?.invoiceNumber` read in the repository was `RegisterPage.jsx:356-357`, updated in the same commit to `tx.invoiceNumber`; a repo-wide search finds no other `tx.sale` / `.session` / `.paymentMethod` access in `Web.Frontend/src` (the surviving `sale?.` matches are cart-domain objects in `CartContext.jsx` / `EditSaleModal.jsx`).
- Web modals ignore the `transaction` response body: `CashInModal.jsx:52-62` and `CashOutModal.jsx:58-68` `await api.post('/api/cashdrawer/transaction', ...)` and then only close/reload — the response value is never read. The claim holds.
- `CashAdvanceModal.jsx:100` reads `res.invoiceNumber` — the **result-level** `CashAdvanceResultDto.InvoiceNumber` (`ICashDrawerService.cs:16`), preserved and still populated from `createdSale?.InvoiceNumber` (`CashAdvanceCoordinator.cs:164`). The WPF view model does the same (`CashDrawerViewModel.cs:421-423`).
- `POST /api/cashdrawer/open`, `/close` and `/transaction` bodies are supersets of the old fields; only the null navigation members (`sale`/`session`/`paymentMethod`) are dropped, and no consumer binds them.

**Assessment**: field parity holds for every member any client binds today, the flattened `invoiceNumber` is proven equal to the old `sale.invoiceNumber`, and the only removed members are EF navigation properties no consumer reads. Caveat `RESIDUAL-S4b-01`: the "before" body is a re-serialization of the entity graph under the production options, not a captured pre-S4b HTTP response, and no test drives the MVC pipeline for the drawer endpoints (the controller test mocks the service).

### S4b Requirement Evidence Matrix

| Requirement | Scenario | Covering test / evidence | Result |
|-------------|----------|--------------------------|--------|
| REQ-ADB-02 | Drawer session response is a DTO | `DrawerSessionDto_GoldenJson_PreservesLegacyBoundFields_AndDropsEntityNavigationMembers` (golden literal + nav absence on a real `CashDrawerService` over InMemory) + `GetActiveSession_ReturnsDeclaredDto_AndPreservesEmptySessionSemantics` (real controller: 200 DTO, serialized nav absence, `Ok(null)` empty-session semantics) + `CashDrawerController_Actions_DeclareDrawerResponseDtos` + the grep/reflection boundary evidence | **COMPLIANT** — runtime serialization is asserted for the GET; the open/close/transaction/clone bodies are pinned at the declared-type level (residual `RESIDUAL-S4b-01`) |
| REQ-ADB-02 | Cash advance result exposes DTOs | `CashAdvanceResultDto_ExposesTransactionResponseDtos` (reflection pins both members to `CashTransactionResponseDto`); `ICashDrawerService.cs:9-10`; `CashAdvanceCoordinatorTests` and `CashAdvanceTests` green over the re-pointed contract; Web/WPF read the preserved result-level `invoiceNumber` | **COMPLIANT** |
| REQ-ADB-03 | History is projected, not hand-built | `GetHistoryAsync_ReturnsProjectedDtos_WithoutMaterializingEntityInstances` (real service: DTO type, `InvoiceNumber == 4242`, non-physical excluded, empty `ChangeTracker`) + `CashDrawerServiceUnitTests.GetHistoryAsync_ProjectsInvoiceNumberFromSale_WithoutLoadingFullSaleEntity` (re-pointed to `item.InvoiceNumber`); source: the LINQ `Select` projection in `CashDrawerService.cs:448-464` replaces the deleted `new CashTransaction { Sale = new Sale { ... } }` | **COMPLIANT** (residual `RESIDUAL-S4b-03`) |
| REQ-ADB-04 | DTOs expose no public setter (drawer) | `DrawerDtos_ExposeNoPublicSetter` reflects both types and rejects any setter lacking the `IsExternalInit` modreq; `DrawerResponseDtos_AreDeclaredInSalesModuleDtosNamespace` pins `sealed` + namespace | **COMPLIANT** |
| REQ-ADB-04 | Existing client fields survive the swap (drawer) | `DrawerSessionDto_GoldenJson_...` per-member comparison + the `invoiceNumber` flatten proof; consumer search across WPF (DTO members + XAML bindings) and Web (only the same-commit `RegisterPage.jsx` rebinding) | **COMPLIANT** |

**Compliance summary**: **5/5 in-scope scenarios compliant**, 0 UNTESTED, 0 FAILING. Requirement level: REQ-ADB-02 (2/2 scenarios), REQ-ADB-03 (1/1) and the drawer half of REQ-ADB-04 (2/2) are satisfied. Spec-wide at `65e038a`: the `api-dto-boundary` delta spec is **4/4 requirements and 7/7 scenarios** complete (REQ-ADB-01 and the closure half of REQ-ADB-04 were admitted under S4a at `829778c`).

### S4b Discrimination Review

The eight S4b tests were inspected for the tautology failure mode found in earlier slices.

- **`DrawerSessionDto_GoldenJson_...` is discriminating.** `Assert.Equal(GoldenSessionJson, dtoJson)` is a strict whole-body equality against a literal; any added, renamed, reordered or dropped DTO member turns it red. The member loop compares two independently produced serializations (legacy entity graph vs DTO), and the nav-absence assertions are pinned against the legacy body's own content.
- **`GetHistoryAsync_ReturnsProjectedDtos_...` is partially discriminating** (residual `RESIDUAL-S4b-03`). `item.InvoiceNumber` does not exist on `CashTransaction`, so a revert to the old materializing projection fails to compile; the single-item filter (`t.SaleId == sale.Id`) and the excluded non-physical id are real state assertions. Two components are weak: `Assert.Equal(typeof(CashTransactionResponseDto), item.GetType())` restates the compile-time element type of `List<CashTransactionResponseDto>`, and `Assert.Empty(ChangeTracker.Entries<CashTransaction>())` would also hold for the old `AsNoTracking` entity materialization. The "without materializing entity instances" clause therefore rests on the type contract plus source inspection of the projection.
- **`DrawerDtos_ExposeNoPublicSetter` is discriminating.** Replacing a positional record with a mutable `{ get; set; }` class fails it.
- **`CashDrawerServiceAndControllerSignatures_DoNotExposeSalesModuleEntities` is discriminating.** It reflects real signatures; re-introducing a `Sales.Module.Entities` class in any return or parameter (including inside generics and arrays) fails it. Scope note: it reflects only the interface and controller, not the concrete `CashDrawerService` class (whose public methods also return DTOs today).
- **`CashDrawerController_Actions_DeclareDrawerResponseDtos` and `CashAdvanceResultDto_ExposesTransactionResponseDtos` are discriminating structural pins** (real reflected types).
- **`GetActiveSession_ReturnsDeclaredDto_AndPreservesEmptySessionSemantics` is discriminating but light**: it drives the real controller with a mocked service and asserts the DTO body, the serialized nav absence and the preserved `Ok(null)` semantics; it cannot detect a service-side entity leak (the mock supplies the DTO) — that is what the golden/reflection tests cover.
- **`DrawerResponseDtos_AreDeclaredInSalesModuleDtosNamespace`** is a low-value but discriminating structural pin (namespace + `sealed`).
- **No tautology found**: no assertion verifies a mock the code under test never reaches, and no `Times.Never` guards an unreachable path. The 19 re-pointed test files are compile-time-discriminating only (they bind the new DTO types); their behavioral assertions are unchanged from the pre-S4b suite.
- **Method caveat**: discrimination is established by inspection only; the verification brief forbids code writes, so no verifier-owned mutation was executed (the same limitation as S4a). Execution evidence: 8/8 passed with `Omitido: 0`.

### S4b Scope Check

- **No S5 creep**: the commit touches no `AuthController.cs`, no `MainWindow.xaml.cs`, no WPF view model, no `RegisterClosePage.jsx` and no `AsNoTracking`/guard/`CancellationToken` production work. `tasks.md` flips exactly the ten Phase 4b checkboxes and adds the S4a bookkeeping fold-in note; Phases 5a-5c remain unchecked.
- **Deviation D1 (full `ICashDrawerService` surface change) — accepted.** The whole public interface was swapped, including the non-controller methods (`GetActiveSessionAsync`, `GetOrCreateActiveSessionAsync`, `RecordSaleChangeAsync`), not just the four controller-facing ones. This is what REQ-ADB-02 literally requires ("the public `ICashDrawerService` methods MUST return immutable DTOs") and registry item 19 names the full surface. Behavior-preserving: every internal production caller consumes only scalar members — `SalesService.Payments.cs:82,203`, `SalesService.HoldOrders.cs:82,219,228` and `SalesService.Checkout.cs:135,231,237` use `.Id`; `ExchangeRateWriteService.cs:111-113` uses `.OpeningExchangeRate` plus a null check. No entity-only member is read from the new DTOs.
- **`S4b-R1` (registered, not fixed)**: `GetActiveSessionWithTransactionsAsync` still uses `Include(s => s.Transactions).ThenInclude(t => t.Sale)` with tracking and no `AsNoTracking`/`AsSplitQuery` (`CashDrawerService.cs:38-41`); AD-16 defers the read tuning to S5b, exactly as registered in `apply-progress.md`. S4b did not alter the query shape.
- **Size signal (`SIZE-S4b`)**: 30 files changed, **1053 changed lines (887 inserted / 166 deleted)** — above the 400-line review budget declared in the session preflight. Excluding the documentation-only lines (`docs/reporte.txt` 146, `apply-progress.md` 108, `tasks.md` 22) and the 423-line new golden test file, the authored change is ~354 lines: production ~288 (`CashDrawerController.cs` 103, `CashDrawerService.cs` 101, `CashDrawerDtos.cs` 19 deleted, `ICashDrawerService.cs` 21, two new DTOs 40, `RegisterPage.jsx` 4) and ~66 lines of mechanical re-pointing across 19 test files. The budget exceedance is driven by the new test file and the docs, not by the production surface.
- **S4a bookkeeping fold-in executed**: the commit adds the S4a corrections note to `tasks.md` (`S4a bookkeeping corrections (fold-in during S4b)`) covering `RESIDUAL-S4a-02`/`RESIDUAL-S4a-03`. The commit subject still carries the mislabeled `(slice S4a, items 3/15/19/20)` for `829778c` — commit subjects are immutable without a git write (forbidden here), so that record stands as `RESIDUAL-S4a-02`.

### S4b Changed Files

The twenty-six S4b production/test files below — every commit file except the three documentation files and the deleted `Backend.API/DTOs/CashDrawerDtos.cs` — in this order, are the input to the head envelope's `evidence_revision`.

| File | Action | Role in S4b |
|------|--------|-------------|
| `Backend.API/Controllers/CashDrawerController.cs` | Modified | All drawer actions return DTOs; `MapLocalTimesAsync`/`MapLocalTime` build DTOs with `with` instead of mutating entities |
| `CommandCenter.Tests/CashAdvanceTests.cs` | Modified | `GetOrCreateActiveSessionAsync` mock re-pointed |
| `CommandCenter.Tests/CheckoutAndPaymentTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/FinancialRobustnessTests.cs` | Modified | 6 mock sites re-pointed |
| `CommandCenter.Tests/HoldOrderClaimTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/HoldSalePaymentRemediationTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/Integration/CashAdvanceEnvelopeTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/OnHoldSalesTests.cs` | Modified | 4 mock sites re-pointed |
| `CommandCenter.Tests/PendingPickupTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/Sprint1PerformanceOptimizationTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/Sprint2PerformanceOptimizationTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/Unit/BsPriceCeilingStandardTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/Unit/CashAdvanceCoordinatorTests.cs` | Modified | 2 mock sites re-pointed |
| `CommandCenter.Tests/Unit/CashDrawerServiceUnitTests.cs` | Modified | `GetHistoryAsync` assertion re-pointed to `item.InvoiceNumber` (no `item.Sale`) |
| `CommandCenter.Tests/Unit/CloseShiftResolverClassificationTests.cs` | Modified | `GetActiveSessionAsync` mock re-pointed |
| `CommandCenter.Tests/Unit/DrawerDtoBoundaryTests.cs` | Created | The eight S4b tests: golden JSON, history projection, init-only reflection, namespace/seal, cash-advance DTOs, no-entity signatures, action body types, controller GET semantics |
| `CommandCenter.Tests/Unit/ErrorContractTests.cs` | Modified | `GetActiveSessionAsync` mock re-pointed |
| `CommandCenter.Tests/Unit/ExchangeRateReferenceBoundaryTests.cs` | Modified | `AddTransactionAsync` mock re-pointed |
| `CommandCenter.Tests/Unit/Phase4SharedTransactionAndIdempotencyTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/Unit/SalesServiceUnitTests.cs` | Modified | Mock re-pointed |
| `CommandCenter.Tests/Unit/SecurityTests.cs` | Modified | Mock re-pointed |
| `Sales.Module/DTOs/CashDrawerSessionResponseDto.cs` | Created | 10 init-only session scalars + `Transactions`; `sealed record` (AD-14) |
| `Sales.Module/DTOs/CashTransactionResponseDto.cs` | Created | 15 init-only members incl. the flattened `InvoiceNumber`; the union of the moved API DTO fields and the entity scalars (AD-14) |
| `Sales.Module/Interfaces/ICashDrawerService.cs` | Modified | DTO return types on every public method; `CashAdvanceResultDto` transaction members are DTOs |
| `Sales.Module/Services/CashDrawerService.cs` | Modified | `MapSession`/`MapTransaction`; private `LoadActiveSessionEntityAsync`; `GetHistoryAsync` projects DTOs without entity materialization |
| `Web.Frontend/src/pages/RegisterPage.jsx` | Modified | `tx.sale?.invoiceNumber \|\| tx.invoiceNumber` -> `tx.invoiceNumber` (AD-15) |

Plus the documentation files `docs/reporte.txt` (ANEXO 8.140), `apply-progress.md` (S4b section + S4a corrections) and `tasks.md` (Phase 4b checkboxes + fold-in note), which are **not** part of `evidence_revision`, and the deleted `Backend.API/DTOs/CashDrawerDtos.cs` (excluded: a deleted file has no current bytes to hash).

### S4b Residual Warnings (non-blocking for S4b)

- **RESIDUAL-S4b-01 (parity evidence level)** — the "before" body is a re-serialization of the legacy entity graph under the production JSON options (`DrawerDtoBoundaryTests.cs:118-124`), not a captured pre-S4b HTTP response, and no test drives the MVC pipeline for the drawer endpoints (the controller test mocks `ICashDrawerService`). The golden-literal `Assert.Equal` and the member-by-member comparison are still strict; the gap is at the pipeline level. Same class as `RESIDUAL-S4a-01`.
- **RESIDUAL-S4b-02 (checkbox vs. changed file)** — task 4b.8 ("Modify WPF client services/views to bind to DTO field names") is checked but **no WPF file is in the commit**: the WPF `CashTransactionDto`/`CashDrawerSessionDto` already declare the DTO JSON names and ignore the additive members, so there was nothing to change (deviation D2). The box over-reports the changed-file set; the behavioral requirement (REQ-ADB-04 field parity) holds. Same class as `RESIDUAL-S4a-03`.
- **RESIDUAL-S4b-03 (weak assertions)** — in `GetHistoryAsync_ReturnsProjectedDtos_WithoutMaterializingEntityInstances`, the `GetType()` assertion restates the compile-time element type and the `ChangeTracker` assertion would also pass for the old `AsNoTracking` entity materialization; discrimination rests on `item.InvoiceNumber` (a DTO-only member), the filtered single-item assertion and source inspection of the projection (see the Discrimination Review).
- **RESIDUAL-S4b-04 (out-of-scope Web bug found during the consumer search)** — `RegisterPage.jsx:122` filters the "advance" source with `tx.source !== 4`, but `CashAdvance = 2` and `Closing = 4` (`CashTransaction.cs:13`), so the "Adelanto" filter matches closing transactions and excludes cash advances. Pre-existing (the line is untouched by `65e038a`, whose RegisterPage diff is lines 353-357) and outside S4b; recorded for the S5c triage or a follow-up.
- **Carried and untouched**: `WARNING-04` (hardcoded `"Balanced"` status and mixed units in the merged undeclared-method lines), `WARNING-07`/`S3-06`/`S4a-R2` (645-line `DailyClosureService.cs`), `S3-07`/`S4a-R1` (legacy entity-returning closure entry point) and `S4b-R1` (drawer read tuning deferred to S5b/AD-16). S4b neither fixes nor worsens them.

### S4b Verdict

**PASS_WITH_WARNINGS** — the drawer DTO boundary is genuinely established at `65e038a`. Both new DTOs are `sealed record`s with no mutable setter; `ICashDrawerService` and `CashDrawerController` expose no `Sales.Module.Entities` class type (only the three value-type enums); the service projects history through a single LINQ `Select` instead of materializing entities; and the golden-JSON test pins the response body byte-for-byte while proving that every client-bound member survives, that `sale.invoiceNumber` moves to a top-level `invoiceNumber` with the same value, and that the `sale`/`session`/`paymentMethod` navigations disappear with no client binding them. Every claim was re-executed: build 0/0, backend suite 1193/1193, the `Dto` filter 79/79, the new class 8/8, frontend 271/271 with clean lint, and the coverage gate reproduces the claim exactly. No S5 creep and no tautological test were found. The residuals are non-blocking and explicitly recorded: `RESIDUAL-S4b-01` (serializer-level parity evidence), `RESIDUAL-S4b-02` (task 4b.8 over-checked), `RESIDUAL-S4b-03` (weak assertion components), `RESIDUAL-S4b-04` (out-of-scope pre-existing Web filter), `S4b-R1` and `SIZE-S4b`. S4b may be chained into S5a.

### Change-Level Verdict

**Pending**. The change cannot receive a change-level verdict while S5a-S5c are unimplemented. S1, S2, S3, S4a and S4b are each verified `pass_with_warnings`. Implemented requirement/scenario surface: S1 (3 requirements / 8 scenarios), S2 (4/9), S3 (4/8) and the whole `api-dto-boundary` spec (4 requirements / 7 scenarios — closure half at S4a, drawer half at S4b), i.e. **15 of the 18 requirements and 32 of the 38 scenarios** across the five delta specs. Pending: the whole of `async-cancellation-propagation` (3 requirements / 6 scenarios) in S5a/S5c. `WARNING-04` (hardcoded `"Balanced"` status and mixed units in the merged undeclared-method lines) and `WARNING-07`/`S3-06` (class size, 645 lines at S4a) remain open and escalate to S5, alongside the registered `S3-07`, `S4a-R1` and the new `S4b-R1`. S1 RESIDUAL-07 is **CLOSED** by S2/AD-9. S4b is now cleared to chain into S5a.

### Verdict

**PASS_WITH_WARNINGS (Slice S1)** - the remediation commit `c6c767f` closes all five findings registered against the prior verdict: CRITICAL-01 (the 400 path now emits an RFC 7807 `ProblemDetails` payload), WARNING-01 (five real-path tests execute the production `CreateClosureFromCommandAsync` against an InMemory `SalesDbContext`), WARNING-02 (the REQ-PMC-04 end-to-end scenario is covered and the diverging-name test is discriminating), WARNING-03 (the unauthorized rate source is fully reverted to the pre-S1 `ExchangeRateResolver` semantics, with `ITodayExchangeRateProvider` and AD-5/AD-6 left intact for S3) and WARNING-06 (the remediation adds no comment line and the apply-progress labels are corrected). All eight S1 scenarios and all three S1 requirements are compliant, so the verdict moves from `fail` to `pass_with_warnings`. The change carries no blockers and no critical findings; the residual warnings (RESIDUAL-01..09) are non-blocking, and WARNING-04/WARNING-07 remain deliberately deferred to S3 as recorded. S1 may be chained into S2.
