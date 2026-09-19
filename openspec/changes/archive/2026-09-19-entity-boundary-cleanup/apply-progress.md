# Apply Progress: entity-boundary-cleanup

## Work Unit: WU-1 read boundary (Phase 1, tasks 1.1-1.8)

Status: complete, awaiting independent SDD verification.
Attempt: `WU-1-read-boundary` token `sha256:d8f447ae2bfccb06af1b15ff879361b93068af6158f774383a7ef0d84e516857`.

### Completed Tasks

- [x] 1.1 RED `SaleProductInfoDtoShapeTests`
- [x] 1.2 RED interface reflection boundary test
- [x] 1.3 `Core/DTOs/SaleProductInfoDto.cs` + `IInventoryService` 3 DTO reads / 4 entity reads removed
- [x] 1.4 `InventoryService.cs` shared in-query projection; SKU lookup deleted
- [x] 1.5 `SalesService` + 4 partials re-pointed; `UnitCostUSD = dto.CostPriceUSD`, no `?? 0m`
- [x] 1.6 4 fallback branches deleted (`Pricing`, `HoldOrders` x2, `Checkout`)
- [x] 1.7 `SaleItemCostSnapshotTests` NULL-cost pin added; `ProductCostMaskingTests` green untouched
- [x] 1.8 read-mock + inventory test files re-pointed

### Files Changed

| File | Action | What Was Done |
|------|--------|---------------|
| `Core/DTOs/SaleProductInfoDto.cs` | Created | `sealed record`, init-only, 15 members, `decimal? CostPriceUSD` |
| `Core/Interfaces/IInventoryService.cs` | Modified | 3 DTO reads in; `GetProductsByIdsAsync`/`GetProductByIdAsync`/`GetProductBySkuAsync` out; `using Core.Entities` retained for WU-2 write members |
| `Inventory.Module/Services/InventoryService.cs` | Modified | `ProjectSaleProduct` projection; 3 in-query DTO reads; SKU lookup deleted |
| `Sales.Module/Services/SalesService.cs` | Modified | `ValidateAndAdjustQuantity` + `AddItemAsync` use DTO reads |
| `Sales.Module/Services/SalesService.Pricing.cs` | Modified | `RecalculateTotalAsync` batch DTO read; fallback deleted |
| `Sales.Module/Services/SalesService.HoldOrders.cs` | Modified | 2 batch DTO reads; 2 fallbacks deleted |
| `Sales.Module/Services/SalesService.Checkout.cs` | Modified | batch DTO read; `PopulateItemsMetadataAsync` fallback deleted |
| `Backend.API/Controllers/ProductsController.cs` | Modified | entity fallback removed from `GetByIdAsync` (compile-required; see Deviations) |
| `CommandCenter.Tests/Builders/ProductBuilder.cs` | Modified | `BuildSaleProductInfo()` + `ToSaleProductInfo()` mapping |
| `CommandCenter.Tests/**` (25 files) | Modified | mock/query re-point to DTO reads |
| `CommandCenter.Tests/Unit/SaleProductInfoDtoShapeTests.cs` | Created | RED shape pins (REQ-ADB-07) |
| `CommandCenter.Tests/Unit/InventoryServiceReadBoundaryTests.cs` | Created | RED interface boundary pins (REQ-ADB-06) |

### Work Unit Evidence

| Evidence | Required value |
|---|---|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~SaleItemCostSnapshot|FullyQualifiedName~ProductCostMasking|FullyQualifiedName~SaleProductInfoDtoShape|FullyQualifiedName~InventoryServiceReadBoundary"` → passed 17, failed 0 |
| Runtime harness command/scenario and exact result | `N/A` — WU-1 is an in-process read-boundary refactor; no routing/shell/process boundary. Full-suite integration paths (`SaleFlowIntegrationTests`, `CashAdvanceEnvelopeTests`, `PerformanceAndOptimizationSprint3Tests` over real DbContext) exercise the runtime service path. |
| Rollback boundary | Revert `Core/DTOs/SaleProductInfoDto.cs`, `Core/Interfaces/IInventoryService.cs`, `Inventory.Module/Services/InventoryService.cs`, `Sales.Module/Services/SalesService{,.Pricing,.HoldOrders,.Checkout}.cs`, the `ProductsController.GetByIdAsync` fallback removal, `ProductBuilder` helpers, and the re-pointed test files. No migrations, no schema/behavior change outside the boundary. |

