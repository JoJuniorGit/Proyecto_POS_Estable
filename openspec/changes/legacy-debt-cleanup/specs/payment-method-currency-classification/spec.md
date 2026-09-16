# payment-method-currency-classification Specification — Delta

## Purpose

Extends the existing capability, which bound only the report path, to the closure write path so the
resolver is the single source of truth everywhere and a client can no longer alter the arqueo by
declaring a currency, and removes the client-side currency heuristic of the close page.

Delta mapping against `openspec/specs/payment-method-currency-classification/spec.md` (stable IDs
assigned here by document order):

| ID | Existing requirement | Delta action |
|----|----------------------|--------------|
| REQ-PMC-01 | Single Classifier Source of Truth | MODIFIED — scope extended to `CloseShift` |
| REQ-PMC-02 | Conversions via PricingCalculator.ToUSD | Unchanged |
| REQ-PMC-03 | Report and Receipt Agreement | Unchanged |
| REQ-PMC-04 | — | ADDED — client currency not authoritative |
| REQ-PMC-05 | — | ADDED — close page uses server classification |

## MODIFIED Requirements

### Requirement: REQ-PMC-01 Single Classifier Source of Truth

The report path (`ShiftsController.GetReportById`, reached by `GET /api/shifts/current/report`
and `GET /api/shifts/{id}/report`) and the closure write path (`ShiftsController.CloseShift`,
`POST /api/shifts/close`) MUST classify payment-method currency using
`PaymentMethodCurrencyResolver`, and MUST NOT use a local heuristic or a client-supplied currency.
(Previously: only the report path was bound; `CloseShift` read `request.Currency` and used it to
echo and to decide amounts.)

#### Scenario: Report uses the shared classifier

- GIVEN a payment method whose name diverges from its code (e.g. "Dólares")
- WHEN `GetReportById` classifies it
- THEN the classification MUST come from `PaymentMethodCurrencyResolver`

#### Scenario: Close classifies every declared method via the resolver

- GIVEN a close request declaring methods of both currencies
- WHEN `CloseShift` builds the closure
- THEN every declared method MUST be classified by `PaymentMethodCurrencyResolver`

#### Scenario: A diverging method name does not change the close classification

- GIVEN a declared method named "Dólares" whose code classifies as Bs.S
- WHEN the closure is built
- THEN the classification MUST be the resolver's (Bs.S)

## ADDED Requirements

### Requirement: REQ-PMC-04 Client Currency Is Not Authoritative

`request.Currency` MUST NOT be read in `CloseShift` and MUST NOT influence the amounts, the
balanced/surplus/shortage status, or the persisted closure of any declared method. Amounts MUST be
validated against the server-side classification only.

#### Scenario: Client declares USD for a local-currency method

- GIVEN a declared amount submitted with `currency: "USD"` for a method the resolver classifies as Bs.S
- WHEN the close runs
- THEN the amounts and the closure status MUST be computed with the resolver classification (Bs.S)
- AND the persisted closure MUST record the resolver classification

#### Scenario: Currency omitted from the declaration

- GIVEN a declared amount submitted without a currency value
- WHEN the close runs
- THEN the close MUST succeed using the resolver classification

#### Scenario: Unknown declared payment method

- GIVEN a declaration referencing a payment method id that does not exist
- WHEN `CloseShift` validates it
- THEN the response MUST be HTTP 400 with an RFC 7807 payload
- AND no closure MUST be persisted

### Requirement: REQ-PMC-05 Close Page Uses Server Classification

`Web.Frontend/src/pages/RegisterClosePage.jsx` MUST NOT classify currency from method names or
substrings (`usd`, `dolar`, `$`, `divisa`). Any currency label it displays MUST come from a
server-side classification (the resolver reached through a server contract).

#### Scenario: No local heuristic remains

- GIVEN the close page source
- WHEN currency classification is inspected
- THEN no name/substring based fallback MUST remain

#### Scenario: Diverging method name is labelled by the server value

- GIVEN a method named "Dólares" classified as Bs.S on the server
- WHEN the close page presents it
- THEN it MUST be presented as Bs.S
