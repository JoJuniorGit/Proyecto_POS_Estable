```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:8c702552d61ac47eb450f46b1f9eee2b7730d0215eed5cc1c78bdab5c0ae3468
verdict: pass
blockers: 0
critical_findings: 0
requirements: 6/6
scenarios: 13/13
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj
test_exit_code: 0
test_output_hash: sha256:1acf5a9166956f8e22a2d9a8ff36de2adb5d371c6596e26d27e511aa7f45206a
build_command: dotnet build CommandCenter.slnx -c Release
build_exit_code: 0
build_output_hash: sha256:cc9074be8031b773dc3da679418ee5a202d7903687119d434331c74a00929afb
```

## Verification Report

**Change**: entity-boundary-cleanup
**Version**: N/A (delta over `openspec/specs/api-dto-boundary/spec.md`, REQ-ADB-01..05 unchanged)
**Mode**: Standard (Strict TDD inactive — resolved `strict_tdd: false` in `openspec/config.yaml`)

### Counts (authoritative, from the delta spec headings)

| Metric | Value |
|--------|-------|
| Requirements in delta (`### Requirement:`) | 6 (REQ-ADB-06..REQ-ADB-11) |
| Scenarios in delta (`#### Scenario:`) | 13 |
| Tasks total | 28 |
| Tasks complete | 28 |
| Tasks incomplete | 0 |

Note: the launch brief stated "12 scenarios"; the delta spec contains 13 `#### Scenario:` headings
(REQ-ADB-06:2, REQ-ADB-07:2, REQ-ADB-08:3, REQ-ADB-09:2, REQ-ADB-10:2, REQ-ADB-11:2). The
authoritative heading count is used here. See WARNING-3.

### Build & Tests Execution

**Build**: Passed (exit 0) — `dotnet build CommandCenter.slnx -c Release`
```text
Compilacion correcta.
    0 Advertencia(s)
    0 Errores
Tiempo transcurrido 00:00:04.13
```
All 9 projects compiled: UpdaterService, Core, Logistics.Module, Sales.Module, Inventory.Module,
Desktop.Client.Core, Desktop.Client, Backend.API, CommandCenter.Tests.
Output bytes hashed as `sha256:cc9074be8031b773dc3da679418ee5a202d7903687119d434331c74a00929afb`
(sha256 over the captured stdout+stderr of that command).

**Tests**: Passed (exit 0) — `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`
```text
Correctas! - Con error:     0, Superado:  1322, Omitido:     0, Total:  1322, Duracion: 12 s
```
1322 passed / 0 failed / 0 skipped — matches the recorded baseline exactly.
Output bytes hashed as `sha256:1acf5a9166956f8e22a2d9a8ff36de2adb5d371c6596e26d27e511aa7f45206a`
(sha256 over the captured stdout+stderr of that command).

**Coverage**: not re-executed during this verification (scope was build + test). Recorded in
`sdd-apply` Phase 5.3 from `scripts/check-coverage.py` over
`CommandCenter.Tests/TestResults/15526bd2-75b1-4168-9322-85e1a1b6cf9f/coverage.cobertura.xml`:
Core 0.8373 (min 0.7000, OK), Sales.Module 0.8582 (min 0.8000, OK), Inventory.Module 0.7997
(min 0.7200, OK), exit 0.

### Spec Compliance Matrix

