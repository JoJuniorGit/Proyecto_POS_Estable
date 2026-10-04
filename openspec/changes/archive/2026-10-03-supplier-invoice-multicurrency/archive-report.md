# Archive Report: supplier-invoice-multicurrency

**Change**: 2026-10-03-supplier-invoice-multicurrency
**Date**: 2026-10-03 (archive entry date)
**Store**: openspec (repo-local)
**Verdict**: PASS_WITH_WARNINGS (final independent verification; 0 blockers, 0 critical findings)

## Summary

Multi-currency supplier invoices: per-document currency (`USD`/`Bs.S`) with an immutable applied-rate
snapshot, server-side normalization of document unit costs to the USD base (`PricingCalculator.ToUSD`),
and reworked staging semantics (unmatched → `NEW` with a "Crear Producto" action; zero-cost matched →
`UPDATE`; approval gated on a resolved product). Hot product creation from a staged line: atomic
endpoint (identity-only product with captured EAN/UPC as SKU + `SupplierProductCode` alias upsert +
line resolution), alias learning on confirm, and the WPF header/grid/modal UI. Cost, margins, prices
and stock keep applying only at confirm (single apply point).

## Implementation Slices

| Slice | Scope | Commit |
|-------|-------|--------|
| 1 | Domain (`Currency`/`AppliedRate`/`UnitCostDocument`) + additive migration `20261004040512_AddSupplierInvoiceMulticurrency` + backfill | `a772152` |
| 2 | Staging: currency/rate validation + USD normalization + classification rework | `35b9639` |
| 3 | Hot creation + alias learning + endpoint + desktop client API | `423c2c2` |
| 4 | WPF UI: currency/rate header, `[NUEVO]` + action, EAN/UPC modal, gating | `7c7caf7` |
| Docs | Plan `dac0a11`; final verification + ANEXO 8.146 + verify-report `49e91a2`; tracker hash `0e6c560` | — |

## Verify Report (Final State)

| Metric | Value |
|--------|-------|
| Verdict | PASS WITH WARNINGS — 0 blockers, 0 critical findings |
| Requirements | 7/7 |
| Scenarios | 23 total: 19 compliant, 4 PARTIAL (HTTP 400/404/409 static + gated Postgres), 0 untested, 0 failing |
| Tasks | 6/6 ODD tracker; SDD `tasks.md` 20/20 (see reconciliation note) |
| Build | 0 errors / 0 warnings |
| Suite at close | 1651/1651 (+60 net vs the 1591 baseline) |
| Coverage at close | Core 0.8815 / Sales.Module 0.8893 / Inventory.Module 0.8517 |
| Per-slice verification | T2 PASS · T3 PASS · T4 PASS WITH WARNINGS · T5 PASS · final PASS WITH WARNINGS |

**Archive-time task reconciliation (recorded exception)**: the persisted SDD `tasks.md` still had
20 unchecked boxes at archive time because the apply phase tracked completion in the ODD tracker
instead. The orchestrator authorized the mechanical reconciliation (20 unchecked → checked) under the
Task Completion Gate exception: completion is proven by the final verify-report (7/7 requirements),
the ODD tracker T1–T6 rows with commits, and the 1651/1651 suite. No other task state was altered.

## Specs Synced

| Domain | Action | Result |
|--------|--------|--------|
| `supplier-invoice-staging` | Updated | `Staging Review Semantics` replaced (4 requirements; 9 → 11 scenarios) — byte prefix preserved, new section = delta body |
| `supplier-invoice-apply` | Updated | `Supplier Code Learning on Apply` added (7 → 8 requirements; 7 → 11 scenarios) — append with byte prefix preserved |
| `supplier-invoice-multicurrency` | Created | 2 requirements / 6 scenarios |
| `supplier-product-hot-creation` | Created | 3 requirements / 8 scenarios |

Composition deviation (recorded): `gentle-ai sdd-archive-compose` is absent in the installed build
`4.0.1-0.20261003190532-0dda8895f664` (it existed at the 2026-09-17 archives). Composition ran as a
deterministic script instead: the updated canonical keeps every byte before the replaced/appended
region (prefix assertion), and new domains reuse the delta requirements body byte-for-byte (counts
asserted equal). Native compose refusal semantics were not applicable — the command does not exist in
this build; the deviation follows the same pattern recorded for the missing `sdd-verify-validate`
(ANEXO 8.144 F2). No destructive deltas (0 REMOVED) — the config's archive warning does not apply.

## Archive Notes

- Mechanical move: pre-move recursive snapshot + per-file SHA256 readback → 0 differences;
  `git diff --no-index` empty (only autocrlf LF/CRLF warnings).
- Residuals preserved in the archived `verify-report.md`: Postgres-gated tests run in CI; endpoint
  400/404/409 verified statically (403 via TestServer); creation rollback after partial product flush
  not fault-injected; WPF runtime verified compile+static only; accepted edge: a zero-cost `[NUEVO]`
  line reclassifies to `Unchanged` after creation; E2E UIA flake observed in the unrelated
  `PendingPickupsTests` (isolated re-run 4/4).
- Delivered to main as part of the V0.15 → main chain PR created in this same closeout (see GitHub).
