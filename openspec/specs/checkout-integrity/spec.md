# checkout-integrity Specification

## Purpose

Server-authoritative checkout integrity: the rounding adjustment is recomputed from persisted payments at completion (client value ignored), and cash-advance products are rejected in sale item mutations so they only enter through the cash-advance coordinator flow.

## Requirements

### Requirement: Server-Authoritative Rounding Adjustment

The backend MUST derive the sale's rounding adjustment deterministically from the persisted payments at completion time and MUST NOT persist any value received from the client. The adjustment MUST equal `PricingCalculator.RoundToDigital(totalPaidBsS − TotalBsS)` when the remaining balance (USD) is ≤ 0.01, and `0` otherwise, mirroring the checkout-preview computation. The request field remains accepted for backward compatibility but is ignored server-side.

#### Scenario: Injected adjustment is discarded

- GIVEN a sale whose payments cover the total and a client request with `RoundingAdjustment = 250`
- WHEN the sale completes
- THEN the persisted `RoundingAdjustment` equals the server recomputation
- AND the injected value is not persisted anywhere

#### Scenario: Partial payment yields zero adjustment

- GIVEN a pending-pickup sale with a remaining balance greater than 0.01 USD
- WHEN the sale completes
- THEN the persisted `RoundingAdjustment` is 0

#### Scenario: Recomputation mirrors the preview

- GIVEN a mixed-currency payment set
- WHEN checkout-preview and completion run for the same data
- THEN both produce the same adjustment value

### Requirement: Cash-Advance Products Excluded from Sale Item Flows

Sale item mutations MUST reject cash-advance products (`IsCashAdvance`) in `AddItemAsync` and `UpdateSaleItemsAsync` with the exact message `"Los productos de adelanto de efectivo no pueden agregarse ni modificarse en una venta; use el flujo de adelanto de efectivo."`. Cash-advance sales MUST remain creatable exclusively through `CreateCashAdvanceSaleAsync` (CashAdvanceCoordinator).

#### Scenario: AddItem rejects a cash-advance product

- GIVEN an open sale and a cash-advance product
- WHEN POST `/api/sales/{id}/items` references that product
- THEN the request fails with the exact message
- AND no item is added to the sale

#### Scenario: UpdateItems rejects a cash-advance product

- GIVEN a held sale
- WHEN PUT `/api/sales/{id}/items` includes a cash-advance product with any price
- THEN the request fails with the exact message
- AND no item is modified

#### Scenario: Legitimate cash-advance flow unaffected

- GIVEN a cashier performing a cash advance
- WHEN the coordinator creates the cash-advance sale
- THEN the sale is created with its item and no guard fires
