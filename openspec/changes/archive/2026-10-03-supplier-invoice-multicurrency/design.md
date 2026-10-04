# Design: Supplier Invoice Multi-Currency & Hot Product Creation

## Technical Approach

Extend the 8.144 staging/apply stack. `SupplierInvoice` gains an immutable currency + applied-rate snapshot; line document costs are normalized to USD server-side (zero-trust) with `PricingCalculator.ToUSD` so classification, margins and suggested prices never drift with devaluation. Classification is reworked so unmatched lines become creation candidates (`[NEW]`) and zero-cost products become `[UPDATE]`; approval gates on a resolved product. Hot creation is one atomic server operation (product identity + supplier-code alias + line resolution) reusing `IProductManagementService.CreateProductFromDtoAsync`; cost/margin/price/stock still land only at confirm. The WPF client adds the header selector/rate, a per-row create action and a lightweight barcode-capture modal; the alias is also learned during confirm so future invoices auto-match. Money stays `decimal`; rates use ceiling-2dp; normalized costs use AwayFromZero-2dp.

## Architecture Decisions

| # | Decision | Choice | Alternatives | Rationale |
|---|----------|--------|--------------|-----------|
| D1 | Currency representation | String codes `"USD"` / `"Bs.S"` + decimal `AppliedRate` snapshot | New Core enum; reuse `PaymentMethodCurrencyResolver` | POS de-facto convention is string codes + rate snapshot (`Sale.AppliedRate`); `Sales.Module` constants are not referenceable from Core/Inventory and an enum would add a serialization contract for two values. |
| D2 | Normalization authority | Server-side in `StageAsync` (`ToUSD`, AwayFromZero 2dp) | Client converts before staging | Zero-trust precedent (server re-validates everything); matching/classification/margins must compare normalized USD values. |
| D3 | Document cost persistence | Persist both `UnitCostDocument` (numeric(18,2)) and `UnitCostUSD` | Only normalized | Audit fidelity: shows what the supplier actually billed; snapshot immutability guideline. |
| D4 | Classification rule | No product → `New`; else cost differs (incl. 0) → `Update`; equal → `Unchanged`; `Conflict` legacy | Keep `Conflict` for unmatched + UI-only relabel | Confirmed user decision (option 1): `[NUEVO]` is the single creation signal; zero-cost products are cost updates; approval gated by `ResolvedProductId`, not by status. |
| D5 | Creation scope | Identity only (name, `SKU = barcode`, alias, line resolved); cost/margins/prices/stock at confirm | Full pricing at creation | Preserves the module invariant "no apply before confirm"; the line reads `[UPDATE] 0.00 → cost`; single catalog-mutation point at confirm. |
| D6 | Creation API shape | Atomic `POST /api/supplier-invoices/{id}/lines/{lineId}/create-product` reusing `IProductManagementService` inside one transaction | Client orchestrates `POST /api/products` + separate link call | Atomicity (product + alias + line resolution), zero-trust validation, RBAC in one place; no partial state. |
| D7 | Barcode field | `Product.SKU` (unchanged schema); captured EAN/UPC 8–14 digits validated client+server | New `Product.Barcode` column | SKU is already the barcode in this system (`Matching.cs:43-55`, `IX_Products_SKU`); a new column would break barcode=Sku matching. (Assumption flagged: universal numeric codes.) |
| D8 | Alias semantics | Upsert `(SupplierId, Code)` on creation and on apply; last-write-wins toward the confirmed product | Insert-only; manual alias management | Requirement 5 (auto-recognition); confirm is human-reviewed, so the confirmed mapping is authoritative. |
| D9 | Rate rounding | `RoundExchangeRateCeiling` (2dp) on capture; `ToUSD` AwayFromZero-2dp | Store raw rate | §2.4 guideline: the rounded rate is the absolute reference; AwayFromZero for commercial rounding. |
| D10 | Modal shape | New lightweight `CreateInvoiceProductDialog` via `IDialogService` extension | Reuse full `ProductDialogViewModel` | The operation requires only barcode + name; `ProductDialog` is the full catalog form (stock, UoM, wholesale config) and its save path owns product creation — incompatible with the atomic line-resolve endpoint. |
| D11 | Migration | Additive columns + backfill (`Currency='USD'`, `AppliedRate=1`, `UnitCostDocument=UnitCostUSD`) | Data migration rewriting costs | Existing rows are USD-era invoices; backfill is exact, down-migration drops only the new columns. |
| D12 | DTO boundary | `StageLineDto.UnitCostDocument` (renamed); request carries `Currency`+`AppliedRate`; responses carry invoice `Currency`/`AppliedRate` and line `UnitCostDocument`+`UnitCostUSD` | Keep `UnitCostUSD` name for document costs | The old name would lie for Bs.S documents; both values are needed by the UI. |

