# Exploration: enforce-hold-claim-precondition

## Current State

The OnHold lock is enforced by `EnsureHoldClaimAccess` (`Sales.Module/Services/SalesService.HoldClaims.cs:106-113`):

```csharp
private static void EnsureHoldClaimAccess(Sale sale, int? actingUserId)
{
    if (sale.Status != SaleStatus.OnHold) return;
    if (sale.ClaimedByUserId == null) return;   // <-- the hole: no claim = allowed
    if (sale.ClaimedByUserId == actingUserId) return;
    throw BuildSaleLockedException(sale);       // 409, "bloqueado por otro cajero"
}
```

The server only blocks when a claim already exists and belongs to someone else. A free (unclaimed) OnHold sale accepts mutations from any authenticated actor — this is the 8.128 root cause. Claim lifecycle lives in the same file: `ClaimSaleAsync` (`:13`), `ReleaseSaleAsync` (`:58`), `ApplyHoldClaim` (`:137`), `ClearHoldClaim` (`:115`), `HasHoldClaim` (`:123`). `ClearHoldClaim` is already invoked on `CancelSaleAsync` (`SalesService.cs:383`), `CompleteSaleAsync` (`SalesService.Checkout.cs:286`), and both `HoldSaleAsync` branches (`SalesService.HoldOrders.cs:133,243`).

`SaleLockedException : InvalidOperationException` (`Sales.Module/Exceptions/SaleLockedException.cs:5`) is mapped by `GlobalExceptionHandlerMiddleware` to **409 Conflict** with `message` (`Backend.API/Middleware/GlobalExceptionHandlerMiddleware.cs:261-275`). Only `SalesController.Claims.cs:47` catches it specially (adds claimedBy fields); mutator controllers do not catch it, so it flows to the middleware as 409 + `message`.

Actor resolution: `SalesController.GetActorUserId()` (`SalesController.cs:79-84`) returns `null` when the JWT has no parseable user id. Every mutator endpoint passes `GetActorUserId()`.

## Affected Areas

### 1. Mutators that call `EnsureHoldClaimAccess` (13 call sites)

| # | Mutator | Call site | Endpoint | Client callers |
|---|---------|-----------|----------|----------------|
| 1 | `UpdateExchangeRateAsync` | `SalesService.Pricing.cs:18` | `PUT /api/sales/{id}/exchange-rate` (`SalesController.cs:163`) | Web `CartContext` rate effect; WPF `SalesService.UpdateExchangeRateAsync` |
| 2 | `UpdatePriceListAsync` | `SalesService.Pricing.cs:92` | (via cart) | Web `changePriceList`; WPF `CartViewModel.SetPriceListAsync:147` |
| 3 | `AddPaymentToHoldSaleAsync` | `SalesService.Payments.cs:27` | `POST /api/sales/{id}/payments` (`SalesController.HoldOrders.cs:60`) | WPF `CheckoutViewModel:352` |
| 4 | `AddPaymentsBatchToHoldSaleAsync` | `SalesService.Payments.cs:119` | `POST /api/sales/{id}/payments/batch` (`SalesController.HoldOrders.cs:91`) | Web `PendingOrdersPage.jsx:357` |
| 5 | `HoldSaleAsync` | `SalesService.HoldOrders.cs:26` | `POST /api/sales/{id}/hold` (`:11`) | Web `HoldSaleModal.jsx:199`; WPF `PosViewModel.Orders.cs:288` |
| 6 | `UpdateSaleItemsAsync` | `SalesService.HoldOrders.cs:258` | `PUT /api/sales/{id}/items` (`:40`) | Web `EditSaleModal.jsx:159`; WPF `PendingOrdersViewModel.cs:258` |
| 7 | `ConfirmPickupAsync` | `SalesService.History.cs:29` | `POST /api/sales/{id}/confirm-pickup` (`SalesController.Checkout.cs:231`) | Web/WPF pickups (Completed status — see risks) |
| 8 | `UpdateSaleCustomerAsync` | `SalesService.Customers.cs:76` | `PUT /api/sales/{id}/customer` | Web `CartContext.updateCustomer:449`, `CheckoutModal.jsx:174`; WPF `PosViewModel.Orders.cs:25,262` |
| 9 | `AddItemAsync` | `SalesService.cs:192` | `POST /api/sales/{id}/items` (`SalesController.cs:121`) | Web `CartContext.addItem:299`; WPF `CartViewModel`, `PosViewModel.Scanning:97` |
| 10 | `RemoveItemAsync` | `SalesService.cs:300` | `DELETE /api/sales/{id}/items/{itemId}` (`:139`) | Web `CartContext.removeItem:259`; WPF `CartViewModel:338` |
| 11 | `UpdateItemQuantityAsync` | `SalesService.cs:332` | `PUT /api/sales/{id}/items/{itemId}` (`:151`) | Web `CartContext.updateQuantity:348`; WPF `CartViewModel:300,323,361,395` |
| 12 | `CancelSaleAsync` | `SalesService.cs:372` | `POST /api/sales/{id}/cancel` (`SalesController.HoldOrders.cs:134`) | Web `PendingOrdersPage.jsx:148`; **no WPF caller found** |
| 13 | `CompleteSaleAsync` | `SalesService.Checkout.cs:53` | `POST /api/sales/{id}/complete` (`SalesController.Checkout.cs:92`) | Web `PendingOrdersPage.jsx:326`; WPF `CheckoutViewModel:337,372` |

