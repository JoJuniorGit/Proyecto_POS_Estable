# Archive Report: custody-partial-deliveries

**Change**: 2026-10-03-custody-partial-deliveries
**Date**: 2026-10-03 (archive entry date)
**Store**: openspec (repo-local)
**Verdict**: PASS_WITH_WARNINGS (0 blockers, 0 critical findings; remediation closed all actionable warnings)

## Summary

Partial deliveries (merchandise in custody) for paid pending pickups: three logistic states, per-item
delivered/pending quantities, append-only `SaleDelivery`/`SaleDeliveryItem` log, idempotent delivery
endpoint, per-event delivery-note PDF, and dispatch UI in both clients (WPF modal + badge/progress +
printing; Web modal + badge/progress + authenticated blob printing). Post-delivery remediation fixed
the WPF row-render crash (TwoWay on read-only property), added the "Retiro Completo" shortcut on both
clients, and added interactive E2E coverage (WPF FlaUI + Web Playwright).

## Implementation Slices

| Slice | Scope | Commit |
|-------|-------|--------|
| 1 | Domain + additive migration + backfill + smoke | `1f17988` |
| 2 | Transaccional service + idempotent endpoint + tests | `106dcdc` |
| 3 | Per-event delivery-note PDF + endpoint | `c5d9ae7` |
| 4 | WPF dispatch UI (modal, badge/progress, printing) | `d23e299` |
| 5 | Web dispatch UI + regenerated bundle | `331b975` |
| Rem. | Postgres-real residuals + DTO alias + rounding; crash fix; full-delivery button; interactive E2E | `069a7fe`, `0b4fcdf`, `2839817`, `b93c8b9` |

Planning/docs chain: `e5b7c19`, `aea7296`, `b6741dc`, `35b0edd`, `624a3af`, `59a8473`, `a57165b`;
final ANEXO `034899a`.

## Verify Report (Final State, per ANEXO 8.145 + remediation)

| Metric | Value |
|--------|-------|
| Verdict | PASS WITH WARNINGS (final independent verification) |
| Requirements | 15/15 |
| Scenarios | 31/34 conformes; 3 runtime-gated by Postgres (CI) |
| Tasks | 25/25 checked |
| Build | 0 errors / 0 warnings |
| Suite at close | 1591/1591 (post-remediation; +56 vs the 8.144 baseline) |
| Coverage at close | Core 0.8805 / Sales.Module 0.8895 / Inventory.Module 0.8428 |
| E2E WPF | 18/18 (interactive; includes health 2/2) · Web Playwright 3/3 · Web 301/301 + lint 0 |

## Specs Synced

| Domain | Action | Result |
|--------|--------|--------|
| `custody-partial-delivery` | Created | 7 requirements / 16 scenarios |
| `custody-delivery-receipt` | Created | 2 requirements / 4 scenarios |
| `custody-delivery-wpf` | Created | 3 requirements / 7 scenarios |
| `custody-delivery-web` | Created | 3 requirements / 7 scenarios |

Composition deviation (recorded): `gentle-ai sdd-archive-compose` absent in the installed build
`4.0.1-0.20261003190532-0dda8895f664`; first-time domains composed by deterministic transform
(purpose prologue + byte-for-byte requirements body; counts asserted equal to the deltas — all
matched). No destructive deltas (0 REMOVED).

## Archive Notes

- Mechanical move: pre-move recursive snapshot + per-file SHA256 readback → 0 differences;
  `git diff --no-index` empty (only autocrlf LF/CRLF warnings).
- Accepted residuals recorded in the change's verify-report (archived alongside): E2E mock always
  responds `Delivered`; print-offer dialog not asserted; PDF viewer opening not automated.
- Delivered to main as part of the V0.15 → main chain PR created in this same closeout (see GitHub).
