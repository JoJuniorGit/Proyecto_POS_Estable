# Archive Report: legacy-debt-cleanup

**Date**: 2026-09-17
**Mode**: openspec
**Verdict**: PASS_WITH_WARNINGS (archive admissible; 0 blockers, 0 critical findings)

## Summary

Closed the legacy-debt-cleanup change: blocker-first cleanup of the 41-item legacy debt registry
(`docs/deuda-legacy-gga-2026-09-16.md`) plus the overlapping C2/C3 findings of
`docs/analisis-exhaustivo-sistema-2026.md`, delivered as eight chained slices under
`delivery strategy: ask-on-risk`. The close path is now zero-trust (server-side
`PaymentMethodCurrencyResolver` classification; `request.Currency` is structurally gone), touched
endpoints return RFC 7807 payloads via `ApiProblemResults`, closure orchestration lives entirely in
`IDailyClosureService` (controllers authorize and delegate only; `CreateClosure` under McCabe 10),
EF entities no longer cross the drawer/closure API boundary, every touched async surface propagates
`CancellationToken`, and the J findings (LAN cookie `Secure`, WPF VM disposal, client-side pagination,
`async void OnClosing`) are closed. No schema change; persisted closure snapshots are never
recomputed.

## Implementation Slices

| Slice | Scope | Commit(s) |
|---|---|---|
| S1 | Zero-trust close — server resolver + Web page (items 26/39); REQ-PMC-01/04/05 | `d7593c1`, remediation `c6c767f` |
| S2 | Error contract + dead fields (items 2/18/25/12/35); REQ-AEC-01..04 | `fe1b60b`, remediation `77b2d16` |
| S3 | Closure orchestration consolidation (items 1/30/6/13/14); REQ-COC-01..04 | `200cdaa`, remediation `579347b` |
| S4a | Immutable closure DTOs (items 3/15/19/20); REQ-ADB-01/04 (closure half) | `829778c` |
| S4b | Immutable drawer DTOs (items 15/19/20); REQ-ADB-02/03/04 (drawer half) | `65e038a` |
| S5a | CancellationToken propagation (items 4/9/16/21/27) + H-14 fold-in; REQ-ACP-01..03 | `227ee5c` |
| S5b | EF read tuning + guards/naming/comments (items 7/22/31 + groups H/D) | `dab4d16` |
| S5c | J findings H-05/H-06/H-08 + item-11 carry | `bb9a277` |

All eight slices verified `pass_with_warnings` — none failed, none blocked. Implementation commits
span `d7593c1` → `bb9a277`. Interleaved docs commits record each acceptance (`b36f8e6`, `30bae63`,
`0f5a8c3`, `7543d22`, `2d3c7f6`, `03091bb`, `9c0696b`, `39a388d`) plus `00adc45` (S1 verify `fail`),
`ef6efe4` (GGA findings appended to the registry) and `7eb430f` (final verify + consolidated
inventory). Planning: `3e90ff3` (change base `3e90ff36`); 22 commits after base.

## Verify Report (Final State)

| Metric | Value |
|--------|-------|
| Verdict | PASS_WITH_WARNINGS — 0 blockers, 0 critical findings |
| Requirements | 18/18 compliant |
| Scenarios | 38/38 compliant |
| Tasks | 72/72 checked (0 unchecked) |
| Build | 0 errors / 0 warnings (`dotnet build CommandCenter.slnx -c Release`) |
| Backend suite | 1227/1227 passed, 0 failed, 0 skipped |
| Frontend | `npm test` 273/273; `npm run lint` clean |
| Coverage (Core) | 0.8364 (gate ≥ 0.70) |
| Coverage (Sales.Module) | 0.9073 (gate ≥ 0.80) |
| Coverage (Inventory.Module) | 0.8251 (gate ≥ 0.72) |
| Admission | `gentle-ai sdd-verify-validate --requirements 18 --scenarios 38` exit 0; persisted report re-admitted |

Per capability: `payment-method-currency-classification` (MODIFIED) 3/8, `api-error-contract` 4/9,
`closure-orchestration-consolidation` 4/8, `api-dto-boundary` 4/7, `async-cancellation-propagation`
3/6. Verified revision: HEAD `39a388d` (code tree identical to `bb9a277`); final docs commit
`7eb430f`. The change-level verification re-executed build, backend, frontend, lint and coverage
independently with a clean working tree (hashes recorded in the verify report).

## Specs Synced

| Domain | Action | Details |
|--------|--------|---------|
| `api-error-contract` | Created | 4 requirements, 9 scenarios — mechanical byte-identical copy → `openspec/specs/api-error-contract/spec.md` |
| `api-dto-boundary` | Created | 4 requirements, 7 scenarios — mechanical copy → `openspec/specs/api-dto-boundary/spec.md` |
| `closure-orchestration-consolidation` | Created | 4 requirements, 8 scenarios — mechanical copy → `openspec/specs/closure-orchestration-consolidation/spec.md` |
| `async-cancellation-propagation` | Created | 3 requirements, 6 scenarios — mechanical copy → `openspec/specs/async-cancellation-propagation/spec.md` |
| `payment-method-currency-classification` | Updated | REQ-PMC-01 replaced (scope extended to `CloseShift`; +2 scenarios), REQ-PMC-04/05 appended; REQ-PMC-02/03 preserved byte-for-byte; final 5 requirements / 10 scenarios — native `gentle-ai sdd-archive-compose`, exit 0 |

