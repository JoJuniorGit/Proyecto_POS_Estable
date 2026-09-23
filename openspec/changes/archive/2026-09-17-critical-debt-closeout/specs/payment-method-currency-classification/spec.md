# payment-method-currency-classification Specification - Delta

## Purpose

Closes `WARNING-04`: the closure/report lines for payment methods merged in because they were
undeclared must derive their amounts and status from the method's resolved currency, instead of
hardcoding `ClosureStatus.Balanced` and mixing Bs.S declared values with USD-converted system
values. Response-only: no persisted closure snapshot changes.

Delta mapping against `openspec/specs/payment-method-currency-classification/spec.md` (stable IDs
assigned here by document order):

| ID | Existing requirement | Delta action |
|----|----------------------|--------------|
| REQ-PMC-01 | Single Classifier Source of Truth | Unchanged |
| REQ-PMC-02 | Conversions via PricingCalculator.ToUSD | Unchanged |
| REQ-PMC-03 | Report and Receipt Agreement | Unchanged |
| REQ-PMC-04 | Client Currency Is Not Authoritative | Unchanged |
| REQ-PMC-05 | Close Page Uses Server Classification | Unchanged |
| REQ-PMC-06 | - | ADDED - per-currency line values and derived status |

## ADDED Requirements

### Requirement: REQ-PMC-06 Per-Currency Line Values and Derived Status

Closure and shift-report lines produced for a payment method merged in because it was undeclared
MUST express the declared amount, the system amount and their difference in the method's resolved
currency (`PaymentMethodCurrencyResolver`). A line's status MUST be derived from that per-currency
difference, with the same tolerance as the declared path, and MUST NOT be hardcoded:
`ClosureStatus.Balanced` MUST NOT be emitted when the per-currency difference is non-zero. No line
value MAY mix Bs.S and USD. The already-persisted Bs.S snapshot amounts (`DifferenceBsS`,
`TotalDifferenceBsS`) MUST NOT be recomputed or rewritten.

#### Scenario: Merged USD line is single-currency

- GIVEN an undeclared method the resolver classifies as USD with a non-zero expected amount
- WHEN the closure response is built
- THEN the declared, system and difference amounts MUST all be expressed in USD
- AND no Bs.S value MAY be mixed into that line

#### Scenario: Non-zero difference is never Balanced

- GIVEN a merged line whose per-currency difference is outside the declared-path tolerance
- WHEN the line status is produced
- THEN it MUST be `Surplus` or `Shortage` by the sign of the difference
- AND it MUST NOT be `Balanced`

#### Scenario: Within-tolerance difference stays Balanced

- GIVEN a merged line whose per-currency difference is within the declared-path tolerance
- WHEN the line status is produced
- THEN it MUST be `Balanced`

#### Scenario: Persisted snapshot is untouched

- GIVEN a closure persisted before the change
- WHEN it is read back after the change
- THEN its persisted Bs.S amounts and snapshot fields MUST be unchanged
