# Tasks: Audit Critical Remediation

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~800–1,000 |
| Budget risk | High |
| Chained PRs | Yes |
| Split | 7 chained work units |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached from the V0.15 chain) |

Decision needed before apply: No
Chained PRs recommended: Yes
400-line budget risk: High

### Suggested Work Units

| Goal / PR | Focused test command | Harness / Rollback boundary |
|---|---|---|
| 1. Deterministic stock deduction (SRE-01) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~StockDeduction"` | Revert helper + method; no schema |
| 2. Server-authoritative rounding (SEC-01) | `--filter "FullyQualifiedName~FinancialIntegrity"` | Revert recompute; no schema |
| 3. Cash-advance price guard (SEC-02) | `--filter "FullyQualifiedName~SaleItems|~AddItem|~Hold"` | Revert guards; no schema |
| 4. Backend idempotency (SRE-02 server) | `--filter "FullyQualifiedName~Idempotenc|~DailyClosure"` | Migration Down drops the unique index |
| 5. Client idempotency keys (SRE-02 clients) | `--filter "FullyQualifiedName~Idempotency"` + `npm test` | Revert client key sends |
| 6. Cart commit resilience (CLEAN-02) | `--filter "FullyQualifiedName~Cart"` | Revert VM changes |
| 7. Final verification + closure | full suite + coverage + build | — |

## Phase 1: Deterministic Stock Deduction (SRE-01)

- [x] 1.1 Pure helper `StockDeductionConsolidator.ResolveAndConsolidate` (resolve target + conversion; group by `(target, reason, saleId)`; order by target asc) — RED tests: reversed-order invariance, consolidation, totals.
- [x] 1.2 `UpdateStockBatchAsync` uses the helper before the update loop; one update + one movement per group.
- [x] 1.3 Regression: `Phase3PerformanceRemediationTests` + `ProductVariantsTests.IndependentPricing` green.

## Phase 2: Server-Authoritative Rounding (SEC-01)

- [x] 2.1 `CompleteSaleAsync`: drop the ±1000 guard and both client-value assignments; recompute after payments attach (formula mirrors preview).
- [x] 2.2 RED tests: injected `RoundingAdjustment = 250` discarded; partial payment → 0; preview == persisted for same data.
- [x] 2.3 Update any test asserting the removed defensive rejection.

## Phase 3: Cash-Advance Price Guard (SEC-02)

- [x] 3.1 Guard in `AddItemAsync` and `UpdateSaleItemsAsync` with the exact message; RED tests: no item added / no mutation.
- [x] 3.2 Regression: coordinator flow (`CreateCashAdvanceSaleAsync`) unaffected.

## Phase 4: Idempotency (SRE-02)

- [x] 4.1 Extract `ResolveIdempotencyAsync` into `IdempotencyRequestResolver`; existing complete/hold/deliveries behavior unchanged (regression tests).
- [x] 4.2 Required key on POST items / cash transaction / daily closure with replay + 422 semantics; RED tests per endpoint.
- [x] 4.3 Idempotent cancel: already-cancelled → success (no 409); RED test.
- [x] 4.4 Closure duplicate guard + unique `ClosureDate` index migration (Down drops it); RED tests: second closure rejected.
- [x] 4.5 WPF + Web clients send `Idempotency-Key` on the three operations (fresh GUID / stable-per-attempt); tests.

## Phase 5: Cart Commit Resilience (CLEAN-02)

- [x] 5.1 `CommitItemQuantityAsync` + `FlushAllQuantitiesAsync`: log + server re-sync + exact operator messages; success-path setter hardening.
- [x] 5.2 Best-effort catches log via `ClientStateLogger.LogError` (no empty catches).
- [x] 5.3 New `CartCommitResilienceTests`: rollback message, flush, re-sync-failure warning.

## Phase 6: Closure

- [x] 6.1 Build 0/0; full suite green; coverage thresholds; focused filters per work unit.
- [x] 6.2 Independent verification per work unit + final; ANEXO 8.149 in `docs/reporte.txt`; tracker close.
