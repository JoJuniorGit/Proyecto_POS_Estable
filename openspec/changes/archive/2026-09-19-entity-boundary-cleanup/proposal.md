# Proposal: entity-boundary-cleanup

## Intent

Closes **AUD-10 (CRÍTICA)** (`docs/errores_vigentes.txt:187-201`). Mutable EF `Product` still crosses
product contracts and the WPF client after slice 1 (`60b0e7f`) closed `ISalesService`.

## Scope

### In Scope

- **WU-1 (2a reads)**: server-only `sealed record SaleProductInfoDto` (15 members incl.
  `CostPriceUSD`, `IsDeleted`); batch + cash-advance reads; re-point Sales; delete 4 fallback
  branches; ~21 tests. *(Large)*
- **WU-2 (2b writes)**: `CreateSystemProductAsync(CreateSystemProductRequest) -> Task<int>`; drop
  `CreateProductAsync`/`UpdateProductAsync` from `IInventoryService` (private helpers); ADV-001 tests.
  *(Medium)*
- **WU-3 (slice 3)**: remove both `ProductsController` entity fallbacks + compat ctor; 9 tests.
  *(Small)*
- **WU-4 (slice 4)**: DTO re-point of `IProductService`/`ProductService` + 3 ViewModels; partial
  SEAM-01 `ProductServiceQueryContractTests` extension; 12 tests. *(Large)*
- Delete `GetProductBySkuAsync` (zero production callers).

### Out of Scope

- AUD-21, AUD-13, Web.Frontend, slice 1 (closed).
- Research product choices (token-type, dto-granularity, strict-json): not scope.
- No `ProductQuickInfoDto` widening; no migrations.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `api-dto-boundary`: no EF `Product` crosses `IInventoryService` or client `IProductService`; DTOs
  are `sealed`/init-only.

## Approach

Adopt **A2+B+C+D1+E** as four chained work units, server-side first; each verifiable and revertible.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Core/DTOs/` | New | `SaleProductInfoDto`, `CreateSystemProductRequest` |
| `Core/Interfaces/IInventoryService.cs` | Modified | 7 entity members out |
| `Inventory.Module/Services/InventoryService*.cs` | Modified | New mappings; CRUD private |
| `Sales.Module/Services/SalesService*.cs` | Modified | Reads re-pointed |
| `Backend.API/Controllers/ProductsController.cs` | Modified | Fallbacks + ctor removed |
| `Desktop.Client.Core/{Services,ViewModels}/` | Modified | DTO re-point |
| `CommandCenter.Tests/**` | Modified | ~60 files re-pointed |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Cost masking / `UnitCostUSD` snapshot | Low | `ProductQuickInfoDto` untouched; `CostPriceUSD` exposed |
| `xmin` token lost | Low | WU-2 leaves tracked `FindAsync`+`SetValues` untouched; guard |
| `IsCashAdvance` stock-skip lost | Low | Field in DTO; checkout guards |
| `StockQuantity` create regression | Med | Map into `CreateProductDto` |
| Test churn dominates schedule | High | Per-WU re-point; chained PRs |
| Unused usings fail build | Med | `TreatWarningsAsErrors`; 0/0 gate |

## Rollback Plan

Behavior-preserving refactor; no migrations. Each WU is an isolated, independently revertible commit
(server WU-1/2 before client WU-4). Regression net: full suite + `ProductCostMaskingTests`,
`ProductConcurrencyTokenTests`, `SaleItemCostSnapshotTests`, `ControllerConstructionTests`.

## Dependencies

None added. Evidence base: `research.md` (revisions 2-3); residual G1/G2 is documented; no
detached-update path is touched.

## Affected Projects & Commands

- `CommandCenter.Tests` -> `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`
- Build -> `dotnet build CommandCenter.slnx --configuration Release`
- `Web.Frontend` NOT affected.

## Success Criteria

- [ ] Build 0 errors/0 warnings; `dotnet test` 100%.
- [ ] No `Core.Entities.Product` in `IInventoryService`/`IProductService`; no `ProductsController`
      fallback/ctor.
- [ ] Guard tests green; coverage Core >=0.70, Sales >=0.80, Inventory >=0.72.

**Workload**: >400 lines; split in `sdd-tasks`.
