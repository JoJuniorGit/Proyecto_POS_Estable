# Exploration: entity-boundary-cleanup (AUD-10, slices 2-4)

## Context

Closes finding **AUD-10 (CRÍTICA)** — `docs/errores_vigentes.txt`: mutable EF entities
(`Core.Entities.Product`) exposed across service contracts and client boundaries.

- **Slice 1 — CLOSED** (`60b0e7f`): `ISalesService.CreateCashAdvanceSaleAsync` returns
  `SaleDto`; `ISalesService` (`Sales.Module/Interfaces/ISalesService.cs`) declares no entity
  type. The leftover `using Sales.Module.Entities;` (L2) only serves `SaleClaimAction`.
- **Guards — COMMITTED** (`25a3c84`): `ProductCostMaskingTests.cs` (7 tests),
  `ProductConcurrencyTokenTests.cs` (2 tests).
- This exploration validates and extends the prior read-only mapping, refreshed against
  current `HEAD 4bea704` (branch `V0.15`, clean tree).

Interfaces still exposing `Product` today:

| Contract | Members | file:line |
|---|---|---|
| `IInventoryService` | `GetProductsByIdsAsync` -> `List<Product>` | `Core/Interfaces/IInventoryService.cs:12` |
| | `GetProductByIdAsync` -> `Product?` | `:13` |
| | `GetCashAdvanceProductAsync` -> `Product?` | `:14` |
| | `GetProductBySkuAsync` -> `Product?` | `:16` |
| | `CreateProductAsync(Product)` -> `Product` | `:21` |
| | `CreateSystemProductAsync(Product)` -> `Product` | `:22` |
| | `UpdateProductAsync(Product)` | `:23` |
| `IProductService` (WPF client) | `GetByIdAsync` -> `Product?` | `Desktop.Client.Core/Services/IProductService.cs:9` |
| | `CreateAsync(Product)` -> `Product` | `:10` |
| | `UpdateAsync(Product)` | `:11` |

## Current State

### Crux — resolved and still valid
**No entity-returning `IInventoryService` member actually crosses an HTTP/client boundary in
production.** Every entity member has exactly one production consumer: `Sales.Module`
(server-internal, in-process). The only two client-facing uses are the *redundant* fallbacks in
`ProductsController` (`GET {id}` at `:68`, `POST` at `:108`), both shadowed by DTO twins
(`GetProductDtoByIdAsync`, `CreateProductFromDtoAsync`) and dead in production because DI always
registers `IProductManagementService` (`Backend.API/Startup/ServiceCollectionExtensions.cs:41`).

### Slice 2 — server-internal consumers and read union (verified)
Production call sites of entity members (`Sales.Module`):

| Consumer | Call sites |
|---|---|
| `SalesService.Pricing.cs` | `:140` `GetProductsByIdsAsync` (+ fallback `:150` `GetProductByIdAsync`) |
| `SalesService.HoldOrders.cs` | `:142` batch (+ fallback `:151`), `:316` batch (+ fallback `:325`) |
| `SalesService.Checkout.cs` | `:289` batch, `:436` batch (+ fallback `:445`) |
| `SalesService.cs` | `:185`, `:201`, `:256`, `:268` single-id `GetProductByIdAsync` |
| `SalesService.CashAdvance.cs` | `:40` `GetCashAdvanceProductAsync`, `:48` `CreateSystemProductAsync(new Product{...})` |

The mock-only fallback branches exist because tests configure only one of the two read methods
(explicit comment at `SalesService.Pricing.cs:147`). They are removable once tests are re-pointed
— **4 fallback branches** total (`Pricing:145-156`, `HoldOrders:147-154`, `HoldOrders:321-328`,
`Checkout(PopulateItemsMetadataAsync):441-448`).

