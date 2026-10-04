# Delta for supplier-invoice-staging

## MODIFIED Requirements

### Requirement: Staging Review Semantics

Each staged line MUST show exactly one status derived from the resolved product and the normalized USD cost: no resolved product → `[NEW]` (creation candidate, NOT approvable until a product is resolved); resolved product whose normalized cost differs from `Product.CostPriceUSD` — including `0.00` — → `[UPDATE]` (old → new cost); equal → `[UNCHANGED]`. Approval MUST require a resolved product. The per-row margin override prefill, approval toggle default, instant client-side recalculation and the no-apply-before-confirm invariant keep applying unchanged. `Conflict` is retained only as a legacy persisted value.

#### Scenario: Unmatched line is a creation candidate

- GIVEN a staged line matching no `Product`
- WHEN staging renders it
- THEN it is flagged `[NEW]`
- AND the "Create Product" action is available for that line
- AND its approve toggle is disabled until the line resolves to a product

#### Scenario: Zero-cost product classifies as update

- GIVEN a staged line matching a product whose `CostPriceUSD` is `0`
- WHEN staging renders it
- THEN it is flagged `[UPDATE]` showing `0.00 → new cost`
- AND it is approvable

#### Scenario: Status classification per line uses normalized cost

- GIVEN matched lines with differing and equal normalized costs
- WHEN staging renders them
- THEN differing cost → `[UPDATE]` with old → new, equal cost → `[UNCHANGED]`

#### Scenario: Instant client-side recalc unchanged

- GIVEN a staged line with a pre-filled margin override
- WHEN the user edits the margin value
- THEN the suggested sale price updates instantly using `PricingCalculator.RoundPriceUp` over the normalized cost
- AND no backend request is issued per keystroke

#### Scenario: No apply before confirm

- GIVEN staged and approved lines exist
- WHEN the user has not triggered confirm
- THEN `Product.CostPriceUSD`, margins and `StockQuantity` remain unchanged
