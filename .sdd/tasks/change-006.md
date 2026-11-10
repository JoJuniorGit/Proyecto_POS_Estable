# change-006 Tasks - 8.212 WU5

## Review Workload Forecast
- Estimated authored lines: 500-650.
- Budget risk: High; one web work unit plus a small SignalR notifier.
- Chained PRs recommended: No for the local work unit; the accumulated feature branch follows the
  existing delivery strategy.
- Decision needed before apply: No.

## Tasks

- [x] 6.1 Add `cashDrawerApi.js` with physical balance read and method-aware advance request.
- [x] 6.2 Load physical balances in `RegisterPage`, render cards, and pass them to the modals.
- [x] 6.3 Add dispenser selector, projection, validation, and extended request to `CashAdvanceModal`.
- [x] 6.4 Add per-method opening breakdown and aggregate derivation to `CashDrawerOpenModal`.
- [x] 6.5 Add read-only per-method balances to `CashDrawerCloseModal`.
- [x] 6.6 Add `CashDrawerChanged` to the SignalR hub and refresh the register.
- [x] 6.7 Add backend notifier and post-commit calls in the drawer controllers.
- [x] 6.8 Add focused web tests; run web tests, lint, build and relevant backend tests.

## Acceptance
- R1-R8 of `.sdd/specs/change-006.md` satisfied.
- Legacy payloads and existing tests remain green.