### Verification

- `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errors.
- `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`: 1308 passed / 0 failed (baseline 1300; +8 new pins).
- `ProductCostMaskingTests` untouched and green; `ProductConcurrencyTokenTests` untouched (WU-2 regression net).

### Deviations from Design

1. `ProductsController.GetByIdAsync` entity fallback was removed in WU-1. Removing `GetProductByIdAsync` from `IInventoryService` makes the controller's entity fallback uncompilable, so one of the two WU-3 fallback deletions was pulled forward to keep the chained PR1 build self-standing. The compat constructor, the `CreateAsync` entity fallback, and the remaining 7 controller constructions stay in WU-3. Two `ProductVariantsTests.VariantManagement` `GetById` tests were re-pointed to the 3-arg ctor (2 of WU-3's 9 constructions).
2. `using Core.Entities;` stays in `IInventoryService.cs`; write members (`CreateProductAsync`/`CreateSystemProductAsync`/`UpdateProductAsync`) still use `Product` until WU-2. It was not "no longer needed".
3. `ProductVariantsTests.*` read-back assertions moved from `service.GetProductByIdAsync` (removed) to `db.Products.AsNoTracking().FirstOrDefaultAsync`, preserving the untracked-read semantics that the original method had.

### Workload / PR Boundary

- Mode: stacked PR slice (PR1, base `main`).
- Authored changed lines: ~727 (`564` tracked additions+deletions excluding the pre-existing `opencode.json` edit, plus `163` lines of 3 new files).
- Over the 400-line budget; WU-1 has no cohesive sub-split (spec + DTO + projection + Sales re-point + test churn form one deliverable behavior). Recommend `size:exception` for PR1.

### Remaining Tasks

- [ ] Phase 3 WU-3 (3.1-3.4)
- [ ] Phase 4 WU-4 (4.1-4.7)
- [ ] Phase 5 Verification (5.1-5.3)

---

## Work Unit: WU-2 write boundary (Phase 2, tasks 2.1-2.6)

Status: complete, awaiting independent SDD verification.
Attempt: `WU-2-write-boundary` token `sha256:88c9fb07656d3cbc4104663d3385d05ae8e5833537d65e10dc6caccd946df967`.

### Completed Tasks

- [x] 2.1 `Core/DTOs/CreateSystemProductRequest.cs` created (sealed record, 6 members)
- [x] 2.2 `IInventoryService` exposes `CreateSystemProductAsync(CreateSystemProductRequest) -> Task<int>`; entity `CreateProductAsync`/`UpdateProductAsync`/`CreateSystemProductAsync(Product)` removed; `using Core.Entities` dropped (REQ-ADB-08)
- [x] 2.3 `InventoryService.ProductCrud.cs`: entity `CreateProductAsync`/`UpdateProductAsync` now private behind the DTO twins; `CreateSystemProductAsync(request)` maps the request to the entity via `CreateProductCoreAsync` and returns the created `Id`; tracked `FindAsync` + `CurrentValues.SetValues` body byte-identical (REQ-ADB-08)
- [x] 2.4 `SalesService.CashAdvance.cs` passes a `CreateSystemProductRequest` and assigns the returned `int` to `productId`; no entity handling
- [x] 2.5 `ProductConcurrencyTokenTests` extended with the gated Npgsql `UpdateProductFromDto_StaleToken_Conflicts` (returns early when `TEST_POSTGRES_CONNECTION` is absent; real `DbUpdateConcurrencyException` on stale `xmin`)
- [x] 2.6 `CashAdvanceTests` ADV-001 concrete `InventoryService` create re-pointed to the DTO twin (permission-gate parity preserved); the remaining concrete-writer test files re-pointed per design

### Files Changed

| File | Action | What Was Done |
|------|--------|---------------|
| `Core/DTOs/CreateSystemProductRequest.cs` | Created | `sealed record`; `Name`, `SKU`, `PriceRetailUSD`, `StockQuantity`, `IsCashAdvance`, `IsActive` |
| `Core/Interfaces/IInventoryService.cs` | Modified | `CreateSystemProductAsync(CreateSystemProductRequest) -> Task<int>` in; 3 entity CRUD members out; `using Core.Entities` removed |
| `Inventory.Module/Services/InventoryService.ProductCrud.cs` | Modified | Entity CRUD private; request→entity system-create returns `Id`; tracked update body untouched |
| `Sales.Module/Services/SalesService.CashAdvance.cs` | Modified | Request-based system create; consumes returned `int` |
| `Backend.API/Controllers/ProductsController.cs` | Modified | POST entity fallback deleted (compile-required; see Deviations) |
| `CommandCenter.Tests/Unit/ProductConcurrencyTokenTests.cs` | Modified | Gated `UpdateProductFromDto_StaleToken_Conflicts` added |
| `CommandCenter.Tests/ProductDtoMappingTestExtensions.cs` | Created | Test-only `ProductDto -> UpdateProductDto` mapper |
| `CommandCenter.Tests/Unit/InventoryServiceUnitTests.cs` | Modified | 8 create sites re-pointed to `CreateProductFromDtoAsync` (permission + cash-advance normalization parity preserved) |
| `CommandCenter.Tests/RbacPermissionTests.cs` | Modified | Permission-gate create/delete tests re-pointed to the DTO twin |
| `CommandCenter.Tests/CashAdvanceTests.cs` | Modified | ADV-001 direct-create assertion re-pointed |
| `CommandCenter.Tests/PriceListTests.cs`, `PriceListTests.Products.cs` | Modified | Create/update sites re-pointed (update via `UpdateProductFromDtoAsync` + mapper) |
| `CommandCenter.Tests/Unit/ProductVariantsTests{,.Controllers,.ConversionFactor,.IndependentPricing,.AdjustStock,.Stock,.VariantManagement}.cs` | Modified | Entity-CRUD callers re-pointed to the DTO twins; immutable-flag and conversion-factor guard assertions preserved |

### Work Unit Evidence

| Evidence | Required value |
|---|---|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ProductConcurrencyToken\|FullyQualifiedName~CashAdvance\|FullyQualifiedName~Rbac"` → passed 71, failed 0 |
| Runtime harness command/scenario and exact result | `N/A` production boundary — in-process write path. Gated provider harness: `UpdateProductFromDto_StaleToken_Conflicts` runs against real Npgsql (`TEST_POSTGRES_CONNECTION`) and is a no-op return when unset; this environment has no `TEST_POSTGRES_CONNECTION`, so it reported as passed-without-execution. |
| Rollback boundary | Revert `Core/DTOs/CreateSystemProductRequest.cs`, `Core/Interfaces/IInventoryService.cs`, `Inventory.Module/Services/InventoryService.ProductCrud.cs`, `Sales.Module/Services/SalesService.CashAdvance.cs`, the `ProductsController.CreateAsync` fallback removal, `ProductDtoMappingTestExtensions.cs`, and the re-pointed test files. No migrations, no schema change, no WPF client change. |

