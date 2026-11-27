# change-004 Tasks - 8.212 WU3

## Review Workload Forecast
- Estimated authored lines: 300-380.
- Budget risk: Medium.
- Chained PRs recommended: No; one focused backend work unit.
- Decision needed before apply: No.

## Tasks

- [x] 3.1 Extend `CashAdvanceRequest` and `CashAdvanceResultDto` with dispensed/received
  method, amount, and currency fields while retaining legacy fields.
- [x] 3.2 Add optional received-side parameters to `SalesService.CreateCashAdvanceSaleAsync`;
  keep the legacy behavior when they are absent.
- [x] 3.3 Resolve and validate dispensed/received methods in `CashAdvanceCoordinator`, including
  regime filtering, auto-selection, ambiguity errors, and currency-specific rounding.
- [x] 3.4 Move the balance check inside `ProcessEnvelopeAsync` after the session lock and use
  `GetPhysicalMethodBalancesAsync` for the exact dispensed method.
- [x] 3.5 Record the physical expense against the dispensed method/currency and the commission
  income against the received method/currency; return the extended result.
- [x] 3.6 Add focused tests for legacy compatibility, explicit dispensed method, insufficient
  method balance, same-currency, cross-currency in both directions, invalid roles, and result
  fields; run build and cash-advance regressions.

## Acceptance
- R1-R6 of `.sdd/specs/change-004.md` satisfied.
- Existing cash-advance tests remain green.
- No historical amounts are recalculated.
