# Archive Report: enforce-hold-claim-precondition

**Date**: 2026-09-14
**Store**: openspec
**Verdict**: pass_with_warnings (no CRITICAL findings)

## Summary

Enforced the active-claim precondition on every OnHold mutator. The `EnsureHoldClaimAccess` guard now rejects mutations when no claim is held (throwing `HoldNotClaimedException` → 409) while preserving the existing foreign-claim `SaleLockedException`. Web adapts its Anular and cart-restore paths. No HTTP contract or migration change.

## Spec Sync

**Delta merged into**: `openspec/specs/pending-order-hold-lock/spec.md`
**Delta path**: `openspec/changes/enforce-hold-claim-precondition/specs/pending-order-hold-lock/spec.md`
**Composition tool**: `gentle-ai sdd-archive-compose` (exit 0, mechanical merge)

| Metric | Before | After | Delta |
|--------|--------|-------|-------|
| Requirements | 6 | 11 | +5 ADDED |
| Scenarios | 11 | 21 | +10 ADDED |

### Requirements Added

| # | Requirement | Scenarios |
|---|-------------|-----------|
| 7 | Active Claim Required for Every OnHold Mutation | 3 (Rejected without a claim, Allowed when the actor holds the claim, Blocked when another actor holds the claim) |
| 8 | Inert and Unchanged Hold Paths | 2 (ConfirmPickup on a Completed sale, System recalculation and force-release) |
| 9 | Web Cancel Claims Before Cancelling | 2 (Claim succeeds, Claim rejected with 409) |
| 10 | Web Cart Refuses OnHold Restore | 2 (Restoring an OnHold sale, Rate effect avoided) |
| 11 | Compatibility Preserved | 1 (No contract change) |

No requirements were MODIFIED, REMOVED, or RENAMED.

## Archive Contents

| Artifact | Status |
|----------|--------|
| proposal.md | ✅ archived |
| exploration.md | ✅ archived |
| specs/pending-order-hold-lock/spec.md | ✅ archived (delta) |
| design.md | ✅ archived |
| tasks.md | ✅ archived (20/20 tasks complete) |
| apply-progress.md | ✅ archived |
| verify-report.md | ✅ archived (verdict: pass_with_warnings) |
| archive-report.md | ✅ archived (this file) |

## Verification Summary

- **Build**: `dotnet build CommandCenter.slnx -c Release` — 0 errors, 0 warnings
- **Tests (.NET)**: 985 passed / 0 failed
- **Tests (Web)**: 178 passed / 0 failed
- **Lint (Web)**: 0 errors (oxlint)
- **Spec compliance**: 6/10 scenarios runtime-backed; 4/10 verified by source inspection only

### Warnings (non-blocking)

- **W1**: Web scenarios (Req 3, Req 4) lack dedicated automated tests — verified by source inspection + primitive controller tests.
- **W2**: apply-progress.md undercounted new test cases (14 vs actual 15).
- **W3**: No dedicated 409 integration test for Req 5 — verified by middleware source inspection + green suites.

## File Operations

- `gentle-ai sdd-archive-compose` merged delta into main spec (exit 0)
- `git mv` moved change folder to `openspec/changes/archive/2026-09-14-enforce-hold-claim-precondition/`
- Active changes directory no longer contains this change
- No application code was modified; no commit was made