### Verification

- `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errors.
- `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`: 1309 passed / 0 failed (post-WU-1 baseline 1308; +1 gated test).
- Focused `ProductConcurrencyToken|CashAdvance|Rbac`: 71 passed / 0 failed.
- `ProductConcurrencyTokenTests` model pins green; tracked `FindAsync` + `SetValues` body unchanged; `ProductQuickInfoDto` untouched.

### Deviations from Design

1. Compile-forced: `ProductsController.CreateAsync` POST entity fallback was removed. Making entity `CreateProductAsync` private renders `_inventoryService.CreateProductAsync(product, ...)` uncompilable, so that fallback was deleted (same pattern as WU-1's `GetByIdAsync` fallback). The compat constructor, the `IProductManagementService` null path, and the remaining WU-3 material stay untouched.
2. Test re-point scope: task 2.6 named only `CashAdvanceTests` ADV-001, but the design's WU-2 "Entity-CRUD callers → DTO twins" plus the private-CRUD mandate forced re-pointing every concrete-`InventoryService` writer test (`InventoryServiceUnitTests`, `RbacPermissionTests`, `PriceListTests*`, `ProductVariantsTests.*`). A test-only `ToUpdateProductDto` mapper was added so update tests drive `UpdateProductFromDtoAsync` while preserving their guard/price assertions.
3. `AdjustStockAsync_DeletedProduct_ThrowsInvalidOperationException` originally seeded `IsDeleted = true` through the entity create. `CreateProductDto` has no `IsDeleted`, so the test now creates via the DTO twin and marks `IsDeleted` on the persisted entity before the assertion — same observable behavior.

### Workload / PR Boundary

- Mode: stacked PR slice (PR2, base = PR1 `WU-2` branch / `main` after PR1).
- Authored changed lines: ~370 (estimate; WU-1 residue is uncommitted in the same worktree so shared test files cannot be split exactly). Majority is test re-point churn required by the private-CRUD mandate.
- Within the 400-line review budget; above the tasks forecast band (~150-250) because the private-CRUD requirement pulled every entity-CRUD caller through the DTO twins. No `size:exception` required for PR2 unless the maintainer's exact isolation measures above budget.

### Remaining Tasks (WU-2 view)

- [x] Phase 2 WU-2 (2.1-2.6)
- [ ] Phase 3 WU-3 (3.1-3.4)
- [ ] Phase 4 WU-4 (4.1-4.7)
- [ ] Phase 5 Verification (5.1-5.3)

---

## Work Unit: WU-3 controller DTO-only (Phase 3, tasks 3.1-3.4)

Status: complete, awaiting independent SDD verification.
Attempt: `WU-3-controller-dto-only` token `sha256:2a4d24d2531daf5b6ed9b04d8033501011d32bed4108f77474066e34a778f231`.

### Completed Tasks

- [x] 3.1 RED single-ctor reflection test (REQ-ADB-09)
- [x] 3.2 `ProductsController.cs`: single required `IProductManagementService` ctor; compat ctor and all null-path branches deleted (REQ-ADB-09)
- [x] 3.3 Re-point all remaining controller constructions to the 3-arg ctor (7 in this unit; 2 in WU-1 → 9 total)
- [x] 3.4 RED GET missing id → 404 with no entity-derived product (REQ-ADB-09)

### Files Changed

| File | Action | What Was Done |
|------|--------|---------------|
| `Backend.API/Controllers/ProductsController.cs` | Modified | `IProductManagementService` now non-nullable and constructor-required; compat 2-arg ctor deleted; `[ActivatorUtilitiesConstructor]` kept on the single ctor; `GetByIdAsync`/`CreateAsync`/`UpdateAsync` null-path branches deleted (the two entity fallbacks were already removed in WU-1/WU-2) |
| `CommandCenter.Tests/Unit/ProductsControllerBoundaryTests.cs` | Created | RED pins: exactly one public ctor that takes `IProductManagementService`; GET by absent id → 404 with `IInventoryService.VerifyNoOtherCalls()` |
| `CommandCenter.Tests/Unit/ControllerConstructionTests.cs` | Modified | Constructor-guard theory now enumerates controllers that either declare >1 public ctor or declare `[ActivatorUtilitiesConstructor]` (non-empty data; ProductsController keeps its guard; multi-ctor requirement preserved) |
| `CommandCenter.Tests/Unit/ProductCostMaskingTests.cs` | Modified | 6 constructions re-pointed to `new ProductsController(inventory, Mock.Of<IProductManagementService>(), user)` |
| `CommandCenter.Tests/Unit/ProductVariantsTests.VariantManagement.cs` | Modified | `AdjustStock` construction re-pointed to the 3-arg ctor (final 1 of the 9; 2 were done in WU-1) |

### Work Unit Evidence

| Evidence | Required value |
|---|---|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ProductsController\|FullyQualifiedName~ControllerConstruction\|FullyQualifiedName~ProductCostMasking"` → passed 32, failed 0 |
| Runtime harness command/scenario and exact result | Controller action `ProductsController.GetByIdAsync(999)` executed against a real `IProductManagementService` whose `GetProductDtoByIdAsync` returns null → `NotFoundObjectResult` 404 with zero `IInventoryService` calls. Auth/routing middleware is excluded because `[Authorize]` gates the route and the suite's only real HTTP harness (`WebApplicationFactorySmokeTests`) requires `TEST_POSTGRES_CONNECTION`; with `TEST_POSTGRES_CONNECTION` and `ConnectionStrings__DefaultConnection` both unset in this environment, every smoke test early-returns (`if (!PostgresConfiguredForPipeline()) return;`) before `CreateFactory()` and silently no-ops — it does not boot the production pipeline. |
| Rollback boundary | Revert `Backend.API/Controllers/ProductsController.cs` (ctor/field/null-path removal), `CommandCenter.Tests/Unit/ProductsControllerBoundaryTests.cs`, the `ControllerConstructionTests` theory change, and the re-pointed constructions in `ProductCostMaskingTests`/`ProductVariantsTests.VariantManagement`. No migration, no WPF client change, no `ProductQuickInfoDto` or `IInventoryService` change. |

