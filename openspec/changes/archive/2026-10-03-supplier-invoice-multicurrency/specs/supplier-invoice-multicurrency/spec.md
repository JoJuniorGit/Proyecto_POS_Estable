# Delta for supplier-invoice-multicurrency

## ADDED Requirements

### Requirement: Document Currency and Applied Rate

The system MUST capture a per-document currency (`USD` or `Bs.S`) and, for `Bs.S` documents, a required applied exchange rate greater than zero; both MUST be persisted as an immutable snapshot on the `SupplierInvoice` (`Currency`, `AppliedRate`). `AppliedRate` MUST be normalized with ceiling rounding to two decimals (`PricingCalculator.RoundExchangeRateCeiling`). `USD` documents MUST snapshot rate `1` regardless of the client-sent value.

#### Scenario: Stage a Bs.S invoice with applied rate

- GIVEN a supplier invoice issued in `Bs.S`
- WHEN the user stages it with an applied rate `36.50`
- THEN the draft invoice persists `Currency = "Bs.S"` and `AppliedRate = 36.50`
- AND the line document costs are persisted as issued

#### Scenario: Bs.S without a valid rate is rejected

- GIVEN a `Bs.S` staging request with a missing, zero or negative applied rate
- WHEN the request is staged
- THEN the system MUST reject it with a `ProblemDetails` error
- AND MUST NOT persist a draft `SupplierInvoice`

#### Scenario: Unsupported currency is rejected

- GIVEN a staging request with a currency other than `USD` or `Bs.S`
- WHEN the request is staged
- THEN the system MUST reject it with a `ProblemDetails` error

#### Scenario: USD snapshot is canonical

- GIVEN a `USD` staging request carrying an applied rate other than `1`
- WHEN the request is staged
- THEN the persisted `AppliedRate` is `1`

### Requirement: Cost Normalization to the USD Base

The backend MUST normalize each line's document unit cost to the system base cost in USD using the document's applied rate (`PricingCalculator.ToUSD`, AwayFromZero, 2 decimals) and MUST use the normalized cost for classification, margin recalculation and suggested prices. The document unit cost MUST be persisted alongside the normalized cost, and client-sent normalized values MUST NOT be trusted (server recomputes).

#### Scenario: Bs.S cost normalized

- GIVEN a line with document cost `365.00` and applied rate `36.50`
- WHEN staged
- THEN the persisted `UnitCostUSD` is `10.00`
- AND `UnitCostDocument` remains `365.00`
- AND suggested margins and prices derive from `10.00`

#### Scenario: Margin recalc is independent of devaluation

- GIVEN the same product staged twice with equivalent USD costs at different applied rates
- WHEN both lines are classified against `Product.CostPriceUSD`
- THEN both compare equal because every comparison uses the normalized cost
