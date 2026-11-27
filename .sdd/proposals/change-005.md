# change-005: 8.212 WU4 - WPF physical method drawer UI

## Status
PROPOSED

## Description
Expose per-physical-method drawer balances in the WPF cash drawer, send the physical dispenser and
amount from the cash-advance dialog, validate the selected method balance client-side, and support
an explicit per-method opening breakdown while preserving the legacy local/USD fields.

## Scope
- [ ] Frontend
- [ ] WPF
- [ ] Tests

## Acceptance Criteria
- [ ] WPF reads `PhysicalMethodBalances` from the active session and falls back to the endpoint.
- [ ] Drawer view shows one card per physical method with its currency and current balance.
- [ ] Cash-advance dialog selects the dispensing physical method, shows the projected balance, and
      blocks confirmation on insufficient or inactive methods.
- [ ] The client sends `DispensedCashPaymentMethodId`, `DispensedAmount`, and
      `ReceivedPaymentMethodId`; legacy requests remain unchanged.
- [ ] The opening overlay sends `MethodBalances` when physical methods are available and derives the
      legacy local/USD aggregates from them.
- [ ] Focused tests and build pass.
