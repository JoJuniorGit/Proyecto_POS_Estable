# Design: entity-boundary-cleanup

## Technical Approach

Four chained, server-first work units: WU-1 read model, WU-2 write boundary, WU-3 controller,
WU-4 WPF client. A server-only `SaleProductInfoDto` is projected in-query; entity members leave
`IInventoryService`; `ProductsController` entity fallbacks are deleted; the client uses
`ProductDto`/`CreateProductDto`/`UpdateProductDto`. Behavior-preserving, no migrations. Research
C1–C4 (purpose-specific minimal DTO), C7 (tracked `SetValues` keeps the token), C11/C15 (web JSON
defaults) support these choices.

## Architecture Decisions

| # | Decision | Rejected | Rationale |
|---|----------|----------|-----------|
| 1 | New `sealed record SaleProductInfoDto`, 15 init-only members | Widen `ProductQuickInfoDto`; reuse `ProductDto` | Quick-info is client-facing → new cost leak + `IsDeleted` always false (query filters it); `ProductDto` pulls recursive `Variants` → N+1 / wrong group totals (S9/S10). |
| 2 | DTO `CostPriceUSD` is `decimal?`; entity stays non-nullable | Widen entity to `decimal?` | Widening needs a migration (out of scope). Projection `(decimal?)p.CostPriceUSD` is never coerced to 0; a found row is always concrete, NULL means *no product resolved for the line*. Sales assigns `UnitCostUSD = dto.CostPriceUSD` with no `?? 0m`. Testable by reflection + snapshot. |
| 3 | `CreateSystemProductAsync(CreateSystemProductRequest) -> Task<int>`; entity `CreateProductAsync`/`UpdateProductAsync` private behind the DTO twins | Keep entity CRUD public | REQ-ADB-08 mandates private CRUD behind the twins; `InventoryService` is the only implementer. |
| 4 | One public ctor `(IInventoryService, IProductManagementService, ICurrentUserService)`, all required | Compat/optional ctor | DI always registers `IProductManagementService` (`ServiceCollectionExtensions.cs:41`); removing the null path deletes the last client entity route. |
| 5 | Dialog result is `CreateProductDto ResultProduct` + `int ResultProductId` | Either DTO alone | `CreateProductDto` has `StockQuantity`, no `Id`; `UpdateProductDto` has `Id`, no `StockQuantity`. |
| 6 | WU-2 must not touch `UpdateProductAsync`'s body (`FindAsync` + `CurrentValues.SetValues`) | Detached / `ExecuteUpdate` refactor | Only the tracked path puts `xmin` in the UPDATE predicate (C5/C7). |

## Data Flow

