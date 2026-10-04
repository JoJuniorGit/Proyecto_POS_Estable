# Proposal: Supplier Invoice Multi-Currency (Bs.S) & Hot Product Creation

## Intent

Suppliers issue invoices in Bs.S at a document-specific rate. Today the module assumes USD costs and cannot create catalog products from an invoice, so local-currency invoices force manual product/cost work and lose supplier code recognition. Add a per-document currency + applied-rate snapshot, normalize document costs to the USD base server-side, and enable creating catalog products from staged `[NEW]` lines with a mandatory real EAN/UPC barcode and automatic supplier-code alias learning.

## Scope

### In Scope
- Document currency (`USD`/`Bs.S`) + required applied rate for `Bs.S`, persisted as an immutable snapshot per invoice; `USD` snapshots rate `1`.
- Server-side (zero-trust) normalization of document unit costs to USD base cost (`PricingCalculator.ToUSD`), used by classification, margins and suggested prices; document cost persisted alongside.
- Staging semantics rework: unmatched → `[NEW]` (creation candidate, not approvable until resolved); zero-cost matched → `[UPDATE]` (`0.00 → new cost`); approval requires a resolved product; `Conflict` becomes legacy.
- Hot product creation from a staged line: atomic server operation (product identity + supplier-code alias + line resolution) with mandatory EAN/UPC capture (no inheritance of invoice codes); WPF modal + per-line action.
- `SupplierProductCode` alias upsert on creation and on confirm for approved lines carrying a supplier code.

### Out of Scope
- `Web.Frontend`; OCR/image ingestion (queued separate change); applying cost/margins/prices/stock at creation time (confirm remains the single apply point); currencies other than `USD`/`Bs.S`.

## Capabilities

### New Capabilities
- `supplier-invoice-multicurrency`: document currency + applied-rate snapshot + USD cost normalization.
- `supplier-product-hot-creation`: create-product-from-line (mandatory universal barcode) + supplier-code alias persistence.

### Modified Capabilities
- `supplier-invoice-staging`: classification rule + approval gating.
- `supplier-invoice-apply`: supplier-code alias learning inside the confirm transaction.

## Approach

Extend the existing staging/apply stack (8.144). New fields on `SupplierInvoice` (`Currency`, `AppliedRate`) and `SupplierInvoiceLine` (`UnitCostDocument`) in one additive migration. `StageAsync` validates currency/rate, normalizes costs server-side and classifies with the new rule. A new partial `SupplierInvoiceService.Creation.cs` reuses `IProductManagementService.CreateProductFromDtoAsync` inside a transaction to create the product, upsert the `SupplierProductCode` alias and re-resolve/reclassify the line; `Apply.cs` upserts aliases for applied lines. The WPF client adds header currency/rate input (prefilled from `IExchangeRateService`), a per-row "Crear Producto" action, and a lightweight modal (`IDialogService` extension) enforcing digit-only EAN/UPC capture.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Core/Entities/SupplierInvoice*.cs` | Modified | `Currency`, `AppliedRate`, `UnitCostDocument` |
| `Core/DTOs/SupplierInvoiceDtos.cs` | Modified | Request/response currency, rate, document cost; creation DTO |
| `Core/Interfaces/ISupplierInvoiceService.cs` | Modified | `CreateProductFromLineAsync` contract |
| `Inventory.Module/Data/InventoryDbContext.cs` | Modified | Column config for new fields |
| `Inventory.Module/Migrations/` | New | Additive `AddSupplierInvoiceMulticurrency` + backfill |
| `Inventory.Module/Services/SupplierInvoiceService.{Staging,Matching,Apply}.cs` | Modified | Normalization, classification, alias learning |
| `Inventory.Module/Services/SupplierInvoiceService.Creation.cs` | New | Atomic create-product-from-line |
| `Backend.API/Controllers/SupplierInvoicesController.cs` | Modified | `POST /{id}/lines/{lineId}/create-product` |
| `Desktop.Client.Core/Services/SupplierInvoiceService*.cs` + `ISupplierInvoiceService.cs` | Modified | Document cost parse + creation call |
| `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel*.cs` + `CreateInvoiceProductDialogViewModel` | Modified/New | Header selector/rate, create action, modal VM |
| `Desktop.Client/Views/SupplierInvoiceView.xaml`, `CreateInvoiceProductDialog.xaml`, `WpfDialogService` | Modified/New | Staging UI + modal |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Wrong applied rate distorts normalized cost (and margins/prices) | Med | Rate is an explicit required input, snapshotted per document, visible in staging review; confirm is the only apply point |
| Duplicate/invalid barcode breaks product creation | Med | Server validates 8–14 digits + SKU uniqueness; modal validates before submit; `ProblemDetails` surfaced inline |
| Alias remap (existing supplier code → different product) | Low | Last-write-wins toward the human-confirmed product; documented; deterministic matching precedence (barcode > code > fuzzy) |
| Partial write between product creation, alias and line resolution | Med | Single transaction + execution strategy; rollback leaves nothing |
| Feature exceeds 400-line review budget | High | Chained work units (domain → staging → creation → UI) |

## Rollback Plan

- Additive migration only (three columns with defaults + backfill); down-migration drops them.
- Feature is dark until the UI/endpoint is used: revert the endpoint + modal + header to disable.
- Cost/margin/stock still apply only at confirm; created products are ordinary catalog rows (deactivable via existing catalog tooling).

## Dependencies

- None new. Reuses `PricingCalculator`, `IExchangeRateService` (client), `IProductManagementService`, existing `SupplierProductCode` table.

## Success Criteria

- [ ] Bs.S invoice staged with applied rate; document costs persisted; `UnitCostUSD` normalized (AwayFromZero, 2 dp) and used for classification/margins.
- [ ] Unmatched lines show `[NUEVO]` + create action, not approvable until resolved; zero-cost lines show `[UPDATE] 0.00 → cost`.
- [ ] Create product captures a fresh EAN/UPC (never the supplier code), creates the product (identity only), resolves the line, and upserts the alias atomically; Cashier blocked with 403.
- [ ] Confirm learns aliases for applied lines with supplier codes; next invoice from that supplier auto-matches.
- [ ] `dotnet build CommandCenter.slnx -c Release` 0/0; suite green (Inventory ≥0.72).
