# Proposal: Enforce Active Hold Claim on Every OnHold Mutator

## Intent

`EnsureHoldClaimAccess` (`SalesService.HoldClaims.cs:106-113`) admits any actor when `ClaimedByUserId == null` — the 8.128 root cause. Every OnHold mutation MUST require an active claim held by the acting user.

## Scope

### In Scope
- Require an active claim in all 13 OnHold mutators: AddItem, RemoveItem, UpdateItemQuantity, UpdateExchangeRate, UpdatePriceList, UpdateSaleCustomer, UpdateSaleItems, AddPayment, AddPaymentsBatch, HoldSale (re-hold), CompleteSale, CancelSale, ConfirmPickup.
- A `null` actor is NOT a holder → reject.
- New `HoldNotClaimedException : InvalidOperationException` → 409 via existing middleware; message `"El pedido #N no está reclamado; reclame el pedido antes de modificarlo."`. `SaleLockedException` (blocked by another) preserved.
- Web Anular claims `Editing` before cancel and releases after.
- Web cart refuses to load/restore an OnHold sale; directs to Cuentas Abiertas.
- Update ~27 tests to seed a claim+actor; add rejection coverage.

### Out of Scope
- Same user in two sessions; terminal identity; HTTP 428; migrations/data (none).
- `force-release` and `RecalculateOnHoldSalesAsync`: unchanged.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `pending-order-hold-lock`: require an active claim before any OnHold mutation; adjust free/no-claim scenarios.

## Approach

Throw `HoldNotClaimedException` when `ClaimedByUserId != actingUserId`. It runs before business validations, so unclaimed calls surface the claim error first. Deriving from `InvalidOperationException` keeps the 409 middleware untouched. Web reuses `holdOrderLockController` for Anular; `CartContext.restoreOrStartSale` rejects OnHold. WPF is already compliant.

## Affected Areas

| Area | Impact |
|------|--------|
| `SalesService.HoldClaims.cs` | Modified |
| `Sales.Module/Exceptions/HoldNotClaimedException.cs` | New |
| `Web.Frontend/src/pages/PendingOrdersPage.jsx` | Modified |
| `Web.Frontend/src/context/CartContext.jsx` | Modified |
| `CommandCenter.Tests/*` | Modified |

Commands: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`; `npm test` + `npm run lint` (Web.Frontend).

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| ~27 tests flip exception type/order | High | Seed claim+actor; assert new type |
| Web Anular breaks for free orders | Med | Claim before cancel, release after |
| Cart rate-effect 409 on OnHold load | Med | Refuse OnHold restore |
| Old clients get 409 on unclaimed OnHold | Med | Desired; UI is message-driven |

## Rollback Plan

Revert the guard and delete `HoldNotClaimedException`; revert UI changes and restore tests. No data rollback.

## Success Criteria

- [ ] .NET suite green with claim-seeded tests.
- [ ] Web suite (178+n) green; lint 0 errors.
- [ ] 409 + message verified for each mutator without a claim.

## Size Forecast

~350-500 authored lines (tests dominate). **Likely exceeds 400 → recommend chained PRs**: (1) backend guard + exception, (2) test migration + rejection tests, (3) web Anular + cart refusal.
