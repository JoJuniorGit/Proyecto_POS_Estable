# Archive Report: critical-debt-closeout

**Date**: 2026-09-17 (archive entry date)
**Mode**: openspec
**Verdict**: PASS_WITH_WARNINGS (change-level; archive admissible — 0 blockers, 0 critical findings)

## Summary

Closed `critical-debt-closeout`, the critical carry-forward change from the archived
`legacy-debt-cleanup` (pass_with_warnings). It closes the three money-visible defects deferred there
plus the structural closure debt: the closure/shift-report line rule for undeclared merged methods
is now per-currency with derived status (no hardcoded `ClosureStatus.Balanced`, no Bs.S/USD mixing);
the advance commission preview on both clients is server-resolved and fail-closed (no 7/10
literals); the Web drawer `advance` filter resolves against the contract source enum (matches
`CashAdvance` 2, not `Closing` 4); `DailyClosureService` is split into three cohesive partials
within the 500-line ceiling; and the legacy public entity-returning closure entry point is deleted
with its 18 test sites re-pointed to the command path. H-13 (decimal-only `[Range]` bounds) folded
in as hygiene. No schema or migration; persisted closure snapshots are never recomputed or
rewritten.

## Implementation Slices

| Slice | Scope | Commit | Verdict |
|-------|-------|--------|---------|
| S1 | Closure-line currency — `WARNING-04` (REQ-PMC-06) | `605b3aa` | PASS_WITH_WARNINGS |
| S2 | Server-sourced advance commission + source mapping — item 41 + Web twin, `RESIDUAL-S4b-04`, H-13 (REQ-CAP-02/03, REQ-ADB-05) | `67409da` | PASS_WITH_WARNINGS |
| S3a | `DailyClosureService` split — `S3-06` (REQ-COC-03/06) | `b4f7d40` | PASS_WITH_WARNINGS |
| S3b | Legacy entry removal + 18 re-points — `S3-07` (REQ-COC-05/03) | `8baf54c` | PASS_WITH_WARNINGS |

Docs/planning chain: `87aa6a2` (planning), `80c171c` (S1 accept), `a511300` (S2 accept),
`a076d05` (S3a accept), `642a1d3` (S3b accept), `e14e1a6` (final verify + consolidated inventory —
HEAD at archive; terminal docs commit landed 2026-09-18). Archive prefixed `2026-09-17` per the
change closeout date.

## Verify Report (Final State)

| Metric | Value |
|--------|-------|
| Change-level verdict | PASS_WITH_WARNINGS — 0 blockers, 0 critical findings |
| Requirements | 7/7 (4 ADDED, 3 MODIFIED; 0 REMOVED) |
| Scenarios | 21/21 compliant, 0 UNTESTED, 0 FAILING |
| Tasks | 28/28 checked (S1 5, S2 11, S3a 4, S3b 8) |
| Build | 0 errors / 0 warnings (`dotnet build CommandCenter.slnx -c Release`) |
| Backend suite | 1259/1259 passed (baseline floor 1227) |
| Frontend | `npm test` 287/287; `npm run lint` (oxlint) 0 warnings / 0 errors |
| Coverage (Core) | 0.8364 (gate ≥ 0.70) |
| Coverage (Sales.Module) | 0.9091 (gate ≥ 0.80) |
| Coverage (Inventory.Module) | 0.8251 (gate ≥ 0.72) |
| Admission | `gentle-ai sdd-verify-validate --requirements 7 --scenarios 21` exit 0; persisted report re-admitted |

Frozen-surface evidence revision (32 production/test files touched by the four slice commits):
`sha256:a6bdf34ee6710d7f8eb98130240b71f150389e0f4e5eb421bdd8afb938c0c431`. The verified code
surface is slice commit `8baf54c`; `642a1d3` and `e14e1a6` are docs-only over it.

## Specs Synced

All four capabilities already existed in `openspec/specs/`, so composition ran through the native
`gentle-ai sdd-archive-compose` (candidate `.compose-tmp` → readback diff → atomic move). Every run
exited `0`, and the persisted bytes are hash-identical to the admitted candidate (per-file SHA-256
before/after each move matched).

| Domain | Action | Result | Compose evidence |
|--------|--------|--------|------------------|
| `payment-method-currency-classification` | Updated | +`REQ-PMC-06` ADDED (1 requirement / 4 scenarios); 5→6 requirements, 10→14 scenarios | exit 0; readback diff shows only the appended requirement block |
| `cash-advance-payout-integrity` | Updated | `REQ-CAP-02` + `REQ-CAP-03` MODIFIED (full-block replace; +5 scenarios); 6→6 requirements, 7→12 scenarios | exit 0; readback diff shows only the two replaced blocks |
| `api-dto-boundary` | Updated | +`REQ-ADB-05` ADDED (1 requirement / 4 scenarios); 4→5 requirements, 7→11 scenarios | exit 0; readback diff shows only the appended block |
| `closure-orchestration-consolidation` | Updated | `REQ-COC-03` MODIFIED (2 scenarios updated) + `REQ-COC-05`/`REQ-COC-06` ADDED (2+2 scenarios); 4→6 requirements, 8→12 scenarios | exit 0; readback diff shows only the replaced block plus the two appended blocks |

