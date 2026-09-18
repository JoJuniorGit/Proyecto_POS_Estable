# payment-method-currency-classification Specification

## Purpose

Makes `PaymentMethodCurrencyResolver` the single source of truth for classifying a payment
method as USD or Bs.S, so reports and stored receipts never disagree.

## Requirements

### Requirement: Single Classifier Source of Truth

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

### Requirement: Conversions via PricingCalculator.ToUSD

Bs.S-to-USD conversions on the report path MUST use `Core.Helpers.PricingCalculator.ToUSD` and
MUST NOT divide by the exchange rate inline. The desktop-only `Desktop.Client.Helpers.PricingHelper.ToUSD`
is a pure delegation to this helper; `Backend.API` MUST NOT reference the desktop project.

#### Scenario: Bs.S total is converted with the shared helper

- GIVEN a Bs.S amount and an exchange rate
- WHEN the report computes its USD equivalent
- THEN the value MUST equal `PricingCalculator.ToUSD(amount, rate)`

### Requirement: Report and Receipt Agreement

The report's currency labels and amounts MUST agree with the stored receipt/PDF
classification for the same payment method.

#### Scenario: Report matches the stored receipt

- GIVEN a completed sale with a stored receipt
- WHEN the report is generated for that sale
- THEN the report's label and amount for each method MUST match the receipt/PDF classification
### Requirement: Client Currency Is Not Authoritative

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

### Requirement: Close Page Uses Server Classification

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
