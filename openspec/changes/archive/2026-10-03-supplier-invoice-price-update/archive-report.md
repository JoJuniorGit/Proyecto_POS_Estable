# Archive Report: supplier-invoice-price-update

**Change**: 2026-10-03-supplier-invoice-price-update
**Date**: 2026-10-03 (archive entry date)
**Store**: openspec (repo-local)
**Verdict**: PASS_WITH_WARNINGS (change-level; archive admissible — 0 blockers, 0 critical findings)

## Summary

Built the supplier-invoice module end to end: supplier identity + persisted column mappings, multi-format staging (xlsx/csv/xml) into DB drafts, deterministic matching (barcode/SKU → supplier-code alias → pg_trgm fuzzy), and one atomic confirm applying approved lines (cost, retail/wholesale margins, derived prices, stock + StockMovement audit). WPF-only UI. Delivered via stacked slices s1–s4 and remediation; reached main through PRs #20–#22.

## Implementation Slices

| Slice | Scope | Commits |
|-------|-------|---------|
| s1 | Domain entities + DTOs + InventoryDbContext + additive migration `20261003173222_AddSupplierInvoice` | `4483ac9` (docs) + `a66053d` |
| s2 | Supplier resolution, mapping template, matching + stage/read endpoints | `0125492` |
| s3 | Atomic confirm apply + RBAC + ProblemDetails | `1d53f6a` |
| s4 | WPF client: parsing, staging grid, badges, margin recalc, confirm | `bd54fb0` |
| Rem. | G4 xmin retry fix (StockMovement re-insert) + G5 ComboBox crash fix + hardened E2E | `3f5a014`, `251f565` |

## Verify Report (Final State, per ANEXO 8.144)

| Metric | Value |
|--------|-------|
| Verdict | PASS WITH WARNINGS — 0 critical, 3 warnings |
| Requirements | 15/15 |
| Scenarios | 21/22 conformes (1 gated Postgres) |
| Tasks | 20/20 checked |
| Build | 0 errors / 0 warnings |
| Suite at close | 1535/1535 |
| Coverage at close | Core 0.8554 / Sales.Module 0.8596 / Inventory.Module 0.8130 |
| Admission | Native `gentle-ai sdd-verify-validate` absent in installed build (ANEXO 8.144 F2/G3); report persisted by orchestrator decision |

## Specs Synced

| Domain | Action | Result |
|--------|--------|--------|
| `supplier-invoice-staging` | Created | 4 requirements / 9 scenarios |
| `supplier-product-matching` | Created | 4 requirements / 6 scenarios |
| `supplier-invoice-apply` | Created | 7 requirements / 7 scenarios |

Composition deviation (recorded): `gentle-ai sdd-archive-compose` is absent in the installed build
`4.0.1-0.20261003190532-0dda8895f664` (the command existed at the 2026-09-17 archives). For
first-time domains the canonical file was produced by a deterministic transform of the delta:
purpose prologue + requirements body extracted byte-for-byte, with requirement/scenario counts
asserted equal to the delta (4/9, 4/6, 7/7 — all matched). No destructive deltas (0 REMOVED).

## Archive Notes

- Mechanical move: pre-move recursive snapshot + per-file SHA256 readback → 0 differences;
  `git diff --no-index` empty (only autocrlf LF/CRLF warnings).
- Superseded by `2026-10-03-supplier-invoice-multicurrency` for the staging review semantics and
  apply capabilities (see that archive's report).
