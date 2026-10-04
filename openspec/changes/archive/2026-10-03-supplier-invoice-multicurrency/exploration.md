# Exploration: Supplier Invoice Multi-Currency (Bs.S) & Hot Product Creation

Date: 2026-10-03 · Orchestrator read-only mapping (CodeGraph + explore agent); no code changes.

## Current state (evidence)

- Staging classification (`Inventory.Module/Services/SupplierInvoiceService.Matching.cs:133-139`): no resolved product → `Conflict`; `CostPriceUSD == 0` → `New`; normalized cost differs → `Update`; equal → `Unchanged`. UI badge for unmatched is `[CONFLICT]` (`Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel.Staging.cs:17`); zero-cost matched shows `[NEW]` (`.Staging.cs:53`).
- `SupplierProductCode` exists with unique filtered index `IX_SupplierProductCodes_Supplier_Code` (`(SupplierId, Code)` WHERE `Code IS NOT NULL`) but is only **read** by matching (`Matching.cs:57-74`). Nothing writes it (`SupplierInvoiceService.Apply.cs` never touches it; repo-wide grep).
- `Product.SKU` **is** the barcode (`Core/Entities/Product.cs:23`); unique filtered `IX_Products_SKU`; there is no separate barcode column. Create path: `InventoryService.ProductCrud.cs:17` → `CreateProductFromDtoAsync` (RBAC `EnsureCatalogMutationPermission`; SKU regex + duplicate check → `InvalidOperationException("Product with SKU {sku} already exists.")`; canonical `PriceBsS` from today's rate).
- No currency type in Core. POS convention is string codes (`PaymentMethodCurrencyResolver.Usd = "USD"`, `LocalCurrency = "Bs.S"`) plus a decimal rate snapshot (`Sale.AppliedRate`, rounded with `PricingCalculator.RoundExchangeRateCeiling`). `PricingCalculator.ToUSD(amountBsS, rate, decimals = 2)` uses `MidpointRounding.AwayFromZero`.
- Staging request carries only `UnitCostUSD` (`Core/DTOs/SupplierInvoiceDtos.cs:12-17`); persistence `SupplierInvoiceLine.UnitCostUSD numeric(18,2)`; `SupplierInvoice` has no currency/rate fields.
- WPF staging (`Desktop.Client/Views/SupplierInvoiceView.xaml`): header actions Lookup/CreateSupplier/SelectFile/Stage/Clear/Confirm; grid has badges, editable margins, approve checkbox; **no per-line action column**. Modal precedent: `IDialogService.ShowProductDialog` → `WpfDialogService.Modals.cs:122-138` → `ProductDialog` (`RequestClose` → `DialogResult`), headless-safe (`Application.Current == null`).
- Client file parsing is culture-tolerant (`Desktop.Client.Core/Services/SupplierInvoiceService.Parsing.cs`); the parsed cost is a document-currency amount.
- Tests: focused filter `FullyQualifiedName~SupplierInvoice`; suite baseline 1591/1591; integration Postgres-gated via `TEST_POSTGRES_CONNECTION` (silent early-return). RDD: **off** (clone-local). Next ANEXO: **8.146**.

## Implications

- Currency + applied rate must be new immutable snapshot fields on `SupplierInvoice`; normalization must be server-side (zero-trust); the document unit cost should be persisted for audit fidelity.
- Classification needs an engine change (unmatched → `New`), approval must gate on a resolved product, and alias **writing** must be added (not extended).
- Creation should be a single atomic server-side operation (product + alias + line resolution) reusing `IProductManagementService`; pricing/stock stay at confirm (single apply point).
- No new schema for barcodes (`SKU` already is the barcode); WPF needs a header selector/rate field, a per-line create action and a lightweight barcode-capture modal.
