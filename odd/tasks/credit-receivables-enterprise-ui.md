# Credit Receivables Enterprise UI

Branch: V0.15. Delivery: local work-unit commit; no push. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --no-build`.

## Specs

S1. Fix the empty-state visibility so it is visible only after a completed load with zero rows and never while loading or when rows exist.

S2. Fix the status ComboBox: `MinHeight=40`, `MinWidth=160`, centered content, comfortable padding, no rigid height, and `#EDE9FE`/`#5B21B6` item selection and hover.

S3. Unify copy and currency presentation: singular/plural header count, no duplicated daily-rate line in the subtitle, `Bs.S` display code, and one configured decimal separator for KPIs, rates, and grid amounts.

S4. Enterprise header, filter card, and four KPI cards with the requested palette, flat primary/secondary buttons, conditional overdue styling, and Dual-only local/rate content.

S5. Eight-column virtualized grid: document, client, due date, total/paid progress, balance, today local equivalent, status pill, and actions. Row hover/selection must not leave a permanent lavender background.

S6. Compact inline actions (`Cobrar`, `Edo. Cuenta`, `PDF`) plus a left-click overflow menu for `Devolver` and `Anular`, using safe ContextMenu PlacementTarget bindings and existing commands/RBAC.

S7. Preserve payment, statement, PDF, cancel, return, filter, and reload behavior; add tests for the new read-only presentation state.

S8. Build with 0 warnings/0 errors, run the CxC/full test suite, and validate populated, empty, 96 DPI, and 125 DPI states in a WPF harness.

## Tasks

- [x] T1 (S1-S3) - ViewModel presentation state, pluralization, empty-state gate, clear-search command, currency/rate formatting.
- [x] T2 (S4) - XAML header, filter card, and KPI cards.
- [x] T3 (S5-S6) - Eight-column grid, row states, compact actions, and safe overflow ContextMenu.
- [x] T4 (S7) - DTO presentation helpers and ViewModel tests.
- [x] T5 (S8) - Build, tests, and WPF visual harness.

## Log

L1. User requested a Senior WPF UI/UX and MVVM refactor of the local CxC view, including empty-state visibility, ComboBox sizing, currency/pluralization consistency, enterprise header/filters/KPIs, eight grid columns, safe overflow actions, and 100/125 DPI verification.

L2. Implemented `ShowEmptyState`, pluralized `HeaderSubtitle`, `TodayRateDisplay` with configured separators, local KPI equivalence, status/progress/initials DTO helpers, compact actions, and a ContextMenu driven by `PlacementTarget.Tag`/`PlacementTarget.DataContext`.

L3. Evidence: build 0 warnings/0 errors; CxC tests 15/15; full suite 4544/4544; harness screenshots at 96 and 125 DPI for populated, selected-row, and empty states, plus overflow menu; no row overlap, no ComboBox clipping, neutral zero-mora card, and full-width column layout.
