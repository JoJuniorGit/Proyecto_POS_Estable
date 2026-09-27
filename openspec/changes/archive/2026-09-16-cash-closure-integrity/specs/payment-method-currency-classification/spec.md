# payment-method-currency-classification Specification

## Purpose

Makes `PaymentMethodCurrencyResolver` the single source of truth for classifying a payment
method as USD or Bs.S, so reports and stored receipts never disagree.

## Requirements

### Requirement: Single Classifier Source of Truth

The report path (`ShiftsController.GetReportById`, reached by `GET /api/shifts/current/report`
and `GET /api/shifts/{id}/report`) MUST classify payment-method currency using
`PaymentMethodCurrencyResolver`, and MUST NOT use a local heuristic.

#### Scenario: Report uses the shared classifier

- GIVEN a payment method whose name diverges from its code (e.g. "Dólares")
- WHEN `GetReportById` classifies it
- THEN the classification MUST come from `PaymentMethodCurrencyResolver`

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
