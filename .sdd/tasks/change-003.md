# change-003 Tasks - 8.212 WU2

## Review Workload Forecast
- Estimated authored lines: 260-320.
- Budget risk: Medium.
- Chained PRs recommended: No; one focused work unit.
- Decision needed before apply: No.

## Tasks

- [x] 2.1 Create `PhysicalMethodBalanceDto` and add `PhysicalMethodBalances` to
  `CashDrawerSessionResponseDto` without removing legacy fields.
- [x] 2.2 Implement `GetPhysicalMethodBalancesAsync` in `CashDrawerBalanceService` with
  per-method aggregation, active/regime/CanDispense projection, and symbol resolution.
- [x] 2.3 Delegate the read method through `CashDrawerService`/`ICashDrawerService` and populate
  the active-session DTO.
- [x] 2.4 Add `GET /api/cashdrawer/physical-method-balances` with session validation and the
  same collection contract.
- [x] 2.5 Add focused tests for opening, cash sale, change, petty cash/reversal, cash-in/out,
  and cash-advance movements, plus legacy aggregate parity.
- [x] 2.6 Run focused tests, full suite, build, and record evidence in the 8.212 task log.

## Acceptance
- R1-R6 of `.sdd/specs/change-003.md` satisfied.
- No change to persisted historical amounts.
- Legacy endpoints/aggregates remain green.
