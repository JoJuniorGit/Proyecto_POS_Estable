```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:72c5fbf160e40f510d297b5edb1ab4157365ac8265c392dac35daf78519256c9
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 4/4
scenarios: 15/15
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
test_exit_code: 0
test_output_hash: sha256:f19c9e70c96a8ba879dc37e64b0b1f95e3d045917e9adda4248ad91ae7a68ad5
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:0b35b0a655af01a569528f1c7aaaa2ebc51df1d4b9f86aee6e03d533c54606fb
```

## Verification Report

**Change**: critical-debt-closeout
**Version**: N/A (delta specs, no version headers)
**Mode**: Standard (Strict TDD inactive: `openspec/config.yaml` -> `strict_tdd: false`, `testing.strict_tdd_mode: disabled`)
**Scope of this report**: slices S1 and S2 — S1 WARNING-04 / REQ-PMC-06 (AD-1), verified independently at commit `605b3aa`; S2 advance-commission contract + source mapping + H-13 (REQ-CAP-02/03, REQ-ADB-05; AD-2/3/6), verified independently at commit `67409da`. S3a and S3b are not implemented, so full change verification is not admissible: the change-level verdict is **pending** and no change-level claim is made. The head envelope is the cumulative verified-slice envelope — **4/4 requirements and 15/15 scenarios** of the S1+S2 delta surface (`payment-method-currency-classification` 1/4 + `cash-advance-payout-integrity` 2/7 + `api-dto-boundary` 1/4). The change's cumulative delta surface, counted from the four delta spec heading sets, is 7 requirements / 21 scenarios (payment-method-currency-classification 1/4, cash-advance-payout-integrity 2/7, api-dto-boundary 1/4, closure-orchestration-consolidation 3/6); it belongs to the final change-level verification. The head `evidence_revision` covers the 23 S1+S2 production/test files (definition below); each slice also records its own slice-scoped revision and command hashes in its section.
**Verified revision**: `HEAD` = `605b3aa125e02ed2dcf375c1d15500c633a35b96` ("fix(8.141): lineas combinadas con estado por moneda (S1, WARNING-04) - ANEXO 8.141"); parent `87aa6a2`. Working tree before and after every command: `git status --porcelain` reports only ` M opencode.json` (pre-existing modification present before this verification started; no S1 file dirty; this verification made no code change, no commit and no workspace mutation).
**evidence_revision**: SHA-256 of the ASCII string produced by joining, with `:`, the per-file SHA-256 hex digests of the three S1 production/test files, sorted by path, each hashed from its on-disk bytes: `CommandCenter.Tests/Unit/ClosureReportLineCurrencyTests.cs` `a7eb0d5729005017515cd2efc3b94c2d943d7510219d175310df6d2363a86709`, `Sales.Module/Services/DailyClosureService.cs` `1c31542d7cb3df3edc6c93e8ce5c992fe31fe413045179d1b9522ec97a1cc06c`, `Sales.Module/Services/DailyClosureService.Rules.cs` `5c3f2ba8c11a1976381e253a04a3799305a741d076120c24c0e5aec32486a13f`.
**Hash definition**: `build_output_hash` / `test_output_hash` are the SHA-256 of the combined stdout+stderr captured for the command execution reported below, normalized to UTF-8 without BOM, `CRLF` -> `LF`, trailing newlines trimmed. The same single recipe applies to every command hash recorded in this section.

### Completeness

| Metric | Value |
|--------|-------|
| Slice S1 tasks total (`tasks.md` Phase S1) | 5 |
| Slice S1 tasks complete | 5 |
| Slice S1 tasks incomplete | 0 |
| Change tasks complete (all phases) | 16 / 28 |
| Change phases implemented | 2 of 4 (S1, S2) |

All five S1 tasks (`S1-01`..`S1-05`) and all eleven S2 tasks (`S2-01`..`S2-11`) are checked in `tasks.md`. `S3a-01`..`S3a-04` and `S3b-01`..`S3b-08` are unchecked, so a change-level `pass` is not yet admissible; this report verifies S1 and S2 in isolation and leaves the change-level verdict pending.

### Re-executed Evidence (verbatim)

Every command claimed in `apply-progress.md` (S1) and the `tasks.md` Verification section was re-executed independently from the working tree at `605b3aa`. Results are reported verbatim. All exit codes are `0`; no divergence from the claims was found except the size arithmetic in `apply-progress.md` recorded as RESIDUAL-S1-05.

**1. Build** - `dotnet build CommandCenter.slnx -c Release` - exit `0` - matches claim

```text
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

`build_output_hash: sha256:980e59946331b1a34811664e9923c4449ea2483fa4ee26a80a8316ab751e23b2`

**2. Backend suite** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` - exit `0` - matches claim (`1234/1234`, baseline floor 1227, +7 new S1 tests)

```text
Correctas! - Con error:     0, Superado:  1234, Omitido:     0, Total:  1234, Duración: 10 s - CommandCenter.Tests.dll (net10.0)
```

`test_output_hash: sha256:7e8038a2c385ea0dd6c53f0f4814af6e756276bfa28f7802fbfab0e3cc6578de`

**3. S1 focused filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter 'FullyQualifiedName~ClosureReportLineCurrency'` - exit `0` - matches claim (`7/7`)

```text
Correctas! - Con error:     0, Superado:     7, Omitido:     0, Total:     7, Duración: 5 s - CommandCenter.Tests.dll (net10.0)
```

`focused_output_hash: sha256:6b3a4e3757bd32f3a5a7caea7bc050218597af896df8fad1288917ce2836b6e4`

**4. Closure regression filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter 'FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure'` - exit `0` - matches claim (`100/100`)

```text
Correctas! - Con error:     0, Superado:   100, Omitido:     0, Total:   100, Duración: 5 s - CommandCenter.Tests.dll (net10.0)
```

`closure_output_hash: sha256:2b9f5f0b288391c3f099da46ee7912a6d9622632531f3b938280325469959ada`

**5. Frontend tests** - `npm test` (Web.Frontend) - exit `0` - matches claim (`273/273`)

```text
ℹ tests 273
ℹ suites 59
ℹ pass 273
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
```

`npm_test_output_hash: sha256:f00173080c56a7cdd9d23bbbfb13f81f7954b675b551e80bfe455e476baf8a61`

**6. Frontend lint** - `npm run lint` (Web.Frontend, oxlint) - exit `0`, no findings - matches claim

```text
> web-frontend@0.0.0 lint
> oxlint
```

`npm_lint_output_hash: sha256:1472f392035e28478ac827e6e5301e8adde36ba5a0a27bc61cdf6e42453bc7d3`

**7. Coverage gate (informational)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` then `python scripts/check-coverage.py CommandCenter.Tests/TestResults/36a17d92-851c-48e7-841f-8794284c3a9b/coverage.cobertura.xml` - both exit `0`

```text
Correctas! - Con error:     0, Superado:  1234, Omitido:     0, Total:  1234, Duración: 12 s - CommandCenter.Tests.dll (net10.0)

Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8364 min=0.7000 gap_a_70%=0.0000 [OK]
  Sales.Module       rate=0.9085 min=0.8000 gap_a_70%=0.0000 [OK]
  Inventory.Module   rate=0.8251 min=0.7200 gap_a_70%=0.0000 [OK]