`RecalculateOnHoldSalesAsync` (`SalesService.Pricing.cs:29`) is a system path — no `EnsureHoldClaimAccess`, out of scope per decision 4.

### 2. UI flows that will need "claim before" under the hardening

**Web — Anular (the confirmed gap):**
- `PendingOrdersPage.handleConfirmCancelSale` calls `cancelSale(selectedSale.id)` with **no claim** (`Web.Frontend/src/pages/PendingOrdersPage.jsx:143-162`, call at `:148`).
- Button is enabled for a selected, payment-free, not-locked-by-other sale (`:139,191`). A free OnHold sale can be cancelled by any actor today.
- Proposed change: reuse `createHoldOrderLockController` (`Web.Frontend/src/utils/holdOrderLockController.js:7`) — `await controller.start(selectedSale, 'Editing', currentUserId)` before `cancelSale`, then `await releaseActiveLock()` after success (best-effort; server already cleared the claim in `CancelSaleAsync:383`, so the release is a safe no-op via `ReleaseSaleAsync`'s non-OnHold branch `SalesService.HoldClaims.cs:67-76`). On failure, release best-effort as well (mirror `handleCloseEdit`).

**Web — re-hold over OnHold:**
- `HoldSaleModal` is rendered from `App.jsx:211` with `saleId={currentSale?.id}` and posts `holdSale(saleId, request)` (`HoldSaleModal.jsx:199`). Normally the cart sale is `Pending`, but `CartContext.restoreOrStartSale` explicitly restores a persisted sale when its status is `'Pending' || 'OnHold'` (`CartContext.jsx:196`); a restored OnHold sale then posts `hold` without any claim. Decision 1 puts re-`HoldSale` over OnHold in scope, so this path must reclaim first.
- WPF equivalent: `PosViewModel.Orders.cs:288` `HoldSaleAsync` from the active cart; same caveat if the cart holds an OnHold sale.

**Web — POS cart mutations on an OnHold `currentSale` (largest hidden hole):**
- The cart can hold an OnHold sale (`CartContext.jsx:196`), and these call mutators with no claim: `addItem:299`, `removeItem:259`, `updateQuantity:348`, `changePriceList:371`, `updateCustomer:449`, plus the exchange-rate effect `updateSaleExchangeRate` (`:215-229`). The `validateOnHoldRules` guards (`:231-239`) are business checks, not locks. This is exactly the old-bundle simultaneous-edit scenario; it must gain a claim/release lifecycle (or the cart must refuse to mutate a foreign/unclaimed OnHold sale).

**WPF — already compliant for the main OnHold flows:**
- `PendingOrdersViewModel` claims before acting: `ClaimSaleAsync(...,"Checkout")` (`:193`), `ClaimSaleAsync(...,"Editing")` (`:243`), and force-releases for override (`:283`). No WPF OnHold cancel flow exists.
- `CheckoutViewModel:352` (`AddPaymentToHoldSaleAsync`) runs after the parent `LiquidarAbonarAsync` claim.
- `CartViewModel` mutators (`:147,300,323,338,361,395`) act on the active cart sale; risk only if that sale is a restored OnHold (same caveat as web).

### 3. Existing tests that mutate OnHold without claim/actor (will turn RED)

Rule after hardening: any mutator invoked on `Status == OnHold` with `actingUserId == null` (or a non-holder) throws. All callers below use the default `actingUserId = null`.

| File | Case (line of mutator call) | Mutator |
|------|-----------------------------|---------|
| `OnHoldSalesTests.cs` | `AddPaymentToHoldSale_ConvertsBsSToUSD_AntiDevaluation` (:169) | AddPayment |
| `OnHoldSalesTests.cs` | `CompleteSale_PartialPayment_ThrowsInvalidOperationException` (:199) | CompleteSale |
| `OnHoldSalesTests.cs` | `CompleteSale_FullLiquidation_CompletesSaleAndGeneratesInvoice` (:257) | CompleteSale |
| `OnHoldSalesTests.cs` | `UpdateSaleItemsAsync_WhenNewTotalLessThanPaid_Throws` (:357) | UpdateSaleItems |
| `OnHoldSalesTests.cs` | `UpdateSaleItemsAsync_Allows_ExceedingCreditLimit` (:394) | UpdateSaleItems |
| `OnHoldSalesTests.cs` | `UpdateSaleItemsAsync_ValidEdit_UpdatesItemsAndRecalculates` (:429) | UpdateSaleItems |
| `OnHoldSalesTests.cs` | `LiquidateOnHoldSale_WithPendingPickup_SetsPendingPickupStatusAndDeductsStock` (:469) | CompleteSale |
| `OnHoldSalesTests.Liquidation.cs` | `LiquidateOnHoldSale_WithPendingPickup_RejectsDefaultCustomer` (:38) | CompleteSale |
| `OnHoldSalesTests.Liquidation.cs` | `CancelSaleAsync_WithoutPayments_ChangesStatusToCancelled` (:372) | CancelSale |
| `OnHoldSalesTests.Liquidation.cs` | `CancelSaleAsync_WithPayments_ThrowsInvalidOperationException` (:398) | CancelSale |
| `OnHoldSalesTests.Liquidation.cs` | `CancelSaleAsync_DeliveredOrder_ThrowsInvalidOperationException` (:417) | CancelSale |
| `BatchPaymentTests.cs` | `AddPaymentsBatchToHoldSale_TodosValidosPersisteTodosLosAbonos` (:63) | Batch |
| `BatchPaymentTests.cs` | `AddPaymentsBatchToHoldSale_UnAbonoExcedeTotalNoPersisteNinguno` (:87) | Batch |
| `BatchPaymentTests.cs` | `AddPaymentsBatchToHoldSale_SumaLoteExcedeTotalEnAcumuladoNoPersisteNinguno` (:107) | Batch |
| `BatchPaymentTests.cs` | `AddPaymentsBatchToHoldSale_EfectivoFraccionadoNoPersisteNinguno` (:151) | Batch |
| `PriceListTests.cs` | `UpdatePriceList_Throws_WhenOnHoldAndNewTotalBelowPaid` (:69) | UpdatePriceList |
| `PriceListTests.cs` | `UpdatePriceList_Succeeds_WhenOnHoldAndNewTotalEqualsPaid` (:127) | UpdatePriceList |
| `PriceListTests.cs` | `UpdatePriceList_Succeeds_WhenOnHoldWithNoPayments` (:157) | UpdatePriceList |
| `PriceListTests.Products.cs` | case at :104 (OnHold) → `UpdateSaleItemsAsync` (:118) | UpdateSaleItems |
| `FinancialRobustnessTests.cs` | `AddPaymentToHoldSaleAsync_PartialPayment_RemainsOnHold` (:204) | AddPayment |
| `FinancialRobustnessTests.cs` | `AddPaymentToHoldSaleAsync_ExceedsTotal_Throws` (:246) | AddPayment |
| `FinancialRobustnessTests.cs` | `AddPaymentToHoldSaleAsync_WithCash_RegistersCashTransaction` (:277) | AddPayment |
| `CheckoutAndPaymentTests.cs` | cash-fractional rejection case (:244, OnHold sale :230) | AddPayment |
| `Sprint1PerformanceOptimizationTests.cs` | OnHold batch case (:162, sale :132) | UpdateSaleItems |
| `Integration/EditHoldOrderFlowIntegrationTests.cs` | `EditHoldOrderFlow_...` (:88) | UpdateSaleItems |
| `Integration/PartialPaymentFlowIntegrationTests.cs` | `PartialPaymentFlow_...` (:94) | AddPayment |
| `Integration/LiquidationFlowIntegrationTests.cs` | `LiquidationFlow_FinalizesOnHoldSale_...` (:86) | CompleteSale |

Notes:
- Cases that expect a **domain** error (`ArgumentException`/`InvalidOperationException`) will still fail because the new precondition throws first (see ordering below), so they need a seeded claim + actor, not just a message update.
- `BatchPaymentTests` empty-list (:120) and >50 (:137) validations run **before** `GetSaleEntityAsync` (`SalesService.Payments.cs:112-116`), so they stay green.
- Repeated mutators in `BatchPaymentTests`'s `CreateServiceWithHeldSaleAsync` (`:51-52`) run on `Pending` → unaffected.
- `HoldOrderClaimTests.cs` already seeds claims/actors (`CreateOnHoldSale` :41), and its "claimed by another" cases (`:233-337`) keep throwing `SaleLockedException` — these stay green and are the template for the new positive tests.
- `PendingPickupTests.cs` uses `Completed` status for `ConfirmPickup` (`:127,161`) → `EnsureHoldClaimAccess` no-ops on non-OnHold → unaffected.
- `Sprint2PerformanceOptimizationTests.cs:134`, `GetPendingSalesAsync` (read-only) → unaffected.

## Error Semantics: 409 vs 428

**Recommendation: keep 409** via `InvalidOperationException` (optionally a dedicated `HoldNotClaimedException : InvalidOperationException` carrying `SaleId`).

Rationale:
- Both current clients display the exception `message` (web reads `err.response?.data?.message` — `PendingOrdersPage.jsx:156,381`; WPF surfaces `ex.Message`) and the existing lock conflict is already 409 (`SaleLockedException` → middleware `:261`). Introducing 428 would require middleware surgery (`GlobalExceptionHandlerMiddleware.cs`) plus new client branches, for no user-visible benefit — the UI is message-driven, not status-driven, for this family of errors.
- 409 keeps the claim precondition indistinguishable-in-shape from the "locked by another" case, so the existing banner path and the "reload list" behavior reuse unchanged.
- Suggested message (requires a new `SaleLockedException`-style factory or a message-carrying exception): `"El pedido #N no está reclamado; reclame el pedido antes de modificarlo."`.
- 428 is semantically purist (RFC 6585) but the API is not a generic REST surface; 409 is the established contract here.

Ordering caveat: `EnsureHoldClaimAccess` currently runs after `GetSaleEntityAsync` but **before** every business validation (e.g. `CompleteSaleAsync` line 53 precedes the `isPendingPickup` customer check at :70-82; `CancelSaleAsync` line 372 precedes the payments/delivered checks at :377-380). After hardening, unclaimed-OnHold calls will surface the claim error instead of the current domain error — this is intended, but it flips the exception type on several tests and must be reflected in `spec`/`design`.

## Risks and Edges

- **Complete-at-100% branch in `HoldSaleAsync`** (`SalesService.HoldOrders.cs:109-240`): a `Pending` sale can be created and fully paid in one call. `EnsureHoldClaimAccess` only fires on OnHold, so the first hold (Pending → OnHold/Completed) stays claim-free. Only a **re-hold of an existing OnHold** requires a claim. Confirm the precondition fires before the branch, matching decision 1.
- **`Pending` sales unaffected**: `AddItemAsync`/`RemoveItem`/`UpdateItemQuantity`/`UpdateExchangeRate`/`UpdateSaleCustomer`/`HoldSale` all accept `Pending` and must keep working without a claim. This is what keeps the main POS flow (and most tests) green.
- **`ConfirmPickup`**: already requires `Completed` (`SalesService.History.cs:31`); `EnsureHoldClaimAccess` no-ops, so no claim is needed. Decision 1 lists it, but behaviorally it is inert — spec should state it explicitly to avoid a phantom requirement.
- **Force-release override unchanged** (`ReleaseSaleAsync(force:true)` + `SalesController.Claims.cs:64` role gate). Admins still break a foreign lock.
- **Actor null is rejected**: with `ClaimedByUserId == null` and `actingUserId == null`, the new rule must throw. Consequence: any client that mutates OnHold while the JWT actor is unresolvable fails; verify the web/WPF auth always yields a numeric user id (`GetActorUserId`).
- **Web cart with an OnHold `currentSale`** is the highest production risk: the rate-sync effect fires automatically 1.5 s after load (`CartContext.jsx:215-229`) and would now 409 on an unclaimed/foreign OnHold sale. Needs a claim lifecycle or an explicit refusal in the cart.
- **Web Anular must claim before cancel**; otherwise the button silently breaks for every free order (the majority).
- **Same-user-two-sessions** is out of scope (decision 5): the lock is per cashier, so two terminals logged in as the same cashier both pass `ClaimedByUserId == actingUserId`. Do not claim coverage here.

## Files Likely Affected

- `Sales.Module/Services/SalesService.HoldClaims.cs` — precondition logic + error factory.
- `Sales.Module/Services/SalesService.cs`, `.HoldOrders.cs`, `.Payments.cs`, `.Pricing.cs`, `.Checkout.cs`, `.Customers.cs`, `.History.cs` — verify call ordering (no signature change expected).
- `Backend.API/Middleware/GlobalExceptionHandlerMiddleware.cs` — only if a new exception type is introduced (otherwise unchanged).
- `Web.Frontend/src/pages/PendingOrdersPage.jsx` — claim-before-cancel.
- `Web.Frontend/src/context/CartContext.jsx` — claim lifecycle for OnHold cart mutations (and/or refuse).
- `Web.Frontend/src/components/pos/HoldSaleModal.jsx` / `App.jsx` — re-hold claim path if kept.
- `Desktop.Client.Core/ViewModels/*` — only if the WPF cart can hold an OnHold sale.
- `CommandCenter.Tests/*` — seed claim + actor in the listed cases; add new precondition tests.
- `openspec/specs/pending-order-hold-lock/spec.md` — main spec to extend (delta in the change).

## Open Questions

1. For the web cart holding an OnHold sale: should it **claim Editing on load and release on unload**, or **refuse to load a foreign/unclaimed OnHold into the cart** (forcing editing through the Pending Orders modals only)? The latter is safer but changes UX.
2. Is `CartContext.loadExistingSale` (exported, no in-repo caller — `CartContext.jsx:166,499`) dead code that should be removed with the hardening, or an intended entry point?
3. Should the new "not claimed" error be a distinct exception type (for typed client handling) or a plain `InvalidOperationException` with the new message?
4. For web Anular, is claiming `Editing` acceptable semantically (cancel is not editing), or should the cancel endpoint itself claim/release server-side to keep the UI simple?

## Ready for Proposal

Yes. Scope is well bounded by the five orchestrator decisions; the mutator inventory, UI flows, and test blast radius are enumerated with evidence.
