# Delta for custody-delivery-wpf

## ADDED Requirements

### Requirement: Dispatch Dialog

On "Confirmar Retiro / Entregar", the Desktop client MUST open a modal dispatch view listing every item of the sale with product name, pending quantity, and a numeric input for the quantity to deliver today, plus an optional notes field. The input MUST NOT allow entering a quantity greater than the pending quantity (typed, pasted, or via spinner), MUST accept the product's decimal precision, and MUST reject negative values. The confirm action MUST stay disabled until at least one input is greater than zero, and MUST show per-line validation feedback on invalid values. Cancel MUST send no request.

#### Scenario: Input capped at pending
- GIVEN an item with pending 3
- WHEN the cashier types 4
- THEN the effective value is clamped/blocked at 3 and the input is flagged invalid

#### Scenario: Confirm disabled with empty selection
- GIVEN the dispatch dialog with all inputs at 0
- WHEN the cashier opens it
- THEN the confirm action is disabled
- AND enabling requires at least one quantity greater than zero

#### Scenario: Cancel sends nothing
- GIVEN the dialog with typed quantities
- WHEN the cashier cancels
- THEN no delivery request is sent and the sale is unchanged

### Requirement: Partial Badge and Progress

Rows in custody states MUST show a distinct visual state: `PendingPickup` keeps the existing amber badge; `PartiallyDelivered` MUST use a distinct badge (blue) and MUST show progress text/bar with delivered vs total units (e.g. "Retirado: 4/10"). After a partial confirm, the row MUST remain listed with updated pending quantities until completed, then leave the list.

#### Scenario: Partial row stays listed
- GIVEN a sale partially delivered
- WHEN the list refreshes
- THEN the row shows the partial badge and progress
- AND pending quantities reflect the delivered amount

#### Scenario: Completion removes the row
- GIVEN a sale whose last pending quantity is delivered
- WHEN the list refreshes
- THEN the row is no longer in Retiros Pendientes

### Requirement: Delivery Note Print

After a successful confirm, the Desktop client MUST offer printing the delivery note of that event; the print action MUST download the PDF and open it with the system PDF viewer. A conflict (`409`) MUST show an actionable message and reload the list without losing the entered dialog data when possible.

#### Scenario: Print after confirm
- GIVEN a confirmed partial delivery
- WHEN the cashier chooses "Imprimir Nota de Despacho"
- THEN the PDF of that event opens in the system viewer

#### Scenario: Conflict feedback
- GIVEN a delivery rejected with `409`
- WHEN the error is shown
- THEN the message states the sale changed and the list is reloaded