### Verification

- `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errors.
- `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`: 1315 passed / 0 failed (post-WU-2 baseline 1309; +6 = 2 new facts + 4 expanded guard-theory rows).
- Focused `ProductsController|ControllerConstruction|ProductCostMasking`: 32 passed / 0 failed.
- No 2-arg `new ProductsController(...)` call sites remain; `ControllerConstructionTests` guard stays non-empty and ProductsController stays covered.

### Deviations from Design

1. `ControllerConstructionTests` theory data had to change beyond the single-ctor edit: ProductsController was the only controller with >1 public constructor, so after the compat ctor deletion the theory's MemberData would have been empty (xUnit "no data" exception) and ProductsController would have silently left the guard. The predicate now also includes controllers declaring `[ActivatorUtilitiesConstructor]`, keeping the multi-ctor requirement and covering ProductsController (plus the existing CashDrawer/ExchangeRate/Settings/Users controllers that already carry the marker). The `[ActivatorUtilitiesConstructor]` attribute is retained on the single ctor, consistent with those controllers.
2. The task narrative says "9 constructions" and "compat one-arg constructor"; the compat ctor is actually 2-arg and only 7 call sites remained (2 were re-pointed in WU-1). All remaining 2-arg call sites were converted; no 2-arg ctor exists afterwards.

### Workload / PR Boundary

- Mode: stacked PR slice (PR3, base = PR2 branch / `main` after PR2).
- Authored changed lines: ~97 (controller ~30, `ControllerConstructionTests` 13, `ProductCostMaskingTests` 12, `ProductVariantsTests.VariantManagement` 2, new `ProductsControllerBoundaryTests.cs` 40).
- Within the 400-line review budget; no `size:exception` required for PR3.

### Remaining Tasks (WU-3 view)

- [x] Phase 3 WU-3 (3.1-3.4)
- [x] Phase 4 WU-4 (4.1-4.7)
- [ ] Phase 5 Verification (5.1-5.3)

---

## Work Unit: WU-4 WPF client DTO-only (Phase 4, tasks 4.1-4.7)

Status: complete, awaiting independent SDD verification.
Attempt: `WU-4-wpf-client` token `sha256:dab45b3e18bf53d2d058f5b3df0b79e02816fcd557e88714c056934df7d407fd`.

### Completed Tasks

- [x] 4.1 `IProductService`/`ProductService`: `Task<ProductDto?> GetByIdAsync(int)`, `Task<ProductDto> CreateAsync(CreateProductDto)`, `Task UpdateAsync(UpdateProductDto)`; `using Core.Entities` dropped (REQ-ADB-10)
- [x] 4.2 `Desktop.Client.Core/Services/ProductClientMapping.cs` created: `ToCreateProductDto`, `ToUpdateProductDto(ProductDto)`, `ToUpdateProductDto(CreateProductDto, int)`, `Merge(ProductDto, CreateProductDto)`
- [x] 4.3 `ProductDialogViewModel`: `ProductDto? _initialProduct`/ctor param, `CreateProductDto ResultProduct` + `int ResultProductId`; write block re-mapped preserving `StockQuantity` (create), `GroupKey`, `ConversionFactor`, `ParentProductId`, `IsGroupHeader`, `IsStockShared`, `HasIndependentPricing`, `IsCashAdvance`; dropped `Cost`, `ProfitPercentage`, `CreatedAt`, dead `ReservedQuantity` (REQ-ADB-10)
- [x] 4.4 `VariantManagementViewModel` batch edit builds `UpdateProductDto`; `AddVariantAsync` builds `CreateProductDto`; `InventoryViewModel.Operations` uses `ProductDto` directly and drops the hand-rolled `MapToDto(Product)` (REQ-ADB-10)
- [x] 4.5 RED pin: `ProductDialogViewModel_Create_MapsStockQuantityIntoCreateProductDto` + `ProductService_CreateAsync_SendsStockQuantityInRequestBody` (REQ-ADB-10)
- [x] 4.6 `ProductServiceQueryContractTests` extended: shape guards for `GetByIdAsync`/`CreateAsync`/`UpdateAsync` + no-entity-reflection guard; query-binding/camelCase guards untouched (REQ-ADB-10, SEAM-01 bound)
- [x] 4.7 The 12 exploration-listed `IProductService` mock files are loose Moq mocks that never configure the changed members, so they needed no edits; the one file that did configure them (`ProductVariantsTests.Controllers.cs`, a 13th not in the inventory) was re-pointed

### Files Changed

| File | Action | What Was Done |
|------|--------|---------------|
| `Desktop.Client.Core/Services/IProductService.cs` | Modified | DTO-only read/create/update signatures; `using Core.Entities` removed |
| `Desktop.Client.Core/Services/ProductService.cs` | Modified | Serializes `CreateProductDto`/`UpdateProductDto`, deserializes `ProductDto` |
| `Desktop.Client.Core/Services/ProductClientMapping.cs` | Created | `ProductDto` ↔ `Create/UpdateProductDto` helpers + `Merge` |
| `Desktop.Client.Core/ViewModels/ProductDialogViewModel.cs` | Modified | `ProductDto?` input; `CreateProductDto ResultProduct` + `ResultProductId`; write block re-mapped |
| `Desktop.Client.Core/ViewModels/VariantManagementViewModel.cs` | Modified | Batch edit updates via `UpdateProductDto`; new variant via `CreateProductDto`; `using Core.Entities` removed |
| `Desktop.Client.Core/ViewModels/InventoryViewModel.Operations.cs` | Modified | DTO-only read/create/update/auto-save; `MapToDto(Product)` deleted; `using Core.Entities` removed |
| `CommandCenter.Tests/Unit/ProductServiceQueryContractTests.cs` | Modified | 4 shape/entity-boundary guard facts added |
| `CommandCenter.Tests/Unit/ProductClientStockQuantityTests.cs` | Created | Headless create-flow `StockQuantity` pin + request-payload pin |
| `CommandCenter.Tests/Unit/ProductVariantsTests.Controllers.cs` | Modified | `GetByIdAsync` returns `ProductDto`; `UpdateAsync` verify uses `UpdateProductDto` |
| `CommandCenter.Tests/Unit/ProductVariantsTests.Core.cs` | Modified | Dialog input `Product` → `ProductDto` |
| `CommandCenter.Tests/Unit/ProductVariantsTests.Pricing.cs` | Modified | Dialog inputs `Product` → `ProductDto` |
| `CommandCenter.Tests/Unit/ProductVariantsTests.Stock.cs` | Modified | Dialog inputs `Product` → `ProductDto`; `ResultProduct.Id` → `ResultProductId` |

### Work Unit Evidence

| Evidence | Required value |
|---|---|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~ProductServiceQueryContract\|FullyQualifiedName~Inventory\|FullyQualifiedName~VariantManagement"` → passed 70, failed 0; `--filter "FullyQualifiedName~ProductClientStockQuantity"` → passed 2, failed 0 |
| Runtime harness command/scenario and exact result | Headless WPF create flow: `ProductDialogViewModel.CreateAsync` path exercised in-process — `ProductDialogViewModel_Create_MapsStockQuantityIntoCreateProductDto` drives the real `SaveCommand` and asserts `ResultProduct.StockQuantity == 42`; `ProductService_CreateAsync_SendsStockQuantityInRequestBody` runs the real `ProductService` over an `HttpMessageHandler` and asserts the serialized body contains `"stockQuantity":42`. No Postgres/desktop shell boundary in this unit. |
| Rollback boundary | Revert `Desktop.Client.Core/Services/{IProductService,ProductService,ProductClientMapping}.cs`, the 3 client ViewModels, `ProductServiceQueryContractTests.cs`, `ProductClientStockQuantityTests.cs`, and the 4 re-pointed `ProductVariantsTests.*` files. No server file, no migration, no `IInventoryService`, no `ProductQuickInfoDto` change. |

