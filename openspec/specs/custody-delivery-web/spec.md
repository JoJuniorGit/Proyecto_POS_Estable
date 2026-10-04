# custody-delivery-web Specification

## Purpose

Web dispatch experience for partial deliveries: clamped quantity modal, partial badge/progress, and authenticated blob printing.

## Requirements

### Requirement: Dispatch Modal

On "Confirmar Retiro", the Web client MUST show a modal listing every item of the sale with product name, pending quantity, and a numeric input for the quantity to deliver today. The input MUST NOT accept a quantity greater than the pending quantity (typed or pasted), MUST accept fractional quantities for fractional products, and MUST reject negative values. Confirm MUST stay disabled until at least one input is greater than zero, display an error state when the server rejects the request, and send the full per-line payload with an idempotency key.

#### Scenario: Input capped at pending
- GIVEN an item with pending 3
- WHEN the cashier types 4
- THEN the value is clamped/blocked at 3 and marked invalid

#### Scenario: Confirm disabled with empty selection
- GIVEN the modal with all inputs at 0
- THEN the confirm button is disabled

#### Scenario: Server rejection shown
- GIVEN a stale modal where another cashier already delivered part of the order
- WHEN confirm returns `400`/`409`
- THEN the modal surfaces the error message and the list reloads

### Requirement: Partial Badge and Progress

Sale cards/rows in custody states MUST show a distinct badge per state: `PendingPickup` keeps the existing amber "En Custodia" style; `PartiallyDelivered` MUST use a distinct blue badge plus progress text/bar with delivered vs total units. After a partial confirm, the item MUST stay listed with updated pending quantities until completed, then leave the list.

#### Scenario: Partial item stays listed
- GIVEN a partially delivered sale
- WHEN the list refreshes
- THEN it shows the partial badge and updated progress

#### Scenario: Completion removes the item
- GIVEN the last pending quantity is delivered
- WHEN the list refreshes
- THEN the item is no longer listed

### Requirement: Delivery Note Print

After a successful confirm, the Web client MUST offer printing the delivery note of that event; the action MUST fetch the PDF with authorization and open it in a new browser tab/window for printing. The note fetch MUST reuse the sale-scoped token session, never exposing an unauthenticated URL.

#### Scenario: Print after confirm
- GIVEN a confirmed partial delivery
- WHEN the cashier chooses "Imprimir Nota de Despacho"
- THEN the authenticated PDF blob opens in a new tab ready to print

#### Scenario: Print failure
- GIVEN the PDF request fails
- WHEN the cashier triggers print
- THEN an error message is shown and the delivery itself remains recorded
