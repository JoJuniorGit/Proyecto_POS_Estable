# Archive Report: web-hold-lock-ui-tests

**Archived**: 2026-09-14
**Store**: OpenSpec (openspec)
**Status at close**: All tasks complete, verification pass_with_warnings (0 critical), archive ready.

## What Shipped

New capability `pending-order-hold-lock` — claim/release lifecycle and lock presentation for OnHold sales in the Web.Frontend. Pure controller extraction (`holdOrderLockController.js`) with injected dependencies, 22 new tests (12 controller + 5 desktop + 5 mobile render), shared fixture, and page wiring.

## Specs Synced

| Domain | Action | Details |
|--------|--------|---------|
| pending-order-hold-lock | Created | 6 requirements, 11 scenarios — copied as full spec (main spec did not exist) |

Main spec destination: `openspec/specs/pending-order-hold-lock/spec.md`

## Archive Contents

| Artifact | Status |
|----------|--------|
| proposal.md | ✅ |
| exploration.md | ✅ |
| specs/pending-order-hold-lock/spec.md | ✅ |
| design.md | ✅ |
| tasks.md | ✅ (10/10 tasks complete) |
| apply-progress.md | ✅ |
| verify-report.md | ✅ |

## Final State (per Final-State Authority)

- **Tasks**: 10/10 checked in `tasks.md` (source of truth for completion visibility).
- **Verification**: `pass_with_warnings` — 0 blockers, 0 CRITICAL findings, 6/6 requirements, 11/11 scenarios. `npm test` 178/178, `npm run lint` 0 errors, `npm run build` success.
- **Verify warning W1** (new explanatory comment at `holdOrderLockController.js:27`): **Fixed post-verification** — the comment was removed, `npm test` re-executed (178/178), `npm run lint` re-executed (0 errors). The archived spec is unaffected (no behavioral change).
- **Verify warning W2** (447 changed lines, above 400-line budget): Informational only, not a verification prerequisite. Carried as-is.
- **Verify warning W3** (render tests assert generic `/disabled/`): Pre-existing `holdLock.test.js` covers the full badge label. Non-blocking.
- **Verify warning W4** (cancellation relies on server-side `ClearHoldClaim`): Matches design decision 3. Non-blocking.
- **Verify warning W5** (`opencode.json` staged with unrelated model changes): User-owned, left untouched.

## Mechanical Copy Verification

- **Step 2 (spec sync)**: `fc.exe` confirmed no differences between source and destination spec files.
- **Step 3 (archive move)**: `git mv` succeeded. `git status` confirms all 7 archive files staged, source directory absent, no unexpected residues.

## Decision Log

- No destructive deltas (0 REMOVED requirements).
- Delta spec was a full spec (main spec did not exist prior to archive).
- Archive performed without user override needed.