Totals: 4 ADDED, 3 MODIFIED, 0 REMOVED — the config's destructive-delta warning does not apply.

Commands (repo root; normalized copies under
`%TEMP%\opencode\cdc-compose-normalized\`, all exit `0`):

```powershell
gentle-ai sdd-archive-compose --canonical "openspec/specs/payment-method-currency-classification/spec.md" --delta "<temp>\payment-method-currency-classification.normalized.md" --output "openspec/specs/payment-method-currency-classification/spec.md.compose-tmp"
gentle-ai sdd-archive-compose --canonical "openspec/specs/cash-advance-payout-integrity/spec.md" --delta "<temp>\cash-advance-payout-integrity.normalized.md" --output "openspec/specs/cash-advance-payout-integrity/spec.md.compose-tmp"
gentle-ai sdd-archive-compose --canonical "openspec/specs/api-dto-boundary/spec.md" --delta "openspec/changes/critical-debt-closeout/specs/api-dto-boundary/spec.md" --output "openspec/specs/api-dto-boundary/spec.md.compose-tmp"
gentle-ai sdd-archive-compose --canonical "openspec/specs/closure-orchestration-consolidation/spec.md" --delta "openspec/changes/critical-debt-closeout/specs/closure-orchestration-consolidation/spec.md" --output "openspec/specs/closure-orchestration-consolidation/spec.md.compose-tmp"
```

**Heading normalization (precedent rule)** — the deltas are change-local documents whose requirement
headings carry `REQ-XXX-NN` prefixes, while two canonical capabilities name requirements without
them. The composer refuses a MODIFIED delta whose heading does not match the canonical name
(observed verbatim for `REQ-CAP-02 Commission Resolved from System Settings`: *"no canonical
requirement named ..."*). Composition therefore ran against heading-normalized **copies** of the
`payment-method-currency-classification` and `cash-advance-payout-integrity` deltas, where only the
heading lines lost the change-local prefix (ADDED headings were normalized too, to keep each
canonical file's naming convention self-consistent); every non-heading byte is identical, verified
by diff. `api-dto-boundary` and `closure-orchestration-consolidation` canonical specs already carry
`REQ-` prefixes, so those deltas composed as-authored. The archived deltas under this report's
`specs/` are untouched (the post-move `diff -r` readback against the pre-move snapshot is empty).

**Cosmetic composer seam** — like the previous archive, the composer joins a replaced/appended
requirement block to the adjacent heading without an intervening blank line in some seams (e.g.
before `REQ-PMC-06` and before `Ordering and Rate Anchoring Preserved`). Content is intact and
re-composition against a canonical carrying such a seam succeeded empirically; cosmetic only.

## Archive Contents

- `proposal.md` ✅
- `specs/` ✅ (4 capabilities, deltas as authored)
- `design.md` ✅
- `tasks.md` ✅ (28/28 tasks complete, 0 unchecked)
- `apply-progress.md` ✅
- `verify-report.md` ✅
- `archive-report.md` ✅ (this file)

Moved with `git mv` (pure renames, 9/9 files) and verified against a pre-move recursive snapshot:
`diff -r` output empty, exit `0`; source path absent; destination file count 9/9.

## Source of Truth Updated

- `openspec/specs/payment-method-currency-classification/spec.md`
- `openspec/specs/cash-advance-payout-integrity/spec.md`
- `openspec/specs/api-dto-boundary/spec.md`
- `openspec/specs/closure-orchestration-consolidation/spec.md`

## Closed Criticals

| Item | Closed by | Detail |
|------|-----------|--------|
| `WARNING-04` | S1 `605b3aa` | Single `BuildReportDetail`: merged lines are per-currency with derived status and the declared-path tolerance; persisted Bs.S snapshot untouched (REQ-PMC-06, 4/4) |
| Item 41 + Web twin | S2 `67409da` | One server-resolved commission core behind a public read adapter and a fail-closed 422 route; both clients consume the server value and block submission when unresolved; zero 7/10 literals (REQ-CAP-02/03, 7/7) |
| `RESIDUAL-S4b-04` | S2 `67409da` | Single contract-aligned transaction-source table; `advance` filter matches `CashAdvance` (2) and excludes `Closing` (4); labels from the same table (REQ-ADB-05, 4/4) |
| `S3-06` | S3a `b4f7d40` | 650-line monolith split into `285`/`157`/`165`-line cohesive partials; 23/23 member blocks byte-identical to the parent (REQ-COC-06) |
| `S3-07` | S3b `8baf54c` | Three legacy members deleted; command path is the single closure implementation; relocated duplicate guard mutation-proved; 18 re-pointed tests keep real assertions (REQ-COC-05) |
| H-13 | S2 `67409da` | 12 `decimal` properties use decimal-only `[Range]` bounds; discriminating test verified by mutation (AD-6, hygiene; no spec requirement) |

## Open Residual / Follow-Up Inventory (condensed; all non-blocking)

| ID | Origin | Nature and current status |
|----|--------|---------------------------|
| `W-S3A-01` / `RESIDUAL-S3A-02` | S3a | File ceiling (≤ 500) and McCabe (< 10) are measured, not pinned by an automated regression test; recommend extending the S3b-07 structural-test pattern |
| `RESIDUAL-S1-02` | S1 | `ShiftReportMapper.MapDetails` still holds a third aligned copy of the per-currency arithmetic + `0.05m` tolerance; behaviorally aligned, consolidation candidate |
| `RESIDUAL-S2-04` / `W-S2-01` | S2 | The new route's 401/403 path is proven by attribute reflection + mock-level behavior; the real HTTP pipeline test is pg-gated and skipped without `TEST_POSTGRES_CONNECTION` |
| `RESIDUAL-S2-06` | S2 | Web modal coverage is SSR render + pure helpers + source scans; no mounted fetch→render→submit interaction test |
| `RESIDUAL-S2-02` / `W-S2-02` (D2, D3) | S2 | No headless WPF dialog-window test (no repo infrastructure); one consolidated S2 commit instead of four work units — accepted process/evidence deviations |
| `RESIDUAL-S3B-01` | S3b | Relocated guard uses `ParamName = declarations` (legacy: `closure`); message preserved, no test observes `ParamName` — correct for the command DTO |
| `RESIDUAL-S3B-03` | S3b | `apply-progress.md` records the main file as 286 lines; measured 285 — documentation nit, both ≤ 500 |
| Coverage variance (`W-S3B-01` / `RESIDUAL-S3B-02`) | S3b | Coverage varies run-to-run (Core 0.8364–0.8378; Sales.Module 0.9087–0.9091); every sample clears its floor by > 0.10 |
| Named transient flake (`W-S3A-02` / `W-S3B-02` / `RESIDUAL-S3B-04`) | S3a/S3b | `CheckoutUxTests.UpdateCustomer_PreservesExistingPaymentsAndRecalculatesCustody` failed once in 1259 (`Assert.Single` race on an immediately asserted payment; file untouched since 8.140) and passes on re-run; final runs green |
| Size exceptions S2 / S3a / S3b | S2/S3a/S3b | 979 / 545 (pure-move double-count of 260 relocated lines) / 591 authored changed lines, each above the 400-line review budget and each accepted as `size:exception` (no PR chain available in this repo; delete + re-points are compile-time inseparable) |
| `S5a-R1` / `S5b-R1` / `S5c-R1` | Proposal non-goal | Sessions change deferred to a future change |
| `H-07` | Proposal non-goal | Requires schema/migrations — deferred |
| `H-12` | Proposal non-goal | 10 monoliths, HIGH effort; the backend closure slice was addressed by `S3-06` |
| Item 40 | Proposal non-goal | Minor residual deferred |
| Working-tree note | Environment | `opencode.json` remains locally modified (` M`, pre-existing, not part of this change) |

No item is a blocker, a critical finding, or a spec-scenario failure.

## Process & Environment Notes

- **GGA `--no-verify` exceptions**: all four slices used documented, punctual `--no-verify` GGA
  exceptions for pre-existing, out-of-slice findings; each slice's `apply-progress.md` section and
  the verify report's classification audit recorded the exact finding and why it predates the
  change (e.g. the 8.x traceability comment in `DailyClosureService.cs`, the pre-existing
  `AsNoTracking` gap in `ResolveUserDetailsAsync`, client surfaces without `CancellationToken`,
  moved-verbatim receipt `catch (Exception)`). No in-slice finding remains unaddressed.
- **Dispatch**: the SDD dispatch channel was flaky; this archive phase was executed by the general
  fallback agent under explicit orchestrator authorization (orchestrator-authorized bypass) and
  behaved exactly as the `sdd-archive` phase.
- **Environment**: `TEST_POSTGRES_CONNECTION` unset — Postgres-gated classes early-return as
  vacuous passes (pre-existing policy); machine memory pressure caused transient host OOMs during
  verification, resolved by `dotnet build-server shutdown` and retries (final runs green).
- **Maintenance pending**: `gentle-ai sync` + OpenCode restart.
- **Database/schema**: no migration in any slice commit; persisted closure snapshots untouched.

## Key Learnings

1. The composer refuses a MODIFIED delta heading that carries a change-local `REQ-ID` prefix when
   the canonical capability names the requirement without it; normalize heading lines on a scratch
   delta copy and leave the archived delta bytes untouched.
2. ADDED requirements are appended verbatim, so their headings should also be normalized when the
   canonical file's established naming convention is prefix-free.
3. The composer emits a blank-line-free seam between a replaced/appended block and the adjacent
   heading; it is cosmetic, and re-composition against a canonical already carrying such a seam
   succeeds (proved by the cash-advance compose).
4. A `git mv` directory move plus a pre-move recursive snapshot and `diff -r` readback gives
   independent byte-identity proof for the archive without routing artifact bytes through the model.
5. Punctual `--no-verify` GGA exceptions remain auditable only when each slice records the exact
   pre-existing finding, its evidence, and the future owner slice.