| Requirement | Scenario | Test / Evidence | Result |
|-------------|----------|-----------------|--------|
| REQ-ADB-06 | No entity crosses the read boundary | `CommandCenter.Tests/Unit/InventoryServiceReadBoundaryTests.cs > IInventoryService_ReadMembers_DoNotReturnProductEntity`, `> IInventoryService_ProductReads_ReturnSaleProductInfoDto`; `Inventory.Module/Services/InventoryService.cs:45-80` (`ProjectSaleProduct`, 3 in-query DTO reads) | COMPLIANT |
| REQ-ADB-06 | SKU lookup member removed | `CommandCenter.Tests/Unit/InventoryServiceReadBoundaryTests.cs > IInventoryService_SkuLookupMember_IsRemoved`; repo-wide grep: no caller of `GetProductBySkuAsync` remains (only the negative assertion and two legacy test-method names that call `GetProductQuickInfoAsync`) | COMPLIANT |
| REQ-ADB-07 | Field parity | `CommandCenter.Tests/Unit/SaleProductInfoDtoShapeTests.cs > SaleProductInfoDto_ExposesExactlyTheFifteenMembersSalesConsumes`, `> SaleProductInfoDto_IsSealed`, `> SaleProductInfoDto_EveryMemberIsInitOnly`; `Core/DTOs/SaleProductInfoDto.cs:3-19` | COMPLIANT |
| REQ-ADB-07 | Unknown cost stays null | `CommandCenter.Tests/Unit/SaleItemCostSnapshotTests.cs > AddItemAsync_WhenCatalogCostIsUnknown_PersistsNullUnitCost`; producer `Sales.Module/Services/SalesService.cs:246,287` and `SalesService.HoldOrders.cs:357` assign `UnitCostUSD = dto.CostPriceUSD` with no `?? 0m` | COMPLIANT |
| REQ-ADB-08 | System create returns the id | `CommandCenter.Tests/CashAdvanceTests.cs > CreateCashAdvanceSale_WhenCashierAndInfrastructureProductMissing_ProvisionsProductWithoutCatalogPermission` (asserts `saleItem.ProductId == provisioned.Id`, i.e. the returned `int` is the created id); `Inventory.Module/Services/InventoryService.ProductCrud.cs:66-81` | COMPLIANT |
| REQ-ADB-08 | Entity CRUD absent from interface | `Core/Interfaces/IInventoryService.cs:18` exposes only `CreateSystemProductAsync(CreateSystemProductRequest)`; `InventoryService.ProductCrud.cs:59,203` are `private`; `CommandCenter.Tests/Unit/ServicesContractRegressionTests.cs > IInventoryService_ContractIntegrity_AllPublicMethodsAreIntact` (interface/impl parity) plus the Release build (exit 0) enforce that no entity member is declarable; `InventoryServiceReadBoundaryTests.cs > IInventoryService_SkuLookupMember_IsRemoved` covers the same absence-inspection idiom for the read members. No dedicated reflection pin for the two write members exists (see SUGGESTION-1) | COMPLIANT |
| REQ-ADB-08 | Stale token still conflicts | `CommandCenter.Tests/Unit/ProductConcurrencyTokenTests.cs > UpdateProductFromDto_StaleToken_Conflicts` (Npgsql, `[Trait("Category","RequiresDocker")]`, gated on `TEST_POSTGRES_CONNECTION` — see WARNING-1); model pin `> InventoryModel_NpgsqlProvider_XminIsConfiguredAsConcurrencyToken`; tracked path intact at `InventoryService.ProductCrud.cs:207,346` (`FindAsync` + `CurrentValues.SetValues`) | COMPLIANT (gated) |
| REQ-ADB-09 | DI construction | `CommandCenter.Tests/Unit/ProductsControllerBoundaryTests.cs > ProductsController_DeclaresExactlyOnePublicConstructor`; `CommandCenter.Tests/Unit/ControllerConstructionTests.cs`; `Backend.API/Controllers/ProductsController.cs:25-31` (single `[ActivatorUtilitiesConstructor]` public ctor) | COMPLIANT |
| REQ-ADB-09 | Missing product is not an entity fallback | `CommandCenter.Tests/Unit/ProductsControllerBoundaryTests.cs > ProductsController_GetByIdAsync_MissingId_ReturnsNotFoundWithoutEntityFallback` (asserts 404 and `IInventoryService.VerifyNoOtherCalls()`); `ProductsController.cs:54-64` reads only `IProductManagementService.GetProductDtoByIdAsync` | COMPLIANT |
| REQ-ADB-10 | No entity in client signatures | `CommandCenter.Tests/Unit/ProductServiceQueryContractTests.cs > IProductService_NoMemberReferencesProductEntity`, `> GetByIdAsync_Shape_TakesIntIdAndReturnsProductDto`, `> CreateAsync_Shape_TakesCreateProductDtoAndReturnsProductDto`, `> UpdateAsync_Shape_TakesUpdateProductDto`; `Desktop.Client.Core/Services/IProductService.cs:1-24` and `ProductService.cs` reference only `Core.DTOs` | COMPLIANT |
| REQ-ADB-10 | StockQuantity survives create | `CommandCenter.Tests/Unit/ProductClientStockQuantityTests.cs > ProductDialogViewModel_Create_MapsStockQuantityIntoCreateProductDto` (asserts `ResultProduct.StockQuantity == 42m`) and `> ProductService_CreateAsync_SendsStockQuantityInRequestBody` (asserts `"stockQuantity":42` in the serialized body) | COMPLIANT |
| REQ-ADB-11 | Cashier never sees cost | `CommandCenter.Tests/Unit/ProductCostMaskingTests.cs` (6 facts: `GetAll`/`GetQuickInfo`/`GetSuggestions` cashier-vs-manager masking and recursive `MaskCosts` on variants); `ProductsController.cs:47-50,59-62,82` route every product DTO path through `CanMutateCatalog`/`MaskCosts` | COMPLIANT |
| REQ-ADB-11 | Cash-advance skips stock deduction | `CommandCenter.Tests/Unit/SalesServiceUnitTests.cs > CompleteSale_WithCashAdvanceProduct_DoesNotDeductStockOrThrow`; implementation `Sales.Module/Services/SalesService.Checkout.cs:296-301` (`continue` on `product.IsCashAdvance`) and `:381-382` (excluded from `SaleMadeEvent`) | COMPLIANT |

