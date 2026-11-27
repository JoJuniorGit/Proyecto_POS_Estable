# Daily Closure: Used Methods And Integrated USD Count

Branch: V0.15. Delivery: local work-unit commits; no push. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`. RDD: off (clone_local).

## Specs

S1. The WPF daily closure must let the operator declare an amount only for payment methods employed during the journey. Rows whose expected amount is zero (no activity in the closure window) must not appear in the declaration table.

S2. The USD cash count must live inside the cash-count card (`Métodos de pago`), not in a separate bottom card. Keep the existing optional USD input, its placeholder, its reconciliation values, RBAC/blind behavior, AutomationIds and the `ActualClosingBalanceUsd` payload.

S3. The observations card becomes full width below the cash-count card. Hide the duplicate MaterialDesign character counter on the observation TextBox, keeping the custom header counter.

S4. Preserve the backend contract, closure calculations, validation and API payloads. This is a WPF presentation and row-filtering change only.

S5. Verify with Release build 0/0, closure tests green, the full .NET suite with only the known PostgreSQL-lane environment failures, and WPF screenshots at 96/120 DPI proving the USD block is inside the cash-count card with no overlap.

## Tasks

- [x] T1 (S1) - Filter `DetailRows` to expected amounts different from zero in `LoadExpectedTotalsAsync`.
- [x] T2 (S2-S3) - Move the USD block into the methods card and make observations full width; hide the duplicate character counter.
- [x] T3 (S5) - Add the row-filter ViewModel test and run build/tests/visual harness.

## Log

L1. User requested that closures only allow declaring an amount for payment methods used during the journey, and that the USD count live inside the cash-count modal/card instead of a separate section.

L2. Implemented the `ExpectedAmountBsS != 0` row filter in `DailyClosureViewModel`, moved the USD block into the `Métodos de pago` card as an internal section with a top divider, expanded observations to full width and collapsed the MaterialDesign duplicate character counter.

L3. Evidence: Release build 0 warnings/0 errors; DailyClosure lane 100/100; full suite 4598/4661 with 62 PostgreSQL-lane environment failures and one POS flaky test that passes in isolation; WPF harness screenshots at 96 and 120 DPI showing the integrated USD section and full-width observations without overlap.

L4. Refinement: the USD block now only renders when the USD method had activity in the journey (`ExpectedAmountBsS != 0` and `PaymentMethodCurrency == USD`), and its count is mandatory in that case (empty blocks closure; zero remains a valid physical count). It was visually normalized as a method row: purple wallet icon, `Efectivo USD` / `Conteo físico obligatorio`, 220px input with `$` prefix, no `Opcional` badge. Evidence: Release build 0/0; DailyClosure 101/101; full suite 4600/4662 with only the 62 PostgreSQL-lane environment failures; WPF harness rendered the normalized block at 96/120 DPI.

Assumptions:
- Scope is the WPF daily closure; the web `RegisterClosePage` keeps its current contract for this change.
- "Used method" is the method with a non-zero expected amount in the closure window; zero-activity rows are hidden from declaration.
