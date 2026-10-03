# Design: Automated Price & Profitability Update via Supplier Invoice

## Technical Approach

Reuse the proven import stack end-to-end. The WPF client parses supplier files (ClosedXML / `System.Xml` / CSV) into staging DTOs; the backend performs supplier resolution, `pg_trgm` matching, and the atomic apply. Staging is persisted as DB draft entities in `Inventory.Module` (`InventoryDbContext`), so confirm is one all-or-nothing transaction across cost + margin + stock + `StockMovement`. All authoritative computation stays server-side (zero-trust); the client only recalculates the suggested price instantly from margin. Sales-history snapshots are untouched: this feature only mutates forward-looking catalog fields (`CostPriceUSD`, margins, derived prices, `StockQuantity`), never historical sale amounts. Money remains `decimal` only, derived prices via `PricingCalculator.RoundPriceUp`.

## Architecture Decisions

| # | Decision | Choice | Alternatives | Rationale |
|---|----------|--------|--------------|-----------|
| D1 | Mapping-template entity name | **`SupplierColumnMapping`** (used everywhere: entity, DbSet, DTO, spec) | `SupplierImportTemplate` | Spec scenarios already name it; one canonical name avoids drift across code/tests/UI. |
| D2 | Independent wholesale margin detection | **`product.HasWholesale == true`** | `ProfitMarginWholesale > 0`; `MinWholesaleQuantity > 0` | `HasWholesale` is the explicit product-level flag that wholesale pricing is active. Margin value or min-qty alone are not signals. Deterministic and directly testable. |
| D3 | Historical-margin pre-fill source | **`Product.ProfitMarginRetail`** (fallback `ProfitPercentage` while legacy) | `ProfitMarginWholesale`; average of both | Retail margin is primary (confirmed decision 5); mirrors existing `ProductDialogViewModel` fallback. |
| D4 | Ingestion split | Client parses files; backend re-validates + matches | Parse server-side | Mirrors `ProductImportService*` (client parse) / `InventoryService.Import.cs` (server re-validation). `pg_trgm` only exists in PostgreSQL, so matching must be server-side. |
| D5 | Matching engine | Barcode (`SKU`) exact → supplier code exact → `pg_trgm` similarity; threshold from `SystemSetting` | `ILIKE` name only; `Levenshtein` | Spec priority (9). Npgsql exposes pg_trgm via `EF.Functions`; threshold read from `SystemSettingsService` (cached). |
| D6 | Supplier-code storage | New `SupplierProductCode` join entity (`SupplierId`, `ProductId`, `Code`) | Add `SupplierCode` column to `Product` | `Product` has no supplier code today; a join table supports many suppliers per product and per-supplier codes without polluting `Product`. |
| D7 | Apply API shape | One `POST /api/supplier-invoices/{id}/confirm` (approved line ids + overrides) | per-line apply calls | Spec requirement; only atomicity preserves all-or-nothing cost/margin/stock. |
| D8 | Module placement | `Core.Entities` + `Inventory.Module` / `InventoryDbContext`, single migration stream | new `Suppliers.Module` | Confirmed decision 3. Keeps Product + StockMovement + invoice in one transaction/DbContext. |
| D9 | Draft persistence | DB draft `SupplierInvoice`/`SupplierInvoiceLine` | in-memory client state | Confirmed decision 2; crash-survivable, auditable, enables atomic confirm. |

**Line status rule (deterministic):** `ResolvedProductId == null` → `Conflict`; else `oldCost == 0` → `New`; else `newCost != oldCost` → `Update`; else `Unchanged`.

**Margin apply rule:** `newRetail = OverrideMarginRetail`. If `product.HasWholesale` → `newWholesale = OverrideMarginWholesale ?? product.ProfitMarginWholesale`; else `newWholesale = newRetail`. Pre-fill: retail = `ProfitMarginRetail` (fallback `ProfitPercentage`); wholesale = `ProfitMarginWholesale` when `HasWholesale`, else retail.

## Data Flow

**Ingestion → staging**

```
WPF VM ─ parse (ClosedXML/XmlReader/CSV) ─→ StageLineDto[]
   │ POST /api/supplier-invoices  (+ confirmed mapping)
   ▼
SupplierInvoicesController [Authorize(Admin,Manager)] ─→ SupplierInvoiceService.StageAsync
   ├─ resolve Supplier by RIF/NIT|name  → block if unmatched/ambiguous
   ├─ upsert SupplierColumnMapping (first import) / reuse
   ├─ match each line: SKU → SupplierProductCode → pg_trgm(threshold, tie-break)
   └─ persist draft SupplierInvoice + Lines (status, old→new cost, prefilled margin, IsApproved=true)
   ▼
SupplierInvoiceDetailDto ─→ staging grid (instant client recalc)
```

**Confirm → apply (atomic)**

