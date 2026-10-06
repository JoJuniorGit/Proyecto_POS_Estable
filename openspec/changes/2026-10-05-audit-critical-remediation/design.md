# Design: Audit Critical Remediation

## Context

Five verified critical defects. Fixes respect existing canonical specs: `api-dto-boundary` REQ-ADB-11 (cash-advance stock skip at checkout stays), `cash-advance-payout-integrity` (sale's anchored rate governs drawer movements), `supplier-invoice-apply:66` (untouched).

## D1 — SEC-01: server-authoritative rounding at completion

- **Placement**: `Sales.Module/Services/SalesService.Checkout.cs`. Remove the client-value assignments at `:112` and `:282` and the ±1000 defensive guard (`:107-110`, no longer meaningful once the payload is ignored). After all payments attach (`:190`) and `RecalculateTotalAsync` runs (`:280`), compute:
  ```csharp
  decimal totalPaidBsS = sale.Payments.Sum(p => p.AmountBsS);
  decimal remainingUsd = Math.Round(sale.TotalUSD - sale.Payments.Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
  sale.RoundingAdjustment = remainingUsd <= 0.01m ? PricingCalculator.RoundToDigital(totalPaidBsS - sale.TotalBsS) : 0m;
  ```
- The `roundingAdjustment` parameter stays in the signature (DTO/API compatibility) but is documented as ignored; `[Range(-1000,1000)]` stays harmless in the DTO.
- **Mirror exactness**: identical thresholds to the preview (`SalesController.Checkout.cs:62-63`: `remainingUsd <= 0.05m` paid / `<= 0.01m` rounding; `RoundToDigital` = 2 dp AwayFromZero).
- **Tests**: extend `Phase4FinancialIntegrityAndPreviewTests` — injected value discarded; partial payment → 0; preview value == persisted value for the same data. Update any test asserting the old defensive rejection.

## D2 — SEC-02: cash-advance exclusion guard

- `Sales.Module/Services/SalesService.cs` `AddItemAsync` (after product resolution, before price logic): `if (product?.IsCashAdvance == true) throw new InvalidOperationException(...)`.
- `Sales.Module/Services/SalesService.HoldOrders.cs` `UpdateSaleItemsAsync` (inside the loop, before the price condition at `:338`): same guard.
- Exact message (both sites): `"Los productos de adelanto de efectivo no pueden agregarse ni modificarse en una venta; use el flujo de adelanto de efectivo."` → `InvalidOperationException` (middleware maps to 409 "Conflicto de Operación").
- The legitimate path is untouched: `CashAdvanceCoordinator.cs:109` → `CreateCashAdvanceSaleAsync` creates its `SaleItem` directly (`SalesService.CashAdvance.cs:126-139`).
- **Tests**: AddItem rejects (no item added); UpdateItems rejects with any price (no mutation); coordinator flow unaffected.

## D3 — SRE-01: deterministic deduction order

- New pure static helper (Inventory.Module, e.g. `StockDeductionConsolidator`): `ResolveAndConsolidate(items, productsDictionary)` → resolves each request to its target product (parent/shared resolution + `ApplyConversion` factor), groups by `(targetProductId, Reason, SaleId)` summing `QuantityChange`, orders ascending by `targetProductId` (then Reason/SaleId for stability).
- `UpdateStockBatchAsync` uses the helper before the update loop; one stock update + one movement per consolidated group.
- **Invariant**: per-target total delta identical to legacy behavior for any input order.
- **Tests**: pure-helper unit tests (reversed-order invariance, consolidation, totals) + existing `Phase3PerformanceRemediationTests.UpdateStockBatchAsync_DeductsMultipleProductsInSingleExecution` stays green (adjust if it asserts movement granularity).

## D4 — SRE-02: idempotency

- **Extract** the private `ResolveIdempotencyAsync` (`SalesController.cs:196-241`) into a shared `Backend.API/Services/IdempotencyRequestResolver`; behavior identical (400 missing key, `X-Cache-Lookup: HIT` replay, 422 payload mismatch, registration via `IdempotencyService` + `IdempotentRequests` table).
- **Apply** (required key) to: POST `SalesController.cs:123` (items), POST `CashDrawerController.cs:134` (transaction), POST `DailyClosureController.cs:47` (closure). Stored response registered after success, mirroring complete (`SalesController.Checkout.cs:138-141`).
- **Cancel**: `SalesService.cs:403-404` stops throwing; already-cancelled → idempotent success reflecting the cancelled state (no 409).
- **Closure guard**: in-transaction `AnyAsync(ClosureDate == date)` → `InvalidOperationException("Ya existe un cierre para esta fecha.")` (409); migration adds a UNIQUE index on `DailyClosures.ClosureDate` (precedent `20260906100000_AddIdempotentRequestsAndCashDrawerSingleOpenIndex.cs`), Down drops it; verify dev DB has no duplicate dates before apply.
- **Clients**: WPF `Desktop.Client.Core/Services/SalesService.cs` (items), `CashDrawerService`, closure service → add `Idempotency-Key` per logical call (fresh GUID; `ResilienceHandler` reuses it on retry). Web: `salesApi` items + cash/closure services → stable-per-attempt pattern (`utils/idempotency.js`).
- `ResilienceHandler.cs:68-73` unchanged (keyed POSTs already retryable).
- **Tests**: missing key 400; replay no-duplicate (items/transaction/closure); 422 mismatch; second closure for same date rejected; cancel replay success.

**Implementation note (2026-10-05, recorded deviation)**: the resolver is constructed per-controller from already-injected services instead of being DI-registered (parent-approved Option 2 to keep the write blast radius within the delegated surfaces); see tracker L7.

## D5 — CLEAN-02: cart commit resilience

- `CommitItemQuantityAsync` catch (`:388-391`): `ClientStateLogger.LogError(...)`; re-sync via `_salesService.GetSaleAsync(CurrentSale.Id)` restoring authoritative quantities; success → `_dialogService.ShowError("Error", "No se pudo actualizar la cantidad. Se restauró el valor del servidor.")`; re-sync failure → log + `ShowError` with `"No se pudo actualizar la cantidad y no se pudo restaurar el estado. Verifique el carrito antes de cobrar."`.
- `FlushAllQuantitiesAsync` catch (`:409-412`): same treatment.
- Success path hardening: assign `_currentSale` through the property setter (remove the `:385` bypass so collection/recovery stay coherent).
- Three best-effort catches (`:58`, `:224`, `:294`): add `LogError`; flow unchanged.
- **Tests**: new `CartCommitResilienceTests` (Moq + direct construction + `WeakReferenceMessenger.Default.UnregisterAll`, pattern from `CartTotalsDesyncTests`): failure → rollback + exact message; flush; re-sync failure → stale warning; dialog service mocked.

## D6 — Test strategy

RED-first per task with deterministic tests; pure helpers where possible (D3); backend tests extend `Phase4FinancialIntegrityAndPreviewTests`, hold/pickups, idempotency and closure suites; WPF tests new file; Web `npm test` for key sending. Closure runs build 0/0 + full suite + coverage + focused filters.

## D7 — Delivery

Work units WU1–WU7 (see tasks.md); chain stacked-to-main (cached); ANEXO 8.149; RDD off (clone-local).
