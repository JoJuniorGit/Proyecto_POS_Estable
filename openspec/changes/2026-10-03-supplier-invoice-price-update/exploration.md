# Exploration — Automated Supplier Invoice → Price & Profitability Update

Change: `2026-10-03-supplier-invoice-price-update`
Store: openspec
Scope constraint: **WPF desktop only** (`Desktop.Client` + `Desktop.Client.Core`); backend (`Core`, modules, `Backend.API`) in scope. **No web/React work.**

## Current State

### Supplier domain
- **No supplier concept exists.** A repo-wide search for `Supplier|Proveedor` returns zero files. `Core/Entities/` has Product, Customer, Order, StockMovement, StockReservation, ExchangeRateHistory, SystemSetting, User — no Supplier.
- There is no supplier-invoice, no product supplier-code, and no per-supplier column mapping anywhere. This is greenfield.

### Product / cost / margin model
`Core/Entities/Product.cs` carries the money and margin fields:
- Costs: `CostPriceUSD` (current) and `Cost` (legacy duplicate) — persisted, `numeric(18,2)`.
- Margins: **two** persisted margins, `ProfitMarginRetail` and `ProfitMarginWholesale` (`numeric(18,2)`), plus legacy `ProfitPercentage`.
- Prices: `PriceUSD`, `PriceRetailUSD`, `PriceWholesaleUSD` (`numeric(18,2)`); `PriceBsS` + `LastConversionRate` (tagged price snapshot).
- Identity: `SKU` (docstring says "// Barcode"), unique filtered index `IX_Products_SKU WHERE IsDeleted = false`; client exposes a barcode/SKU lookup at `api/products/quick-check/{sku}`.
- Stock: `StockQuantity` (`numeric(18,3)`), `LowStockThreshold`, `ReservedQuantity`.
- Soft delete: `IsDeleted`; concurrency: `xmin` mapped as `IsConcurrencyToken()` in `Inventory.Module/Data/InventoryDbContext.cs`.

Pricing is cost+margin, rounded **up** to 2 decimals: `Core/Helpers/PricingCalculator.cs` (`RoundPriceUp`, `ToBsSCeiling`). Basis: `PricingHelper.CalculatePriceUSD = cost * (1 + margin/100)`. Bs.S pricing is a display conversion via the live BCV rate; USD is the reference currency. Money is `decimal` only and sales history is snapshot-immutable (not touched by this feature).

### Existing product-import precedent (reusable patterns)
An end-to-end Excel/CSV product import already exists and mirrors most of what this feature needs:
- Client parsing: `Desktop.Client.Core/Services/ProductImportService.cs` + `ProductImportService.Parsing.cs` — **ClosedXML** for `.xlsx`, hand-rolled parser for `.csv` (delimiter `;` or `,`). APIs: `ReadHeadersAsync`, `ParseFileWithMappingAsync(file, mapping, rate)`, `CommitImportAsync`, `ExportProductsToFileAsync`.
- Column-mapping UI + VM: `Desktop.Client.Core/ViewModels/ImportProductsViewModel.cs` — `ColumnMappingItem`, `MapHeaderToProperty`, in-memory staging via `ObservableCollection<ProductImportDto>`, per-row `IsValid`/`ErrorMessage`, `IsBusy`/progress, `IDialogService`, `IFilePickerDialog`, RBAC gate `UserSession.CanMutateCatalog`.
- Backend commit: `Backend.API/Controllers/ProductsController.ImportExport.cs` — `POST api/products/bulk-import` with `[Authorize(Roles = "Admin,Manager")]` + `CanMutateCatalog`; `GET export`, `GET export-template`.
- Transactional apply: `Inventory.Module/Services/InventoryService.Import.cs` — **server-side re-validation** `IsImportable` (never trusts `dto.IsValid`), `MaxImportBatchSize = 5000`, `CreateExecutionStrategy().ExecuteAsync(...)` wrapping `BeginTransactionAsync` → `SaveChangesAsync` → `CommitAsync` / `RollbackAsync`, and `StockMovement` rows with reason `Importación masiva IMP-yyyyMMdd-HHmmss`, `UserId` from `ICurrentUserService`.
- DTOs: `Core/DTOs/ProductImportDto.cs`, `Core/DTOs/BulkImportRequestDto.cs`.
- Tests: `CommandCenter.Tests/ProductImportExportTests.cs`, `CommandCenter.Tests/Unit/ProductImportStockAuditTests.cs`, `CommandCenter.Tests/Unit/ProductVariantsTests*.cs`, `CommandCenter.Tests/Unit/SecurityTests.cs`.

