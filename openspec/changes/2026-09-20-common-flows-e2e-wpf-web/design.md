# Architecture & Test Design: Common E2E Flows (Web & WPF)

## 1. Web Architecture (Playwright)
- **Framework:** `@playwright/test`
- **Network Strategy:** Route mocking via `page.route` (`Web.Frontend/e2e/helpers/mockApi.js`) ensuring zero backend/database dependencies and deterministic, resilient execution.
- **State Management:** Session and auth handled via `loginAsAdmin(page)` in `e2e/helpers/auth.js`.
- **Suites:**
  - `auth.spec.js`: Flow 1
  - `pos-sale.spec.js` & `sales-flow-complete.spec.js`: Flow 2 & Flow 3 & Flow 4
  - `cash-drawer.spec.js`: Flow 5
  - `catalog.spec.js` (NEW): Flow 6

## 2. Desktop WPF Architecture (FlaUI + UIA3)
- **Framework:** `FlaUI.UIA3` with `xUnit`
- **Execution Model:**
  - Sequential execution via `AssemblyInfo.cs` (`DisableTestParallelization = true`) to prevent desktop window focus contention.
  - Process launch via `WpfAppFixture` with `--e2e` flag bypassing single-instance mutex.
- **Automation IDs:**
  - `LoginView.xaml`: `Login_Username`, `Login_Password`, `Login_SubmitButton`, `Login_ErrorMessage`
  - `PosView.xaml`: `Pos_SearchInput`, `Pos_CheckoutButton`, `Pos_CartDataGrid`, `Pos_CustomerButton`
  - `PendingOrdersView.xaml`: `PendingOrders_SearchInput`, `PendingOrders_RefreshButton`, `PendingOrders_DataGrid`
  - `PendingPickupsView.xaml`: `PendingPickups_SearchInput`, `PendingPickups_RefreshButton`, `PendingPickups_List`
  - `CashDrawerView.xaml`: `CashDrawer_CashInButton`, `CashDrawer_CashOutButton`, `CashDrawer_RefreshButton`
  - `InventoryView.xaml`: `Inventory_SearchInput`, `Inventory_DataGrid`, `Inventory_RefreshButton`
  - `MainWindow.xaml`: `Nav_BtnPos`, `Nav_BtnInventory`, `Nav_BtnSalesHistory`, `Nav_BtnCashDrawer`, `Nav_BtnPendingOrders`, `Nav_BtnPendingPickups`
- **Suites:**
  - `LoginViewTests.cs`: Flow 1
  - `PosSaleTests.cs`: Flow 2
  - `PendingOrdersTests.cs`: Flow 3
  - `PendingPickupsTests.cs`: Flow 4
  - `CashDrawerTests.cs`: Flow 5
  - `InventoryTests.cs`: Flow 6

## 3. Automation ID Standard
To guarantee non-flaky FlaUI tests, all target controls must have `AutomationProperties.AutomationId` explicitly assigned in XAML following the `<ViewName>_<ControlName>` convention.
