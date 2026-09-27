# Delta for pending-order-hold-lock

## ADDED Requirements

### Requirement: Active Claim Required for Every OnHold Mutation

A mutation of an `OnHold` sale MUST be performed by the actor holding the active claim. When the claim is absent (`ClaimedByUserId == null`) or the actor is `null`, the system MUST reject the mutation before any business validation with `HoldNotClaimedException : InvalidOperationException` → 409 via the existing middleware, message `"El pedido #N no está reclamado; reclame el pedido antes de modificarlo."`. When another actor holds the claim, the existing `SaleLockedException` (409, message preserved) MUST be thrown. Coverage: the 13 mutators AddItem, RemoveItem, UpdateItemQuantity, UpdateExchangeRate, UpdatePriceList, UpdateSaleCustomer, UpdateSaleItems, AddPayment, AddPaymentsBatch, HoldSale (re-hold), CompleteSale, CancelSale, ConfirmPickup.

#### Scenario: Rejected without a claim

- GIVEN an `OnHold` sale with no claim
- WHEN any mutator (items, batch payment, complete, cancel, re-hold, exchange rate, price list, customer) is invoked with a `null` or non-holder actor
- THEN it MUST throw `HoldNotClaimedException` with the required message, returning 409

#### Scenario: Allowed when the actor holds the claim

- GIVEN an `OnHold` sale claimed by user 7
- WHEN a mutator is invoked with actor 7
- THEN it proceeds to its normal outcome

#### Scenario: Blocked when another actor holds the claim

- GIVEN an `OnHold` sale claimed by user 9
- WHEN a mutator is invoked with actor 7
- THEN `SaleLockedException` (409) MUST be thrown and the mutation MUST NOT execute

### Requirement: Inert and Unchanged Hold Paths

`ConfirmPickup` MUST require status `Completed`, so the claim guard MUST be inert there and this MUST be documented. `RecalculateOnHoldSalesAsync` and the elevated force-release MUST remain unchanged: they MUST NOT require the acting claim and MUST NOT regress.

#### Scenario: ConfirmPickup on a Completed sale

- GIVEN a `Completed` sale
- WHEN ConfirmPickup runs
- THEN the claim precondition MUST NOT be evaluated

#### Scenario: System recalculation and force-release

- GIVEN an `OnHold` sale
- WHEN `RecalculateOnHoldSalesAsync` runs, or an Admin/Manager force-releases a foreign claim
- THEN the operation MUST succeed without a claim by the actor

### Requirement: Web Cancel Claims Before Cancelling

The Anular UI MUST claim the `Editing` action before calling `cancelSale`, MUST release its own claim best-effort afterwards, and MUST NOT execute the cancellation when the claim fails.

#### Scenario: Claim succeeds

- GIVEN a selected free `OnHold` sale
- WHEN Anular is confirmed
- THEN the claim is taken before `cancelSale`, and the claim is released best-effort after

#### Scenario: Claim rejected with 409

- GIVEN an `OnHold` sale claimed by another user
- WHEN Anular is confirmed
- THEN `cancelSale` MUST NOT be called, the server message is shown, and the list reloads

### Requirement: Web Cart Refuses OnHold Restore

Loading or restoring an `OnHold` sale into the POS cart MUST be rejected with a message directing the user to Cuentas Abiertas, consistent with decision 8.121 (retomar is reclamar y abrir modal). This MUST prevent unclaimed `OnHold` cart mutations, including the automatic exchange-rate sync.

#### Scenario: Restoring an OnHold sale

- GIVEN a persisted `OnHold` sale id in session storage
- WHEN the cart restores it
- THEN the restore MUST be refused with a message pointing to Cuentas Abiertas, and no `OnHold` cart mutation occurs

#### Scenario: Rate effect avoided

- GIVEN the cart never holds an `OnHold` sale
- WHEN the exchange-rate sync effect runs
- THEN no unclaimed `updateSaleExchangeRate` 409 is produced

### Requirement: Compatibility Preserved

Clients that mutate an `OnHold` sale without claiming MUST now receive 409 as intended; the HTTP contract MUST NOT change and no migration MUST be required.

#### Scenario: No contract change

- GIVEN an existing client that mutates an `OnHold` sale without a claim
- WHEN it calls any mutator endpoint
- THEN it receives 409 with the documented message, with no 428 status and no schema migration