**Read union of `Product` members consumed by Sales = 15 (CONFIRMED):**
`Id, Name, IsFractional, UnitOfMeasure, HasWholesale, MinWholesaleQuantity, PriceWholesaleUSD,
PriceRetailUSD, PriceUSD, PriceBsS, CostPriceUSD, IsDeleted, IsActive, IsCashAdvance,
IsGroupHeader`.
Evidence for the two "extra" ones beyond the obvious pricing set:
`CostPriceUSD` — snapshot writes at `SalesService.HoldOrders.cs:373` and `SalesService.cs:246,287`;
`IsDeleted` — `SalesService.cs:204` (`product.IsDeleted || !product.IsActive`).

`ProductQuickInfoDto` (`Core/DTOs/ProductQuickInfoDto.cs`) already carries 13 of these 15
(including `IsActive:18`, `IsCashAdvance:17`, `IsGroupHeader:26`); the only gaps are
`CostPriceUSD` and `IsDeleted`.

`CreateSystemProductAsync` consumer uses only `.Id` (`SalesService.CashAdvance.cs:57`).
`GetCashAdvanceProductAsync` consumer uses only `.Id` (`:44`).

### Slice 3 — controller fallbacks (line numbers still accurate)
- `ProductsController.cs:63-73`: `_productManagementService?.GetProductDtoByIdAsync` else entity
  fallback `GetProductByIdAsync` at `:68` + `product.ToDto(...)`.
- `ProductsController.cs:100-113`: `CreateProductFromDtoAsync` else entity fallback
  `CreateProductAsync` at `:108` + `entityCreated.ToDto(...)`.
- Both are only reachable when `IProductManagementService` resolves null. DI never does
  (`ServiceCollectionExtensions.cs:41`). The compat one-arg ctor
  (`ProductsController.cs:26-29`) is what makes the null path constructible — and 9 test
  constructions rely on it.

### Slice 4 — WPF client (bigger than the prior mapping)
`Desktop.Client.Core` still couples the catalog client to the entity:
- `ProductService.cs:18-51` (`GetByIdAsync`/`CreateAsync`/`UpdateAsync` deserialize/serialize
  `Product`, lines `:27,:32,:39,:45`).
- `ProductDialogViewModel.cs` — `_initialProduct` `:24`, `ResultProduct` `:29`, ctor `:103`,
  construction `:114`, read block `:116-141`, write block `:410-487`. (Mapping named only `:114`.)
- `VariantManagementViewModel.cs` — `GetByIdAsync` `:122`, `UpdateAsync` `:147`, `new Product{}`
  `:183`, `CreateAsync` `:231`.
- `InventoryViewModel.Operations.cs` — `:22/:29`, `:145`, `:167/:178`, `:207`, plus a hand-rolled
  `MapToDto(Product)` at `:261-292`. **This third consumer was not in the prior mapping.**

`Desktop.Client` (WPF app) already uses DTOs only (`AdjustStockDialog.xaml.cs:13`,
`WpfDialogService.Modals.cs:128,139,335,349,372`) — no change there.
`AddProductViewModel` / `AddProductView` are **gone** (no files on disk, `5620917`) — confirmed.

### Slice 1 / post-mapping deltas already absorbed
- `c796b99` added `CreateSystemProductAsync` at `IInventoryService.cs:22` and extracted
  `CreateProductCoreAsync` (`InventoryService.ProductCrud.cs:71`). `CreateProductAsync` keeps the
  `EnsureCatalogMutationPermission()` gate (`:61`); `CreateSystemProductAsync` bypasses it (`:66`).
- `5620917` added `CreateProductDto.StockQuantity` (`Core/DTOs/ProductManagementDtos.cs:45`) and
  `ToEntity` maps it (`ProductMappingExtensions.cs:71`) — the create path preserves initial stock.
- `ISalesService` slice 1 verified closed as described above.

## Affected Areas

