# System Integrity Rules

## 1. Immutability of Sales History
**Rule:** Recalculating totals in the history using the current `ExchangeRateService` rate is strictly PROHIBITED.
**Context:** The history module is designed to reflect past transactions exactly as they occurred. All amounts and exchange rates must be pulled from the persisted snapshot fields:
- `AppliedRate`: The exchange rate applied at the moment of the transaction.
- `TotalUSD`: The original subtotal/total in USD at the time of the sale.
- `FinalPaidAmountBsS`: The total amount literally paid by the user in the local currency.
- `TotalBsS`: The system-calculated total (digital) at checkout time.
- `RoundingAdjustment`: The adjustment made for cash payments (the difference between `FinalPaidAmountBsS` and `TotalBsS`).

Any attempt to apply the *current* market exchange rate to *historical* sales violates accounting integrity and is forbidden.
