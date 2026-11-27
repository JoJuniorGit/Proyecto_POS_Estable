# Pending Orders Enterprise UI

Branch: V0.15. Delivery: local work-unit commit; no push. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --no-build`.

## Specs

S1. Unified Enterprise header with clock badge, title/subtitle, Refresh and menu buttons at 38px.

S2. White toolbar card with a 380–460px search, clear action, compact loaded/total chips, and a properly rounded Dual-only BCV pill in `Bs.S / USD`.

S3. Remove the `ESTADO` column and render exactly four columns: `PEDIDO / FECHA`, `CLIENTE`, `TOTAL FACTURA`, and `ACCIONES`, filling the card width.

S4. Use `Cobrar` as the only one-click action and a left-click overflow menu with `Editar pedido` and `Anular pedido`; preserve existing commands and add the existing pending-cancel endpoint through the client service.

S5. Keep row states white/hover/selected, a thin scrollbar, recycling virtualization, integrated footer, and strictly gated empty states.

S6. No financial, checkout, edit, API contract, or calculation changes; cancellation requires explicit confirmation with order number, customer, and amount.

S7. Build 0/0, run PendingOrders tests and the full suite, and visually validate populated/empty/search states at 96/125 DPI.

## Tasks

- [x] T1 (S1-S2) - Header, toolbar, search, chips, and BCV pill.
- [x] T2 (S3-S5) - Four-column grid, row/card/footer/empty-state styling.
- [x] T3 (S4) - Overflow menu, safe ContextMenu binding, cancel confirmation, client endpoint wrapper.
- [x] T4 (S7) - Tests, build, and WPF harness verification.

## Log

L1. User requested an Enterprise visual upgrade of Cuentas Abiertas, removal of the Estado column, a four-column full-width table, a corrected BCV badge, and a Cobrar plus overflow-menu action model.

L2. Implemented display-only list summaries and empty-state presentation, safe `PlacementTarget` bindings for the overflow menu, an explicit cancel confirmation, and the client wrapper for `POST /api/sales/{id}/cancel`.

L3. Evidence: build 0 warnings/0 errors; PendingOrders tests 13/13; full suite 4576/4576; harness screenshots at 96/125 DPI for populated, empty, and search-empty states; action column fills the available width and the BCV pill uses clean rounded geometry.