### Slice 2 (server-internal contract + read model)
Production:
- `Core/DTOs/` — **new** server-only read-model DTO (e.g. `SaleProductInfoDto.cs`).
- `Core/Interfaces/IInventoryService.cs` — replace/remove 7 entity members.
- `Inventory.Module/Services/InventoryService.cs` — `:45/:52/:57` reads, `:98` `GetProductBySkuAsync`.
- `Inventory.Module/Services/InventoryService.ProductCrud.cs` — `:59/:66/:191` create/update/system.
- `Sales.Module/Services/SalesService.cs` — 4 single-id sites.
- `Sales.Module/Services/SalesService.Pricing.cs` — batch + drop fallback.
- `Sales.Module/Services/SalesService.HoldOrders.cs` — 2 batch + drop 2 fallbacks.
- `Sales.Module/Services/SalesService.Checkout.cs` — 2 batch + drop fallback.
- `Sales.Module/Services/SalesService.CashAdvance.cs` — lookup + system-create.

Tests (**21 files** set up `GetProductsByIdsAsync`/`GetProductByIdAsync`; **36 files** mock
`IInventoryService` overall): `PriceListTests.cs`, `PriceListTests.Wholesale.cs`,
`OnHoldSalesTests.cs`, `OnHoldSalesTests.Liquidation.cs`, `CheckoutAndPaymentTests.cs`,
`HoldOrderClaimTests*.cs`, `CashAdvanceTests.cs`, `Sprint1/2/3PerformanceOptimizationTests.cs`,
`Unit/PerformanceTests.cs`, `Unit/SalesServiceUnitTests.cs`, `Unit/SaleItemCostSnapshotTests.cs`,
`Unit/ProductVariantsTests.{AdjustStock,ConversionFactor,IndependentPricing,VariantManagement}.cs`,
`Integration/{SaleFlow,LiquidationFlow,PartialPaymentFlow,EditHoldOrderFlow}IntegrationTests.cs`,
etc.
`GetProductBySkuAsync` has **zero production callers**; only 2 test files reference it:
`Unit/Phase1PerformanceOptimizationTests.cs` (9 refs) and
`Unit/ProductVariantsTests.IndependentPricing.cs` (2 refs).

### Slice 3 (controller fallbacks)
Production: `Backend.API/Controllers/ProductsController.cs` (`:26-37`, `:63-73`, `:95-113`).
Tests: **9** `ProductsController` constructions through the compat one-arg ctor —
`Unit/ProductCostMaskingTests.cs` (`:31,65,97,116,135,158`) and
`Unit/ProductVariantsTests.VariantManagement.cs` (`:259,305,331`). `Unit/BsPriceCeilingStandardTests.cs`
already uses the 3-arg ctor (`:126,217`) and is unaffected.

### Slice 4 (WPF catalog client)
Production: `Desktop.Client.Core/Services/IProductService.cs`, `Services/ProductService.cs`,
`ViewModels/ProductDialogViewModel.cs`, `ViewModels/VariantManagementViewModel.cs`,
`ViewModels/InventoryViewModel.Operations.cs`.
Tests: **12 files** mock `IProductService` — `InventoryPaginationTests.cs`,
`InventorySortingTests.cs`, `InventoryWholesaleVisibilityTests.cs`, `MainViewModelDisposeTests.cs`,
`PosHotkeysTests.cs`, `Unit/{CheckoutCompletionUxTests,InventoryCatalogRefreshTests,
Phase3DesktopOptimizationTests,PosSearchFailureSurfaceTests,PosSearchSuggestionsTests,
PosViewModelPaymentMethodsDeduplicationTests,PosViewModelRecoveryTests}.cs`.

## Approaches

### A. Read-model for the Sales read path (slice 2)

