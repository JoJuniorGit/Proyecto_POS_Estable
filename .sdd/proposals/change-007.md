# change-007: 8.212 WU6 - concurrency hardening and E2E evidence

## Status
PROPOSED

## Description
Close 8.212 with real PostgreSQL concurrency coverage for method-aware cash advances, a focused
post-commit `CashDrawerChanged` notifier test, and a full-stack E2E scenario that opens a session
with an explicit method breakdown, verifies the per-method balances, processes a method-aware
advance, and verifies the resulting balances.

## Scope
- [ ] Tests
- [ ] PostgreSQL
- [ ] E2E

## Acceptance Criteria
- [ ] Two concurrent advances against the same physical method cannot overdraw it.
- [ ] The advisory lock serializes balance validation and the loser persists no sale or movement.
- [ ] The SignalR notifier emits `CashDrawerChanged(sessionId)` through the exchange-rate hub.
- [ ] The E2E client exercises open-with-methods, physical balances, and method-aware advance
      against the real backend/PostgreSQL stack.
- [ ] Build, web tests, PG lane and the known no-PG baseline remain green.
