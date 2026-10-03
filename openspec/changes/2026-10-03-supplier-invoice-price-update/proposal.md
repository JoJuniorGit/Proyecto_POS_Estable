# Proposal: Automated Price & Profitability Update via Supplier Invoice

## Intent

Supplier price changes force manual edits to product cost and margin, risking drift and stale pricing. Automate the loop: ingest a supplier invoice → identify supplier → match lines → review → atomically apply. WPF desktop + backend only.

## Scope

### In Scope
- Supplier identity (RIF/NIT/commercial name) + persisted per-supplier column-mapping template.
- Parse `.xlsx` (ClosedXML), `.csv`, `.xml` (`System.Xml`) into DB draft `SupplierInvoice`/`SupplierInvoiceLine`.
- Line matching: barcode (`SKU`) > supplier code > pg_trgm fuzzy name (configurable threshold); deterministic tie-breaks.
- Suggested sale price from historical margin; per-row UI override; staging review, no auto-apply.
- One atomic confirm applying only approved lines: `CostPriceUSD`, both margins, stock increment + `StockMovement` audit.

### Out of Scope
- `Web.Frontend`, PDF/OCR, product creation from invoices, Bs.S invoice costing/snapshots.

## Capabilities

### New Capabilities
- `supplier-invoice-staging`: supplier identification, persisted mapping template, multi-format ingestion into draft invoice entities.
- `supplier-product-matching`: deterministic invoice-line→product matching with configurable similarity threshold.
- `supplier-invoice-apply`: atomic confirm applying approved costs/margins/stock plus audit.

### Modified Capabilities
- None

## Approach

Reuse the import stack. Entities in `Inventory.Module` (`InventoryDbContext`), single migration stream. Backend partial classes (`SupplierInvoice.Import.cs`, `.Matching.cs`, `.Mapping.cs`) mirror `InventoryService.Import.cs`: server-side re-validation (zero-trust), `CreateExecutionStrategy` + transaction, `StockMovement` audit, `xmin` concurrency retry. Client `SupplierInvoiceService*.cs` mirrors `ProductImportService*`; `SupplierInvoiceViewModel*` mirrors `ImportProductsViewModel` plus instant recalc from `ProductDialogViewModel.Pricing.cs`. New `SupplierInvoicesController` gated `[Authorize(Roles="Admin,Manager")]`; VM-swap nav wired in `App.xaml.cs` / `MainWindow.xaml` / `MainViewModel.cs`.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Core/Entities/Supplier*.cs`, `SupplierInvoice*.cs` | New | Supplier + draft invoice domain entities |
| `Core/DTOs/Supplier*` | New | DTO boundary types |
| `Inventory.Module/Data/InventoryDbContext.cs` | Modified | DbSets, indexes, `xmin` config |
| `Inventory.Module/Services/SupplierInvoice*.cs` | New | Matching + atomic apply service |
| `Inventory.Module/Migrations/` | New | Additive supplier schema migration |
| `Backend.API/Controllers/SupplierInvoicesController.cs` | New | RBAC import/confirm endpoints |
| `Desktop.Client.Core/Services/SupplierInvoiceService*.cs` | New | File parsing + API client |
| `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel*.cs` | New | Staging grid, override, recalc |
| `Desktop.Client/Views/SupplierInvoiceView.xaml` | New | WPF view |
| `Desktop.Client/App.xaml.cs`, `MainWindow.xaml` | Modified | DI + navigation wiring |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Dual cost/margin divergence | Med | Single write path; update both margins together, legacy `Cost` left intact |
| Fuzzy-match false positives corrupting cost/margin | Med | Configurable threshold + mandatory review; exact codes always win |
| Transaction atomicity / `xmin` conflicts | Med | `CreateExecutionStrategy` + retry; all-or-nothing confirm |
| Feature exceeds 400-line review budget | High | Chained PRs by work unit (entities → matching → apply → UI) |

## Rollback Plan

- Confirm is all-or-nothing: a failed confirm rolls back entirely, leaving no partial cost/margin/stock writes.
- Revert the additive migration to drop supplier tables; product data changes are data, reversible by reapplying the prior values stored per invoice line.
- Disable the feature by reverting nav/DI registration and the endpoint.

## Dependencies

- `pg_trgm` (already installed), ClosedXML (present).

## Success Criteria

- [ ] Load xlsx/csv/xml, map columns once, review staged lines.
- [ ] Matching priority + deterministic tie-breaks verified.
- [ ] Confirm applies approved rows atomically; unmatched/rejected rows untouched.
- [ ] `dotnet build CommandCenter.slnx --configuration Release` 0/0; `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` green (Inventory ≥0.72).
