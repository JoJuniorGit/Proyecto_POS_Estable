# Tasks: Supplier Invoice Price Update

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~1,500–2,000 |
| Budget risk | High |
| Chained PRs | Yes |
| Split | 4 chained PRs |
| Delivery | ask-on-risk |
| Chain | stacked-to-main |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

### Suggested Work Units

| Goal / PR | Focused test command | Harness / Rollback boundary |
|---|---|---|
| 1. Domain+migration+DTOs (PR 1) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~SupplierInvoiceMigration"` | dotnet ef database update / down-migration drops supplier tables |
| 2. Matching+staging+endpoints (PR 2) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~SupplierInvoiceMatching\|FullyQualifiedName~SupplierInvoiceStaging"` | Stage xlsx via POST /api/supplier-invoices / revert service+controller |
| 3. Apply+confirm+RBAC (PR 3) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~SupplierInvoiceApply"` | Confirm under concurrent Postgres update / revert apply+endpoint |
| 4. WPF client+nav/DI (PR 4) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~SupplierInvoiceClient"` | Launch Desktop.Client: import, override, confirm / revert UI+DI |

## Phase 1: Domain & Migration

- [x] 1.1 Create `Core/Entities/Supplier.cs`, `Core/Entities/SupplierColumnMapping.cs`, `Core/Entities/SupplierProductCode.cs`, `Core/Entities/SupplierInvoice.cs`, `Core/Entities/SupplierInvoiceLine.cs` (enums) and `Core/DTOs/SupplierInvoiceDtos.cs`.
- [x] 1.2 Modify `Inventory.Module/Data/InventoryDbContext.cs`: DbSets, filtered unique index IX_SupplierProductCodes_Supplier_Code, xmin on SupplierInvoice.
- [x] 1.3 Add additive migration AddSupplierInvoice under `Inventory.Module/Migrations/` via dotnet ef migrations add.
- [x] 1.4 Test migration smoke `CommandCenter.Tests/Integration/SupplierInvoiceMigrationSmokeTests.cs`: tables/indexes applied, products untouched (supplier-invoice-staging Multi-Format Ingestion).

## Phase 2: Matching, Staging & Endpoints

- [x] 2.1 Create `Inventory.Module/Services/SupplierInvoiceService.cs` and `Inventory.Module/Services/SupplierInvoiceService.Staging.cs`: supplier resolve/block, mapping upsert/reuse; mirror `Inventory.Module/Services/InventoryService.Import.cs` (read-only).
- [x] 2.2 Create `Inventory.Module/Services/SupplierInvoiceService.Matching.cs`: SKU, supplier code, pg_trgm threshold from settings; lower-Id tie-break; Conflict status.
- [x] 2.3 Create `Backend.API/Controllers/SupplierInvoicesController.cs` stage/read endpoints, RBAC Admin/Manager, ProblemDetails.
- [x] 2.4 Test supplier-product-matching (Barcode wins; Supplier code fallback; Fuzzy name fallback; Threshold boundary; Two equal-similarity candidates; Match-only v1) `CommandCenter.Tests/Unit/SupplierInvoiceMatchingTests.cs`.
- [x] 2.5 Test supplier-invoice-staging (Resolve supplier by fiscal identity; Unmatched supplier blocks staging; Save/Reuse saved mapping; Unparseable or empty file; Status classification per line; No apply before confirm) `CommandCenter.Tests/Unit/SupplierInvoiceStagingTests.cs`.

## Phase 3: Atomic Apply

- [ ] 3.1 Create `Inventory.Module/Services/SupplierInvoiceService.Apply.cs`: execution strategy, transaction, server re-validation, retail-primary margins, RoundPriceUp, stock + StockMovement, xmin retry; `Core/Helpers/PricingCalculator.cs` (read-only).
- [ ] 3.2 Add confirm endpoint POST /api/supplier-invoices/{id}/confirm in `Backend.API/Controllers/SupplierInvoicesController.cs`.
- [ ] 3.3 Test supplier-invoice-apply (Only approved lines applied; Both margins updated; Override recorded; Stock and audit written together) `CommandCenter.Tests/Unit/SupplierInvoiceApplyTests.cs`.
- [ ] 3.4 Test integration: Forged client validation rejected; atomic rollback leaves untouched rows; Concurrency conflict retried (TEST_POSTGRES_CONNECTION); Cashier blocked 403 `CommandCenter.Tests/Integration/SupplierInvoiceApplyIntegrationTests.cs`.

## Phase 4: WPF Client

- [ ] 4.1 Create `Desktop.Client.Core/Services/SupplierInvoiceService.cs` and `Desktop.Client.Core/Services/SupplierInvoiceService.Parsing.cs` (xlsx/csv/xml); mirror `Desktop.Client.Core/Services/ProductImportService.cs` (read-only).
- [ ] 4.2 Create `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel.cs`, `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel.Staging.cs`, `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel.Pricing.cs`: badges, approval ON, RoundPriceUp recalc; reference `Desktop.Client.Core/ViewModels/ProductDialogViewModel.Pricing.cs` (read-only).
- [ ] 4.3 Create `Desktop.Client/Views/SupplierInvoiceView.xaml` and `Desktop.Client/Views/SupplierInvoiceView.xaml.cs` with BindingProxy virtualization.
- [ ] 4.4 Wire DI/nav in `Desktop.Client/App.xaml.cs`, `Desktop.Client/MainWindow.xaml`, `Desktop.Client.Core/ViewModels/MainViewModel.cs`.
- [ ] 4.5 Test client (Instant client-side recalc; Ingest each supported format) plus approval default ON, no per-keystroke API calls in `CommandCenter.Tests/Unit/SupplierInvoiceClientTests.cs`.

## Phase 5: Verification

- [ ] 5.1 `dotnet build CommandCenter.slnx -c Release`, `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`, coverage via `scripts/check-coverage.py` (read-only; Inventory ≥0.72).
- [ ] 5.2 Register ANEXO in `docs/reporte.txt`; note migration-down rollback.
