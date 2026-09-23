# Delta for api-dto-boundary

Adds the product entity boundary to this capability; REQ-ADB-01..05 are unchanged. Product read, write
and client contracts MUST be DTO-only.

## ADDED Requirements

### Requirement: REQ-ADB-06 Inventory Read Boundary Is DTO-Only

`IInventoryService` product reads MUST return the server-only `SaleProductInfoDto` and MUST NOT return
`Core.Entities.Product`: by-id (replacing `GetProductByIdAsync`), batch-by-ids (replacing
`GetProductsByIdsAsync`) and cash-advance (replacing `GetCashAdvanceProductAsync`). Reads MUST project in
the query. `GetProductBySkuAsync` MUST be removed from the interface.

#### Scenario: No entity crosses the read boundary
- GIVEN a persisted product
- WHEN Sales reads it by id, by batch, or as the cash-advance product
- THEN each result MUST be `SaleProductInfoDto`, not `Core.Entities.Product`

#### Scenario: SKU lookup member removed
- GIVEN the public `IInventoryService` members
- WHEN inspected
- THEN `GetProductBySkuAsync` MUST be absent

### Requirement: REQ-ADB-07 SaleProductInfoDto Shape and Cost Semantics

`SaleProductInfoDto` MUST be sealed and init-only and MUST expose exactly the fields Sales consumes:
`Id`, `Name`, `IsDeleted`, `IsActive`, `IsCashAdvance`, `PriceUSD`, `PriceBsS`, `PriceRetailUSD`,
`PriceWholesaleUSD`, `MinWholesaleQuantity`, `HasWholesale`, `IsGroupHeader`, `IsFractional`,
`UnitOfMeasure`, `CostPriceUSD`. `CostPriceUSD` MUST be nullable and MUST NOT be coerced to `0`.

#### Scenario: Field parity
- GIVEN the fields Sales read before this change
- WHEN a DTO is projected and served
- THEN each field MUST keep its prior type and value

#### Scenario: Unknown cost stays null
- GIVEN a sale line whose product cost is unknown
- WHEN the line is persisted
- THEN `SaleItem.UnitCostUSD` MUST be NULL, never `0`

### Requirement: REQ-ADB-08 Write Boundary and Concurrency Token

`CreateSystemProductAsync` MUST accept a server-only `CreateSystemProductRequest` and return `Task<int>`.
`CreateProductAsync`/`UpdateProductAsync` MUST NOT be exposed on `IInventoryService`; CRUD MUST be private
behind the DTO twins. Updates MUST keep the tracked `FindAsync` + `CurrentValues.SetValues` flow; no
detached-update path MAY be added, so the Npgsql `xmin` token stays in the UPDATE predicate.

#### Scenario: System create returns the id
- GIVEN a valid `CreateSystemProductRequest`
- WHEN `CreateSystemProductAsync` completes
- THEN it MUST return the created id as `int`

#### Scenario: Entity CRUD absent from interface
- GIVEN the public `IInventoryService` members
- WHEN inspected
- THEN `CreateProductAsync` and `UpdateProductAsync` MUST be absent

#### Scenario: Stale token still conflicts
- GIVEN a product changed concurrently after load
- WHEN the DTO-twin update saves
- THEN a concurrency exception MUST be raised

### Requirement: REQ-ADB-09 Controller Uses DTO Twins Only

`ProductsController` MUST expose exactly one public constructor and MUST NOT fall back to entity paths:
`GetProductDtoByIdAsync` and `CreateProductFromDtoAsync` MUST be the only read/create paths.

#### Scenario: DI construction
- GIVEN the DI container
- WHEN `ProductsController` is activated
- THEN exactly one public constructor MUST resolve

#### Scenario: Missing product is not an entity fallback
- GIVEN an id absent from the management service
- WHEN `GET` by id is called
- THEN it MUST return not-found, never an entity-derived product

### Requirement: REQ-ADB-10 Client Boundary Uses DTOs

`IProductService`/`ProductService` MUST use only `ProductDto`, `CreateProductDto` and `UpdateProductDto`
(`GetByIdAsync` to `ProductDto?`, `CreateAsync(CreateProductDto)`, `UpdateAsync(UpdateProductDto)`), and
the re-pointed ViewModels MUST map `StockQuantity` into `CreateProductDto`.

#### Scenario: No entity in client signatures
- GIVEN the public `IProductService` members
- WHEN inspected
- THEN none MUST reference `Core.Entities.Product`

#### Scenario: StockQuantity survives create
- GIVEN a create carrying a stock value
- WHEN the ViewModel maps it to `CreateProductDto`
- THEN `StockQuantity` MUST reach the request unchanged

### Requirement: REQ-ADB-11 Preserved Invariants

Cost masking via `CanMutateCatalog` (`MaskCosts`) MUST stay enforced on every product DTO path; money
fields MUST remain `decimal`; and `IsCashAdvance` MUST remain in the read model so checkout skips its
stock deduction.

#### Scenario: Cashier never sees cost
- GIVEN a user without `CanMutateCatalog`
- WHEN any product DTO is returned
- THEN cost fields MUST be masked

#### Scenario: Cash-advance skips stock deduction
- GIVEN a line whose `IsCashAdvance` is true
- WHEN checkout deducts stock
- THEN that line MUST be skipped
