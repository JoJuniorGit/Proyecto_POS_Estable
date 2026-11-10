# change-006: 8.212 WU5 - Web physical method drawer UI and realtime refresh

## Status
PROPOSED

## Description
Expose per-physical-method drawer balances in the React register, send the physical dispenser and
amount from the web cash-advance modal, support an explicit per-method opening breakdown, show the
per-method theoretical balances at close, and refresh the register when the server publishes
`CashDrawerChanged`.

## Scope
- [ ] Frontend
- [ ] Backend (SignalR notifier only)
- [ ] Tests

## Acceptance Criteria
- [ ] Register page reads `physicalMethodBalances` from the active session or the dedicated endpoint.
- [ ] Register page renders one balance card per physical method.
- [ ] Web cash-advance modal selects the dispensing method, shows the projected balance, and blocks
      confirmation on insufficient or inactive methods.
- [ ] The request sends `dispensedCashPaymentMethodId`, `dispensedAmount`, and
      `receivedPaymentMethodId`.
- [ ] Opening modal sends explicit `methodBalances` and derives the legacy local/USD aggregates.
- [ ] Close modal shows the per-method theoretical balances before closing.
- [ ] `CashDrawerChanged(sessionId)` is published after commit and the register refreshes on it.
- [ ] Focused tests, build, lint and web test suite pass.
