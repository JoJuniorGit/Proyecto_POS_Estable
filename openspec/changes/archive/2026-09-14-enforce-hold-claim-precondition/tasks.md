# Tasks: Enforce Active Hold Claim on Every OnHold Mutator

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | 420–550 |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 → PR 2 → PR 3 |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Backend guard + exception | PR 1 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~HoldNotClaimed"` | N/A — pure unit guard logic | Delete `HoldNotClaimedException.cs`, revert `EnsureHoldClaimAccess` in `SalesService.HoldClaims.cs` |
| 2 | Test migration + rejection coverage | PR 2 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` | N/A — xUnit suite | Revert all `CommandCenter.Tests/*` changes; tests return to prior state |
| 3 | Web Anular + cart refusal | PR 3 | `npm test && npm run lint` | N/A — unit/lint only | Revert `PendingOrdersPage.jsx` and `CartContext.jsx`; web returns to prior state |

---

## Phase 1: Foundation — Backend Guard + Exception

- [x] 1.1 Create `Sales.Module/Exceptions/HoldNotClaimedException.cs` with `SaleId` property and Spanish message (design §Interfaces). **Spec**: Rejected without a claim, No contract change.
- [x] 1.2 Modify `EnsureHoldClaimAccess` in `Sales.Module/Services/SalesService.HoldClaims.cs` — invert null-claim branch to throw `HoldNotClaimedException` instead of returning (design §Guard truth table). **Spec**: Rejected without a claim, Allowed when the actor holds the claim, Blocked when another actor holds the claim.
- [x] 1.3 Add `BuildHoldNotClaimedException` private factory in `SalesService.HoldClaims.cs`. **Spec**: Rejected without a claim.

## Phase 2: Test Migration — Seed Claim+Actor in ~27 Cases + Rejection Coverage

- [x] 2.1 Seed claim+actor in `OnHoldSalesTests.cs` (7 cases: AddPayment, CompleteSale×3, UpdateSaleItems×3). Set `ClaimedByUserId`/`ClaimAction`/`ClaimedByUserName`/`ClaimedAtUtc`; pass `actingUserId: TestActorId`. **Spec**: Allowed when the actor holds the claim.
- [x] 2.2 Seed claim+actor in `OnHoldSalesTests.Liquidation.cs` (4 cases: CompleteSale, CancelSale×3). **Spec**: Allowed when the actor holds the claim.
- [x] 2.3 Seed claim+actor in `BatchPaymentTests.cs` (4 cases: AddPaymentsBatch×4). **Spec**: Allowed when the actor holds the claim.
- [x] 2.4 Seed claim+actor in `PriceListTests.cs` and `PriceListTests.Products.cs` (4 cases). **Spec**: Allowed when the actor holds the claim.
- [x] 2.5 Seed claim+actor in `FinancialRobustnessTests.cs` (3 cases: AddPayment×3). **Spec**: Allowed when the actor holds the claim.
- [x] 2.6 Seed claim+actor in `CheckoutAndPaymentTests.cs` (1 case), `Sprint1PerformanceOptimizationTests.cs` (1 case). **Spec**: Allowed when the actor holds the claim.
- [x] 2.7 Seed claim+actor in `Integration/EditHoldOrderFlowIntegrationTests.cs`, `Integration/PartialPaymentFlowIntegrationTests.cs`, `Integration/LiquidationFlowIntegrationTests.cs` (3 cases). **Spec**: Allowed when the actor holds the claim.
- [x] 2.8 Write new `HoldNotClaimedPreconditionTests` — reject without claim (null `ClaimedByUserId`, null actor) for 3 representative mutators. **Spec**: Rejected without a claim.
- [x] 2.9 Write new rejection test — `SaleLockedException` preserved for foreign claim. **Spec**: Blocked when another actor holds the claim.
- [x] 2.10 Write new inert-path tests — `ConfirmPickup` on `Completed` sale no-ops guard; `RecalculateOnHoldSalesAsync` succeeds without claim. **Spec**: ConfirmPickup on a Completed sale, System recalculation and force-release.

## Phase 3: Web — Anular Claim-Before-Cancel + Cart Refuses OnHold

- [x] 3.1 Modify `handleConfirmCancelSale` in `Web.Frontend/src/pages/PendingOrdersPage.jsx` — claim `Editing` via `holdOrderLockController` before `cancelSale`, release best-effort in `finally`. **Spec**: Claim succeeds, Claim rejected with 409.
- [x] 3.2 Add 409 handling in `handleConfirmCancelSale` — show server message and reload list on claim failure; do not call `cancelSale`. **Spec**: Claim rejected with 409.
- [x] 3.3 Modify `CartContext.jsx` — refuse OnHold restore at `restoreOrStartSale` (:196), persist (:65), cache init (:30). Show message directing to Cuentas Abiertas; clear session keys and start fresh `Pending` sale. **Spec**: Restoring an OnHold sale.
- [x] 3.4 Refuse OnHold in `CartContext.jsx` rate effect (:215–229) and `loadExistingSale` (:166). **Spec**: Rate effect avoided.
- [x] 3.5 Verify `npm test && npm run lint` in `Web.Frontend` passes.
- [x] 3.6 Verify `dotnet build CommandCenter.slnx -c Release` passes (0 errors, 0 warnings).
- [x] 3.7 Verify full `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` suite passes (0 failures).

---

## Task → Scenario Mapping

| Task | Spec Scenario |
|------|---------------|
| 1.1, 1.2, 1.3 | Rejected without a claim; Allowed when the actor holds the claim; Blocked when another actor holds the claim |
| 2.1–2.7 | Allowed when the actor holds the claim (seed claim so existing domain assertions fire) |
| 2.8 | Rejected without a claim |
| 2.9 | Blocked when another actor holds the claim |
| 2.10 | ConfirmPickup on a Completed sale; System recalculation and force-release |
| 3.1, 3.2 | Claim succeeds; Claim rejected with 409 |
| 3.3 | Restoring an OnHold sale |
| 3.4 | Rate effect avoided |
| 3.5–3.7 | No contract change (compatibility preserved) |