No REMOVED deltas — the config's destructive-delta warning does not apply.

## Archive Contents

- `proposal.md` ✅
- `specs/` ✅ (5 capabilities)
- `design.md` ✅
- `tasks.md` ✅ (72/72 tasks complete)
- `apply-progress.md` ✅
- `verify-report.md` ✅
- `archive-report.md` ✅

Moved with `git mv` (pure renames, 0 insertions/0 deletions) and verified against a pre-move
recursive snapshot (`diff -r` empty, exit 0).

## Source of Truth Updated

- `openspec/specs/api-error-contract/spec.md`
- `openspec/specs/api-dto-boundary/spec.md`
- `openspec/specs/closure-orchestration-consolidation/spec.md`
- `openspec/specs/async-cancellation-propagation/spec.md`
- `openspec/specs/payment-method-currency-classification/spec.md`

## Residual / Follow-up Inventory (all non-blocking)

| Item / family | Origin | Nature and current status |
|---|---|---|
| Registry items 40/41 | proposal (deferred, out of scope) | OpenCode `question` tooling; WPF hardcoded advance commission (top follow-up candidate) |
| WARNING-04 | S1 → final | Merged undeclared-method lines hardcode `Balanced` while `DifferenceBsS` can be non-zero and mix units — needs its own fix |
| WARNING-07 / S3-06 / S4a-R2 | S1 → S4a → final | `DailyClosureService.cs` at 650 lines — over the 300-500 line ceiling |
| S3-07 / S4a-R1 | S3 → S4a | Legacy concrete-only `CreateClosureAsync(DailyClosure)` entry point (entity-returning, no production caller) |
| S5a-R1 | S5a (+S5b partial) | CT-less surfaces left outside the registered items (`ISystemSettingsService.GetSettingAsync`, some `SalesService` call sites, `RecalculateOnHoldSalesAsync`) |
| S5b-R1 | S5b GGA triage | H-03 in `CashDrawerController` (`DbContext` injection, BCV anchoring, user lookup) — needs its own work unit |
| S5c-R1 | S5c GGA triage | `AuthController` error contract (anonymous `{ Message }` responses, CT-less actions) — never in the S2 sweep |
| RESIDUAL families S1..S5b | slice verifications | Informational/evidence-quality items; later slices closed `S4b-R1`, `RESIDUAL-S5a-02`, `RESIDUAL-S5b-06`; `RESIDUAL-S4b-04` is a pre-existing Web `advance` filter bug (out of scope) |
| SIZE-S4a / S4b / S5a / S5b / S5c | all slices | 705 / 1053 / 1014 / 753 / 625 changed lines — each authorized as a chained work unit under `ask-on-risk`; no `size:exception` invoked |
| S5c-D2 + bookkeeping suggestion | S5c | Four UserControls deliberately carry no disposal hook (DI-owned singletons); `design.md` H-05/H-06/H-08 Open Question boxes remain unchecked though delivered |

No item is a blocker, a critical finding, or a spec-scenario failure.

## Process & Environment Notes

- **GGA hook exceptions**: slices used documented, punctual `--no-verify` GGA exceptions for
  pre-existing, out-of-scope findings (each slice's section in `apply-progress.md`); none of those
  findings was introduced by this change.
- **Archive composition note**: the delta's MODIFIED heading carries the change-local ID prefix
  (`REQ-PMC-01`), which does not match the canonical requirement name; `sdd-archive-compose`
  refuses such a delta as-authored. Composition ran natively against a canonically-named copy of the
  delta (heading lines only normalized; bodies/scenarios byte-identical — verified by diff). Canonical
  requirement names are unchanged. The native composer joined the first appended requirement heading
  without a separating blank line (cosmetic only; heading and body intact).
- **Database**: `TEST_POSTGRES_CONNECTION` unset — Postgres-gated classes early-return as vacuous
  passes; the change's transactional coverage is provided by the relational SQLite
  `DailyClosureTransactionTests` (2/2, discriminating).
- **Environment**: SDD dispatch channel temporarily blocked; this archive phase was executed by the
  general fallback agent under explicit orchestrator authorization (orchestrator-authorized bypass).
  `gentle-ai sync` + OpenCode restart pending as maintenance.

## Key Learnings

1. A delta MODIFIED heading carrying a change-local ID prefix cannot be composed into a canonical
   spec that names the requirement without the prefix; normalize the heading for the compose input
   while keeping the archived delta bytes untouched.
2. `git mv` of a directory stages a pure rename (0 insertions/0 deletions), proving byte-identity
   independently of the recursive snapshot diff.
3. Punctual `--no-verify` GGA exceptions must name the pre-existing findings and their future slice;
   all eight slices documented each exception before committing.
