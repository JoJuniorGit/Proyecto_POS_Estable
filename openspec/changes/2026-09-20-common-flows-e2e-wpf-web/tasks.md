# Tasks: Full System E2E Flows (Common & WPF-Independent) & Installer Build

## Phase 1: WPF Automation Identifiers Alignment
- [x] **1.1** Add `AutomationProperties.AutomationId` to `MainWindow.xaml` for all 11 navigation buttons (`Nav_BtnPos`, `Nav_BtnInventory`, `Nav_BtnImportProducts`, `Nav_BtnSalesHistory`, `Nav_BtnCashDrawer`, `Nav_BtnPendingOrders`, `Nav_BtnPendingPickups`, `Nav_BtnDailyClosure`, `Nav_BtnUsersManagement`, `Nav_BtnSettings`, `Nav_BtnExchangeRate`).
- [x] **1.2** Add `AutomationProperties.AutomationId` to `PendingOrdersView.xaml` (`PendingOrders_SearchInput`, `PendingOrders_RefreshButton`, `PendingOrders_DataGrid`).
- [x] **1.3** Add `AutomationProperties.AutomationId` to `PendingPickupsView.xaml` (`PendingPickups_SearchInput`, `PendingPickups_RefreshButton`, `PendingPickups_List`).
- [x] **1.4** Add `AutomationProperties.AutomationId` to `CashDrawerView.xaml` (`CashDrawer_CashInButton`, `CashDrawer_CashOutButton`, `CashDrawer_RefreshButton`).
- [x] **1.5** Add `AutomationProperties.AutomationId` to `InventoryView.xaml` (`Inventory_SearchInput`, `Inventory_RefreshButton`, `Inventory_DataGrid`).
- [x] **1.6** Add `AutomationProperties.AutomationId` to `SalesHistoryView.xaml` (`SalesHistory_SearchInput`, `SalesHistory_RefreshButton`).
- [x] **1.7** Add `AutomationProperties.AutomationId` to `DailyClosureView.xaml` (`DailyClosure_Title`).
- [x] **1.8** Add `AutomationProperties.AutomationId` to `ExchangeRateView.xaml` (`ExchangeRate_Title`, `ExchangeRate_SyncButton`).
- [x] **1.9** Add `AutomationProperties.AutomationId` to `ImportProductsView.xaml` (`ImportProducts_Title`).
- [x] **1.10** Add `AutomationProperties.AutomationId` to `UsersManagementView.xaml` (`UsersManagement_Title`).
- [x] **1.11** Add `AutomationProperties.AutomationId` to `SettingsView.xaml` (`Settings_Title`).

## Phase 2: WPF E2E Test Suite Expansion (FlaUI)
- [x] **2.1** Update `tests/CommandCenter.Wpf.E2ETests/Tests/PosSaleTests.cs` (POS Sales flow).
- [x] **2.2** Create `tests/CommandCenter.Wpf.E2ETests/Tests/PendingOrdersTests.cs` (Cuentas Abiertas flow).
- [x] **2.3** Create `tests/CommandCenter.Wpf.E2ETests/Tests/PendingPickupsTests.cs` (Retiros Pendientes flow).
- [x] **2.4** Update `tests/CommandCenter.Wpf.E2ETests/Tests/CashDrawerTests.cs` (Caja Registradora flow).
- [x] **2.5** Create `tests/CommandCenter.Wpf.E2ETests/Tests/InventoryTests.cs` (Catálogo e Inventario flow).
- [x] **2.6** Create `tests/CommandCenter.Wpf.E2ETests/Tests/SalesHistoryTests.cs` (Historial de Ventas flow).
- [x] **2.7** Create `tests/CommandCenter.Wpf.E2ETests/Tests/DailyClosureTests.cs` (Cierre Diario de Caja flow).
- [x] **2.8** Create `tests/CommandCenter.Wpf.E2ETests/Tests/WpfIndependentFlowsTests.cs` (Tasa de Cambio, Importación Excel, Gestión de Usuarios, Configuración del Sistema).

## Phase 3: Web E2E Parity (Playwright)
- [x] **3.1** Create `Web.Frontend/e2e/catalog.spec.js` covering Catalog & Inventory in Web.
- [x] **3.2** Run all Web Playwright tests (`npm run test:e2e`).

## Phase 4: Test Verification
- [x] **4.1** Build WPF client in Release mode (`dotnet build Desktop.Client/Desktop.Client.csproj -c Release`).
- [x] **4.2** Run all WPF FlaUI tests (`dotnet test tests/CommandCenter.Wpf.E2ETests/CommandCenter.Wpf.E2ETests.csproj -c Release`).
- [x] **4.3** Verify unit test suites (`npm test` and `dotnet test`).

## Phase 5: Compile Binaries for Installer
- [x] **5.1** Publish `Backend.API` to `publish\BackendAPI`.
- [x] **5.2** Publish `Desktop.Client` to `publish\DesktopClient`.
- [x] **5.3** Publish `UpdaterService` to `publish\UpdaterService`.
- [x] **5.4** Verify all release binaries, wwwroot bundle, and installer files.
