# Tasks: entity-boundary-cleanup

## Review Workload Forecast

Estimated authored lines: WU-1 ~480-650, WU-2 ~150-250, WU-3 ~60-100, WU-4 ~350-500, total ~1,040-1,500. Split: PR1 WU-1, PR2 WU-2, PR3 WU-3, PR4 WU-4; PR n base = PR n-1 (chain) or main (stacked).

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

Test churn (~60 files) dominates; WU-1/WU-4 each risk 400 and WU-4 has no cohesive sub-split, so overage needs `size:exception`. No migrations; `ProductQuickInfoDto` untouched; `TreatWarningsAsErrors`.

### Suggested Work Units

`T` = `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`.

- WU-1 PR 1: read boundary + Sales re-point. Test `T --filter "~SaleItemCostSnapshot|~ProductCostMasking"`. Runtime: N/A, in-process consumers. Rollback: DTO, 3 interface reads, `InventoryService.cs`, Sales files.
- WU-2 PR 2: write boundary + token guard. Test `T --filter "~ProductConcurrencyToken|~CashAdvance"`. Runtime: gated Postgres test. Rollback: request DTO, `CreateSystemProductAsync`, `ProductCrud.cs`, ADV-001 tests.
- WU-3 PR 3: controller DTO-only. Test `T --filter "~ProductsController|~ControllerConstruction"`. Runtime: `WebApplicationFactory<Program>` GET `/api/products/{missingId}` → 404. Rollback: ctor, fallbacks, 9 constructions.
- WU-4 PR 4: WPF client re-point. Test `T --filter "~ProductServiceQueryContract|~Inventory|~VariantManagement"`. Runtime: headless ViewModel create flow. Rollback: client signatures, `ProductClientMapping.cs`, 3 ViewModels, 12 mocks.

## Phase 1: WU-1 read boundary

- [x] 1.1 RED `SaleProductInfoDtoShapeTests`: sealed, init-only, 15 members, `CostPriceUSD` `decimal?` (REQ-ADB-07).
- [x] 1.2 RED interface reflection: no `Product` return, no `GetProductBySkuAsync` (REQ-ADB-06).
- [x] 1.3 Create `Core/DTOs/SaleProductInfoDto.cs`; `IInventoryService.cs`: 3 DTO reads replace 4 entity reads.
- [x] 1.4 `InventoryService.cs`: shared in-query projection; delete SKU lookup.
- [x] 1.5 Re-point `SalesService` + 4 partials; `UnitCostUSD = dto.CostPriceUSD`, no `?? 0m` (REQ-ADB-07, REQ-ADB-11).
- [x] 1.6 Delete 4 fallback branches in `Pricing`/`HoldOrders`/`Checkout`.
- [x] 1.7 RED `SaleItemCostSnapshotTests` NULL cost; `ProductCostMaskingTests` green (REQ-ADB-07, REQ-ADB-11).
- [x] 1.8 Re-point 21 read-mock + 2 SKU-lookup files.

## Phase 2: WU-2 write boundary

- [x] 2.1 Create `Core/DTOs/CreateSystemProductRequest.cs`.
- [x] 2.2 `IInventoryService.cs`: `CreateSystemProductAsync(request) -> Task<int>`; remove entity CRUD (REQ-ADB-08).
- [x] 2.3 `InventoryService.ProductCrud.cs`: CRUD private behind DTO twins; tracked `FindAsync`+`SetValues` untouched (REQ-ADB-08 stale token).
- [x] 2.4 `SalesService.CashAdvance.cs`: pass request, use returned `int` (REQ-ADB-08 returns id).
- [x] 2.5 RED `ProductConcurrencyTokenTests` + gated `UpdateProductFromDto_StaleToken_Conflicts` (Npgsql, `TEST_POSTGRES_CONNECTION`) (REQ-ADB-08).
- [x] 2.6 Re-point `CashAdvanceTests` ADV-001.

## Phase 3: WU-3 controller

- [x] 3.1 RED single-ctor reflection test (REQ-ADB-09).
- [x] 3.2 `ProductsController.cs`: require `IProductManagementService`; delete compat ctor and both fallbacks (REQ-ADB-09).
- [x] 3.3 Re-point 9 constructions to the 3-arg ctor.
- [x] 3.4 RED GET missing id → 404, no entity fallback (REQ-ADB-09).

## Phase 4: WU-4 WPF client

- [x] 4.1 `IProductService.cs` + `ProductService.cs`: DTO-only signatures (REQ-ADB-10).
- [x] 4.2 Create `ProductClientMapping.cs` merge helpers.
- [x] 4.3 `ProductDialogViewModel.cs`: `ResultProduct` becomes `CreateProductDto`; re-map write block `:410-487` keeping `StockQuantity` (REQ-ADB-10).
- [x] 4.4 `VariantManagementViewModel.cs` + `InventoryViewModel.Operations.cs`: DTO builds; delete `MapToDto(Product)`.
- [x] 4.5 RED create flow keeps `StockQuantity` (REQ-ADB-10).
- [x] 4.6 Extend `ProductServiceQueryContractTests` shape guards, SEAM-01 bound (REQ-ADB-10).
- [x] 4.7 Re-point 12 `IProductService` mocks.

## Phase 5: Verification

- [x] 5.1 `dotnet build CommandCenter.slnx -c Release`: 0 warnings.
- [x] 5.2 Full `dotnet test`: 100%.
- [x] 5.3 `python scripts/check-coverage.py`: Core 0.70, Inventory 0.72, Sales 0.80.