```
grid (approved lines + overrides) ─ POST /{id}/confirm ─→ Controller [Authorize(Admin,Manager)]
   ▼
SupplierInvoiceService.ConfirmAsync
   CreateExecutionStrategy().ExecuteAsync:
     BeginTransaction
       reload invoice + lines (AsSplitQuery, AsNoTracking→tracked as needed)
       foreach approved line:
         re-validate server-side (cost/qty/margin ≥ 0, product exists & active)
         load Product (xmin) → CostPriceUSD, margins (retail primary)
         PriceRetailUSD/PriceWholesaleUSD = RoundPriceUp(cost*(1+margin/100))
         StockQuantity += qty → StockMovement (Reason, UserId)
       invoice.Status = Applied
     SaveChanges  ← DbUpdateConcurrencyException → retry fresh entity
     Commit
   any failure → Rollback (nothing written)
```

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Core/Entities/Supplier.cs`, `SupplierColumnMapping.cs`, `SupplierProductCode.cs`, `SupplierInvoice.cs`, `SupplierInvoiceLine.cs` | Create | Domain entities + `SupplierInvoiceStatus`/`SupplierInvoiceLineStatus`/`MatchMethod` enums |
| `Core/DTOs/Supplier*.cs` | Create | `StageSupplierInvoiceRequestDto`, `StageLineDto`, `SupplierInvoiceDetailDto`, `ConfirmSupplierInvoiceRequestDto`, `SupplierColumnMappingDto` |
| `Inventory.Module/Data/InventoryDbContext.cs` | Modify | New DbSets, indexes, unique filtered index (`IX_SupplierProductCodes_Supplier_Code`), `xmin` on `SupplierInvoice` |
| `Inventory.Module/Services/SupplierInvoiceService.cs` + `.Staging.cs`, `.Matching.cs`, `.Apply.cs`, `.Mapping.cs` | Create | Partial classes: stage, match, confirm |
| `Inventory.Module/Migrations/…_AddSupplierInvoice.cs` | Create | Additive migration |
| `Backend.API/Controllers/SupplierInvoicesController.cs` | Create | Stage / read / confirm endpoints |
| `Desktop.Client.Core/Services/SupplierInvoiceService.cs` + `.Parsing.cs` | Create | Parsing + API client |
| `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel.cs` + `.Staging.cs`, `.Pricing.cs` | Create | Grid, badges, instant recalc |
| `Desktop.Client/Views/SupplierInvoiceView.xaml` (+`.xaml.cs`) | Create | View (BindingProxy, virtualization) |
| `Desktop.Client/App.xaml.cs`, `MainWindow.xaml`, `Desktop.Client.Core/ViewModels/MainViewModel.cs` | Modify | DI, DataTemplate, nav entry |

## Interfaces / Contracts

```csharp
public enum SupplierInvoiceLineStatus { New, Update, Unchanged, Conflict }
public enum MatchMethod { Barcode, SupplierCode, Fuzzy, None }
public enum SupplierInvoiceStatus { Draft, Applied, Failed }

// staging read
public sealed record SupplierInvoiceDetailDto(
    int Id, int SupplierId, string Status, IReadOnlyList<SupplierInvoiceLineDto> Lines);

// confirm request — client-sent validity/prices are NEVER trusted
public sealed record ConfirmSupplierInvoiceRequestDto(IReadOnlyList<ConfirmLineDto> Lines);
public sealed record ConfirmLineDto(int LineId, bool IsApproved, decimal? MarginRetailOverride, decimal? MarginWholesaleOverride);
```

Endpoints: `GET /api/suppliers?rif=` · `POST /api/suppliers` · `POST /api/supplier-invoices` (stage) · `GET /api/supplier-invoices/{id}` · `POST /api/supplier-invoices/{id}/confirm`. All `[Authorize(Roles="Admin,Manager")]`, errors as `ProblemDetails`.

## Testing Strategy

| Layer | What to Test | Approach |
|-------|-------------|----------|
| Unit | Match priority/tie-breaks/threshold boundary; line-status rule; margin derivation (independent vs driven); pre-fill source; status classification | xUnit + in-memory `InventoryDbContext`; Moq pg_trgm via seeded names |
| Integration | Stage→read→confirm atomicity; rejected/`Conflict` untouched; RBAC 403 Cashier; forged `IsValid` rejected; rollback | `WebApplicationFactory`; Postgres-gated `xmin` retry via `TEST_POSTGRES_CONNECTION` |
| Client unit | Instant recalc `RoundPriceUp`; approval default ON; no per-keystroke API call; parse xlsx/csv/xml | xUnit + mocked `ISupplierInvoiceService`/`IFilePickerDialog` |
| Smoke | Migration applied, tables/indexes present | `MigratedSchema` smoke |

Coverage gates: Core ≥0.70, Sales ≥0.80 (unchanged), Inventory ≥0.72.

## Threat Matrix

N/A — no routing, shell, subprocess, VCS/PR automation, executable-file classification, or process-integration boundary. File ingestion is data parsing inside the app, not process execution.

## Migration / Rollout

Additive migration only (`Supplier*` tables); no existing column changes. Rollback: reverse the migration (drop supplier tables) and remove nav/DI registration + controller route to disable the feature. Product changes are reversible data: each line snapshots `OldCostPriceUSD`, `OldProfitMarginRetail`, `OldProfitMarginWholesale`, `OldStockQuantity`. Confirm is all-or-nothing, so no partial writes.

## PR Slicing

1. Domain + migration + DTOs (feature-dark). 2. Matching + staging service + stage/read endpoints. 3. Apply engine + confirm endpoint + RBAC. 4. WPF client + nav/DI. Apply `size:exception` only if a slice exceeds the 400-line review budget.

## Open Questions

None — the three spec open questions (D1 mapping name, D2 independent-wholesale detection, D3 pre-fill source) are resolved above with rationale.
