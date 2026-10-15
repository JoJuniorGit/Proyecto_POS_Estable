# Delta for supplier-invoice-staging

## MODIFIED Requirements

### Requirement: Staging Review Semantics

Each staged line MUST show exactly one status derived from the resolved product and the normalized USD cost: no resolved product → `[NEW]` (creation candidate, NOT approvable until a product is resolved); resolved product whose normalized cost differs from `Product.CostPriceUSD` — including `0.00` — → `[UPDATE]` (old → new cost); equal → `[UNCHANGED]`. Approval MUST require a resolved product. The per-row margin override prefill, approval toggle default, suggested-price derivation and the no-apply-before-confirm invariant keep applying unchanged; the Desktop review grid no longer exposes margin-editing columns (retired by the maintainer, ANEXO 8.160), so the prefilled margin travels to confirm without user editing. `Conflict` is retained only as a legacy persisted value. Additionally, lines staged from OCR MUST render their per-field low-confidence signals in the review grid (yellow 60–85, red below 60 on the 0–100 scale) so the reviewer verifies them against the source document before approving.

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

#### Scenario: Suggested prices derive from prefilled margins

- GIVEN a staged line with a pre-filled margin override
- WHEN the review grid renders
- THEN the suggested sale prices derive using `PricingCalculator.RoundPriceUp` over the normalized cost
- AND no margin-editing column is shown in the Desktop grid

#### Scenario: No apply before confirm

- GIVEN staged and approved lines exist
- WHEN the user has not triggered confirm
- THEN `Product.CostPriceUSD`, margins and `StockQuantity` remain unchanged

#### Scenario: Low-confidence cells are highlighted

- GIVEN an OCR-staged line with quantity confidence `72` and cost confidence `41`
- WHEN the review grid renders
- THEN the quantity cell is highlighted yellow and the cost cell red
- AND the reviewer can still edit both values before approving