**Compliance summary**: 13/13 scenarios have covering tests that passed in the executed run
(one of them, REQ-ADB-08 / stale token, passed as a gated no-op — WARNING-1).

### Correctness (Static Evidence)

| Requirement | Status | Notes |
|------------|--------|-------|
| REQ-ADB-06 Inventory Read Boundary Is DTO-Only | Implemented | `IInventoryService` has no `using Core.Entities`; zero `Core.Entities.Product` in the file. Repo-wide grep for `Core.Entities.Product` finds hits only in EF migrations/model snapshots, `InventoryService.*` internals (entity-internal use, not a boundary), `Backend.API/Startup/DatabaseInitializer.cs`, and `InventoryService.Export.cs` — none on the reviewed boundaries. |
| REQ-ADB-07 SaleProductInfoDto Shape and Cost Semantics | Implemented | `sealed record`, 15 init-only members, `decimal? CostPriceUSD`; projection uses `(decimal?)p.CostPriceUSD` (`InventoryService.cs:62`) with no coercion. |
| REQ-ADB-08 Write Boundary and Concurrency Token | Implemented | Entity CRUD private behind DTO twins; `CreateSystemProductAsync` maps `StockQuantity` (`ProductCrud.cs:74`) and returns `created.Id` (`:80`); tracked `FindAsync` (`:207`) + `CurrentValues.SetValues` (`:346`) untouched; no `DbSet.Update`/`ExecuteUpdate` on the product update path. |
| REQ-ADB-09 Controller Uses DTO Twins Only | Implemented | Exactly one public constructor; both entity fallbacks and the compat ctor removed; `GetProductDtoByIdAsync`/`CreateProductFromDtoAsync` are the only read/create paths. |
| REQ-ADB-10 Client Boundary Uses DTOs | Implemented | `IProductService`/`ProductService` DTO-only; `ProductClientMapping.cs` bridges `ProductDto` <-> `Create/UpdateProductDto`; `StockQuantity` reaches `CreateProductDto`. |
| REQ-ADB-11 Preserved Invariants | Implemented | Masking on every DTO path; money fields remain `decimal`; `IsCashAdvance` present in the read model and honored at checkout. |

