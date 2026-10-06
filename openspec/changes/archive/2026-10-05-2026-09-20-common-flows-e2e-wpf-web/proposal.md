# Change: E2E Common Flows for Web POS and Desktop WPF

## Summary
Implement and standardize End-to-End (E2E) automated tests covering all shared business flows between the Web POS and Desktop WPF applications:
1. Authentication & Session Lifecycle (Login, validation, password rotation)
2. Complete POS Sales Flow (Product search, barcode, cart manipulation, customer selection, checkout preview & execution)
3. Cuentas Abiertas / On-Hold Orders (Hold sale, pending orders listing, edit order, checkout from pending, cancellation)
4. Retiros Pendientes / Pending Pickups (List pickups, search/filter, confirm delivery)
5. Cash Drawer / Caja Registradora (Register status, cash movement, shift balance)
6. Product Catalog & Inventory Lookup (Search catalog, stock visibility)

## Motivation
Both the Web client (React 19 + Vite) and the Desktop client (WPF .NET 10 + MaterialDesign) share the same underlying business domain, REST API, and core functional flows. To ensure absolute parity and prevent behavioral divergence, we require comprehensive automated UI test suites verifying each common flow from the perspective of an actual user on both platforms.

## Scope
- **Web Client:** Playwright test suites in `Web.Frontend/e2e/` (already 9 tests passing, now expanding to ensure parity across all 6 flows).
- **Desktop WPF:** FlaUI UIA3 test suites in `tests/CommandCenter.Wpf.E2ETests/` (expanding from 4 basic tests to a comprehensive suite covering all 6 common flows).
- **WPF Automation Identifiers:** Ensure WPF views (`PendingOrdersView.xaml`, `PendingPickupsView.xaml`, `CashDrawerView.xaml`, `InventoryView.xaml`, `PosView.xaml`) expose required `AutomationProperties.AutomationId` for reliable, non-flaky test execution.
