# Proposal: Custody Partial Deliveries (Retiros Pendientes)

## Intent

Paid custody orders (`IsPendingPickup`) can only be withdrawn in full today: `ConfirmPickupAsync` flips the whole sale to `Delivered`. Real customers pick up goods across several visits. Enable fractional withdrawals with exact per-line traceability (total / delivered / pending), three logistics states, an immutable per-event delivery log (date/time, cashier, quantities), a dispatch modal in both clients, and a printable delivery note.

## Scope

### In Scope
- Delivery states: Pendiente (`PendingPickup`), Entrega Parcial (`PartiallyDelivered`), Completado (`Delivered`).
- Per-line breakdown on every custody item: total, delivered, pending (decimal(18,3)).
- Append-only delivery events: `SaleDelivery` (date/time UTC, cashier id + name snapshot, optional notes) + `SaleDeliveryItem` (product snapshot + quantity).
- `POST /api/sales/{id}/deliveries` (idempotent) with server-side zero-trust validation; legacy `confirm-pickup` keeps working as a full-remaining delivery.
- Delivery-note PDF (`Nota de Despacho`) retrievable per event.
- WPF + Web dispatch modal with per-product quantity inputs capped at pending; distinct badge + progress indicator; print action.

### Out of Scope
- Inventory movements (stock was fully deducted at checkout), payment/refund changes, fiscal invoicing changes.
- Delivery scheduling, notifications, or logistics module integration.
- Reworking `PendingOrdersPage` (hold orders) beyond what already exists.

## Capabilities

### New Capabilities
- `custody-partial-delivery`: states, per-line quantities, append-only delivery log, validation/atomicity/idempotency, legacy confirm equivalence, pending list read model.
- `custody-delivery-receipt`: on-demand delivery-note PDF per event, from immutable log data.
- `custody-delivery-wpf`: dispatch dialog, badge/progress, print action on Desktop.
- `custody-delivery-web`: dispatch modal, badge/progress, print action on Web.

### Modified Capabilities
- None (no existing openspec capability covers pickups).

## Approach

Extend the existing Sales module end-to-end. Entities + one additive migration in `Sales.Module/Migrations`; a new partial service `SalesService.Deliveries.cs` inside the existing execution-strategy + transaction pattern; `xmin` concurrency token on `SaleItem` so concurrent deliveries cannot double-count; controller-level idempotency reusing `ResolveIdempotencyAsync`. Receipt PDF reuses the existing `Sales.Module/Receipts` stack (`SaleReceiptPdfGenerator`) served synchronously; both clients fetch bytes (Desktop opens a temp PDF, Web opens a blob). No monetary or snapshot field is written by this feature.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Sales.Module/Entities/SaleDeliveryStatus.cs`, `SaleItem.cs`, new `SaleDelivery*.cs` | Modified/New | Enum value + delivered counter + log entities |
| `Sales.Module/Data/SalesDbContext.cs` | Modified | DbSets, config, `xmin` on `SaleItem` |
| `Sales.Module/Migrations/` | New | Additive migration + backfill |
| `Sales.Module/DTOs/PendingPickupDto.cs`, new DTO files | Modified/New | Read model with per-line pending + request/receipt contracts |
| `Sales.Module/Services/SalesService.Deliveries.cs`, `SalesService.History.cs`, `Interfaces/ISalesService.cs` | New/Modified | Partial delivery core + pending query + legacy delegate |
| `Sales.Module/Receipts/DeliveryNotePdfGenerator.cs` | New | Delivery-note PDF |
| `Backend.API/Controllers/SalesController.Deliveries.cs` | New | Deliveries + note endpoints |
| `Desktop.Client.Core` (services, VMs, dialog) + `Desktop.Client/Views/PendingPickupsView.xaml` | Modified/New | WPF dispatch + print |
| `Web.Frontend/src/pages/PendingPickupsPage.jsx`, `src/services/pendingPickupApi.js`, `src/utils/` | Modified/New | Web dispatch + print |
| `docs/reporte.txt` | Modified | ANEXO 8.145 |

**Test commands**: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` (CommandCenter.Tests) · `npm test` (Web.Frontend) · `dotnet build CommandCenter.slnx -c Release`.

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Concurrent deliveries double-count or over-deliver | Med | Transaction + `xmin` on `SaleItem`; 409 ProblemDetails, no auto-retry, client re-enters |
| Duplicate POST on flaky network creates two events | Med | Required `Idempotency-Key`; payload-hash mismatch → 422 |
| Legacy `confirm-pickup` behavior drifts | Low | Existing tests kept green; endpoint delegates to the same delivery core |
| Historical data inconsistency (delivered sales without counters) | Med | Migration backfill sets `DeliveredQuantity = Quantity` for `Delivered` sales |
| Feature exceeds the 400-line review budget | High | 5 chained work units (domain → API → note → WPF → Web) |

## Rollback Plan

- Migration `AddCustodyPartialDeliveries` is additive; `Down` drops `SaleDeliveries`/`SaleDeliveryItems` and the `DeliveredQuantity` column.
- Revert service + endpoints; `DeliveryStatus` values already written stay readable (`PartiallyDelivered = 2` remains a valid stored int; pending list falls back to old behavior if the filter is reverted).
- Revert client views; the legacy `confirm-pickup` path remains functional throughout.

## Success Criteria

- [ ] Partial withdrawals accumulate across events; status transitions Pendiente → Parcial → Completado; `PickupDate` set only on completion.
- [ ] Every event persists immutable log rows with date/time, cashier id + name, and per-item quantities.
- [ ] Server rejects over-pending, zero, foreign, and duplicate lines; replay of an idempotency key creates no second event.
- [ ] Both clients: capped numeric inputs, distinct partial badge + progress, printable delivery note.
- [ ] `dotnet build CommandCenter.slnx -c Release` 0/0; `dotnet test` green (Sales.Module ≥0.80); `npm test` + `npm run lint` green.
