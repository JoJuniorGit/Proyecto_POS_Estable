# api-dto-boundary Specification - Delta

## Purpose

Closes `RESIDUAL-S4b-04`: the Web drawer-transaction filter resolves `source` against a bare client
ordinal instead of the server contract enum, so the `advance` filter matches `Closing` (4) and
excludes `CashAdvance` (2).

Delta mapping against `openspec/specs/api-dto-boundary/spec.md`:

| ID | Existing requirement | Delta action |
|----|----------------------|--------------|
| REQ-ADB-01 | Closure Contracts Return DTOs | Unchanged |
| REQ-ADB-02 | Drawer Contracts Return DTOs | Unchanged |
| REQ-ADB-03 | Projection Without Entity Instantiation | Unchanged |
| REQ-ADB-04 | DTO Immutability and Field Parity | Unchanged |
| REQ-ADB-05 | - | ADDED - transaction source resolves against the contract enum |

## ADDED Requirements

### Requirement: REQ-ADB-05 Transaction Source Resolves Against the Contract Enum

The `source` of a drawer transaction is defined by the server contract (`CashTransactionSource`).
Client code that filters or labels movements by `source` MUST resolve the value from one
contract-aligned mapping and MUST NOT compare against a bare ordinal literal.
In `Web.Frontend/src/pages/RegisterPage.jsx`, the `advance` filter MUST match `CashAdvance` (2) and
MUST NOT match `Closing` (4); the movement label for the same value MUST come from the same mapping
so filters and labels cannot diverge.

#### Scenario: Advance filter includes advances

- GIVEN a movement whose source is `CashAdvance` (2)
- WHEN the `advance` filter is applied
- THEN the movement MUST be included

#### Scenario: Advance filter excludes closings

- GIVEN a movement whose source is `Closing` (4)
- WHEN the `advance` filter is applied
- THEN the movement MUST be excluded

#### Scenario: Labels agree with the contract

- GIVEN a movement whose source is `CashAdvance` (2)
- WHEN it is rendered in the movements table
- THEN it MUST be labelled as a cash advance
- AND no value from the contract MAY be labelled as an advance unless it is `CashAdvance` (2)

#### Scenario: Filter resolves from the mapping

- GIVEN the source-filter implementation
- WHEN it is inspected
- THEN each filter MUST reference the contract-aligned mapping
- AND no bare ordinal literal MUST be compared