1. **Widen `ProductQuickInfoDto` with `CostPriceUSD` + `IsDeleted`** — reuse the existing
   `GetProductsByIdsAsync(sku)…`/quick-info mapping.
   - Pros: zero new types; one mapping site (`InventoryService.cs:259-290`).
   - Cons: `ProductQuickInfoDto` is **client-facing** (`ProductsController` `quick-check/{sku}` and
     `suggestions`, consumed by WPF POS). Adding `CostPriceUSD` creates a new cost-leak surface:
     `MaskQuickInfoDto` (`ProductsController.cs:268-297`) and `ProductCostMaskingTests` (which
     asserts full-field survival through that copy) must both change, and every future endpoint
     returning it must mask. Semantics are also wrong: the sales read path needs `IsDeleted`, but
     the quick-info query filters `!p.IsDeleted` (`:264`), so the field would always be false.
   - Effort: Medium. **Not recommended.**

2. **New server-only `sealed record SaleProductInfoDto`** in `Core.DTOs` carrying exactly the 15
   members, returned by dedicated `IInventoryService` read member(s).
   - Pros: no client DTO/masking change; masking guard stays untouched; explicit minimal server
     contract; allows deleting the 4 mock-fallback branches and shrinking the interface; `IsDeleted`
     is meaningful (batch read does not filter deleted rows).
   - Cons: new type + new mapping + test re-points.
   - Effort: Medium.

3. **Reuse `ProductDto`** (already has all 15 + `MaskCosts`).
   - Pros: no new type.
   - Cons: `ProductDto` is the heavyweight catalog DTO — includes a recursive `Variants` list and
     derived `VariantCount`/`ConsolidatedStock` that assume eager loading
     (`ProductMappingExtensions.cs:44-48`). Using it on hot checkout paths either triggers N+1 or
     produces wrong group-header counts; couples sales to catalog DTO churn.
   - Effort: Low change, High risk. **Rejected.**

### B. Create/update input shape (slice 2)
- `CreateProductAsync(Product)` / `UpdateProductAsync(Product)` have **no external production
  caller** once slice 3 lands (only internal `ProductCrud.cs:28`/`:45`). → Remove them from
  `IInventoryService` and keep the logic as private helpers behind the `*FromDtoAsync` twins.
- `CreateSystemProductAsync(Product)` is called cross-module by Sales with only 6 fields and uses
  only `.Id`. → Replace with a small server-only request DTO
  (`record CreateSystemProductRequest(string Name, string SKU, decimal PriceRetailUSD, decimal
  StockQuantity, bool IsCashAdvance, bool IsActive)`) returning `Task<int>`.
   - Alternatives: reuse `CreateProductDto` (couples to client validation attributes) or return
     `SaleProductInfoDto` (unneeded). `Task<int>` is the minimal contract.

### C. `GetProductBySkuAsync`
Zero production callers and already `[EditorBrowsable(Never)]`. **Delete the interface member**
and re-point the 2 test files to `GetProductQuickInfoAsync` (shares the same cache-invalidation
behavior via `InvalidateProductSkuCache`).

### D. Slice 3 — controller fallbacks
1. **Remove both fallback branches + make `IProductManagementService` a required ctor dependency**
   (drop the compat one-arg ctor at `:26-29`).
   - Pros: deletes the last client-facing entity path; removes the null-shape that the AUD-11 guard
     philosophy dislikes; net code reduction.
   - Cons: 9 test constructions must gain an `IProductManagementService` mock.
   - Effort: Low-Medium.
2. **Keep the ctor, drop only the entity fallback** (return 404/500 when null).
   - Pros: no test ctor churn.
   - Cons: keeps a "service unavailable" path that cannot happen in production — a hidden
     behaviour change for tests, and leaves dead code.
   - Effort: Low. **Not recommended.**