### WPF client structure
- `Desktop.Client.Core` (`net10.0-windows`, `UseWPF=false`) holds ViewModels/Services/Messages/Helpers; `Desktop.Client` holds Views, DI (`App.xaml.cs`), and `MainWindow.xaml`.
- Navigation is a **ViewModel swap**: `MainWindow.xaml` maps `DataTemplate` per VM (e.g. `<DataTemplate DataType="{x:Type vm:ImportProductsViewModel}"><views:ImportProductsView/>`), and `MainViewModel.NavigateToImportProducts()` (line ~264) sets `CurrentViewModel`. DI: `App.xaml.cs` registers `IProductImportService` (typed HttpClient, line ~271) and `ImportProductsViewModel` singleton (line ~278).
- Margin editing + instant recalculation already proven client-side: `Desktop.Client.Core/ViewModels/ProductDialogViewModel.Pricing.cs` — `CalculatePricing(trigger)` with an `_isUpdatingPrices` re-entrancy guard, `Math.Ceiling(rawPrice * 100m) / 100m`.
- Grid/hotkey/memory standards: `wpf-performance-and-ui` skill (BindingProxy for DataGrid columns, virtualization/recycling, `IDialogService` instead of `MessageBox.Show`, `WeakReferenceMessenger`, `Dispatcher.BeginInvoke`).

### Backend / persistence structure
- Modules: `Core`, `Sales.Module`, `Inventory.Module`, `Logistics.Module`, `Backend.API`, `UpdaterService`; WPF `Desktop.Client`(+`.Core`); React `Web.Frontend` (out of scope).
- `Product` + `StockMovement` live in `InventoryDbContext` (`Inventory.Module/Data/InventoryDbContext.cs`); migrations in `Inventory.Module/Migrations/` generated via `dotnet ef migrations add` (`--project Inventory.Module --startup-project Backend.API`).
- Stock mutation API: `IInventoryService.UpdateStockAsync` / `UpdateStockBatchAsync` / `AdjustStockAsync`; audit via `StockMovement` (+ archive table).
- RBAC: `UserRole { Admin, Manager, Cashier, Driver }`; `ICurrentUserService.CanMutateCatalog`; endpoint attribute `[Authorize(Roles = "Admin,Manager")]`.
- `pg_trgm` extension is already installed (migration `20260224182348_AddPgTrgm.cs`) — usable for name-similarity matching (`EF.Functions.ILike`, trigram).

### File-parsing feasibility
- Native: `.xlsx` via ClosedXML (present in `Inventory.Module`, `Desktop.Client.Core`, `Desktop.Client`); `.csv` via hand-rolled split. No CsvHelper/EPPlus.
- Backend has `HtmlAgilityPack` (BCV scraping) only. No PDF library (no iText/PdfSharp).
- `Desktop.Client` targets `TargetPlatformVersion 10.0.19041.0` and the csproj comment confirms WinRT projections (`Windows.Media.Ocr`, `Windows.Media.Capture`) are available — so OCR is *technically* reachable but there is no image/PDF rasterizer dependency.
- Realistic: **Excel + CSV** out of the box; **XML** via `System.Xml`; **PDF/OCR** would need a new dependency or WinRT OCR over rasterized pages (high effort, low accuracy).

### Staging persistence precedent
- Held orders are persisted in DB as `Sale` rows with `SaleStatus.OnHold` (`Sales.Module/Services/SalesService.HoldOrders.cs`) — a durable "pending review" pattern.
- Ad-hoc in-memory client state exists (`ClientStateService`, `SaleRecoveryStore`) but is not a reviewable/auditable staging model.

## Affected Areas

