# Archive Report: entity-boundary-cleanup

**Change**: entity-boundary-cleanup
**Date**: 2026-09-19
**Store**: openspec (repo-local)
**Verdict**: PASS

## Summary

Extended the API/DTO boundary to the product entity. Six new requirements (REQ-ADB-06..11) enforce
that `Core.Entities.Product` never crosses the read, write, controller, or WPF-client boundaries.
All work completed; delta specs synced to source of truth; change archived.

## Final Counts

| Metric | Value |
|--------|-------|
| Requirements in delta | 6 (REQ-ADB-06..REQ-ADB-11) |
| Scenarios in delta | 13 |
| Tasks total | 28 |
| Tasks complete | 28 |
| Build | 0 errors / 0 warnings |
| Tests | 1322 passed / 0 failed / 0 skipped |
| Coverage | Core 0.8373, Sales.Module 0.8582, Inventory.Module 0.7997 |
| Blockers | 0 |
| Critical findings | 0 |

## Work Units

### WU-1 — Read Boundary (Phase 1, tasks 1.1–1.8)

Introduced sealed `SaleProductInfoDto` (15 init-only members, `decimal? CostPriceUSD`). Replaced
four entity reads (`GetProductByIdAsync`, `GetProductsByIdsAsync`, `GetCashAdvanceProductAsync`,
`GetProductBySkuAsync`) with three in-query DTO projections on `IInventoryService`. Re-pointed
`SalesService` and all partials. Deleted four entity-fallback branches. ~727 authored lines.

### WU-2 — Write Boundary (Phase 2, tasks 2.1–2.6)

Created `CreateSystemProductRequest` DTO. Made entity CRUD private behind DTO twins. Exposed
`CreateSystemProductAsync(CreateSystemProductRequest) -> Task<int>`. Tracked `FindAsync` +
`CurrentValues.SetValues` flow preserved; Npgsql `xmin` concurrency token stays in the UPDATE
predicate. Gated stale-token conflict test added. ~370 authored lines.

### WU-3 — Controller DTO-Only (Phase 3, tasks 3.1–3.4)

Required single public constructor on `ProductsController` (`IProductManagementService`). Deleted
compat 2-arg ctor and both entity fallbacks. GET missing-id returns 404 without entity fallback.
Re-pointed 9 controller constructions. ~97 authored lines.

### WU-4 — WPF Client DTO-Only (Phase 4, tasks 4.1–4.7)

Re-pointed `IProductService`/`ProductService` to DTO-only signatures. Created
`ProductClientMapping.cs`. Re-mapped `ProductDialogViewModel`, `VariantManagementViewModel`, and
`InventoryViewModel.Operations`. Extended `ProductServiceQueryContractTests` shape guards. ~455
authored lines.

### Phase 5 — Verification

Build 0/0; full suite 1322 passed / 0 failed; coverage gates met (Core 0.8373, Sales.Module 0.8582,
Inventory.Module 0.7997). No source edits — verification only.

## Delivery State

Delivery: chained PRs stacked-to-main on branch `V0.15`.

| PR | Work Unit | Authored Lines | Status |
|----|-----------|---------------|--------|
| PR1 | WU-1 read boundary | ~727 | `size:exception` approved |
| PR2 | WU-2 write boundary | ~370 | within budget |
| PR3 | WU-3 controller | ~97 | within budget |
| PR4 | WU-4 WPF client | ~455 | `size:exception` approved |

**Commits have NOT been made yet** — the working tree is the delivery artifact. The unrelated
`opencode.json` tooling edit must NOT be part of the change's commits. This is
delivery-pending, not a defect.

## SEAM-01 Contribution

This change provides a bounded extension of `ProductServiceQueryContractTests` (shape guards for
`GetByIdAsync`/`CreateAsync`/`UpdateAsync`). The rest of SEAM-01 remains active.

## Spec Sync

| Domain | Action | Details |
|--------|--------|---------|
| api-dto-boundary | Updated | 6 requirements added (REQ-ADB-06..11), 13 scenarios appended |

Merged main spec: `openspec/specs/api-dto-boundary/spec.md`
- REQ-ADB-01..05 preserved intact
- REQ-ADB-06..11 appended after the existing requirements

## Documented Gaps

1. **Npgsql-gated tests**: `TEST_POSTGRES_CONNECTION` is unset in this environment, so the
   `UpdateProductFromDto_StaleToken_Conflicts` and `WebApplicationFactory` HTTP smoke tests pass
   without executing their bodies. Confirmed by static/design evidence plus the passing provider
   model pin. Pre-documented in `verify-report.md` (WARNING-1).

2. **No per-PR isolation**: All WU-1..WU-4 changes are uncommitted in a single worktree on branch
   `V0.15`. The chained-PR slices and `size:exception` approvals remain a delivery-time step.
   Pre-documented in `verify-report.md` (WARNING-2).

## Verification Checklist

- [x] Main specs updated correctly (11/11 requirements)
- [x] Change folder moved to archive
- [x] Archive contains all artifacts (8 files)
- [x] Archived `tasks.md` has no unchecked implementation tasks (28/28 complete)
- [x] Active changes directory no longer has this change
- [x] Byte-identical readback verified (MD5 hash comparison, 8/8 files)

## Files

```
openspec/specs/api-dto-boundary/spec.md          — merged source of truth
openspec/changes/archive/2026-09-19-entity-boundary-cleanup/
  proposal.md
  exploration.md
  research.md
  specs/api-dto-boundary/spec.md
  design.md
  tasks.md
  verify-report.md
  apply-progress.md
  archive-report.md
```