### Verification

- `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errors.
- `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`: 1321 passed / 0 failed (post-WU-3 baseline 1315; +6 = 4 contract guards + 2 create-flow stock pins).
- Focused `ProductServiceQueryContract|Inventory|VariantManagement`: 70 passed / 0 failed.
- No `Core.Entities.Product` remains in `Desktop.Client.Core`; only `Core.Entities.UnitOfMeasureType` (enum) is still referenced.

### Deviations from Design

1. `ProductDialogViewModel` input contract: the constructor/`_initialProduct` became `ProductDto?` (design only fixed the result shape). Required because `IProductService.GetByIdAsync` now returns `ProductDto?`; the read block needed no field remap since `ProductDto` already mirrors the entity fields the dialog reads.
2. The edit-mode `ResultProduct.StockQuantity` reassignment was dropped as dead: `UpdateProductDto` carries no `StockQuantity`, so the value could not reach the server. The create-mode `StockQuantity` mapping (regression `5620917`) is preserved and pinned.
3. Task 4.7 named 12 mock files; the real configured-mock file was `ProductVariantsTests.Controllers.cs` (not in the exploration's 12 because it lives in the `ProductVariantsTests` partial group). The 12 listed files only declare loose mocks and needed no edits.
4. Four `ProductVariantsTests.*` test files had to change because they passed `Core.Entities.Product` into `ProductDialogViewModel`; the design named only "12 mocks", not this dialog-input churn.

### Post-Verification Follow-up (independent verifier)

- (a) Auto-save margin pass-through: `InventoryViewModel.Operations.OnProductItemChangedAsync` maps `changed.ProfitPercentage` into `dto.ProfitMarginRetail` (the old entity flow bound the entity's independent `ProfitMarginRetail`, silently dropping the edit). Pinned by `InventoryViewModel_AutoSave_AfterProfitPercentageEdit_SendsEditedMarginInUpdateProductDto` in `CommandCenter.Tests/Unit/ProductClientStockQuantityTests.cs`: the edited 45% reaches `IProductService.UpdateAsync` in `ProfitMarginRetail`, while the server snapshot still carries 30%.
- (b) Local list-rebuild display nuance (display-only): after `EditProduct`, the list item is rebuilt from `ProductClientMapping.Merge(product, dialogVm.ResultProduct)`. The composition differs from the old `MapToDto(ResultProduct)` path, but server-side pricing invariants make the old/new rendered values equivalent; no persistence impact.

### Workload / PR Boundary

- Mode: stacked PR slice (PR4, base = PR3 branch / `main` after PR3).
- Authored changed lines: ~455 (`272` tracked additions+deletions across 10 files + `183` lines of 2 new files).
- Over the 400-line budget; WU-4 has no cohesive sub-split (interface + mapping + 3 ViewModels + contract guards + dialog-input churn form one deliverable behavior). Recommend `size:exception` for PR4.

### Remaining Tasks (WU-4 view)

- [x] Phase 4 WU-4 (4.1-4.7)
- [x] Phase 5 Verification (5.1-5.3)

---

## Work Unit: Phase 5 verification (Phase 5, tasks 5.1-5.3)

Status: complete. No source edits — verification commands only.

Attempt: `Phase-5-verification-tasks` token `sha256:c0ac2a9b5c6df8ac49f05f496cbc4e5a235dbacc2a010ad1dade84d58ee36148`.

### Completed Tasks

- [x] 5.1 `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errors
- [x] 5.2 Full `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`: 1322 passed / 0 failed / 0 skipped
- [x] 5.3 Coverage gate `python scripts/check-coverage.py`: Core 0.8373, Sales.Module 0.8582, Inventory.Module 0.7997 — all thresholds met, exit 0