**New (likely)**
- `Core/Entities/Supplier.cs` — supplier identity (RIF/NIT, commercial name, matching keys).
- `Core/Entities/SupplierColumnMapping.cs` (or `SupplierImportTemplate`) — persisted per-supplier column→field template.
- `Core/Entities/SupplierInvoice.cs` / `Core/Entities/SupplierInvoiceLine.cs` — staging header + lines with status, old/new cost, margin override, approval toggle.
- `Core/DTOs/SupplierInvoice*.cs`, `Core/DTOs/SupplierMapping*.cs` — DTO boundary types.
- `Inventory.Module/Services/SupplierInvoice.Import.cs` / `SupplierMatching.cs` / `SupplierMapping.cs` (partial classes) — normalization, matching, transactional apply.
- `Desktop.Client.Core/Services/SupplierInvoiceService*.cs` — parsing + API client (mirrors `ProductImportService*`).
- `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel*.cs` — staging grid, badge statuses, instant margin recalc, approval toggles.
- `Desktop.Client/Views/SupplierInvoiceView.xaml` (+ `.xaml.cs`).
- New EF migration in `Inventory.Module/Migrations/`.

**Modified**
- `Inventory.Module/Data/InventoryDbContext.cs` — new DbSets + configuration/indexes/constraints.
- `Core/Interfaces/IInventoryService.cs` (or a new `ISupplierInvoiceService`) — service contract.
- `Backend.API/Controllers/` — new `SupplierInvoicesController` (or `ProductsController.SupplierInvoice.cs` partial), `[Authorize(Roles="Admin,Manager")]`.
- `Desktop.Client/App.xaml.cs` — DI registration; `Desktop.Client/MainWindow.xaml` — `DataTemplate` + nav button; `Desktop.Client.Core/ViewModels/MainViewModel.cs` — navigation entry.
- `Inventory.Module/Services/InventoryService.Import.cs` or new service — reuse transaction/StockMovement patterns.

**Tests**
- `CommandCenter.Tests/ProductImportExportTests.cs` style + `Unit/` — new supplier-invoice suites; coverage gates Core ≥0.70, Sales ≥0.80, Inventory ≥0.72; `MigratedSchema` smoke; warnings-as-errors build.

## Approaches

1. **Reuse import stack; add supplier-invoice as an Inventory.Module partial + InventoryDbContext entities** — new entities in `InventoryDbContext`, backend service as partial classes of the inventory service, client VM/view cloned from the import precedent.
   - Pros: maximal reuse of the proven transactional import + `StockMovement` + column-mapping patterns; single migration stream; lowest new wiring.
   - Cons: bloats `Inventory.Module`; supplier concerns are arguably a separate bounded context.
   - Effort: Medium.

2. **New `Suppliers.Module` bounded context** — dedicated project, its own `SupplierDbContext`, service, migrations.
   - Pros: clean separation; honors domain boundaries.
   - Cons: new project + DbContext + second migration stream + DI + solution wiring; must still atomically touch Product/StockMovement across contexts (cross-context transaction complexity).
   - Effort: High.

3. **Client-only staging, backend only applies** — desktop keeps the whole review in memory; backend exposes a single "apply approved lines" endpoint.
   - Pros: simplest schema (no draft invoice tables); matches the existing import UX exactly.
   - Cons: lost review on crash; no audit of the pre-approval state; large payloads; harder to resume/audit.
   - Effort: Low–Medium.

## Recommendation

Adopt **Approach 1** as the default: reuse the import precedent end-to-end (ClosedXML/CSV parsing, column-mapping UI, DTO boundary, server-side re-validation, `CreateExecutionStrategy` + transaction + `StockMovement`). Stage the review as **draft invoice entities in InventoryDbContext**, because the confirm step must be atomic across cost + margin + stock and the review must be auditable (a durable draft is strictly safer than in-memory). Keep all computation authoritative on the backend at confirm time (zero-trust), while the WPF grid recalculates the New Sale Price instantly client-side on margin edit (reusing the `ProductDialogViewModel.Pricing` pattern) with no per-keystroke round-trip. Defer a dedicated `Suppliers.Module` unless the maintainer explicitly wants the bounded context now.

## Risks

