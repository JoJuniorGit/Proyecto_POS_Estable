# Design: Custody Partial Deliveries (Retiros Pendientes)

## Technical Approach

Extend the Sales module end-to-end without touching money or stock. A custody sale is a `Completed` sale with `DeliveryStatus ∈ {PendingPickup, PartiallyDelivered}`; its `SaleItem`s carry a delivered counter (`DeliveredQuantity`), and every confirmed withdrawal appends an immutable event (`SaleDelivery` + `SaleDeliveryItem`). The partial delivery runs inside the existing `CreateExecutionStrategy` + `ReadCommitted` transaction pattern; `xmin` on `SaleItem` prevents concurrent double-counting, and the controller layers `Idempotency-Key` on top (reusing `ResolveIdempotencyAsync`). The delivery note reuses the `Sales.Module/Receipts` PDF stack and is served synchronously so both clients can fetch bytes with their existing authenticated HTTP clients. Financial snapshots (`TotalUSD`, `TotalBsS`, `AppliedRate`, `FinalPaidAmountBsS`, payments) and inventory are never written by this feature; stock remains deducted in full at checkout.

## Architecture Decisions

| # | Decision | Choice | Alternatives | Rationale |
|---|----------|--------|--------------|-----------|
| D1 | Delivery state model | Append `PartiallyDelivered = 2` to `SaleDeliveryStatus` | New column/table | Stored ints stay stable; `SaleHistoryDto.DeliveryStatus` already serializes the enum name; binary clients keep working. |
| D2 | Delivered quantity storage | `SaleItem.DeliveredQuantity numeric(18,3)` default 0 (pending computed) | Ledger-only, compute pending per query | Fast reads for list/DTO; the append-only log stays the source of truth and the counter is rebuildable. |
| D3 | Audit model | Two entities: `SaleDelivery` (header) + `SaleDeliveryItem` (lines) | Single denormalized row | Matches the AC (event header + per-item detail); supports the delivery note and future queries per product. |
| D4 | Concurrency | `xmin` shadow token on `SaleItem` (mirror `Sale` config) + single transaction; conflict → `409`, no auto-retry | Auto-retry loop; raw SQL `FOR UPDATE` | A retry could silently apply stale quantities; the cashier must re-enter against refreshed pending values. `xmin` is the proven pattern in this repo. |
| D5 | Duplicate submissions | Required `Idempotency-Key`, reuse `ResolveIdempotencyAsync` (replay → stored JSON; mismatch → 422) | Natural-key dedupe | Same contract as `complete`; the store already exists. |
| D6 | Legacy endpoint | `ConfirmPickupAsync` delegates to the delivery core with all remaining quantities | Keep old flag flip + separate new path | Preserves the public contract and existing tests while making the fixture auditable. |
| D7 | Pending list read model | New `PendingPickupItemDto` (server) with `SaleItemId`, total/delivered/pending; `PendingPickupDto` gains aggregate units | Extend shared `SaleItemHistoryDto` | Avoids touching the history contract; the UI needs the item id to send quantities back. |
| D8 | Receipt transport | Synchronous PDF endpoint (`GET .../deliveries/{deliveryId}/receipt`) built by a new `DeliveryNotePdfGenerator` in `Sales.Module/Receipts` | Reuse async print queue; client-side HTML | The note is fetched on demand, not part of the checkout print queue; Desktop already opens PDFs via `Process.Start`, Web can open an authenticated blob. |
| D9 | Module placement | `Sales.Module/Entities` + `SalesDbContext` + one migration stream; service partial `SalesService.Deliveries.cs` | New module | Sale-side data, same transaction and DbContext as the counters it mutates. |
| D10 | API validation ordering | Controller authorizes + idempotency; service validates custody state, membership, pending limits | Validate at controller only | Zero-trust: business invariants live with the data. |

## Data Flow

**Partial delivery**

