# WPF Physical Stock Count + Navigation Categories

Objective: Add the physical stock count flow to the local WPF client and reorganize the sidebar into role-aware categories.
Branch: V0.15
Delivery strategy: work-unit commits on V0.15 (no PR requested)
Test runner: `dotnet build CommandCenter.slnx -c Release` and `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`

## Specs

S1. WPF must expose a physical stock count view for Admin, Manager and Cashier. Driver must not see it or navigate to it. The backend contract is `api/stock-counts` (class-level Admin/Manager/Cashier; create/review/close/apply/cancel only Admin/Manager).

S2. The view must list sessions with a status filter (`Todas`, `Open`, `Closed`, `Applied`, `Cancelled`) and allow Admin/Manager to create a session with:
- required name (max 80 chars),
- optional category scope (null = all inventory),
- optional notes (max 300 chars).

S3. The session detail is blind for Cashier: SKU, product name, counted quantity and last update only. Theoretical stock, unit cost and difference must never be requested or shown for Cashier.

S4. In an Open session, an allowed user can scan by SKU/barcode, increment/decrement the counted quantity, set an absolute quantity and remove an item. The view must keep focus/feedback usable after each scan.

S5. Admin/Manager can load the review/difference panel: theoretical stock, unit cost, difference, difference value and session totals (surplus, shortage, net, unscanned in scope). The panel must mark live theoretical values when the session is Open.

S6. Admin/Manager can close an Open session, apply a Closed session and cancel an Open/Closed session. Apply is irreversible and must be confirmed; it updates stock and records Kardex movements on the server.

S7. The WPF sidebar must be grouped into categories:
- `PRINCIPAL`: Point of Sale, Register, Sales History.
- `OPERACIONES`: Cuentas Abiertas, Retiros Pendientes, Cuentas por Cobrar, Cierre de Caja.
- `INVENTARIO`: Catalog, Conteo Físico, Kardex / Auditoría, Import Products, Facturas de Proveedores.
- `ADMINISTRACIÓN`: Gestión Usuarios, Settings, Exchange Rate.

S8. Role visibility and navigation guards must mirror the server/web matrix:
- Admin: all views.
- Manager: all views except Gestión Usuarios.
- Cashier: POS, Register, Sales History, Cuentas Abiertas, Retiros Pendientes, CxC, Cierre, Catalog, Conteo Físico.
- Driver: only Sales History and Retiros Pendientes.
- Hidden items must not be reachable through the navigation commands either.
- When a Driver logs in or restores a session, the initial view must be an allowed one (Retiros Pendientes), never POS.

S9. Add unit tests for the navigation policy, stock count viewmodel permissions and the main stock count commands. Keep `dotnet build` at 0 warnings/0 errors and the full `dotnet test` suite green.

S10. Add stable automation IDs for the new surface: `Nav_BtnStockCount`, `StockCount_Title`, `StockCount_CreateButton`, `StockCount_StatusFilter`, `StockCount_SessionGrid`, `StockCount_ScanInput`, `StockCount_ScanButton`, `StockCount_ItemsGrid`, `StockCount_ReviewButton`, `StockCount_CloseButton`, `StockCount_ApplyButton`, `StockCount_CancelButton`.

## Tasks

T1. Add `WpfNavigationPolicy` (view/category matrix), role converters and `UserSession.IsDriver`/`IsNotDriver`; add policy tests. Specs: S7, S8, S9. Route: inline. Status: completed.
T2. Add `IStockCountClientService` + `StockCountClientService` and DI registration. Specs: S1-S6. Route: inline. Status: completed.
T3. Add `StockCountViewModel`, row viewmodels, commands and permission gates; add viewmodel tests. Specs: S1-S6, S9. Route: inline. Status: completed.
T4. Add `StockCountView.xaml` + code-behind, DataTemplate and navigation item. Specs: S1-S6, S10. Route: inline. Status: completed.
T5. Reorganize `MainWindow.xaml` sidebar by category with role-aware visibility; harden `MainViewModel` navigation commands and initial fallback view. Specs: S7, S8. Route: inline. Status: completed.
T6. Build, run the full test suite, add ANEXO LI and commit work units. Specs: S9. Route: inline. Status: in progress (build and suites green; commit/ANEXO pending).

## Log

L1. User request (verbatim):
"Agrega el conteo fisico para la version local WPF, y organiza por categoria cada seccion por ejemplo principal para los secciones mas usadas, y cuida las vistas y los permisos de cada rol"

L2. Evidence (2026-11-08):
- `dotnet build CommandCenter.slnx -c Release`: 0 warnings, 0 errors.
- `WpfNavigationPolicyTests` + `StockCountViewModelTests`: 47/47.
- Full `CommandCenter.Tests` with PostgreSQL + E2E: 4182/4182.
- Full WPF E2E suite with `E2E_FIND_TIMEOUT_SECONDS=45`: 9/9.
- New `FullStackStockCountTests` (create session + open + Admin review + scan): 1/1.