### E. Slice 4 — WPF client contract
`IProductService` -> `Task<ProductDto?> GetByIdAsync(int)`, `Task<ProductDto> CreateAsync(CreateProductDto)`,
`Task UpdateAsync(UpdateProductDto)`. This also removes a latent wire mismatch (today the client
deserializes `Product` from a `ProductDto` payload purely by field-name coincidence).
`ProductDialogViewModel.ResultProduct` becomes `CreateProductDto`/`UpdateProductDto`; the
`ProductDto`-returning `GetByIdAsync` means `InventoryViewModel.Operations.MapToDto(Product)` is
deleted. `VariantManagementViewModel` maps `ProductDto` -> `UpdateProductDto` and builds
`CreateProductDto` for new variants.
- Pros: closes the client boundary, aligns client/server DTOs.
- Cons: `ProductDialogViewModel` write block (`:410-487`) is a real rewrite; must preserve
  `StockQuantity`, `IsCashAdvance`, `GroupKey`, `ConversionFactor`, `ParentProductId`,
  `IsGroupHeader/IsStockShared/HasIndependentPricing`. `CreateProductDto` has no `Cost` /
  `ProfitPercentage` / `CreatedAt` mirror fields — those client writes are dropped (the server never
  reads them; `ToEntity`/`UpdateFromDto` ignore them). Dead `Product.ReservedQuantity` writes must
  go too.
- Effort: Medium-High.

### F. SEAM-01 opportunistic note (proposal decision, not scope)
`ProductServiceQueryContractTests.cs` currently guards only `GetPagedAsync` (query-param binding +
camelCase payload shape). Slice 4 touches the same client service; extending the guard to
`GetByIdAsync`/`CreateAsync`/`UpdateAsync` (response/request shape vs controller DTO types) would
give partial coverage for **SEAM-01 (ALTA, active)**. Flag as an explicit proposal decision —
do not expand scope silently.

## Recommendation

Adopt **A2 + B + C + D1 + E**, delivered as chained work units:

1. **WU-1 (slice 2a — reads):** add `SaleProductInfoDto`; add one batch read
   `GetSaleProductsByIdsAsync` and `GetCashAdvanceProductIdAsync`; re-point all Sales read sites;
   delete the 4 fallback branches; delete entity read members (`GetProductsByIdsAsync`,
   `GetProductByIdAsync`, `GetCashAdvanceProductAsync`, `GetProductBySkuAsync`); re-point 21 test
   files. *(Large)*
2. **WU-2 (slice 2b — writes):** `CreateSystemProductAsync(CreateSystemProductRequest) -> int`;
   remove `CreateProductAsync`/`UpdateProductAsync` from the interface (private behind the DTO
   twins); re-point `CashAdvanceTests` / ADV-001 coverage. *(Medium)*
3. **WU-3 (slice 3):** remove both `ProductsController` fallbacks + compat ctor; re-point 9 test
   constructions. *(Small)*
4. **WU-4 (slice 4):** DTO re-point of `IProductService`/`ProductService` + the 3 ViewModels;
   re-point 12 test files. Optionally extend `ProductServiceQueryContractTests` (SEAM-01).
   *(Large)*

`SaleProductInfoDto` as `sealed record` (init-only) matches the repo precedent
(`ICashDrawerService`, ANEXO 8.140) and the clean-architecture skill's DTO isolation rule.

This ordering keeps each work unit independently verifiable and lets slices 2/3 land server-side
before the client churn.

## Risks

- **Cost masking (invariant):** Option A2 avoids touching `ProductQuickInfoDto`/`MaskQuickInfoDto`,
  keeping `ProductCostMaskingTests` semantically stable. Any late decision to widen the quick-info
  DTO instead re-opens a Cashier-visible cost leak and forces new masking + a new guard.
- **`SaleItem.UnitCostUSD` snapshot:** the new read model MUST expose `CostPriceUSD` so
  `HoldOrders.cs:373` / `SalesService.cs:246,287` keep capturing the real cost (NULL = unknown,
  never 0). A forgotten field silently zeroes cost history.
- **`xmin` concurrency token:** only the tracked `FindAsync` + `CurrentValues.SetValues` path
  (`InventoryService.ProductCrud.cs:191-389`) preserves it. WU-2 must demote
  `UpdateProductAsync` without touching its body, and the DTO twins must keep calling it.
