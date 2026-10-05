# Exploration: Audit Critical Remediation (2026-10-05)

## Source

- `docs/auditoria_integral_fases_1_4.txt` (untracked; body lists **20** findings — 6 critical / 10 high / 4 medium; the file's own header miscounts them as 19 with 4/7/8).
- Independent verification (4 read-only verifiers + orchestrator spot check, static evidence): **15 CONFIRMED, 1 CONFIRMED-WITH-DRIFT, 4 PARTIAL, 0 FALSE**.
- The audit's `[x]` remediation matrix is **false 5/5**: SEC-01, SEC-02, SRE-01, SRE-02 and CLEAN-02 are marked as fixed but no fix exists in the V0.15 tree.

## Scope of this change (critical, verified, fixable)

SEC-01 rounding recompute · SEC-02 cash-advance price guard · SRE-01 deterministic deduction order · SRE-02 idempotency · CLEAN-02 cart rollback.

## Key design evidence (current tree, `file:line`)

- **SEC-01**: preview formula `SalesController.Checkout.cs:63` (`remainingUsd <= 0.01m ? RoundToDigital(totalPaidBsS - totalBsS) : 0m`); client value persisted at `SalesService.Checkout.cs:112` and re-persisted at `:282` after payments attach (`:190`); `RoundToDigital` = 2 dp AwayFromZero (`Core/Helpers/PricingCalculator.cs:11-21`); `RecalculateTotalAsync` never touches the adjustment (`SalesService.Pricing.cs:131-200`).
- **SEC-02**: PUT guard exempts cash advance (`HoldOrders.cs:338-341`); POST exemption at `SalesService.cs:242-249`; the legitimate path is `CashAdvanceCoordinator.cs:109` → `CreateCashAdvanceSaleAsync` (`SalesService.CashAdvance.cs:126-139`, direct `SaleItems.Add`, never calls AddItem/UpdateItems).
- **SRE-01**: `UpdateStockBatchAsync` preserves caller order (`InventoryService.StockDeduction.cs:19-34`); `Distinct()` only feeds the fetch dictionary; callers build requests per sale order (`Checkout.cs:296-313`, `HoldOrders.cs:148-166`, `InventorySaleMadeEventHandler.cs:70-122`); pure-helper precedent `ApplyConversion` (`:13`).
- **SRE-02**: reusable private helper `ResolveIdempotencyAsync` (`SalesController.cs:196-241`) + `IdempotentRequests` unique `(Key,RequestPath)` (`SalesDbContext.cs:342-350`), replay with `X-Cache-Lookup: HIT`, 422 on payload mismatch; closure has **no** duplicate guard and its `ClosureDate` index is non-unique (`SalesDbContext.cs:265`); unique-index migration precedent `20260906100000_AddIdempotentRequestsAndCashDrawerSingleOpenIndex.cs`; cancel throws 409 on replay (`SalesService.cs:403-404` → middleware); clients send keys today only for complete/hold/payment/deliveries (desktop `SalesService.cs:142-143`; web `utils/idempotency.js` stable-per-attempt); `ResilienceHandler.cs:68-73` auto-retries only keyed POSTs.
- **CLEAN-02**: defect catches `CartViewModel.cs:388-391` (commit) and `:409-412` (flush) use `Debug.WriteLine` only; optimistic mutation `CartItemViewModel.cs:93-104` (no committed snapshot); no rollback precedent anywhere; `ClientStateLogger.LogError(message, origin)` exists (`Core/Logging/ClientStateLogger.cs:60`); `_dialogService` already injected in `CartViewModel`; no tests cover commit/flush (patterns in `Unit/CartTotalsDesyncTests.cs`).

## Out of scope (follow-up changes, documented roadmap)

SEC-03, SEC-04, SEC-05, SEC-06, SEC-07; PERF-01, PERF-02, PERF-03, PERF-04, PERF-05; CLEAN-01, CLEAN-03, CLEAN-04, CLEAN-05; SRE-03.
