# change-007 Tasks - 8.212 WU6

## Review Workload Forecast
- Estimated authored lines: 250-380.
- Budget risk: Medium; tests only, no production behavior change expected.
- Chained PRs recommended: No; final local work unit.
- Decision needed before apply: No.

## Tasks

- [x] 7.1 Add the PostgreSQL concurrency test for two method-aware advances against one physical
  method; assert one winner, no loser persistence, and a non-negative final balance.
- [x] 7.2 Add the SignalR `CashDrawerChanged` transport test.
- [x] 7.3 Extend `E2eApiClient` with open-with-methods, active-session, physical-balance and
  method-aware advance helpers.
- [x] 7.4 Add the gated full-stack physical-method drawer E2E scenario.
- [x] 7.5 Run focused tests, the PG lane, the no-PG baseline, build and web tests.

## Acceptance
- R1-R6 of `.sdd/specs/change-007.md` satisfied.
- Existing tests remain green; no production behavior regression.