- **Stock-deduction `IsCashAdvance` skips:** `Checkout.cs:298`, `HoldOrders.cs:159,382` rely on the
  read model's `IsCashAdvance`; a dropped field would create cash-advance stock deductions.
- **Initial stock regression:** `ProductDialogViewModel.cs:474` writes `ResultProduct.StockQuantity`
  on create — the exact regression `5620917` fixed. WU-4 must map it into `CreateProductDto`
  (which now has `StockQuantity`, `ProductManagementDtos.cs:45`).
- **Create/update validation parity:** `CreateProductDto`/`UpdateProductDto` carry
  `[Required]`/`[Range]` attributes; the dialog's own `ObservableValidator` range rules must not
  diverge from them.
- **Test churn dominates:** ~60 test files across slices (36 mock `IInventoryService`, 12 mock
  `IProductService`, 21 set up the read methods, 9 construct `ProductsController`). The real cost
  and the main schedule risk is re-pointing tests, not production code.
- **`TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`:** unused `using Core.Entities;` in the
  re-pointed interfaces/services will fail the build.
- **Scope creep:** SEAM-01, AUD-21, AUD-13 and web/frontend stay out.

## Mapping corrections / deltas (slices 2-4)

1. **AUD-10's registry entry omits `IInventoryService.cs:23` (`UpdateProductAsync`)**, though the
   orchestrator scope includes it. `c796b99` inserted `CreateSystemProductAsync` at L22, shifting
   `UpdateProductAsync` to L23; the audit text still lists "12, 13, 14, 16, 21, 22". Slice 2 must
   cover L23 (and remove it, per B).
2. **Slice 4 churn was understated.** The prior mapping named `ProductDialogViewModel.cs:114` +
   `VariantManagementViewModel.cs:183`; the real set adds `InventoryViewModel.Operations.cs`
   (6 call sites + a hand-rolled `MapToDto(Product)` at `:261-292`) and the full
   `ProductDialogViewModel` entity surface (`_initialProduct`/`ResultProduct` types + `:116-141`
   read block + `:410-487` write block).
3. **Slice 3 fallback lines confirmed** at `ProductsController.cs:68` (GET) and `:108` (POST) —
   still accurate.
4. **`ProductQuickInfoDto` gap confirmed** (missing only `CostPriceUSD` + `IsDeleted`); note the
   quick-info query filters `!p.IsDeleted`, so widening it for the server read would also be
   semantically wrong.
5. **Slice 3 test coupling discovered:** 9 `ProductsController` constructions use the compat
   one-arg ctor (6 in `ProductCostMaskingTests`, 3 in `ProductVariantsTests.VariantManagement`).
   Removing the ctor changes those files.
6. **`GetProductBySkuAsync` test coverage localized** to `Phase1PerformanceOptimizationTests.cs`
   and `ProductVariantsTests.IndependentPricing.cs` (zero production callers).
7. **`CreateProductAsync`/`UpdateProductAsync` can be deleted from the interface** after slice 3 —
   no external production caller remains; they become private helpers.
8. **`AddProductViewModel`/`AddProductView` deletion confirmed** (`5620917`) — nothing to plan.
9. **Slice 1 confirmed closed:** no entity member in `ISalesService`.

## Ready for Proposal

**Yes.** The proposal should lock: (a) the new `SaleProductInfoDto` (not widening
`ProductQuickInfoDto`), (b) `Task<int>` + `CreateSystemProductRequest` for the system create,
(c) deletion of `GetProductBySkuAsync`, (d) whether WU-3 removes the compat ctor, and
(e) whether SEAM-01's contract-test extension is in or out. Delivery must be **chained PRs**:
each work unit is well under 400 authored lines except WU-1 and WU-4, which individually
risk the budget and should be reviewed as separate slices.
