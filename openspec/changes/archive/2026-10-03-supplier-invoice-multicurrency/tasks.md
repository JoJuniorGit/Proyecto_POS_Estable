# Tasks: Supplier Invoice Multi-Currency & Hot Product Creation

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~1,400–2,000 |
| Budget risk | High |
| Chained PRs | Yes |
| Split | 4 chained PRs |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached from 8.144/8.145) |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

### Suggested Work Units

| Goal / PR | Focused test command | Harness / Rollback boundary |
|---|---|---|
| 1. Domain+migration+DTOs (PR 1) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoiceMigration"` | `dotnet ef database update` / down-migration drops the three columns |
| 2. Staging currency+normalization+classification (PR 2) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoiceStaging\|FullyQualifiedName~SupplierInvoiceMatching"` | Revert staging/matching service changes |
| 3. Hot creation + alias + endpoint + client API (PR 3) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoiceApply\|FullyQualifiedName~SupplierInvoiceCreation"` | Revert creation service/endpoint/alias writes |
| 4. WPF header+grid+modal (PR 4) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoiceClient"` | Revert view/VM/dialog + client service method |

## Phase 1: Domain, DTOs & Migration

- [x] 1.1 `Core/Entities/SupplierInvoice.cs`: add `Currency` (max 8, default `"USD"`), `AppliedRate` (decimal(18,4), default 1). `Core/Entities/SupplierInvoiceLine.cs`: add `UnitCostDocument` (decimal(18,2)).
- [x] 1.2 `Core/DTOs/SupplierInvoiceDtos.cs`: request adds `Currency` + `AppliedRate`; rename `StageLineDto.UnitCostUSD` → `UnitCostDocument`; detail adds `Currency`/`AppliedRate`; line DTO adds `UnitCostDocument`.
- [x] 1.3 `Inventory.Module/Data/InventoryDbContext.cs`: lengths/precision; mechanical compile fixes in service/client/tests (rename only, identity mapping — no behavior change).
- [x] 1.4 Additive migration `AddSupplierInvoiceMulticurrency` + backfill (`Currency='USD'`, `AppliedRate=1`, `UnitCostDocument=UnitCostUSD`) + Down; update `SupplierInvoiceMigrationSmokeTests` model/gated assertions.

## Phase 2: Staging — Currency, Normalization & Classification

- [x] 2.1 `StageAsync`: validate currency ∈ {`USD`,`Bs.S`}; `Bs.S` requires `AppliedRate > 0`; normalize rate with `RoundExchangeRateCeiling`; `USD` forces rate 1; persist on the invoice.
- [x] 2.2 Normalize `UnitCostUSD = ToUSD(UnitCostDocument, rate)` (USD → identity); persist both; new classification: no product → `New`; else differs (including 0) → `Update`; equal → `Unchanged`.
- [x] 2.3 `ToDetailDto`/`ToLineDto` map `Currency`, `AppliedRate`, `UnitCostDocument`.
- [x] 2.4 Tests: normalization + validation rejections; classification updates (unmatched → `New`, zero-cost → `Update`); matching tests unaffected; client parsing tests updated.

## Phase 3: Hot Creation, Alias & Client API

- [x] 3.1 `Core/Interfaces/ISupplierInvoiceService` + `CreateInvoiceProductRequestDto(Barcode, Name)`; inject `IProductManagementService` into `SupplierInvoiceService` (DI + test constructors).
- [x] 3.2 `Inventory.Module/Services/SupplierInvoiceService.Creation.cs`: atomic (execution strategy + transaction) flow — validations (draft, line ownership, unresolved, `^\d{8,14}$`, name), create identity-only product (cost/margins/prices/stock 0), alias upsert, line resolve + reclassify + snapshots, return detail.
- [x] 3.3 `Backend.API/Controllers/SupplierInvoicesController.cs`: `POST /{id:int}/lines/{lineId:int}/create-product` mirroring controller error conventions (400/403/404/409).
- [x] 3.4 `Apply.cs`: alias upsert inside the confirm transaction for applied lines with non-empty `SupplierCode`.
- [x] 3.5 Desktop client `ISupplierInvoiceService`/`SupplierInvoiceService`: `CreateProductFromLineAsync` + route test.

## Phase 4: WPF UI

- [x] 4.1 Header: currency ComboBox (USD/Bs.S), rate TextBox (required when Bs.S; prefilled from `IExchangeRateService`), `CanStageInvoice` includes rate validity; stage request sends currency/rate.
- [x] 4.2 Grid: `[NUEVO]` badge for New, `CanApprove => ResolvedProductId != null`, per-line "Crear Producto" action (only for unresolved lines), document-cost display for Bs.S.
- [x] 4.3 `CreateInvoiceProductDialog.xaml(+.cs)` + `CreateInvoiceProductDialogViewModel` (ObservableValidator: barcode `^\d{8,14}$` required, name required; read-only cost/qty context; headless-testable) + `IDialogService.ShowCreateInvoiceProductDialog` + `WpfDialogService` impl (`RequestClose` → `DialogResult`).
- [x] 4.4 `SupplierInvoiceViewModel`: create command → `CreateProductFromLineAsync` → refresh `StagedInvoice`; inline errors.
- [x] 4.5 Client tests: labels/`[NUEVO]`, approval gating, rate validation, create command wiring, modal VM validation, no inheritance of invoice codes.

## Phase 5: Verification

- [x] 5.1 `dotnet build CommandCenter.slnx -c Release` (0/0); full `dotnet test`; coverage via `scripts/check-coverage.py` within gates.
- [x] 5.2 Independent verification per slice + final; register ANEXO 8.146 in `docs/reporte.txt`; document down-migration rollback.
