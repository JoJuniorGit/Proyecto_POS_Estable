```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:29db321c0fc18ad558ef2939abf178659a480312b050789bdf1da393a70daa58
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 5/5
scenarios: 10/10
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
test_exit_code: 0
test_output_hash: sha256:85f90e721802fd26be95ef995508d70f86d943b63af24c93aca7e1e1fee4a3ea
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:2e9742d477cf3a37a618e8bb321ccfb7dd555a79cc5b44a1c4614e8f3c13d628
```

# Verification Report

**Change**: enforce-hold-claim-precondition
**Store**: openspec
**Mode**: Standard (strict_tdd: false per `openspec/config.yaml`)
**Independence**: all commands were re-executed by this sub-agent; `apply-progress.md` claims were treated as untrusted.

## Completeness
| Metric | Value |
|--------|-------|
| Tasks total | 20 |
| Tasks complete | 20 |
| Tasks incomplete | 0 |

Implementation tasks 1.1–3.4 are checked in `tasks.md`. Verification tasks 3.5–3.7 (npm test+lint, build, dotnet test) are satisfied by this phase and are therefore countable as complete.

## Build & Tests Execution

**Build**: ✅ Passed (exit 0) — `0 Advertencia(s) / 0 Errores`
```text
dotnet build CommandCenter.slnx -c Release
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

**Tests (.NET)**: ✅ 985 passed / 0 failed / 0 skipped (exit 0)
```text
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release
Correctas! - Con error: 0, Superado: 985, Omitido: 0, Total: 985 - CommandCenter.Tests.dll (net10.0)
```
The pre-flagged flaky test `CashDrawerClosureTests.CashDrawer_Closure_PreservesMovementsAndExpectedCash_InActiveSession` did NOT fail; no isolated re-run was required.

**Tests (Web)**: ✅ 178 passed / 0 failed / 0 skipped (exit 0)
```text
npm test  → tests 178 | pass 178 | fail 0 | skipped 0
```
`test_output_hash` for this run: `sha256:5ab6f74391f8b986fd81bcc3d50aafbf8b3b89868875c80d698b1ecb46703bb9`

**Lint (Web)**: ✅ 0 errors (exit 0)
```text
npm run lint → oxlint (exit code 0, no findings)
```
`build_output_hash` for this run: `sha256:771a06edf7adad235923ae975eed07c46f6ce3508476b2a65b117d58ee1101fa`

**Coverage**: ➖ Not available — `rules.verify.test_command` is empty and coverage thresholds are not part of this change's acceptance criteria.

Legend for the matrix: `COMPLIANT` = covering test exists and passed at runtime; `PARTIAL` = behavior verified by source inspection (`file:line`) plus passing primitive tests, but no dedicated covering test for this scenario.

## Spec Compliance Matrix

| Requirement | Scenario | Evidence | Result |
|-------------|----------|----------|--------|
| Req 1 — Active claim required | Rejected without a claim | `CommandCenter.Tests/HoldNotClaimedPreconditionTests.cs` (9 `*_SinReclamo_*` facts: UpdateSaleItems, AddPaymentsBatch, CompleteSale, CancelSale, UpdateExchangeRate, UpdatePriceList, UpdateSaleCustomer, HoldSale re-hold, AddItem; plus `ActorNulo_...`) | ✅ COMPLIANT |
| Req 1 — Active claim required | Allowed when the actor holds the claim | `HoldNotClaimedPreconditionTests.ClaimDelActor_Permite_Mutacion` (seeds claim for actor 42, mutation proceeds) | ✅ COMPLIANT |
| Req 1 — Active claim required | Blocked when another actor holds the claim | `HoldNotClaimedPreconditionTests.ClaimAjeno_LanzaSaleLockedException` (claim by 99, actor 42 → `SaleLockedException`) | ✅ COMPLIANT |
| Req 2 — Inert/unchanged paths | ConfirmPickup on a Completed sale | `HoldNotClaimedPreconditionTests.ConfirmPickup_SobreCompleted_NoRequiereReclamo` (+ `SalesService.History.cs:29-32` guard no-ops on non-OnHold) | ✅ COMPLIANT |
| Req 2 — Inert/unchanged paths | System recalculation and force-release | `HoldNotClaimedPreconditionTests.RecalculateOnHoldSalesAsync_NoRequiereReclamo` and `ForceRelease_NoAfectadoPorPrecondicion` | ✅ COMPLIANT |
| Req 3 — Web cancel claims first | Claim succeeds | `holdOrderLockController.test.js` (claim `Editing` lifecycle) + `PendingOrdersPage.jsx:147` source | ⚠️ PARTIAL |
| Req 3 — Web cancel claims first | Claim rejected with 409 | `holdOrderLockController.test.js` (claim failure → `onError` + `reload`) + `PendingOrdersPage.jsx:148-152` source | ⚠️ PARTIAL |
| Req 4 — Cart refuses OnHold | Restoring an OnHold sale | (no dedicated test) — `CartContext.jsx:30,63-68,179-186,212-219` source | ⚠️ PARTIAL |
| Req 4 — Cart refuses OnHold | Rate effect avoided | (no dedicated test) — `CartContext.jsx:240` source (`status !== 'Pending'` short-circuit) | ⚠️ PARTIAL |
| Req 5 — Compatibility preserved | No contract change | (no dedicated 409 integration test) — `GlobalExceptionHandlerMiddleware.cs:261-274` maps `InvalidOperationException` → 409 + message; full suites green; no migration | ⚠️ PARTIAL |

**Compliance summary**: 6/10 scenarios runtime-backed; 4/10 verified by source inspection only (see WARNING W1).

## Correctness (Static Evidence)

| Requirement | Status | Notes |
|-------------|--------|-------|
| Req 1 | ✅ Implemented | `SalesService.HoldClaims.cs:106-112` inverts the null-claim branch: no-claim or `null` actor → `HoldNotClaimedException`; foreign claim → `SaleLockedException`. All 13 mutators call the guard (14 matches = 13 call sites + 1 definition). |
| Req 1 — message | ✅ Implemented | `HoldNotClaimedException.cs:10` = `El pedido #{saleId} no está reclamado; reclame el pedido antes de modificarlo.` (exact spec text). |
| Req 1 — ordering | ✅ Implemented | Guard runs after `GetSaleEntityAsync` and before business validations in every mutator (e.g. `Pricing.cs:18`, `History.cs:29`, `SalesService.cs:372`). |
| Req 2 | ✅ Implemented | `RecalculateOnHoldSalesAsync` (`Pricing.cs:29-70`) and `ReleaseSaleAsync(force:true)` (`HoldClaims.cs:58-104`) are byte-identical to HEAD (git diff empty). `ConfirmPickup` keeps its `Completed` requirement at `History.cs:31`. |
| Req 3 | ✅ Implemented | `PendingOrdersPage.jsx:147-152` claims `Editing` before `cancelSale`; returns without cancelling when the claim fails; `controller.releaseActive()` in `finally:166`. |
| Req 4 | ✅ Implemented | `CartContext.jsx` cache init `:30`, persist `:63-68`, `loadExistingSale` `:179-186`, `restoreOrStartSale` `:212-219`, and rate effect `:240` all reject/exclude `OnHold`. `createNewSale` resets `error` at `:154`. |
| Req 5 | ✅ Implemented | `Backend.API/Middleware/GlobalExceptionHandlerMiddleware.cs` is NOT in `git status` (untouched). No migration added or modified. No 428 anywhere. |