- **Two persisted margins** (`ProfitMarginRetail` + `ProfitMarginWholesale`) vs the brief's singular "historical margin %": ambiguous which margin the invoice updates.
- **Two cost fields** (`CostPriceUSD` and legacy `Cost`) — must not diverge.
- **Currency of invoice cost** is unspecified: USD reference vs Bs.S with a rate snapshot; snapshot immutability rules apply.
- **`[NEW]` badge semantics** imply product creation from invoice lines — this materially expands scope (SKU generation, validation, dedup like AUD-23).
- **Name-similarity matching** needs a defined algorithm + threshold (pg_trgm available) and deterministic tie-breaks; false matches corrupt cost/margin.
- **Stock increments** must create `StockMovement` audit rows inside the same transaction and respect `xmin` optimistic concurrency retry.
- **Large feature vs 400-line review budget** — likely needs chained/stacked PRs and partial-class splitting to stay within 300–500-line files.
- **File formats**: PDF/OCR feasibility is unproven and may require a new dependency and licensing review.
- **RBAC**: must reuse `Admin/Manager` gating; `Cashier`/`Driver` must be blocked from cost/margin mutation.

## Ready for Proposal

**Yes — conditioned on user confirmation of the Open Decisions below.** The codebase evidence is sufficient; the only blockers are product/scope decisions, not missing code.

## Open Decisions

1. **Staging persistence.** Options: (a) draft `SupplierInvoice`/`SupplierInvoiceLine` rows in DB; (b) in-memory desktop state only. Consequences: (a) survives crashes, is auditable, supports atomic apply; (b) simpler schema but loses review and audit. **Recommended default: (a) DB draft staging.**

2. **Entity/module placement.** Options: (a) add Supplier entities to `Inventory.Module`/`InventoryDbContext`; (b) create a new `Suppliers.Module` with its own context. Consequences: (a) fastest, but mixes concerns; (b) cleaner boundary but cross-context atomicity is harder. **Recommended default: (a) Inventory.Module now, modularize later if it grows.**

3. **File formats to support.** Options: (a) `.xlsx` + `.csv` only; (b) also `.xml`; (c) also `.pdf`/OCR. Consequences: (a) ships now with existing deps; (b) cheap (`System.Xml`); (c) new dependency/licensing + accuracy risk. **Recommended default: (a) xlsx + csv, XML optional; PDF/OCR explicitly out of v1.**

4. **Margin model for updates.** Options: (a) update both `ProfitMarginRetail` and `ProfitMarginWholesale` together; (b) update retail only; (c) add a single new canonical margin field. Consequences: (a) preserves current dual-margin model; (c) changes the pricing model and touches pricing everywhere. **Recommended default: (a) retail is primary and drives wholesale unless an independent wholesale margin exists.**

5. **Margin override persistence.** Options: (a) store override per invoice line for audit AND apply to Product defaults on confirm; (b) apply to Product defaults only. Consequences: (a) full traceability of who/why a margin changed; (b) loses rationale. **Recommended default: (a) per-line override + Product update in one transaction.**

6. **Cost field + currency.** Options: (a) treat `CostPriceUSD` as authoritative, invoice cost assumed USD; (b) support invoice cost in Bs.S converted at a snapshot rate. Consequences: (b) requires rate snapshot columns and rounding rules. **Recommended default: (a) USD authoritative; add Bs.S only if real invoices demand it.**

7. **`[NEW]` product behavior.** Options: (a) only match existing products; unmatched lines are informational/conflict, no creation; (b) create new products from invoice lines. Consequences: (b) needs SKU generation, dedup (AUD-23 pattern), validation, and broadens scope substantially. **Recommended default: (a) match-only for v1; creation deferred.**

8. **Matching priority + similarity.** Options: barcode > supplier code > name. Implement name similarity with pg_trgm (`ILIKE`/`similarity()`) with a configurable threshold; ties resolved deterministically (exact code first, then highest similarity, then lowest product Id). **Recommended default: accept the stated priority with a similarity threshold exposed as a system setting.**

9. **Apply API shape.** Options: (a) one `POST /api/supplier-invoices/{id}/confirm` applying only approved lines; (b) per-row apply calls. Consequences: (b) breaks atomicity. **Recommended default: (a) single atomic confirm endpoint.**

10. **Web scope.** The brief's "web component/DOM" phrasing is superseded — confirm the change is WPF-desktop-only and backend-only (`Web.Frontend` untouched). **Recommended default: confirmed WPF-only per parent instruction.**
