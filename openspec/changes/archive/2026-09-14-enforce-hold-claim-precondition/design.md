# Design: Enforce Active Hold Claim on Every OnHold Mutator

## Technical Approach

Harden `EnsureHoldClaimAccess` so every `OnHold` mutation requires the acting actor to hold the claim. A missing claim (or `null` actor) throws a new `HoldNotClaimedException : InvalidOperationException` → 409 via the existing middleware; a foreign claim keeps throwing `SaleLockedException`. No HTTP contract, entity, or migration change. Web adapts its two unclaimed paths (Anular, cart restore); WPF is already compliant.

## Architecture Decisions

| # | Decision | Choice | Alternative | Rationale |
|---|----------|--------|-------------|-----------|
| 1 | Guard contract | Keep `EnsureHoldClaimAccess(Sale, int?)`, new semantics | Rename to `EnsureActiveHoldClaim` | 13 call sites stay untouched; logic in one place |
| 2 | Error type | New `HoldNotClaimedException` in `Sales.Module/Exceptions/`, derives from `InvalidOperationException` | Plain `InvalidOperationException` | Typed/testable; middleware 409 branch (`:261`) unchanged |
| 3 | Status | 409 (no middleware edit) | 428 | Established contract; message-driven clients |
| 4 | Web cart + OnHold | Refuse load/restore, point to Cuentas Abiertas | Claim lifecycle on load | Aligns with 8.121; kills the automatic rate-sync hole |
| 5 | Anular | Claim `Editing` → cancel → best-effort release | Server auto-claim in cancel | Reuses `holdOrderLockController`; server stays pure |
| 6 | WPF | No changes | Guard recovery paths | WPF recovery restores only `Pending` |

### Guard truth table

| Status | `ClaimedByUserId` | `actingUserId` | Result |
|--------|-------------------|----------------|--------|
| not OnHold | any | any | no-op |
| OnHold | non-null | same value | proceed |
| OnHold | non-null | different / `null` | `SaleLockedException` |
| OnHold | `null` | any (incl. `null`) | `HoldNotClaimedException` |

## Data Flow

    Mutator ─▶ GetSaleEntityAsync ─▶ EnsureHoldClaimAccess
                                        │ OnHold + no own claim
                                        ▼
                     HoldNotClaimedException ─▶ Middleware(409) ─▶ message

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Sales.Module/Exceptions/HoldNotClaimedException.cs` | Create | Carries `SaleId`; message `El pedido #{N} no está reclamado; reclame el pedido antes de modificarlo.` |
| `Sales.Module/Services/SalesService.HoldClaims.cs` | Modify | New `EnsureHoldClaimAccess` semantics; `BuildHoldNotClaimedException` factory |
| `Web.Frontend/src/pages/PendingOrdersPage.jsx` | Modify | `handleConfirmCancelSale` (:143-162) claims `Editing` before `cancelSale`, releases best-effort in `finally` |
| `Web.Frontend/src/context/CartContext.jsx` | Modify | Refuse OnHold at cache init (:30), persist (:65), `restoreOrStartSale` (:196), rate effect (:216), `loadExistingSale` (:166) |
| `CommandCenter.Tests/*` | Modify | Seed claim + actor in 27 cases; add rejection tests |
| `Web.Frontend` tests | Modify | Anular claim-order and cart refusal tests |

Unchanged on purpose: `GlobalExceptionHandlerMiddleware`; `ISalesService` (no signature change); `Desktop.Client.Core/*` — WPF is compliant (`PosViewModel.RestoreOrStartSaleAsync:200,219` and `CartViewModel.PersistRecoveryState:195` gate on `Pending`; `PendingOrdersViewModel` claims before acting).

## Interfaces / Contracts

```csharp
public class HoldNotClaimedException : InvalidOperationException
{
    public int SaleId { get; }
    public HoldNotClaimedException(int saleId)
        : base($"El pedido #{saleId} no está reclamado; reclame el pedido antes de modificarlo.")
        => SaleId = saleId;
}
```

```csharp
private static void EnsureHoldClaimAccess(Sale sale, int? actingUserId)
{
    if (sale.Status != SaleStatus.OnHold) return;
    if (sale.ClaimedByUserId != null && sale.ClaimedByUserId == actingUserId) return;
    if (sale.ClaimedByUserId != null) throw BuildSaleLockedException(sale);
    throw new HoldNotClaimedException(sale.Id);
}
```

Ordering: the guard stays after `GetSaleEntityAsync` and **before** business validations, so unclaimed OnHold calls surface the 409 claim error first (e.g. `CancelSaleAsync` payments check `SalesService.cs:377-380`). `ConfirmPickup` stays inert (requires `Completed`).

Web refusal message: `Los pedidos en espera se editan desde Cuentas Abiertas; retome el pedido #N allí para reclamarlo.` On refusal, clear the three `active_pos_*` session keys and start a fresh `Pending` sale. Web `recoverPendingSale` (:412) and the WPF snapshot restore only `Pending`, so recovery is unaffected.

## Testing Strategy

| Layer | What | Approach |
|-------|------|----------|
| Unit (backend) | Guard matrix, exact type/message | New `HoldNotClaimedPreconditionTests`; reject-without-claim + allow-with-claim |
| Unit (web) | Anular claim order; cart refuses OnHold | Extend controller/page/cart tests |
| Integration | 409 + message end-to-end | `WebApplicationFactory` on cancel, complete, batch payment |

27 existing cases seed a claim + actor (helper sets `ClaimedByUserId`/`ClaimAction`/`ClaimedByUserName`/`ClaimedAtUtc`, pass `actingUserId: TestActorId`) so original domain assertions still fire: `OnHoldSalesTests.cs` (7), `OnHoldSalesTests.Liquidation.cs` (4), `BatchPaymentTests.cs` (4), `PriceListTests*.cs` (4), `FinancialRobustnessTests.cs` (3), `CheckoutAndPaymentTests.cs` (1), `Sprint1PerformanceOptimizationTests.cs` (1), `Integration/*` (3). `BatchPaymentTests` empty-list and >50 validations run before `GetSaleEntityAsync` → stay green.

## Threat Matrix

N/A — no routing, shell, subprocess, VCS/PR automation, executable-file classification, or process-integration boundary.

## Migration / Rollout

No migration, data change, or flag. `force-release` and `RecalculateOnHoldSalesAsync` unchanged. Estimate ~350-500 authored lines (tests dominate) → **exceeds 400: chain 3 slices**: (1) backend guard + exception; (2) test migration + rejection tests; (3) web Anular + cart refusal. Rollback: revert guard, delete exception, revert UI/tests.

## Open Questions

- [ ] `CartContext.loadExistingSale` is exported with no in-repo caller: keep its refusal for consistency or delete it?
- [ ] With the cart refusing OnHold, web re-hold (`HoldSaleModal`) is unreachable from the cart; confirm no other caller depends on it (server guard still covers it).