### Command Results

| Task | Command | Result |
|---|---|---|
| 5.1 | `dotnet build CommandCenter.slnx -c Release` | `0 Advertencia(s)`, `0 Errores` — all 9 projects compiled (Release, net10.0 / net10.0-windows) |
| 5.2 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` | `Con error: 0, Superado: 1322, Omitido: 0, Total: 1322` (VSTest 18.0.1, net10.0, 13 s) — matches the expected 1322 baseline exactly |
| 5.3a | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` | 1322 passed / 0 failed; attachment `CommandCenter.Tests/TestResults/15526bd2-75b1-4168-9322-85e1a1b6cf9f/coverage.cobertura.xml` |
| 5.3b | `python scripts/check-coverage.py CommandCenter.Tests/TestResults/15526bd2-75b1-4168-9322-85e1a1b6cf9f/coverage.cobertura.xml` | `Core rate=0.8373 min=0.7000 [OK]`, `Sales.Module rate=0.8582 min=0.8000 [OK]`, `Inventory.Module rate=0.7997 min=0.7200 [OK]`; exit code 0 |

### Work Unit Evidence

| Evidence | Required value |
|---|---|
| Focused test command and exact result | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` → 1322 passed, 0 failed (same total as the plain 5.2 run). |
| Runtime harness command/scenario and exact result | `N/A` — Phase 5 is a verification-only unit with no production code path of its own. The WU-1..WU-4 runtime harnesses (in-process DTO read/write boundary, controller 404 path, headless WPF create flow, gated Npgsql stale-token conflict) were already recorded in their own sections and are re-exercised by the 1322-test full suite. `TEST_POSTGRES_CONNECTION` remains unset in this environment, so the Postgres-gated harnesses report as passed-without-execution. |
| Rollback boundary | None — no source, test, or build file was modified. The only writes are the `[x]` marks in `openspec/changes/entity-boundary-cleanup/tasks.md` and this section. Coverage output lives under the untracked `CommandCenter.Tests/TestResults/` folder and can be deleted freely. |

### Deviations from Design

None. All three Phase 5 tasks matched their expected outcome; no source file was edited and no threshold gap was found.

### Issues Found

None. Coverage thresholds pass with margin on every layer (Core +0.1373, Sales.Module +0.0582, Inventory.Module +0.0797, against `min`).

### Workload / PR Boundary

- Mode: N/A — verification-only slice; contributes `0` authored changed lines and no PR content.
- Prior slices keep their standing recommendations: `size:exception` for PR1 (WU-1, ~727 lines) and PR4 (WU-4, ~455 lines); PR2 (~370) and PR3 (~97) are within budget.

### Remaining Tasks

- [x] Phase 1 WU-1 (1.1-1.8)
- [x] Phase 2 WU-2 (2.1-2.6)
- [x] Phase 3 WU-3 (3.1-3.4)
- [x] Phase 4 WU-4 (4.1-4.7)
- [x] Phase 5 Verification (5.1-5.3)

All tasks complete — ready for independent SDD verification.