```
SalesService.{Pricing,HoldOrders,Checkout,AddItem,UpdateSaleItems}
  └─ GetSaleProductsByIdsAsync(ids) / GetSaleProductByIdAsync(id)
       └─ Products.AsNoTracking().Where(...).Select(ProjectionExpr)   // item.UnitCostUSD = dto.CostPriceUSD
CashAdvance ── GetCashAdvanceProductAsync().Id / CreateSystemProductAsync(request) -> int
InventoryViewModel ── CreateAsync(CreateProductDto) ──▶ POST /api/products
    ──▶ ProductsController ──▶ IProductManagementService ──▶ ProductDto
```

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Core/DTOs/SaleProductInfoDto.cs`, `CreateSystemProductRequest.cs` | Create | Server-only read model; 6-member write request |
| `Core/Interfaces/IInventoryService.cs` | Modify | 5 members out, 4 in; drop `using Core.Entities` |
| `Inventory.Module/Services/InventoryService.cs` | Modify | Projection + 3 DTO reads; delete `GetProductBySkuAsync` |
| `Inventory.Module/Services/InventoryService.ProductCrud.cs` | Modify | CRUD private; system create maps request → entity |
| `Sales.Module/Services/SalesService.cs`, `.Pricing/.HoldOrders/.Checkout/.CashAdvance.cs` | Modify | Batch reads; delete 4 fallback branches; system-create request |
| `Backend.API/Controllers/ProductsController.cs` | Modify | Single ctor; delete both entity fallbacks |
| `Desktop.Client.Core/Services/IProductService.cs`, `ProductService.cs` | Modify | DTO-only signatures |
| `Desktop.Client.Core/ViewModels/ProductDialogViewModel.cs` | Modify | DTO `ResultProduct`; write block `:410-487` re-mapped |
| `Desktop.Client.Core/ViewModels/VariantManagementViewModel.cs` | Modify | `Product` → DTO builds |
| `Desktop.Client.Core/ViewModels/InventoryViewModel.Operations.cs` | Modify | Drop `MapToDto(Product)`; DTO merge |
| `Desktop.Client.Core/Services/ProductClientMapping.cs` | Create | `ProductDto ↔ Create/UpdateProductDto` helpers |
| `CommandCenter.Tests/**` | Modify | ~60 files re-pointed |

## Interfaces / Contracts

`SaleProductInfoDto` (sealed record, init-only) exposes exactly: `Id`, `Name`, `IsDeleted`,
`IsActive`, `IsCashAdvance`, `PriceUSD`, `PriceBsS`, `PriceRetailUSD`, `PriceWholesaleUSD`,
`MinWholesaleQuantity`, `HasWholesale`, `IsGroupHeader`, `IsFractional`, `UnitOfMeasure`,
`decimal? CostPriceUSD`. `CreateSystemProductRequest` exposes `Name`, `SKU`, `PriceRetailUSD`,
`StockQuantity`, `IsCashAdvance`, `IsActive`.

```csharp
// IInventoryService
Task<IReadOnlyList<SaleProductInfoDto>> GetSaleProductsByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default);
Task<SaleProductInfoDto?> GetSaleProductByIdAsync(int id, CancellationToken ct = default);
Task<SaleProductInfoDto?> GetCashAdvanceProductAsync(CancellationToken ct = default);
Task<int> CreateSystemProductAsync(CreateSystemProductRequest request, CancellationToken ct = default);
// removed: GetProductsByIdsAsync, GetProductByIdAsync, GetProductBySkuAsync, CreateProductAsync, UpdateProductAsync

// IProductService (WPF)
Task<ProductDto?> GetByIdAsync(int id);
Task<ProductDto> CreateAsync(CreateProductDto dto);
Task UpdateAsync(UpdateProductDto dto);
```

The projection is one private `Expression<Func<Product, SaleProductInfoDto>>` in
`InventoryService.cs`, shared by batch/by-id/cash-advance. Sales consumes the DTO directly.

## Testing Strategy

| WU | Pins | Re-point |
|----|------|----------|
| WU-1 | `SaleProductInfoDto` reflection test (sealed, init-only, 15 fields, `CostPriceUSD` is `decimal?`); `SaleItemCostSnapshotTests` (unknown cost → NULL); `ProductCostMaskingTests` | 21 read-mock files |
| WU-2 | `ProductConcurrencyTokenTests`; new `UpdateProductFromDto_StaleToken_Conflicts` (Npgsql, gated on `TEST_POSTGRES_CONNECTION`); ADV-001 `CashAdvanceTests` | Entity-CRUD callers → DTO twins |
| WU-3 | Single-ctor reflection test; GET missing id → 404 | 9 constructions → `new ProductsController(inventory, Mock.Of<IProductManagementService>(), user)` |
| WU-4 | `StockQuantity` survives create; `ProductServiceQueryContractTests` extended (SEAM-01 bound: shape guards for `GetByIdAsync`/`CreateAsync`/`UpdateAsync`; query-binding guard untouched) | 12 `IProductService` mocks; 2 `GetProductBySkuAsync` files → `GetProductQuickInfoAsync` |

## Threat Matrix

N/A — no routing, shell, subprocess, VCS/PR automation, executable-file classification, or
process-integration boundary.

## Migration / Rollout

No migration. Chained PRs WU-1 → WU-4, server before client; each independently revertible. Gate:
`dotnet build CommandCenter.slnx -c Release` (0/0) + `dotnet test`.

## Open Questions

None blocking. Documented limitation: the cash-advance synthetic line keeps `UnitCostUSD = 0m`
(service line, known zero cost — not the catalog "unknown cost" case).
