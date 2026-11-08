# change-005 Tasks - 8.212 WU4

## Review Workload Forecast
- Estimated authored lines: 550-700.
- Budget risk: High; one focused WPF work unit, local commit only.
- Chained PRs recommended: No for the local work unit; the accumulated feature branch follows the
  existing delivery strategy.
- Decision needed before apply: No.

## Tasks

- [x] 5.1 Add client `PhysicalMethodBalanceClientDto`, session property, endpoint method, open
  breakdown overload, and method-aware cash-advance overload with legacy defaults.
- [x] 5.2 Load physical method balances in `CashDrawerViewModel`, expose cards, and refresh after
  session operations.
- [x] 5.3 Build the opening breakdown from active physical payment methods and derive the legacy
  local/USD aggregates.
- [x] 5.4 Add the dispenser selector, projection, inline error, and confirmation rules to
  `CashAdvanceRegisterViewModel`.
- [x] 5.5 Wire the method-aware dialog result through `WpfDialogService` and
  `CashDrawerViewModel.ProcessCashAdvanceAsync`.
- [x] 5.6 Add drawer cards and opening/advance XAML.
- [x] 5.7 Add focused ViewModel tests and run build plus cash-drawer/cash-advance regressions.

## Acceptance
- R1-R7 of `.sdd/specs/change-005.md` satisfied.
- Legacy dialog and cash-advance contracts remain available.
- Build is 0 errors / 0 warnings.
