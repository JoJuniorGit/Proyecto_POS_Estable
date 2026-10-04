# Exploration: Custody Partial Deliveries (Retiros Pendientes)

Date: 2026-10-03. Evidence from codegraph index + a read-only explorer pass.

## Current State

- `Sale.DeliveryStatus` is binary: `SaleDeliveryStatus { Delivered = 0, PendingPickup = 1 }` (`Sales.Module/Entities/SaleDeliveryStatus.cs:3-7`); `Sale` carries `DeliveryStatus` + `PickupDate` (`Sales.Module/Entities/Sale.cs:30-31`). `IsProductDelivered` no longer exists (removed by migration `20260803083500_AddDeliveryStatusToSale`).
- `SaleItem` (`Sales.Module/Entities/SaleItem.cs:6-49`) has `Quantity numeric(18,3)` but no delivered-quantity field. `SaleItem` has no `xmin` token; `Sale` does (`Sales.Module/Data/SalesDbContext.cs:239-245`).
- Checkout sets `sale.DeliveryStatus = isPendingPickup ? PendingPickup : Delivered` (`SalesService.Checkout.cs:277`); stock is deducted in full at checkout (`:284-319`) — custody deliveries must never touch inventory.
- `ConfirmPickupAsync` (`SalesService.History.cs:16-40`) flips the whole sale to `Delivered` + `PickupDate = UtcNow`; no audit row, no idempotency. Endpoint `POST /api/sales/{id}/confirm-pickup` (`Backend.API/Controllers/SalesController.Checkout.cs:193-210`), no `Idempotency-Key`.
- `GetPendingPickupsAsync` filters `Status == Completed && DeliveryStatus == PendingPickup` (`SalesService.History.cs:44-85`); `PendingPickupDto` exposes `List<SaleItemHistoryDto>` (no per-line pending data).
- No immutable per-event delivery log exists. Audit precedents: `StockMovement` (`Core/Entities/StockMovement.cs:5-21`, has `UserId`, `MovementDate`, `SaleId`) and `CashTransaction` (`Sales.Module/Entities/CashTransaction.cs:27-65`).
- Receipts: `SaleReceiptPdfGenerator.BuildPdf` (`Sales.Module/Receipts/SaleReceiptPdfGenerator.cs:12,62`) renders PDF; queued by `ChannelReceiptPrintQueue` + `ReceiptPrintBackgroundService`; served by `ReceiptsController`. Desktop downloads bytes and opens via `Process.Start` (`Desktop.Client.Core/ViewModels/PosViewModel.Orders.cs:196-217`). Web has no ticket printing today (single `window.print()` in `RegisterClosePage.jsx:326`).
- WPF: `PendingPickupsViewModel` (`ConfirmPickupAsync:131-168`) shows a confirm dialog then removes the row; `PendingPickupsView.xaml` has amber "Pendiente de Retiro" badge (`:176-184`), items grid (`:230-267`), "Confirmar Retiro / Entregar" button (`:270-281`). No quantities dialog, no print service.
- Web: `PendingPickupsPage.jsx` (484 lines) — load/list (`:45-79`), static "En Custodia" badge (`:338-340`), confirm button (`:376-382`), confirm modal (`:433-469`) calling `confirmPickup(saleId)` (`Web.Frontend/src/services/pendingPickupApi.js:25-27`). No partial inputs, no reprint.
- Idempotency helper `ResolveIdempotencyAsync` (`Backend.API/Controllers/SalesController.cs:196-241`) requires `Idempotency-Key`, validates format, hashes payload, and replays the stored JSON body on HIT; mismatched payload → 422.
- Tests today: `CommandCenter.Tests/PendingPickupTests.cs` (InMemory + `TransactionIgnored`), `Unit/CoverageConvergenceTests.cs:53-122` (paging/scope), `Unit/PendingPickupsViewModelTests.cs`, `Unit/ClientHttpContractTests.cs:239-247`. Web runner is `node --test` + esbuild JSX loader (`npm test`), no vitest; no pickup tests on Web.
- Migration tooling: `dotnet-ef 10.0.4` local tool; stream in `Sales.Module/Migrations/` (Npgsql + `Microsoft.EntityFrameworkCore.Design`). Last ANEXO is 8.144 → this feature is **8.145**.

## Key Constraints

1. Custody deliveries move no money and no stock; financial snapshots and sale history stay immutable (`docs/coding-guidelines-core.md` §2.4).
2. The existing `confirm-pickup` endpoint and its tests are a public contract: it must keep working, now recording a full-remaining delivery event instead of only flipping a flag.
3. Both clients (WPF + Web) expose Retiros Pendientes and share the same API surface.
4. Delivery audit rows are append-only: date/time, delivering cashier id + name snapshot, per-item delivered quantities.