```
WPF/Web modal (per-line quantities, capped at pending)
  │ POST /api/sales/{id}/deliveries  + Idempotency-Key
  ▼
SalesController.Deliveries [Authorize] → IsAuthorizedForSaleAsync → Driver blocked
  ├─ ResolveIdempotencyAsync (replay → stored receipt JSON; mismatch → 422)
  ▼
SalesService.DeliverPartialAsync  (execution strategy + transaction)
  ├─ load sale tracked (+Items) → guard Status==Completed && custody state
  ├─ EnsureHoldClaimAccess
  ├─ per line: exists in sale? qty > 0? qty <= (Quantity - DeliveredQuantity)? no dupes?
  ├─ item.DeliveredQuantity += qty
  ├─ append SaleDelivery + lines (UtcNow, acting user id + name snapshot)
  ├─ all pending == 0 ? Delivered + PickupDate : PartiallyDelivered
  └─ SaveChanges (xmin on Sale/SaleItem) → commit
  ▼
DeliveryReceiptDto → success UI → "Imprimir Nota de Despacho"
  └─ GET .../deliveries/{deliveryId}/receipt → PDF (from log data)
```

**Backfill** (migration, one-time): `UPDATE "SaleItems" SET "DeliveredQuantity" = "Quantity" FROM "Sales" WHERE ... "DeliveryStatus" = 0` — delivered sales get full counters; custody sales stay 0.

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Sales.Module/Entities/SaleDeliveryStatus.cs` | Modify | Add `PartiallyDelivered = 2` |
| `Sales.Module/Entities/SaleDelivery.cs`, `SaleDeliveryItem.cs` | Create | Event header + line entities |
| `Sales.Module/Entities/SaleItem.cs` | Modify | `DeliveredQuantity` counter |
| `Sales.Module/Data/SalesDbContext.cs` | Modify | DbSets, precision/FK/index config, `xmin` on `SaleItem` |
| `Sales.Module/Migrations/…_AddCustodyPartialDeliveries.cs` | Create | Additive migration + backfill + `Down` |
| `Sales.Module/DTOs/PendingPickupDto.cs`, `PendingPickupItemDto.cs`, `PartialDeliveryDtos.cs` | Modify/Create | Read model + request/receipt contracts |
| `Sales.Module/Services/SalesService.Deliveries.cs` | Create | `DeliverPartialAsync` core |
| `Sales.Module/Services/SalesService.History.cs` | Modify | Pending filter + item mapping; `ConfirmPickupAsync` delegates |
| `Sales.Module/Interfaces/ISalesService.cs` | Modify | New signatures |
| `Sales.Module/Receipts/DeliveryNotePdfGenerator.cs` | Create | Delivery-note PDF from log data |
| `Backend.API/Controllers/SalesController.Deliveries.cs` | Create | Deliveries + note endpoints |
| `Desktop.Client.Core/Services/ISalesService.cs`, `SalesService.cs`, `IDialogService.cs` | Modify | Client contracts + HTTP calls; dialog hooks |
| `Desktop.Client.Core/ViewModels/PendingPickupsViewModel.cs`, new `PartialDeliveryDialogViewModel.cs` | Modify/Create | Dispatch flow + progress |
| `Desktop.Client/Views/PendingPickupsView.xaml`, new `PartialDeliveryDialog.xaml(.cs)` | Modify/Create | Badge/progress + modal |
| `Web.Frontend/src/services/pendingPickupApi.js`, `src/pages/PendingPickupsPage.jsx`, `src/utils/deliveryProgress.js` | Modify/New | API + modal + badge/progress + blob print |
| `CommandCenter.Tests/…` | Create/Modify | Service, controller, migration, VM, generator, web helper tests |
| `docs/reporte.txt` | Modify | ANEXO 8.145 |

## Interfaces / Contracts

```csharp
public enum SaleDeliveryStatus { Delivered = 0, PendingPickup = 1, PartiallyDelivered = 2 }

public sealed class SaleDelivery
{
    public int Id { get; set; }
    public int SaleId { get; set; }
    public DateTime DeliveredAt { get; set; }            // UTC
    public int? DeliveredByUserId { get; set; }
    public string DeliveredByName { get; set; } = string.Empty;   // snapshot
    public string? Notes { get; set; }
    public List<SaleDeliveryItem> Items { get; set; } = new();
}

// request (controller → service)
public sealed class PartialDeliveryRequest  { public List<PartialDeliveryItemRequest> Items { get; set; } = new(); public string? Notes { get; set; } }
public sealed class PartialDeliveryItemRequest { public int SaleItemId { get; set; } public decimal Quantity { get; set; } }

