# Archive Report: wpf-e2e-full-stack

**Change**: 2026-10-04-wpf-e2e-full-stack
**Date**: 2026-10-05 (archive entry date)
**Store**: openspec (repo-local)
**Verdict**: PASS_WITH_WARNINGS (final independent verification; 0 blockers)

## Summary

Full-stack desktop E2E (Level B): gated fixture with real Backend.API + isolated PostgreSQL + real client without
`--e2e`; complete real flows (sale + history, cash drawer session, daily closure with server-verified totals);
bounded UIA retry hygiene (`UiaRetry` COMException + Win32(5), `E2E_FIND_TIMEOUT_SECONDS`); blocking CI job
`wpf-e2e`; persistent client-settings sidecar recovery; id-only AutomationIds.

## Implementation Slices

| Slice | Scope | Commit |
|-------|-------|--------|
| Plan | SDD plan (Level B) + ANEXO 8.148 | `f61d306` |
| 1 | Harness: `FullStackFixture`, `E2eApiClient`, project `Npgsql` reference, smoke + gating | `6fdde9e` |
| 2 | UIA hygiene: `UiaRetry`, `E2E_FIND_TIMEOUT_SECONDS`, known flaky dialog waits | `0313f04` |
| 3 | Real sale flow (search → cart → checkout → history) | `4bc5df7` |
| 4 | Real cash drawer session + daily closure | `b333602` |
| 5 | Blocking CI job `wpf-e2e` | `ac00e38` |
| 6 | Settings sidecar recovery + closure | `cad838a` |
| Docs | Documentation closeout | `6293937` |

## Verify Report (Final State)

| Metric | Value |
|--------|-------|
| Verdict | PASS WITH WARNINGS — 0 blockers |
| Requirements | 4/5 + 1 PARTIAL (CI runtime pending next push) |
| Scenarios | 13 (11 compliant + 2 PARTIAL CI-runtime) |
| Build | 0 errors / 0 warnings |
| Suite at close | 1770/1770 |
| Coverage (final) | Core 0.8835 / Sales.Module 0.8893 / Inventory.Module 0.8547 |
| Mock E2E | 49/49 |
| Gated full-stack | smoke 1/1, sale 1/1, cash 1/1 |

**Archive-time task reconciliation (recorded exception)**: the persisted SDD `tasks.md` was reconciled
(all unchecked → checked) by the orchestrator at T7 closure under the Task Completion Gate exception.
At archive time the artifact was verified to contain 17 checked checkbox tasks, 0 unchecked. Completion
is proven by the final verify-report, the ODD tracker rows with commits
(`odd/tasks/e2e-full-stack-higiene.md` L9/L10), and the observed suite runs.

## Specs Synced

| Domain | Action | Result |
|--------|--------|--------|
| `wpf-e2e-full-stack` | Created | 3 requirements / 7 scenarios |
| `wpf-e2e-stability` | Created | 1 requirement / 4 scenarios |
| `ci-pipeline` | Updated | +`Desktop E2E Job in CI` (1 requirement / 2 scenarios); 6→7 requirements, 11→13 scenarios — original bytes preserved as an exact prefix (prefix SHA256 `C06F1C5FDE69A2BAEFE650BD5D23FA4D30EB2BEAC87A2B57459361EE2D5B0FF9`, unchanged), 0 REMOVED |

Composition deviation (recorded): `gentle-ai sdd-archive-compose` is absent in the installed build
`4.0.1-0.20261003190532-0dda8895f664`; composition ran as a deterministic script with prefix-byte assertions
and requirement/scenario count equality. Extracted-section SHA256 equality was asserted for all three merges
(full-stack `F7DB7DB0EFC94D79131C06EF10238DCB6D24DCD1C71566671B19A17B8E9C9B8A`,
stability `91A66D72626933609A993AE06DED68F3D0A1A8146C6E6377AB1EF4E8910B6F2D`,
ci-pipeline `6FBC931AFD74DD25A0614BA5D5986357EB5DEB34A633731EC9048E5875279B76`).
No destructive deltas (0 REMOVED).

## Archive Notes

- Mechanical move: pre-move recursive snapshot + per-file SHA256 readback → 0 differences (9 files).
  The first readback harness run emitted a false "differences=18" from a relative-path bug in the
  verification script (a relative base path compared against absolute file paths); no restore and no
  second move were performed, and a corrected comparison re-run confirmed byte-identity (0 differences)
  for all 9 files.
- Residuals preserved: CI-runtime scenarios PARTIAL until the next push to V0.15; sidecar recovery only
  activates on the next gated run by design; accepted residuals: PerformantDataGrid null UIA peer,
  UIA-only reentrancy of `ConfirmPickupCommand`, BCV job rate race window, transient dialog fallbacks,
  pre-existing ProgramData receipts warning, pre-existing crash.log noise.
- ANEXO 8.148 registered in `docs/reporte.txt` (L12100).