**Line status rule (deterministic, replaces 8.144 rule):** `ResolvedProductId == null` → `New`; else `UnitCostUSD != Product.CostPriceUSD` (including `0`) → `Update`; else `Unchanged`. `Conflict` retained for stored data only.

**Approval rule:** `CanApprove => ResolvedProductId != null`. Confirm still protects server-side (skips lines without a resolved product regardless of client input).

**Creation rule:** validate draft + line ownership + unresolved + EAN/UPC (`^\d{8,14}$`) + name; create product with `CostPriceUSD = 0`, zero margins/prices/stock (`HasWholesale = false`); upsert alias when `SupplierCode` present; set `ResolvedProductId`, recompute classification (→ `[UPDATE] 0.00 → cost`) and clear stale snapshots.

## Data Flow

**Staging (multi-currency)**

```
WPF: currency selector + applied rate (prefilled from IExchangeRateService)
  → StageSupplierInvoiceRequestDto { Currency, AppliedRate, Lines[{..., UnitCostDocument}] }
  ▼
SupplierInvoiceService.StageAsync
  ├─ validate currency ∈ {USD, Bs.S}; Bs.S ⇒ rate > 0; rate = RoundExchangeRateCeiling (USD ⇒ 1)
  ├─ UnitCostUSD = Currency == USD ? UnitCostDocument : ToUSD(UnitCostDocument, rate)
  ├─ match product (unchanged: SKU → alias → pg_trgm)
  └─ classify: no product → New; else cost differs → Update; equal → Unchanged
  ▼
draft persisted { Currency, AppliedRate, UnitCostDocument, UnitCostUSD, Status }
```

**Hot creation (atomic)**

```
grid [NEW] line → modal (barcode EAN/UPC + name) → POST /{id}/lines/{lineId}/create-product
  ▼
CreateExecutionStrategy + transaction
  ├─ validations (RBAC, draft, ownership, unresolved, barcode, name)
  ├─ IProductManagementService.CreateProductFromDtoAsync(identity-only; cost 0, stock 0)
  ├─ upsert SupplierProductCode(SupplierId, SupplierCode) → new product
  ├─ line.ResolvedProductId = product.Id; recompute status/snapshots
  └─ commit → SupplierInvoiceDetailDto (grid refreshes; line approvable, [UPDATE] 0.00 → cost)
```

**Confirm (existing + alias learning)**