```

`coverage_test_output_hash: sha256:afce5f1ad26578d34c4fd79801b146c3afa53ac377e6ffd62a6bc5e1cae4fbf6`; `coverage_gate_output_hash: sha256:e2296349ca131c65439f6c81f4f72c28dc36db986e50e8289687f81a460ca9dd`. Per `tasks.md`, the coverage gate belongs to the S3b baseline/restore; it is recorded here as informational only and is not an S1 completion gate.

### S1 Structure Evidence — single shared helper (AD-1)

| Check | Evidence | Status |
|-------|----------|--------|
| One rule implementation | `Sales.Module/Services/DailyClosureService.Rules.cs:8-32` defines the only `BuildReportDetail(paymentMethodId, methodName, declaredNative, expectedBsS, rate)` | VERIFIED |
| Declared path consumes it | `DailyClosureService.cs:341` -> `reportDetails.Add(BuildReportDetail(declared.PaymentMethodId, expected.PaymentMethodName, declared.Amount, expectedAmountBsS, exchangeRate))` | VERIFIED |
| Merged path consumes it | `DailyClosureService.cs:452` -> `reportDetails.Add(BuildReportDetail(exp.PaymentMethodId, exp.PaymentMethodName, declaredNative, exp.ExpectedAmountBsS, exchangeRate))` | VERIFIED |
| No divergent `ShiftReportDetailResult` copy remains | Repo-wide source scan (all `*.cs`, bin/obj excluded): exactly **one** `new ShiftReportDetailResult(` — `DailyClosureService.Rules.cs:24` | VERIFIED |
| Structural test is real | `SalesModule_HasExactlyOneShiftReportDetailResultConstructionSite` regex-scans every `.cs` under `Sales.Module` (bin/obj excluded) and asserts the count is exactly `1`; it is part of the focused 7/7 green run | VERIFIED |
| Structural test discriminates | Pre-fix the scan found **2** construction sites (one per copy), which is exactly why the test failed in the RED run; post-fix it finds **1** | VERIFIED |
| No inlined rule text left in the call sites | The pre-fix inline blocks (currency resolve, `ToUSD`, diff, tolerance status) are deleted from both `BuildDeclaredDetails` and `MergeMissingMethodsWithReport`; the modified file's only `ClosureStatus.Balanced` occurrence is the tolerance branch of the helper | VERIFIED |

### S1 Per-Currency Semantics Evidence (AD-1 exact)

`BuildReportDetail` matches the design formula line for line: `currency = PaymentMethodCurrencyResolver.Resolve(methodName)`; `systemAmount = currency == Usd ? PricingCalculator.ToUSD(expectedBsS, rate) : expectedBsS`; `difference = declaredNative - systemAmount`; `status = |difference| < 0.05m ? Balanced : (difference > 0 ? Surplus : Shortage)` (`DailyClosureService.Rules.cs:15-22`).

| Check | Evidence | Status |
|-------|----------|--------|
| Declared value is converted to the resolved currency | Declared path passes `declared.Amount`, already native by contract; merged path converts the persisting Bs.S actual before the diff: `Usd ? PricingCalculator.ToUSD(actualAmount, exchangeRate) : actualAmount` (`DailyClosureService.cs:447-450`) | VERIFIED |
| Diff is per currency | Both operands of `difference` are in the resolved currency: `declaredNative` (native) minus `systemAmount` (native-converted for USD) | VERIFIED |
| Tolerance is `0.05m` | `Math.Abs(difference) < 0.05m` in the single helper (`:20`); same tolerance in `ShiftReportMapper.MapDetails` (`:52`) | VERIFIED |
| No hardcoded `Balanced` on the merged path | Pre-fix `MergeMissingMethodsWithReport` emitted `ClosureStatus.Balanced` unconditionally; the post-fix merged call site contains no status literal; the only helper literal is inside the tolerance branch | VERIFIED |
| No Bs.S/USD mixing on a line | Pre-fix the merged line compared a Bs.S `actualAmount` against a USD `SystemAmount` (mixed-unit diff). Post-fix both sides are converted first; runtime tests assert USD-only values for USD methods and Bs.S-only values for local methods | VERIFIED |
| Former defect lines are gone | `git show 605b3aa -- Sales.Module/Services/DailyClosureService.cs` deletes the inline `systemAmount/declaredAmount/diff/status` block, the mixed-unit `actualAmount - (expCurrency == Usd ? ToUSD(...) : ...)` expression and the `ClosureStatus.Balanced` argument; the replacement is the two `BuildReportDetail` calls | VERIFIED |

### S1 Scenario Evidence Matrix

Counts taken from the S1 delta spec headings: **1 requirement / 4 scenarios** (`openspec/changes/critical-debt-closeout/specs/payment-method-currency-classification/spec.md`, REQ-PMC-06).

| Requirement | Scenario | Covering test (focused 7/7 green) | Result |
|-------------|----------|-----------------------------------|--------|
| REQ-PMC-06 | Merged USD line is single-currency | `ClosureReportLineCurrencyTests.MergedUsdLine_ExpressesDeclaredSystemAndDifferenceInUsd` (non-cash USD, expected 2500 Bs.S -> declared/system/difference all USD `50/50/0`, `DeclaredAmount != 2500`) + `MergedUsdCashMethod_NonZeroDifference_IsShortageNeverBalanced` (USD cash: `0/50/-50` USD) | ✅ COMPLIANT |
| REQ-PMC-06 | Non-zero difference is never Balanced | `MergedUndeclaredCashMethod_NonZeroDifference_IsShortageNeverBalanced` (local cash, `-1000` -> `Shortage`, explicitly `!= Balanced`) + `MergedUsdCashMethod_NonZeroDifference_IsShortageNeverBalanced` (`-50` USD -> `Shortage`) + `DeclaredUsdLine_PerCurrencyDifference_IsSurplus` (declared USD `60` vs `50` -> `Surplus`) | ✅ COMPLIANT |
| REQ-PMC-06 | Within-tolerance difference stays Balanced | `MergedLocalLine_WithinTolerance_StaysBalanced` (local non-cash, `0` difference -> `Balanced`; zero-difference falls inside `< 0.05m`) | ✅ COMPLIANT (guard; see discrimination note) |
| REQ-PMC-06 | Persisted snapshot is untouched | `PersistedClosureSnapshot_IsNotRewrittenByLaterClosureCommand` (pre-seeded closure `Id=10` read back through `GetClosureAsync` and a raw `AsNoTracking` EF read after a later closure command; `TotalExpectedBsS/TotalActualBsS/TotalDifferenceBsS` and detail `ExpectedAmountBsS/ActualAmountBsS/DifferenceBsS` unchanged) | ✅ COMPLIANT |

**Compliance summary**: 4/4 scenarios compliant, 0 UNTESTED, 0 FAILING; requirement-level completeness 1/1 (REQ-PMC-06 satisfied).

### Discrimination Review — RED evidence assessed

The RED claim in `apply-progress.md` cannot be re-executed without mutating the workspace, so it was re-derived logically from the parent revision (`git show 605b3aa^`), which is deterministic for these tests. Pre-fix semantics: `actualAmount = methodEntity != null && methodEntity.IsCash ? 0m : exp.ExpectedAmountBsS`; the merged report line used `actualAmount` as declared, `ToUSD(expectedBsS)` as system for USD, a mixed-unit difference, and an unconditional `ClosureStatus.Balanced`; two `ShiftReportDetailResult` construction sites existed.

| Test | Expected result on pre-fix code | Consistent with RED claim? |
|------|--------------------------------|----------------------------|
| `MergedUsdLine_ExpressesDeclaredSystemAndDifferenceInUsd` | FAIL — pre-fix declared `2500` (Bs.S) vs asserted `50` (USD) and difference `2450` vs asserted `0` | Yes (claimed failure) |
| `MergedUndeclaredCashMethod_NonZeroDifference_IsShortageNeverBalanced` | FAIL — pre-fix status `Balanced` vs asserted `Shortage` (declared `0` and difference `-1000` already matched) | Yes (claimed failure) |
| `MergedUsdCashMethod_NonZeroDifference_IsShortageNeverBalanced` | FAIL — pre-fix status `Balanced` vs asserted `Shortage` | Yes (claimed failure) |
| `SalesModule_HasExactlyOneShiftReportDetailResultConstructionSite` | FAIL — pre-fix scan count `2` vs asserted `1` | Yes (claimed failure) |
| `MergedLocalLine_WithinTolerance_StaysBalanced` | PASS — pre-fix non-cash actual equals expected, so the line was already `1000/1000/0`, `Balanced` | Yes (claimed pass; guard, not a defect discriminator) |
| `DeclaredUsdLine_PerCurrencyDifference_IsSurplus` | PASS — the declared-path arithmetic was already correct pre-fix | Yes (claimed pass) |
| `PersistedClosureSnapshot_IsNotRewrittenByLaterClosureCommand` | PASS — the persisted snapshot was untouched pre-fix too | Yes (claimed pass; regression guard) |

The derived pre-fix outcome is exactly **4 failed / 3 passed**, matching the recorded RED evidence. The discriminating set for the defect is tests 1, 2, 3 and 7; tests 4-6 are regression guards. No workspace mutation was performed for this review (working tree unchanged), so the "revert byte-identically" precondition was never needed.

### Response-Only Evidence (REQ-PMC-06 last sentence)

| Check | Evidence | Status |
|-------|----------|--------|
| Persisted `ClosureDetail` write path untouched | `MergeMissingMethodsWithReport` still persists `ExpectedAmountBsS = exp.ExpectedAmountBsS`, `ActualAmountBsS = actualAmount`, `DifferenceBsS = actualAmount - exp.ExpectedAmountBsS` (`DailyClosureService.cs:438-445`) — byte-identical to the parent; only the report-detail construction below it changed | VERIFIED |
| No snapshot recomputation | `GetClosureAsync` (`:170-174`) loads the persisted entity through `LoadClosureEntityAsync` (`:176-183`, `AsNoTracking` + `AsSplitQuery` + `Include`) and projects it via `ShiftReportMapper.MapClosure`; no rate/market recomputation path exists | VERIFIED |
| `RecalculateTotals` untouched | `DailyClosureService.cs:462-477` unchanged by the commit | VERIFIED |
| Regression test covers read-back | `PersistedClosureSnapshot_IsNotRewrittenByLaterClosureCommand` asserts via both the service DTO and a raw EF read after a later closure command | VERIFIED |

### Correctness (Static Evidence)

| Requirement | Status | Notes |
|-------------|--------|-------|
| REQ-PMC-06 single-currency declared/system/difference | ✅ Implemented | Single `BuildReportDetail`; both call sites consume it; exactly one construction site repo-wide |
| REQ-PMC-06 derived status with declared-path tolerance | ✅ Implemented | `0.05m` tolerance; `Surplus`/`Shortage` by sign; no hardcode on the merged path |
| REQ-PMC-06 no unit mixing | ✅ Implemented | Merged declared value converted to the resolved currency before the diff |
| REQ-PMC-06 persisted snapshot not recomputed/rewritten | ✅ Implemented | Persisted block unchanged; read path maps persisted rows only |
| Declared-path semantics unchanged (S1-03) | ✅ Preserved | Closure filter 100/100 green; `DeclaredUsdLine_PerCurrencyDifference_IsSurplus` green |

### Coherence (Design)

| Decision | Followed? | Notes |
|----------|-----------|-------|
| AD-1 (one `BuildReportDetail`; both call sites consume it; merged declared converted to native; `0.05m` tolerance; persisted `ClosureDetail` untouched) | ✅ Yes | Exact signature and formula; both call sites re-pointed; persisted block untouched |
| OQ-4 (merged USD declared value uses `PricingCalculator.ToUSD` for symmetry) | ✅ Yes (as chosen) | The design's parenthetical says "4-dp rounding"; the helper's actual default is 2 dp, and both converted sides use the same helper, so symmetry holds — documentation nit only (RESIDUAL-S1-06) |
| AD-8 S1 structural check (exactly one `new ShiftReportDetailResult` in `Sales.Module`) | ✅ Yes | Enforced by the structural test and confirmed by a repo-wide scan |
| AD-7 slice chaining; S1 revertible alone | ✅ Yes | The commit touches only S1 files plus docs/openspec artifacts; rollback boundary matches `apply-progress.md` |

### Scope Check

| Check | Evidence | Status |
|-------|----------|--------|
| No S2+ production creep | `git show 605b3aa --name-only` lists only `CommandCenter.Tests/Unit/ClosureReportLineCurrencyTests.cs`, `Sales.Module/Services/DailyClosureService.Rules.cs`, `Sales.Module/Services/DailyClosureService.cs`, `docs/reporte.txt`, `apply-progress.md`, `tasks.md`. No `CashAdvance*`, `CashDrawer*`, `RegisterPage.jsx`, `ProductDialogViewModel*` file appears | VERIFIED |
| S1 diff inside `DailyClosureService.cs` limited to the slice | `+14 / -27`: the `partial` keyword plus the two call-site replacements; the rest of the file (incl. legacy `CreateClosureAsync` `:112`, `ExecuteClosureCoreAsync` `:123`, receipts, rollback) is untouched | VERIFIED |
| No schema/migration | No migration file in the commit | VERIFIED |
| Size signal | Authored code+test delta = **331 changed lines** (`+257` tests, `+33` Rules, `+14/-27` service) — under the 400-line review budget. The commit's 499 insertions / 32 deletions include `docs/reporte.txt` (+100), `apply-progress.md` (+90) and `tasks.md` (±5), which are not authored production code | VERIFIED |

### GGA Classification Audit (`--no-verify` claim)

`apply-progress.md` records `gga run` = STATUS FAILED and classifies every remaining finding as pre-existing/out-of-slice. Independently checked:

| GGA finding | Independently verified state | Classification correct? |
|-------------|------------------------------|-------------------------|
| Explanatory comment `DailyClosureService.cs:287` (`// 8.7-B5: ...`) | Present at `:287` and already present in the parent revision (`git show 605b3aa^` contains the same comment); an allowed 8.x traceability marker | ✅ Pre-existing |
| `DailyClosureService.cs` 637 lines > 500 ceiling | Current file `637` lines (`.Count`); parent `650` lines — the file was already over the ceiling and S1 reduced it by net 13 lines; split is S3a scope | ✅ Pre-existing / out of slice |
| `ResolveUserDetailsAsync` `FindAsync` without `AsNoTracking` (`:360`) | Present at `:360`; `ResolveUserDetailsAsync` is untouched by the S1 diff | ✅ Pre-existing |
| Legacy `CreateClosureAsync` returning the `DailyClosure` entity (`:112`) | Present at `:112`; deleted by S3b-06; untouched by S1 | ✅ Pre-existing / out of slice |
| Test naming 2-segment vs 3-segment (advisory) | New tests follow the repo-wide convention (e.g. `PersistedClosureSnapshot_IsNotRewrittenByLaterClosureCommand`); advisory only | ✅ Accepted |
| In-slice finding (missing `CancellationToken` in new test helpers) | The committed test file passes `CancellationToken` through both seed helpers | ✅ Resolved |

No S1-introduced GGA finding remains; the `--no-verify` justification is consistent with the evidence.

### Issues Found

**CRITICAL**: None.
**WARNING**: None blocking for the slice. Non-blocking residuals recorded below (RESIDUAL-S1-01..07).
**SUGGESTION**: Consolidate `ShiftReportMapper.MapDetails` (`:41-66`) with `BuildReportDetail` when a future slice touches the report path — the tolerance/conversion arithmetic is duplicated today (see RESIDUAL-S1-02).

### Residual Warnings (non-blocking for S1)

- **RESIDUAL-S1-01 (change scope)** — S2/S3a/S3b are unimplemented (23 of 28 tasks unchecked); the change-level verdict is pending and must not be inferred from this slice verdict.
- **RESIDUAL-S1-02 (duplicate arithmetic site)** — `ShiftReportMapper.MapDetails` (`Sales.Module/Services/ShiftReportMapper.cs:41-66`) independently derives the same per-currency declared/system/difference values and `0.05m` tolerance status for the persisted-report/DTO surface. It is behaviorally aligned with `BuildReportDetail` (both sides converted; derived status) and was not part of the WARNING-04 defect, but it is a second copy of the rule that AD-1's dedup did not remove. Consolidation candidate.
- **RESIDUAL-S1-03 (file size, pre-existing)** — `DailyClosureService.cs` is 637 lines (`> 500` ceiling); parent was 650. Explicitly S3a scope (S3a-01..S3a-04).
- **RESIDUAL-S1-04 (GGA status)** — `gga run` reports STATUS FAILED; every remaining finding audited as pre-existing/out-of-slice (see audit table). No in-slice finding remains.
- **RESIDUAL-S1-05 (evidence arithmetic defect)** — `apply-progress.md` states "331 additions + 27 deletions = 358 changed lines". The `git show 605b3aa --numstat` values are `+257/+33/+14` (304 additions) and `27` deletions = **331 changed lines**; the record double-counts the deletions. The correct figure is still under the 400-line budget.
- **RESIDUAL-S1-06 (design wording)** — AD-1/OQ-4 text says the merged declared value uses `PricingCalculator.ToUSD` with "4-dp rounding"; the helper's actual default is 2 dp (`Core/Helpers/PricingCalculator.cs:75`). Symmetry is unaffected because both converted values use the same helper. Documentation nit only.
- **RESIDUAL-S1-07 (environment)** — `opencode.json` is modified in the working tree (` M`) and was already modified before this verification; it is not part of the S1 commit and was not touched. The coverage numbers are recorded against a fresh run: Core 0.8364 / Sales.Module 0.9085 / Inventory.Module 0.8251, all above threshold.

### Remaining Slices (Pending)

| Slice | Status | Verification |
|-------|--------|--------------|
| S1 - Closure-line currency (WARNING-04, REQ-PMC-06) | Implemented at `605b3aa` | **PASS_WITH_WARNINGS** - 0 blockers, 0 critical findings; REQ-PMC-06 1/1 and 4/4 compliant; this section |
| S2 - Advance commission contract + source mapping + H-13 | Implemented at `67409da` (11/11 tasks) | **PASS_WITH_WARNINGS** - 0 blockers, 0 critical findings; REQ-CAP-02/03 + REQ-ADB-05 3/3 and 11/11 compliant; see the Slice S2 section |
| S3a - `DailyClosureService` split | Not implemented (0/4 tasks) | Pending |
| S3b - Legacy entry removal + 18 re-points | Not implemented (0/8 tasks) | Pending |

### Validator Admission

For S1, `gentle-ai sdd-verify-validate --input <candidate bytes> --requirements 1 --scenarios 4` (exit `0`) admitted the exact candidate bytes before persistence; the same command over the persisted `openspec/changes/critical-debt-closeout/verify-report.md` (exit `0`) re-admitted the written file. No write occurred before admission; the report file did not exist before the S1 verification, so no prior report was overwritten. For the S2 update, `gentle-ai sdd-verify-validate --input <S2 candidate bytes> --requirements 4 --scenarios 15` (exit `0`) admitted the exact cumulative candidate bytes before persistence, and the same command over the persisted report (exit `0`) re-admitted the written file. No write occurred before admission in either slice.

### Slice S1 Verdict

**PASS_WITH_WARNINGS** — REQ-PMC-06 is implemented exactly as designed (single shared `BuildReportDetail`, per-currency values, derived `0.05m`-tolerance status, response-only persistence) and proved by fresh runtime evidence at `605b3aa`: build `0/0`, backend `1234/1234`, S1 filter `7/7`, closure filter `100/100`, frontend `273/273`, lint clean, coverage gate green. The RED evidence claim (4 failed / 3 passed pre-fix) re-derives exactly and the structural test is genuinely discriminating. The change-level verdict remains **pending** until S2, S3a and S3b are implemented and verified.

---

## Slice S2 — Advance commission contract + source mapping + H-13 (REQ-CAP-02/03, REQ-ADB-05; AD-2/3/6)

**Commit**: `67409daa140396ca07a65a8cb8134ab76f9f5252` ("fix(8.141): comision server-sourced + source mapping (S2, item 41 + S4b-04 + H-13) - ANEXO 8.141"); parent `80c171c` (S1 acceptance docs). All eleven S2 tasks (`S2-01`..`S2-11`) are checked in `tasks.md`.
**Verified revision**: `HEAD` = `67409da`; working tree before and after every command: `git status --porcelain` reports only ` M opencode.json` (pre-existing; present before this verification started). This verification made no commit. The only workspace touch was the temporary H-13 mutation, reverted byte-identically (hash proof below).
**evidence_revision (S2)**: SHA-256 of the ASCII string produced by joining, with `:`, the per-file SHA-256 hex digests of the 20 S2 production/test files, sorted by path, each hashed from its on-disk bytes: `Backend.API/Controllers/CashDrawerController.cs` `a7b80787e3e612024acac029b8e66b77965e75db055839056aaede2c7a27d82d`, `CommandCenter.Tests/CashAdvanceTests.cs` `6f4cb9ae1004f6d9b0a8d20952cbe7f37bfee99476b489ae124e06981b096e05`, `CommandCenter.Tests/CashDrawerClosureTests.cs` `8a36897dbeac96497c38f96b6f898032f7cffe458910aadf759938a07971dbb4`, `CommandCenter.Tests/Unit/CashAdvanceCommissionEndpointTests.cs` `1921aabc110a14b3c11e3112adefd4bf62fa599545957ed19859e39f3961c1f1`, `CommandCenter.Tests/Unit/ProductDecimalRangeValidationTests.cs` `b44f9c9a22dbf01efe2a14382380177c1207464351670d27a4426af289332837`, `Desktop.Client.Core/Services/CashDrawerService.cs` `6a5323b576fb16b530d20d2ef12a1e86083af55cde21796cabe7750b10766e4e`, `Desktop.Client.Core/Services/ICashDrawerService.cs` `b887f93a308b7fccdb8073b0363d32f9c4c857fc0725904dc3fca68ac01ed230`, `Desktop.Client.Core/ViewModels/AddProductViewModel.cs` `b5cf7bb54aec2306387da34f336733bc703e32525d0fc0b0dd4c367a4c72c3fc`, `Desktop.Client.Core/ViewModels/CashAdvanceRegisterViewModel.cs` `6b358e8a0dc74960ab951d90719c0f0d9beac86241b06dec461b5d39eb099883`, `Desktop.Client.Core/ViewModels/ProductDialogViewModel.Pricing.cs` `1fe40e29224571cd2609f9fd7a5069079cdc68fd966e8e87aaf2dd277360e465`, `Desktop.Client/Services/WpfDialogService.cs` `8ea17294a17ca1f5930af010c978e8b2f8f4a9a5aecaaac9e768485f60829c31`, `Desktop.Client/Services/WpfDialogService.Modals.cs` `7fd1ff9e4e872cdc4a7970ec702da163f3c78722113631d58e2fb1cdcbebe8a4`, `Sales.Module/DTOs/CashAdvanceCommissionDto.cs` `259f96daaa484cc056eee7feb735d95b05dabd941a22c52db888e6a63ed87896`, `Sales.Module/Services/CashAdvanceCoordinator.cs` `b1e4c68c74cfd04abf440bfda297bf6eea647455b366680d1a47dada070db9f9`, `Web.Frontend/src/components/register/CashAdvanceModal.commission.test.js` `407f5d12b7d517c8c52afb862ebffd22ca8aac83670b0aaaa30393a34e718254`, `Web.Frontend/src/components/register/CashAdvanceModal.jsx` `ec18253955c6ed219fcb2ce499412c689dea39b80b0aa4f0b0047168c1fa2dd3`, `Web.Frontend/src/constants/cashTransactionSource.js` `ff2d9a5a066eb5b1a2afcec791e965ebf5b404c2b60ba907f758918af21c01b1`, `Web.Frontend/src/constants/cashTransactionSource.test.js` `1566f15b0c6a0dce330a2df8f4af9c816fd6575443264988b395fce4020d90fe`, `Web.Frontend/src/pages/RegisterPage.jsx` `46ef630123664e39c9b045b63605d0fcb7747dd4c21d73220e2c112a9f46edf8`, `Web.Frontend/src/pages/RegisterPage.sourceFilter.test.js` `c73aea38f5c7e658a91832578252a68a3cf6eae026025cf7dcff5aee11c3668c`; combined digest `sha256:4e9e8ebbfd12d23a45562a90fa069f63c7e00d0fdb7c7da0f96a5f4503c58de5`. The head `evidence_revision` (`sha256:72c5fbf1...`) is the same construction over the 23-file S1+S2 union.
**Hash definition**: same single recipe as S1 — `build_output_hash` / `test_output_hash` are the SHA-256 of the combined stdout+stderr captured for the command execution reported below, normalized to UTF-8 without BOM, `CRLF` -> `LF`, trailing newlines trimmed. The same recipe applies to every command hash recorded in this section.

### Re-executed Evidence (verbatim)

Every S2 claim in `apply-progress.md` and the `tasks.md` Verification section was re-executed independently from the working tree at `67409da`. All exit codes are `0`; every count matches the claim exactly.

**1. Build** - `dotnet build CommandCenter.slnx -c Release` - exit `0` - matches claim (`0` errors, `0` warnings)

```text
Compilación correcta.
    0 Advertencia(s)
    0 Errores

Tiempo transcurrido 00:00:04.59
```

`build_output_hash: sha256:0b35b0a655af01a569528f1c7aaaa2ebc51df1d4b9f86aee6e03d533c54606fb`. A first uncaptured full-solution build also compiled all nine projects with `0/0`; the captured incremental re-run is the reported evidence.

**2. Backend suite** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` - exit `0` - matches claim (`1254/1254`, baseline floor 1227, +20 vs S1)

```text
Correctas! - Con error:     0, Superado:  1254, Omitido:     0, Total:  1254, Duración: 13 s - CommandCenter.Tests.dll (net10.0)
```

`test_output_hash: sha256:f19c9e70c96a8ba879dc37e64b0b1f95e3d045917e9adda4248ad91ae7a68ad5`

**3. CashAdvance filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~CashAdvance"` - exit `0` - matches claim (`55/55`)

```text
Correctas! - Con error:     0, Superado:    55, Omitido:     0, Total:    55, Duración: 2 s - CommandCenter.Tests.dll (net10.0)
```

`filter_output_hash: sha256:20be59e3aa35a80464f086e6d8a8792bec9857c30b7b8023140a7348a1e10a8a`

**4. S2 focused filter** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~CashAdvanceCommission|FullyQualifiedName~ProductDecimalRangeValidation|FullyQualifiedName~CashAdvanceRegisterViewModel"` - exit `0` - matches claim (`21/21`)

```text
Correctas! - Con error:     0, Superado:    21, Omitido:     0, Total:    21, Duración: 2 s - CommandCenter.Tests.dll (net10.0)
```

`focused_output_hash: sha256:c18e1285e46d4d09b5915600c49116151e7c2575fef40dce5cc7930cd62b98ee`

**5. Frontend tests** - `npm test` (Web.Frontend) - exit `0` - matches claim (`287/287`, baseline floor 273, +14)

```text
ℹ tests 287
ℹ suites 62
ℹ pass 287
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
```

`npm_test_output_hash: sha256:8dd368373849f50a5f80ee52d452c5e4c495dc9068594857ec41c088668c9e72`

**6. Frontend lint** - `npm run lint` (Web.Frontend, oxlint) - exit `0`, no findings - matches claim

```text
> web-frontend@0.0.0 lint
> oxlint
```

`npm_lint_output_hash: sha256:1472f392035e28478ac827e6e5301e8adde36ba5a0a27bc61cdf6e42453bc7d3` (identical to the S1 lint output by construction)

**7. Coverage gate (informational until S3b)** - `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` then `python scripts/check-coverage.py CommandCenter.Tests/TestResults/d3a5cfe9-e7bd-42bc-8113-1a7013515eae/coverage.cobertura.xml` - both exit `0`

```text
Correctas! - Con error:     0, Superado:  1254, Omitido:     0, Total:  1254, Duración: 13 s - CommandCenter.Tests.dll (net10.0)

Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):
  Core               rate=0.8364 min=0.7000 gap_a_70%=0.0000 [OK]
  Sales.Module       rate=0.9087 min=0.8000 gap_a_70%=0.0000 [OK]
  Inventory.Module   rate=0.8251 min=0.7200 gap_a_70%=0.0000 [OK]
```

`coverage_test_output_hash: sha256:affe94b596bc2cc2781a7bf83551740b98ddf91886ec67eaf41822d1ba256bd3`; `coverage_gate_output_hash: sha256:43df91a15b387db78611ba846d677f5d30a4596c437d6f428eee4d0d52384303`. Per `tasks.md`, the coverage gate belongs to the S3b baseline/restore; it is recorded here as informational only and is not an S2 completion gate.

### S2 Structure Evidence — shared commission read contract (AD-2)

| Check | Evidence | Status |
|-------|----------|--------|
| Route exists with roles and fail-closed behavior | `Backend.API/Controllers/CashDrawerController.cs:250-262`: `[HttpGet("advance-commission")]` + `[Authorize(Roles = "Admin,Manager,Cashier")]`; resolvable → `Ok(new CashAdvanceCommissionDto(isTransfer, percentage))`; unresolvable → `this.ApiUnprocessableEntity(...)`, which builds a `ProblemDetails` `ObjectResult` with `StatusCode = 422` (`ApiProblemResults.cs:24-28`) | VERIFIED |
| One rule core — no duplicated calculation | `Sales.Module/Services/CashAdvanceCoordinator.cs`: public `TryGetCommissionPercentageAsync` (`:180-181`) and throwing `ResolveCommissionPercentageAsync` (`:202-218`) both delegate to the single private `ResolveCommissionCoreAsync` (`:183-200`); `ProcessAsync` consumes the throwing adapter (`:64`) | VERIFIED |
| Only one production commission parser | Repo-wide source scan: the only production uses of `CashAdvance.TransferCommissionPct` / `CashAdvance.CashCommissionPct` are the `Core/Constants/SettingKeys.cs` definitions and `CashAdvanceCoordinator` (`:188-189` core; `:209-210` error-key echo). No other production service parses or defaults a commission | VERIFIED |
| Verbatim rejection message preserved | The `InvalidOperationException` text is unchanged from the parent (the diff moved the throw into the adapter without editing the string); `ProcessAsync_WhenCommissionUnresolvable_StillThrowsVerbatimMessage` asserts `"no está configurada"` + the key name | VERIFIED |
| DTO boundary | `Sales.Module/DTOs/CashAdvanceCommissionDto.cs` — `sealed record (bool IsTransfer, decimal Percentage)`; the action returns the DTO, never an entity | VERIFIED |
| WPF consumes the server value | `Desktop.Client.Core/Services/CashDrawerService.cs:76-89` GETs the route, 422 → `null`, success → `dto.Percentage > 0 ? dto.Percentage : null`; `CashAdvanceRegisterViewModel.RefreshCommissionAsync` (`:82-103`) is request-versioned, failures log + null; `CanConfirm` requires `CommissionPercentage > 0` (`:80`, lifted comparison false for `null`); dialog wiring passes the service and awaits the read before `ShowDialog` (`Desktop.Client/Services/WpfDialogService.Modals.cs`) | VERIFIED |
| Web consumes the server value | `CashAdvanceModal.jsx:68-85` reads the route per selected channel (effect keyed on `isOpen, isTransfer, selectedMethodId`); `resolveCommissionPercentage` accepts only a finite number `> 0` (`:11-14`); unresolved → fixed blocked message (`:185-189`), `!hasResolvedCommission` blocks submit (`:130-133, :255`) | VERIFIED |
| Zero 7/10 commission literals in clients | Independent scan of the 8 S2 production client files (`7.0m|10.0m|0.07m|0.10m|7.0|10.0|7%|10%`) → zero matches. Parent had `IsTransfer ? 7.0m : 10.0m` (VM), `isTransfer ? 7 : 10` and `'Comisión 7%'/'Comisión 10%'` (modal) | VERIFIED |
| Previewed percentage equals charged percentage | Both adapters share the one core, and `CashAdvanceRequest` carries no commission field, so the server always computes the charge from the same setting read; `PreviewedPercentage_EqualsChargedPercentage` asserts `previewed == charge.CommissionPercentage` | VERIFIED |

### S2 Structure Evidence — single transaction-source mapping (AD-3)

| Check | Evidence | Status |
|-------|----------|--------|
| Single contract-aligned module | `Web.Frontend/src/constants/cashTransactionSource.js`: frozen `CashTransactionSource` 0..6 matching the server enum (`Sales.Module/Entities/CashTransaction.cs:13-24`), one frozen `SOURCE_DEFINITIONS` `{id,key,label}` table, and `getSourceLabel` / `getSourceIdByFilterKey` / `matchesSourceFilter` all resolving from it | VERIFIED |
| Filter resolves from the mapping | `RegisterPage.jsx:27` imports the module; `:122` `if (!matchesSourceFilter(sourceFilter, tx.source)) return false;`; the four `sourceFilter === '...' && tx.source !== N` lines are gone | VERIFIED |
| Labels resolve from the same mapping | `RegisterPage.jsx:327, :451, :479` render `getSourceLabel(tx.source)`; no local `function getSourceLabel` exists | VERIFIED |
| Inverted labels corrected | Parent mapping: 2 → 'Ajuste Manual', 3 → 'Cierre Caja', 4 → 'Adelanto Efectivo' (inverted). Current table: 2 → 'Adelanto Efectivo', 3 → 'Ajuste Manual', 4 → 'Cierre Caja' | VERIFIED |
| The `advance` defect is fixed | Parent filter `advance` → `tx.source !== 4` (matched `Closing` 4, excluded `CashAdvance` 2). Current: `matchesSourceFilter('advance', 2) === true`, `matchesSourceFilter('advance', 4) === false`, asserted by tests | VERIFIED |
| No bare source ordinal in filters/labels | Own grep + `RegisterPage.sourceFilter.test.js`: no `source (===|!==|==|!=) <digit>`; the only remaining source comparisons use `CashTransactionSource.Opening` (`:93, :105`) | VERIFIED |

### S2 Structure Evidence — decimal-only ranges (AD-6 / H-13)

| Check | Evidence | Status |
|-------|----------|--------|
| 12 decimal properties use decimal-only bounds | `ProductDialogViewModel.Pricing.cs` lines 11, 15, 19, 23, 27, 31, 38, 42 (8 properties) and `AddProductViewModel.cs` lines 30, 34, 38, 53 (4 properties) use `[Range(typeof(decimal), "0", "79228162514264337593543950335", …)]`; no `double.MaxValue` remains in either file | VERIFIED |
| Test enumerates exactly those 12 | `CommandCenter.Tests/Unit/ProductDecimalRangeValidationTests.cs` lists the 8+4 `(Type, Property)` pairs and asserts `OperandType == typeof(decimal)`, `IsValid(decimal.MaxValue)`, `!IsValid(-1m)`, and `Validator.TryValidateValue(decimal.MaxValue, …)` | VERIFIED |
| Test genuinely discriminates (independent mutation) | Temporarily re-introducing `[Range(0, double.MaxValue, …)]` on `Price` produced focused run exit `1` with `DecimalMoneyProperties_UseDecimalOperandRange_NotDouble` FAILED (1 failed / 2 passed) — exactly the apply-progress claim. The mutation was reverted byte-identically: `git hash-object` of the file = `171ef9f493fe740ebb4149b10e530fa79cd1d865` = `HEAD:Desktop.Client.Core/ViewModels/ProductDialogViewModel.Pricing.cs`, `git diff` empty, `git status --porcelain` shows no entry for the file; the post-revert focused run is 3/3 green | VERIFIED |
| Discriminator rationale | On .NET 10 the double-bounded attribute does not overflow on `decimal.MaxValue` (the value converts to double and compares), so the max-value assertion alone is not discriminating; the `OperandType` assertion is the one that genuinely fails on reintroduction — empirically proved by the mutation | VERIFIED |

### Discrimination Review — RED evidence assessed

The RED claims cannot be re-executed without mutating the workspace, so they were re-derived deterministically from the parent revision (`git show 67409da^`).

| Claim | Parent-state check (deterministic) | Consistent with claim? |
|-------|------------------------------------|------------------------|
| Backend RED = compile failures (CS0117/CS1061) naming the new symbols | The parent lacks `CashDrawerController.GetAdvanceCommission` / the `advance-commission` route, `CashAdvanceCommissionDto`, `CashAdvanceCoordinator.TryGetCommissionPercentageAsync`, client `ICashDrawerService.GetAdvanceCommissionAsync`, the `cashDrawer` ctor parameter and `RefreshCommissionAsync`; every cited symbol is absent | Yes |
| Web RED = `ERR_MODULE_NOT_FOUND` for `src/constants/cashTransactionSource.js` | The constants file does not exist in the parent tree (`git cat-file -e 67409da^:...` fails, exit 128); the new test imports it | Yes |
| Web RED = RegisterPage scan failures | The parent has the bare `advance` filter `tx.source !== 4` and the local inverted `getSourceLabel` switch — exactly what the scan asserts absent | Yes |
| Web RED = modal helper imports missing | The parent modal exports no `resolveCommissionPercentage` / `computeAdvanceSummary` and computes `isTransfer ? 7 : 10` with hardcoded labels | Yes |
| H-13 RED | The parent uses `[Range(0, double.MaxValue)]`, so the `OperandType` assertion fails | Yes |

Tautology flags (assessed; none blocking):

- `PreviewedPercentage_EqualsChargedPercentage` is near-tautological at unit level because both adapters share the single core — that sharing is precisely the AD-2 guarantee. It would still catch a divergent adapter, key or conversion; accepted as a contract guard (RESIDUAL-S2-03).
- `GetAdvanceCommission_AllowsCashierAndRejectsAnonymousAndDriver` is attribute-level reflection, not a runtime 401/403 (see the auth caveat below). It would fail on a role or `AllowAnonymous` change, so it is not tautological, but it is weaker than the threat-matrix wording implies.
- `CashAdvanceModal.commission.test.js` uses `renderToString`, so `useEffect` does not run: it proves the unresolved-state render and disabled submit, not the fetch→render→submit interaction. The pure resolver tests and the source scan cover the read path; the interaction itself has no test (RESIDUAL-S2-06).
- The literal scans (`7 : 10`, `Comisión 7`, `percentage = (7|10)`) target the recorded defect shape; the discriminating property holds for the actual regression.

### S2 Scenario Evidence Matrix

Counts taken from the S2 delta spec headings: **3 requirements / 11 scenarios** (`cash-advance-payout-integrity` REQ-CAP-02 1/4 + REQ-CAP-03 1/3; `api-dto-boundary` REQ-ADB-05 1/4).

| Requirement | Scenario | Covering test (all green in the re-execution) | Result |
|-------------|----------|-----------------------------------------------|--------|
| REQ-CAP-02 | Configured commission is applied | `CashAdvanceCoordinatorTests.ProcessAsync_ConfiguredCommissionApplied` (5.5 → commission 55 / total 1055) + `PreviewedPercentage_EqualsChargedPercentage` | ✅ COMPLIANT |
| REQ-CAP-02 | Preview shows the server-resolved percentage | `CashAdvanceRegisterViewModel_PreviewShowsServerResolvedPercentage` (5.5, not 7/10; amount 55; total 1055) + `GetAdvanceCommission_WithConfiguredTransferPercentage_Returns200WithServerValue` + web `takes the percentage from the server response, not from a literal` and `computes the preview from the resolved percentage only` | ✅ COMPLIANT |
| REQ-CAP-02 | Preview follows the selected channel | `CashAdvanceRegisterViewModel_PreviewFollowsTheSelectedChannel` (5.5 → 12.25, amount 122.50) + `GetAdvanceCommission_WithConfiguredCashPercentage_ReturnsTheOtherChannelValue` + `TryGetCommissionPercentageAsync_ResolvesPerSelectedChannel` | ✅ COMPLIANT |
| REQ-CAP-02 | No client literal remains | Web `keeps no hardcoded 7/10 commission literal` + VM tests compile against the server-sourced nullable property + independent grep = zero 7/10 literals | ✅ COMPLIANT |
| REQ-CAP-03 | Missing commission rejects the advance | `ProcessAsync_WhenCommissionUnresolvable_StillThrowsVerbatimMessage` + pre-existing `ProcessAsync_MissingCommissionRejectsWithoutPayoutOrSale` and `ProcessCashAdvance_Cash_MissingCommissionRejectsWithoutPayoutOrSale` (drawer balance unchanged at 2000, `Sales.Count == 0`) | ✅ COMPLIANT |
| REQ-CAP-03 | Preview without a resolvable percentage blocks submission | `CashAdvanceRegisterViewModel_WhenCommissionUnresolved_BlocksConfirmation` (null percentage, `CanConfirm == false`) + `GetAdvanceCommission_WhenUnresolvable_Returns422WithoutPercentageOrDefault` + `ClientGetAdvanceCommission_OnUnprocessableEntity_ReturnsNull` + web `reads the new server route and blocks submission while unresolved` and `renders without a fabricated percentage and with submission blocked` | ✅ COMPLIANT |
| REQ-CAP-03 | Previewed percentage equals the charged percentage | `PreviewedPercentage_EqualsChargedPercentage` (previewed 5.5 == `charge.CommissionPercentage`) — guard, see tautology note | ✅ COMPLIANT |
| REQ-ADB-05 | Advance filter includes advances | `cashTransactionSource.test.js` → `resolves the advance filter to CashAdvance (2) and never to Closing (4)` (`matchesSourceFilter('advance', 2) === true`) | ✅ COMPLIANT |
| REQ-ADB-05 | Advance filter excludes closings | Same test (`matchesSourceFilter('advance', 4) === false`; also 3 false) | ✅ COMPLIANT |
| REQ-ADB-05 | Labels agree with the contract | `labels 2 as Adelanto Efectivo, 3 as Ajuste Manual and 4 as Cierre Caja` + `never labels a non-advance value as an advance` | ✅ COMPLIANT |
| REQ-ADB-05 | Filter resolves from the mapping | `RegisterPage.sourceFilter.test.js` (module import, `matchesSourceFilter(sourceFilter, tx.source)`, no bare ordinal, no local switch, `CashTransactionSource.Opening` exclusion) + static file inspection | ✅ COMPLIANT |

**Compliance summary**: 11/11 S2 scenarios compliant, 0 UNTESTED, 0 FAILING; requirement-level completeness 3/3 (REQ-CAP-02, REQ-CAP-03, REQ-ADB-05 satisfied).

### Correctness (Static Evidence)

| Requirement | Status | Notes |
|-------------|--------|-------|
| REQ-CAP-02 server-resolved commission for both clients | ✅ Implemented | One core behind a public read adapter; both clients fetch the server value per channel |
| REQ-CAP-02 no hardcoded 7/10 | ✅ Implemented | Literal deleted from VM and modal; zero matches in the client scan; visual channel switch tested |
| REQ-CAP-03 fail-closed preview | ✅ Implemented | 422 → null → no percentage + blocked submit on both clients; server rejection path still throws before any payout |
| REQ-CAP-03 previewed == charged | ✅ Implemented | Same core; request carries no commission |
| REQ-ADB-05 single source mapping | ✅ Implemented | One frozen table drives filter, labels and resolvers; inverted switch deleted |
| AD-6 H-13 decimal-only ranges | ✅ Implemented | 8+4 attributes; discriminating test verified by mutation |

### Coherence (Design)

| Decision | Followed? | Notes |
|----------|-----------|-------|
| AD-2 (route on `CashDrawerController`, roles, 422 ProblemDetails, one private core + public `TryGetCommissionPercentageAsync`, throwing adapter verbatim) | ✅ Yes | Exact shape and status code; endpoint deliberately not on the settings controller |
| AD-3 (one `cashTransactionSource.js` table; filter + labels read it; no ordinal literal in the filter) | ✅ Yes | Exact file shape; `advance` → 2; labels 2/3/4 corrected |
| AD-6 (decimal-only `[Range]` on 8+4 properties) | ✅ Yes | Exact bounds string; test enumerates all 12 |
| AD-7 (S1 → S2 → S3a → S3b; slice revertible alone) | ⚠️ Partial (documented) | tasks.md suggested four work-unit commits; the dispatch produced one consolidated commit (`cdc-s2-commission`, D3 in `apply-progress.md`). Clients stayed together as the contract pair and the rollback boundary is atomic; no PR chain exists in this repo. Does not break a spec |
| AD-8 (discriminating tests + structural/mutation check per slice) | ✅ Yes | RED tests, structural scans, parity and the independently reproduced H-13 mutation |

### Scope Check

| Check | Evidence | Status |
|-------|----------|--------|
| No S3 creep | `git show 67409da --name-only` lists only S2 production/tests + docs/openspec; no `DailyClosureService.Receipts.cs`, no `ClosureLegacyEntryRemovalTests.cs`, no closure-service split; the `DailyClosureService*` files are untouched | VERIFIED |
| No schema/migration | No migration file in the commit | VERIFIED |
| H-13 in slice | tasks.md Work Unit 5 assigns S2-11 (H-13) to Phase S2 | VERIFIED |
| Size signal / `size:exception` | `git show 67409da --numstat`: 1205 insertions / 94 deletions (1299 total). Excluding `docs/reporte.txt` (+175), `apply-progress.md` (+123) and `tasks.md` (+11/−11): **896 additions + 83 deletions = 979 authored changed lines**. The record's "907 additions + 94 deletions = 1001 (docs excluded ≈ 979)" mixes exclusion sets (907 excludes only the two docs; 979 excludes all three). Both figures are far above the 400-line budget, so D4's `size:exception` is factually required and was dispatched/accepted | VERIFIED (residual note RESIDUAL-S2-05) |

### GGA Classification Audit (`--no-verify` claim)

| GGA finding | Independently verified state | Classification correct? |
|-------------|------------------------------|-------------------------|
| Read query without `AsNoTracking` (`CashDrawerController.cs:171-174`) | The `_inventoryContext.ExchangeRateHistory` query sits at `:171-174` today; the S2 diff only appends the new endpoint at `:250-262`, so the method is untouched | ✅ Pre-existing |
| Explanatory comments cited across `AddProductViewModel`, `CashAdvanceRegisterViewModel`, `ProductDialogViewModel.Pricing.cs`, `WpfDialogService*`, `ICashDrawerService` | Added-comment scan over the commit: production files add **zero** comment lines; the only added comments are the two `/**` traceability headers in the new web test files (repo convention) and docs/openspec prose | ✅ No S2-introduced finding |
| Async methods without `CancellationToken` in client `CashDrawerService.cs` | Pre-existing methods unchanged; the new `GetAdvanceCommissionAsync` accepts and propagates a token | ✅ Pre-existing |
| Controller direct `DbContext` access + thrown validation instead of ProblemDetails | `ResolveAnchoredRateAsync` unchanged; the new endpoint returns 422 ProblemDetails and touches no data | ✅ Pre-existing |

No S2-introduced GGA finding remains; the `--no-verify` justification is consistent with the evidence.

### Auth Evidence Caveat (threat matrix)

The new route's authorization is proven at two levels: (1) attribute reflection (`[Authorize(Roles = "Admin,Manager,Cashier")]`, no `AllowAnonymous`, class-level `[Authorize]`) and (2) controller-level runtime behavior with mocked services. The actual HTTP pipeline (authentication middleware → 401/403 rendering → route execution) is not exercised for this route. The repo's only real-pipeline harness (`CommandCenter.Tests/Integration/WebApplicationFactorySmokeTests.cs`) is silently skipped without `TEST_POSTGRES_CONNECTION`, so a pipeline test cannot run in this environment and verification must not add code. Assessment: **cannot be strengthened in this phase**; registered as RESIDUAL-S2-04 (WARNING-level coverage note, not a spec failure — no S2 spec scenario demands pipeline authz, while the threat-matrix plan did). Recommendation: add a pg-gated `WebApplicationFactory` test for `/api/cashdrawer/advance-commission` (anonymous → 401, `Driver` → 403, `Cashier` → 200/422) when the harness is available.

### Issues Found

**CRITICAL**: None.
**WARNING**:
- W-S2-01 — the threat-matrix 401/403 claim rests on attribute reflection plus mock-level behavior, not a real HTTP pipeline (RESIDUAL-S2-04).
- W-S2-02 — process deviations accepted but recorded: no WPF dialog-window construction test (D2; the repo has no headless WPF infrastructure) and one consolidated commit instead of four work units (D3).
**SUGGESTION**:
- Render the static source-filter `<option>` captions from `SOURCE_DEFINITIONS` so AD-3's one-table intent also covers the filter UI text (the spec does not require it).
- Broaden the `RegisterPage.sourceFilter.test.js` bare-ordinal regex to also catch an ordinal on the left (`4 === tx.source`) and include `adjustment`/`closing` option keys when they are added.
- Add a mounted (jsdom) modal test for the fetch→render→submit interaction when the web test harness supports it.

### Residual Warnings (non-blocking for S2)

- **RESIDUAL-S2-01 (change scope)** — S3a (0/4) and S3b (0/8) are unimplemented; the change-level verdict remains pending and must not be inferred from slice verdicts.
- **RESIDUAL-S2-02 (WPF dialog proof, D2)** — no headless WPF window infrastructure exists in the repo; proof is build `0/0` plus the VM contract tests, which exercise the exact ctor wiring the dialog uses.
- **RESIDUAL-S2-03 (parity guard)** — `PreviewedPercentage_EqualsChargedPercentage` is near-tautological by construction (shared core = the AD-2 guarantee); kept as a contract guard.
- **RESIDUAL-S2-04 (auth pipeline evidence)** — see the caveat above; cannot be strengthened in this phase.
- **RESIDUAL-S2-05 (evidence arithmetic)** — the record's "907 additions + 94 deletions = 1001 changed lines (docs excluded ≈ 979)" mixes exclusion sets; the true authored code+test delta excluding `docs/reporte.txt`, `apply-progress.md` and `tasks.md` is 896 additions + 83 deletions = **979 changed lines**. The conclusion (over the 400-line budget; `size:exception` required) is unchanged.
- **RESIDUAL-S2-06 (web interaction coverage)** — SSR render + pure helpers + source scans; no mounted interaction test, and the filter captions remain static duplicates of two labels.

### Slice S2 Verdict

**PASS_WITH_WARNINGS** — REQ-CAP-02, REQ-CAP-03 and REQ-ADB-05 are implemented exactly as designed (one server-resolved commission core behind a public adapter and a fail-closed 422 read route; both clients consume the server value and block submission when unresolved; one contract-aligned source table drives the `advance` filter and the labels; H-13 decimal-only ranges) and proved by fresh runtime evidence at `67409da`: build `0/0`, backend `1254/1254`, `CashAdvance` filter `55/55`, S2 focused filter `21/21`, frontend `287/287`, lint clean, coverage gate green, and the H-13 mutation genuinely discriminates and was reverted byte-identically. The RED claims re-derive deterministically from the parent. The two warnings are evidence-depth and process deviations, not spec failures; 0 blockers, 0 critical findings.

### Change-Level Verdict

**PENDING** — S1 and S2 are verified (`pass_with_warnings` each). S3a (0/4 tasks) and S3b (0/8 tasks) remain unimplemented, so the cumulative 7/21 delta surface is not complete and no change-level verdict is admissible (RESIDUAL-S2-01).
