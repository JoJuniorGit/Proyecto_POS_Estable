# Checkout Enterprise UI

Branch: V0.15. Delivery: local work-unit commit; no push. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --no-build`.

## Specs

S1. Translate all visible checkout copy to Spanish and keep the active monetary profile: `Finalizar Cobro`, `Total a Cobrar`, `Pagado`, `Restante`/`Vuelto a Entregar`, dynamic payment currency, `Pagos Aplicados`, and Spanish table/action labels.

S2. Enterprise shell: 640px white rounded modal, soft ambient shadow, dark backdrop, contextual customer/rate header, close action, and fixed footer.

S3. Three financial KPI cards with conditional states: neutral paid-at-zero, green paid-positive, violet remaining, green covered, and amber change.

S4. Segmented payment modality (`Contado`/`Crédito`), structured payment entry with method/amount/reference, and accessible custody row.

S5. Reordered payment table with method/reference/local/USD/actions, explicit edit/remove actions, double-click compatibility, and a clean empty state.

S6. Preserve all financial calculations, DTOs, commands, bindings, and credit/custody rules; only add presentation-only display helpers.

S7. Update the POS parent title/rate presentation (`PUNTO DE VENTA`, `Bs.S / USD`).

S8. Build with 0 warnings/0 errors, all tests green, and visually validate Contado, multipago, change, covered/custody, and Crédito at 96/125 DPI.

## Tasks

- [x] T1 (S1-S3) - ViewModel presentation helpers and header/KPI markup.
- [x] T2 (S4-S5) - Payment modality, entry block, custody, and payment table.
- [x] T3 (S6-S7) - Preserve behavior and update POS shell copy.
- [x] T4 (S8) - Build, tests, and WPF harness validation.

## Log

L1. User requested a Principal-level visual, structural, and language refactor of the WPF Checkout modal, with no changes to financial calculations or payment DTOs.

L2. Implemented display-only properties for customer context, rate context, payment change/covered state, and receipt reference; the existing payment/finalization logic remains untouched.

L3. Evidence: build 0 warnings/0 errors; full suite 4545/4545; harness screenshots at 96/125 DPI for Contado, multipago, change, covered/custody, and Crédito. No English visible labels, clipped fields, black table lines, or overly tall empty modal states.
