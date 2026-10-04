# Tasks: Custody Partial Deliveries (Retiros Pendientes)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~1,800–2,400 |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 → PR 2 → PR 3 → PR 4 → PR 5 |
| Delivery strategy | ask-on-risk |
| Chain strategy | stacked-to-main |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Domain + migration + backfill | PR 1 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~CustodyPartialDelivery"` | Postgres migration smoke via `TEST_POSTGRES_CONNECTION`; N/A locally (gated) | `Down` drops tables + column |
| 2 | Delivery service + API | PR 2 | same filter `~CustodyPartialDelivery` | `curl POST /api/sales/{id}/deliveries` against local API | Revert service + endpoint; counters unused |
| 3 | Delivery note PDF | PR 3 | `... --filter "FullyQualifiedName~DeliveryNote"` | GET receipt PDF and open it | Revert generator + endpoint |
| 4 | WPF dispatch UI | PR 4 | `... --filter "FullyQualifiedName~PendingPickupsViewModel|FullyQualifiedName~PartialDeliveryDialog"` | Launch Desktop.Client → Retiros Pendientes → partial confirm | Revert VM/dialog/XAML |
| 5 | Web dispatch UI | PR 5 | `npm test` (Web.Frontend) | Open Web → Retiros Pendientes → partial confirm | Revert page/api/helper |

## Phase 1: Domain & Migration

- [x] 1.1 Append `PartiallyDelivered = 2` to `Sales.Module/Entities/SaleDeliveryStatus.cs` (Delivery States).
- [x] 1.2 Add `DeliveredQuantity` to `Sales.Module/Entities/SaleItem.cs`; create `Sales.Module/Entities/SaleDelivery.cs` + `SaleDeliveryItem.cs` (Per-Line Quantities, Delivery Event Log).
- [x] 1.3 Configure `Sales.Module/Data/SalesDbContext.cs`: DbSets, `numeric(18,3)`, FKs Restrict, indexes, `xmin` on `SaleItem`.
- [x] 1.4 Add migration `AddCustodyPartialDeliveries` under `Sales.Module/Migrations/` with backfill (Delivered → qty = total) and `Down` (Migration and Backfill).
- [x] 1.5 Tests: `CommandCenter.Tests/Integration/CustodyPartialDeliveryMigrationSmokeTests.cs` (Postgres-gated backfill cases) + model test.

## Phase 2: Service & API

- [x] 2.1 DTOs: extend `Sales.Module/DTOs/PendingPickupDto.cs`; create `PendingPickupItemDto.cs`, `PartialDeliveryDtos.cs` (Pending list read model).
- [x] 2.2 RED: `CommandCenter.Tests/Unit/CustodyPartialDeliveryTests.cs` — over-pending, zero, foreign, duplicate, accumulation, completion, legacy parity (Validation, Legacy Equivalence).
- [x] 2.3 Create `Sales.Module/Services/SalesService.Deliveries.cs`: transactional `DeliverPartialAsync` + conflict mapping (Delivery Validation and Atomicity).
- [x] 2.4 Modify `SalesService.History.cs`: include `PartiallyDelivered`; `ConfirmPickupAsync` delegates to full-remaining core.
- [x] 2.5 Extend `Sales.Module/Interfaces/ISalesService.cs`; keep legacy signature.
- [x] 2.6 Create `Backend.API/Controllers/SalesController.Deliveries.cs`: `POST /{id}/deliveries` (Idempotency-Key required, RBAC, Driver 403, ProblemDetails 400/409/422) (Idempotent Delivery Endpoint).
- [x] 2.7 Tests: controller/idempotency/409 (`TEST_POSTGRES_CONNECTION`) + `ClientHttpContractTests` route assertions.

## Phase 3: Delivery Note PDF

- [x] 3.1 RED: `CommandCenter.Tests/Unit/DeliveryNotePdfGeneratorTests.cs` (content: date, cashier, items, remaining; PDF non-empty).
- [x] 3.2 Create `Sales.Module/Receipts/DeliveryNotePdfGenerator.cs` from immutable log data (Note Content).
- [x] 3.3 Add `GET /{id}/deliveries/{deliveryId}/receipt` (sale-scoped; 404 wrong sale/id) (Retrieval Endpoint and Access).

## Phase 4: WPF Dispatch UI

- [x] 4.1 Extend `Desktop.Client.Core/Services/ISalesService.cs` + `SalesService.cs`: deliver partial + fetch note; mirror client DTOs.
- [x] 4.2 RED: extend `CommandCenter.Tests/Unit/PendingPickupsViewModelTests.cs` + new dialog VM tests (clamp, disabled confirm, mapping, progress).
- [x] 4.3 Create `PartialDeliveryDialogViewModel` + `Desktop.Client/Views/PartialDeliveryDialog.xaml(.cs)`; success print action.
- [x] 4.4 Modify `PendingPickupsViewModel.cs`, `IDialogService`, `Desktop.Client/Views/PendingPickupsView.xaml`: badge, progress, modal launch (Dispatch Dialog, Partial Badge and Progress, Delivery Note Print).
- [x] 4.5 Print wiring: note bytes → temp PDF → `Process.Start`; 409 → message + reload.

## Phase 5: Web Dispatch UI

- [x] 5.1 Extend `Web.Frontend/src/services/pendingPickupApi.js`: `deliverPartialPickup` (idempotency key) + `getDeliveryNote` (blob).
- [x] 5.2 Create `Web.Frontend/src/utils/deliveryProgress.js` + `node:test` (clamp, progress).
- [x] 5.3 Update `Web.Frontend/src/pages/PendingPickupsPage.jsx`: inputs modal, badge + progress, blob print, error reload (Dispatch Modal, Partial Badge and Progress, Delivery Note Print).

## Phase 6: Verification & Docs

- [x] 6.1 `dotnet build CommandCenter.slnx -c Release` 0/0; `dotnet test`; `npm test` + `npm run lint`; `scripts/check-coverage.py`.
- [x] 6.2 ANEXO 8.145 in `docs/reporte.txt` (slices, evidence, GGA, rollback, limitations).
