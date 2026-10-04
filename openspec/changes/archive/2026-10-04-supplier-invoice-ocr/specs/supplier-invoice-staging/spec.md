# Delta for supplier-invoice-staging

## ADDED Requirements

### Requirement: OCR-Sourced Staging Without Template

A staging request marked as OCR-sourced MUST be accepted without a column-mapping template (the tabular first-import mapping requirement does not apply), MUST persist the per-field OCR confidence values on its lines, and MUST keep every other staging guarantee unchanged (supplier resolution, zero-trust validation, no catalog mutation before confirm).

#### Scenario: First OCR import for a supplier without template

- GIVEN a supplier with no persisted column mapping
- WHEN an OCR-sourced invoice is staged
- THEN a draft invoice with its lines is persisted
- AND no mapping error is raised

#### Scenario: Confidence persisted on lines

- GIVEN OCR rows carrying name/quantity/cost confidences
- WHEN the invoice is staged
- THEN each line persists its three confidence values
- AND non-OCR lines persist null confidences

#### Scenario: Tabular flow untouched

- GIVEN a file-based staging request without the OCR flag
- WHEN it is staged
- THEN the existing first-import mapping requirement still applies

## MODIFIED Requirements

### Requirement: Staging Review Semantics

Each staged line MUST show exactly one status derived from the resolved product and the normalized USD cost: no resolved product → `[NEW]` (creation candidate, NOT approvable until a product is resolved); resolved product whose normalized cost differs from `Product.CostPriceUSD` — including `0.00` — → `[UPDATE]` (old → new cost); equal → `[UNCHANGED]`. Approval MUST require a resolved product. The per-row margin override prefill, approval toggle default, instant client-side recalculation and the no-apply-before-confirm invariant keep applying unchanged. `Conflict` is retained only as a legacy persisted value. Additionally, lines staged from OCR MUST render their per-field low-confidence signals in the review grid (yellow 60–85, red below 60 on the 0–100 scale) so the reviewer verifies them against the source image before approving.

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

#### Scenario: Low-confidence cells are highlighted

- GIVEN an OCR-staged line with quantity confidence `72` and cost confidence `41`
- WHEN the review grid renders
- THEN the quantity cell is highlighted yellow and the cost cell red
- AND the reviewer can still edit both values before approving