```
approved lines → ApplyApprovedLineAsync (unchanged pricing/stock/audit)
  + upsert SupplierProductCode for applied lines with SupplierCode (same transaction)
```

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Core/Entities/SupplierInvoice.cs`, `SupplierInvoiceLine.cs` | Modify | `Currency` (max 8, default `"USD"`), `AppliedRate` (18,4, default 1); `UnitCostDocument` (18,2) |
| `Core/DTOs/SupplierInvoiceDtos.cs` | Modify | Currency/rate on request+detail; `UnitCostDocument` rename/add; `CreateInvoiceProductRequestDto` |
| `Core/Interfaces/ISupplierInvoiceService.cs` | Modify | `CreateProductFromLineAsync` |
| `Inventory.Module/Data/InventoryDbContext.cs` | Modify | Column config |
| `Inventory.Module/Migrations/*_AddSupplierInvoiceMulticurrency.cs` | Create | Additive + backfill + Down |
| `Inventory.Module/Services/SupplierInvoiceService.{cs,Staging,Matching,Apply}.cs` | Modify | Mapping, normalization, classification, alias learning |
| `Inventory.Module/Services/SupplierInvoiceService.Creation.cs` | Create | Atomic create-product-from-line |
| `Backend.API/Controllers/SupplierInvoicesController.cs` | Modify | New endpoint |
| `Desktop.Client.Core/Services/SupplierInvoiceService*.cs`, `ISupplierInvoiceService.cs` | Modify | Document cost + creation call |
| `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel*.cs` | Modify | Header, gating, create command |
| `Desktop.Client.Core/ViewModels/CreateInvoiceProductDialogViewModel.cs` | Create | Modal VM (ObservableValidator) |
| `Desktop.Client/Views/SupplierInvoiceView.xaml`, `CreateInvoiceProductDialog.xaml(+.cs)` | Modify/Create | UI |
| `Desktop.Client.Core/Services/IDialogService.cs`, `Desktop.Client/Services/WpfDialogService*.cs` | Modify | `ShowCreateInvoiceProductDialog` |
| Tests: `SupplierInvoice{Staging,Matching,Apply,Client}Tests`, `SupplierInvoiceApplyIntegrationTests`, `SupplierInvoiceMigrationSmokeTests` (+ new creation tests) | Modify/Create | Coverage of new rules |

## Interfaces / Contracts

```csharp
// Request boundary (client-sent normalized values are NEVER trusted)
public sealed record StageSupplierInvoiceRequestDto(
    int? SupplierId, string? SupplierRifOrNit, string? SupplierCommercialName,
    SupplierColumnMappingDto? ColumnMapping,
    IReadOnlyList<StageLineDto> Lines,
    string Currency,      // "USD" | "Bs.S"
    decimal AppliedRate); // > 0 for Bs.S; forced to 1 for USD

public sealed record StageLineDto(
    string? SupplierCode, string? Barcode, string? Name,
    decimal Quantity, decimal UnitCostDocument); // document-currency cost

public sealed record CreateInvoiceProductRequestDto(string Barcode, string Name);

// Detail boundary
public sealed record SupplierInvoiceDetailDto(
    int Id, int SupplierId, string Status, string Currency, decimal AppliedRate,
    IReadOnlyList<SupplierInvoiceLineDto> Lines);
// SupplierInvoiceLineDto adds: UnitCostDocument (UnitCostUSD already exists)

// Endpoint
POST /api/supplier-invoices/{invoiceId}/lines/{lineId}/create-product  [Admin,Manager]
  → 200 SupplierInvoiceDetailDto | 400 validation | 403 RBAC | 404 not found | 409 duplicate SKU
```

Endpoint error mapping mirrors `ProductsController.CreateAsync` conventions (`ArgumentException` → 400, duplicate SKU `InvalidOperationException` → 409; writer verifies against the existing controller).

## Testing Strategy

| Layer | What to Test | Approach |
|-------|-------------|----------|
| Unit | Currency/rate validation; `ToUSD` normalization; document cost persisted; classification (unmatched → New, zero-cost → Update, equal → Unchanged); creation happy path + rejections (draft/ownership/resolved/barcode/name); alias upsert insert+update; apply alias learning; rollback leaves nothing | xUnit + in-memory `InventoryDbContext` (existing helpers) |
| Integration | Creation 403 Cashier via TestServer; SQLite transactional rollback; migration smoke (columns/types/backfill); Postgres-gated xmin path unaffected | `SupplierInvoiceApplyIntegrationTests`/`MigrationSmokeTests` patterns |
| Client unit | Parsing → `UnitCostDocument`; stage request carries currency/rate; `[NUEVO]`/`[UPDATE]` labels; `CanApprove` gating; rate required for Bs.S; create command + modal VM validation (barcode digits, no inheritance) | xUnit + mocked services (headless VM) |
| Regression | Full suite 1591+ green; existing 8.144 scenarios keep passing except the intentionally reclassified expectations | `dotnet test` |

Coverage gates: Core ≥0.70, Sales ≥0.80 (untouched), Inventory ≥0.72.

## Threat Matrix

N/A — no routing, shell, subprocess, VCS/PR automation, executable-file classification or process-integration boundary. RBAC reused unchanged (Admin/Manager both for staging and creation; Cashier blocked at controller class level).

## Migration / Rollout

Additive migration `AddSupplierInvoiceMulticurrency`: `SupplierInvoices.Currency` (max 8, not null, default `'USD'`), `SupplierInvoices.AppliedRate` (numeric(18,4), not null, default `1`), `SupplierInvoiceLines.UnitCostDocument` (numeric(18,2), not null, default `0`) with backfill `UnitCostDocument = UnitCostUSD`; Down drops the three columns. Feature is dark until UI/endpoint used; product creation is additive (new rows only). Existing drafts remain readable (legacy `Conflict` renders as before).

## PR Slicing

1. Domain + migration + DTOs (feature-dark; service mechanical rename only).
2. Staging: currency validation + normalization + classification (+ tests).
3. Hot creation + alias learning + endpoint + client API (+ tests).
4. WPF: header selector/rate, grid action/badges, modal (+ tests).
Apply `size:exception` only if a slice exceeds the 400-line review budget.

## Open Questions

None — the user confirmed the classification decision (option 1). The EAN/UPC 8–14-digit assumption is flagged in the specs (S4/D7) and can be relaxed later without schema impact.