### Coherence (Design)

| Decision | Followed? | Notes |
|----------|-----------|-------|
| 1. New sealed `SaleProductInfoDto`, 15 init-only members (not `ProductQuickInfoDto`, not `ProductDto`) | Yes | Matches the design's interface contract exactly. |
| 2. DTO `CostPriceUSD` is `decimal?`; entity stays non-nullable; no `?? 0m` | Yes | Verified at producer and consumer sites. |
| 3. `CreateSystemProductAsync(request) -> Task<int>`; entity CRUD private | Yes | Confirmed in interface + implementation. |
| 4. One public ctor `(IInventoryService, IProductManagementService, ICurrentUserService)` | Yes | Confirmed; `[ActivatorUtilitiesConstructor]` retained. |
| 5. Dialog result `CreateProductDto ResultProduct` + `int ResultProductId` | Yes | Confirmed by the create-flow pin. |
| 6. WU-2 must not touch `UpdateProductAsync`'s `FindAsync` + `CurrentValues.SetValues` body | Yes | Tracked body unchanged; xmin token remains in the UPDATE predicate. |
| Documented deviation: WU-1/WU-2 pulled the `GetByIdAsync`/`CreateAsync` controller fallback removals forward (compile-forced) | Accepted | Consistent with "no entity leaves the interface"; the remaining controller work landed in WU-3 as planned. |
| Documented limitation: cash-advance synthetic line keeps `UnitCostUSD = 0m` | Accepted | `SalesService.CashAdvance.cs:132` — a known-zero service line, not the catalog "unknown cost" case. |

### Issues Found

**CRITICAL**: None.

**WARNING**:
1. `TEST_POSTGRES_CONNECTION` is unset in this environment, so the Npgsql-gated tests
   (`ProductConcurrencyTokenTests.UpdateProductFromDto_StaleToken_Conflicts`, and the
   `WebApplicationFactory` HTTP smoke tests) report as passed without executing their bodies. The
   stale-token conflict behaviour is therefore confirmed by static/design evidence plus the passing
   provider model pin, not by an executed Npgsql round-trip in this run. Pre-documented in
   `apply-progress.md` (WU-2, WU-3, Phase 5).
2. All WU-1..WU-4 + Phase 5 changes are uncommitted in a single worktree on branch `V0.15`
   (`git status --porcelain` = 65 entries, ahead of upstream by 37), so there is no per-PR
   isolation to verify; the chained-PR slices and `size:exception` approvals for PR1/PR4 remain a
   delivery-time step. Pre-documented in `apply-progress.md`.
3. The launch brief stated 12 delta scenarios; the delta spec contains 13 `#### Scenario:`
   headings. The authoritative heading count (13) is used in this report's totals.

**SUGGESTION**:
1. Add a dedicated reflection pin for the write side
   (`Assert.DoesNotContain("CreateProductAsync"/"UpdateProductAsync", IInventoryService member names)`)
   to mirror `InventoryServiceReadBoundaryTests.IInventoryService_SkuLookupMember_IsRemoved`; today
   the write-member absence is enforced by the Release build and `ServicesContractRegressionTests`
   parity rather than by an explicit absence assertion.
2. The legacy test method names `Phase1PerformanceOptimizationTests.GetProductBySkuAsync_*` no
   longer match their bodies (they exercise `GetProductQuickInfoAsync`); rename them to avoid a
   false grep hit when auditing the removed member.

### Verdict

PASS — Release build is 0 warnings / 0 errors and the full suite is 1322 passed / 0 failed /
0 skipped; all 6 delta requirements and all 13 delta scenarios map to implemented code and to
covering tests that passed in this run, with the two environment/delivery caveats reported as
warnings rather than defects.