// reply / idempotency-replayed body
public sealed class DeliveryReceiptDto
{
    public int DeliveryId { get; set; }  public int SaleId { get; set; }  public int? InvoiceNumber { get; set; }
    public DateTime DeliveredAt { get; set; } public string DeliveredByName { get; set; } = "";
    public string? CustomerName { get; set; } public string? CustomerCedula { get; set; }
    public string DeliveryStatus { get; set; } = "";     // resulting state token
    public decimal TotalUnits { get; set; } public decimal DeliveredUnits { get; set; } public decimal PendingUnits { get; set; }
    public List<DeliveryReceiptItemDto> Items { get; set; } = new();
}

// service surface
Task<DeliveryReceiptDto> DeliverPartialAsync(int saleId, IReadOnlyList<(int SaleItemId, decimal Quantity)> items, string? notes, int? actingUserId, CancellationToken ct = default);
byte[] GetDeliveryNotePdfAsync(int saleId, int deliveryId, CancellationToken ct = default);
```

Endpoints: `POST /api/sales/{id}/deliveries` (Idempotency-Key required) · `GET /api/sales/{id}/deliveries/{deliveryId}/receipt` (`application/pdf`) · existing `POST /api/sales/{id}/confirm-pickup` unchanged in shape. Errors: `400` validation, `403` authorization/Driver, `404` unknown ids, `409` concurrency conflict, `422` idempotency mismatch — all ProblemDetails.

DTO tokens stay machine-readable (`PendingPickup`, `PartiallyDelivered`, `Delivered`); UI localizes to Spanish labels. Exact validation messages (Spanish, used in tests): over-pending → `"La cantidad a entregar supera la cantidad pendiente del producto {ProductName}."`; empty/zero → `"Debe indicar al menos una cantidad a entregar."`; foreign line → `"El artículo {SaleItemId} no pertenece a la venta."`; duplicate → `"La solicitud contiene artículos duplicados."`; non-custody state → `"La venta no se encuentra en estado de retiro pendiente."`; concurrency → `"Otro usuario modificó el retiro simultáneamente. Actualice la lista e intente de nuevo."`

## Threat Matrix (security-relevant cases)

| Case | Expected safe behavior | Planned RED test |
|------|------------------------|------------------|
| Forged per-line quantities exceeding pending | `400`, nothing persisted | Service unit test |
| Driver calling deliveries endpoint | `403`, nothing persisted | Controller/RBAC test |
| Cross-sale item id | `400`, nothing persisted | Service unit test |
| Duplicate idempotency key, same payload | Original receipt replayed, one event | Controller integration test |
| Duplicate key, different payload | `422` | Controller integration test |
| Concurrent deliveries on same item | One event, other `409`, counters consistent | Postgres-gated integration test |

## Testing Strategy

| Layer | What to Test | Approach |
|-------|--------------|----------|
| Unit (service) | State transitions, accumulation, completion, validation set, legacy full-remaining parity, pending list mapping, financial immutability | xUnit + InMemory (`TransactionIgnored`) + `SaleBuilder` (add `WithItemDeliveredQuantity`) |
| Unit (generator) | Note content: date, cashier, items, remaining summary; non-empty PDF | xUnit |
| Integration | Migration smoke + backfill (Postgres-gated), idempotent replay, RBAC 403, conflict `409` | `WebApplicationFactory` / `TEST_POSTGRES_CONNECTION` |
| WPF | Input clamp, confirm enablement, request mapping, progress text, error reload | xUnit + Moq (`PendingPickupsViewModelTests`, new dialog VM tests) |
| Web | Progress/clamp helpers, API function shape | `node --test` (esbuild JSX loader) |

## Migration Notes

- Name: `AddCustodyPartialDeliveries` in `Sales.Module/Migrations/` via `dotnet dotnet-ef migrations add ... --project Sales.Module --startup-project Backend.API` (local tool `dotnet-ef 10.0.4`).
- Up: add `SaleItems.DeliveredQuantity numeric(18,3) NOT NULL DEFAULT 0`; create `SaleDeliveries` (FK Sale `Restrict`, index `SaleId`), `SaleDeliveryItems` (FKs, indexes `SaleDeliveryId`/`SaleItemId`); raw SQL backfill for `DeliveryStatus = 0`.
- Down: drop tables, then the column. No financial or snapshot column is touched.

## Open Questions

- None blocking. Reprint from an old row is a MAY (not planned in v1); the endpoint exists if needed later.