## Coherence (Design)

| Decision | Followed? | Notes |
|----------|-----------|-------|
| 1 — Keep `EnsureHoldClaimAccess(Sale,int?)` | ✅ Yes | Signature unchanged; only body semantics changed (`HoldClaims.cs:106`). |
| 2 — New `HoldNotClaimedException : InvalidOperationException` | ✅ Yes | `HoldNotClaimedException.cs:5-11`, carries `SaleId`, Design §Interfaces matched exactly. |
| 3 — 409 (no middleware edit) | ✅ Yes | Middleware untouched; `InvalidOperationException` → 409 branch reused. |
| 4 — Cart refuses OnHold | ✅ Yes | Refusal at all five cart entry points. |
| 5 — Anular claims `Editing` then best-effort release | ✅ Yes | Reuses existing `holdOrderLockController`. |
| 6 — WPF unchanged | ✅ Yes | No `Desktop.Client.Core/*` file modified. |
| Guard truth table | ✅ Yes | Non-OnHold no-op; own claim proceeds; foreign claim `SaleLockedException`; null claim (incl. null actor) → `HoldNotClaimedException`. |

## Rules Compliance Audit

Adheres to the system invariants. Specifically:
- **Money types**: no `float`/`double` introduced; the new exception carries only an `int SaleId`.
- **History immutability**: the guard only reads `Status`/`ClaimedByUserId`; no historical amount is recomputed.
- **`async void` prohibition**: the new/changed guard code is synchronous; no async signature was added.
- **No explanatory comments**: `HoldNotClaimedException.cs` and the guard diff add no comments, per AGENTS.md.
- **RBAC / zero-trust**: the Admin/Manager force-release path (`ReleaseSaleAsync(force:true)`) is untouched; `Driver` scope unaffected.
- **Contract stability**: no DTO, entity, migration, or middleware change.

## Issues Found

**CRITICAL**: None.

**WARNING**:
- **W1 — Web scenarios have no dedicated automated tests.** `design.md` §Testing Strategy promised "Extend controller/page/cart tests" for Anular claim-order and cart refusal, but no `Web.Frontend` test file was added or modified (`git status` shows only `CartContext.jsx`, `PendingOrdersPage.jsx`, `README.md`, `package.json`). Web test total is unchanged at 178 (proposal forecast "178+n"). Req 3 and Req 4 are therefore verified by source inspection and by the pre-existing `holdOrderLockController.test.js` primitive, not by page/cart-level tests. Non-blocking under the orchestrator's stated evidence contract (file:line accepted), but it is a real regression-coverage gap.
- **W2 — Report count discrepancy.** `apply-progress.md` states "14 casos nuevos", but `HoldNotClaimedPreconditionTests.cs` contains **15** `[Fact]` methods. The report undercounts by one; the artifact itself is correct.
- **W3 — No dedicated 409 integration test.** Req 5 relies on middleware source inspection plus the green suites; no test asserts `HoldNotClaimedException` → HTTP 409 with the documented message end-to-end (`design.md` proposed `WebApplicationFactory` coverage).

**SUGGESTION**:
- The `status === 'OnHold'` branches retained in `CartContext.addItem/removeItem/updateQuantity` are now unreachable (the cart can never hold an `OnHold` sale); consider removing them as cleanup.
- Authored size (~420+ changed lines) exceeds the 400-line review budget; the chained-PR split recommended in `tasks.md` remains the right delivery path.

### Verdict
**PASS WITH WARNINGS**
The root-cause fix (Req 1) and the inert/unchanged paths (Req 2) are fully proven by 985 green .NET tests including 15 new claim-precondition facts; the web adaptations (Req 3/Req 4) and the contract guarantee (Req 5) are correct by source inspection but lack dedicated automated tests.
