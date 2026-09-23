# Archive Report: ci-green-test-schema

**Archived**: 2026-09-14
**Store**: OpenSpec (openspec)
**Status at close**: All tasks complete, verification pass_with_warnings (0 critical), archive ready.

## What Shipped

Three new capabilities for a deterministic, green CI pipeline and a race-free shared test database:

- **ci-pipeline**: Backend job on `windows-2025` with PostgreSQL service start fix, `DROP/CREATE DATABASE pos_test`, PowerShell 7 coverage gate and vuln audit, `timeout-minutes: 45`, `SMOKE_DB_SUFFIX`; frontend job on Node 24 (npm 11).
- **test-schema-bootstrap**: Process-wide, per-connection-string bootstrap materializing both Sales and Inventory schemas on `pos_test` via marker tables, with single-flight (`ConcurrentDictionary<string, Lazy<Task>>`), half-initialized repair, and `Migrate` exclusion to preserve model parity (`AccessFailedCount`).
- **test-data-seeding**: `pg_advisory_xact_lock`-serialized, per-row idempotent seeding of `PaymentMethods` (Ids 1-5) and default `Customer`; all test writers routed through the seeder, no ad-hoc inserts.

No product code, migrations, or production schema was modified. `TestSchemaBootstrap.cs` is deletable; all other files are revertable. `pos_test` is disposable.

## Specs Synced

| Domain | Action | Details |
|--------|--------|---------|
| ci-pipeline | Created | 6 requirements, 11 scenarios — copied as full spec (main spec did not exist) |
| test-schema-bootstrap | Created | 6 requirements, 9 scenarios — copied as full spec (main spec did not exist) |
| test-data-seeding | Created | 4 requirements, 8 scenarios — copied as full spec (main spec did not exist) |

Main spec destinations:
- `openspec/specs/ci-pipeline/spec.md`
- `openspec/specs/test-schema-bootstrap/spec.md`
- `openspec/specs/test-data-seeding/spec.md`

## Archive Contents

| Artifact | Status |
|----------|--------|
| proposal.md | present |
| exploration.md | present |
| specs/ci-pipeline/spec.md | present |
| specs/test-schema-bootstrap/spec.md | present |
| specs/test-data-seeding/spec.md | present |
| design.md | present |
| tasks.md | present (14/14 tasks complete) |
| apply-progress.md | present |
| verify-report.md | present |

## Final State (per Final-State Authority)

- **Tasks**: 14/14 checked in `tasks.md` (source of truth for completion visibility).
- **Verification**: `pass_with_warnings` — 0 blockers, 0 CRITICAL findings, 16/16 requirements, 28/28 scenarios.
- **.NET suite**: 970 passed / 0 failed / 0 skipped on a freshly created `pos_test` and again on repeat build Release 0 warnings / 0 errors.
- **Coverage gate** (with `TEST_POSTGRES_CONNECTION` set): Core 0.8333 / Sales.Module 0.8911 / Inventory.Module 0.8214 — all `[OK]`; `check-coverage.py` exit 0.
- **Frontend**: `npm ci` clean (0 vulnerabilities), lint 0 errors, `npm test` 178/178 pass. The earlier `164/167 with 3 OOM` recorded in `apply-progress.md` was a transient resource-exhaustion run and is superseded.
- **Half-initialized repair**: independently verified end-to-end (Products-only scratch DB gained `Users` after bootstrap).

## Verify Warnings Carried as Follow-Ups

- **W1 — No automated guard tests for bootstrap/seeding** (design test-strategy gap): The `design.md` testing strategy promised `RequiresDocker` integration guard tests covering bootstrap idempotency, concurrent serialization, missing marker abort, and concurrent seeders. No such test file exists. Concurrency and marker-failure scenarios rest on code inspection plus the full-suite runtime only. Recommended follow-up.
- **W3 — Two non-breaking design deviations**: (a) Bootstrap does not create the database when missing; CI creates it explicitly via `CREATE DATABASE`. (b) Seeder uses a raw Npgsql transaction instead of `CreateExecutionStrategy().ExecuteAsync(...)`. Functionally sound; no spec scenario depends on these implementation details.
- **S1-S4 — Minor suggestions**: dead `SeedLockKey` vs hardcoded lock key 42000; redundant `SemaphoreSlim(1,1)` given single-flight `Lazy<Task>`; no `Test-Path` check on resolved `psql`; smoke DBs never dropped.

## Residual Risks

1. **Actual GitHub Actions execution NOT locally verifiable.** All `windows-2025` runner behavior (PostgreSQL service start, readiness timing, timeout sufficiency) and the Ubuntu + Node 24 frontend job are asserted from static YAML plus local equivalents. The first CI push/PR run is the authoritative confirmation.
2. **Concurrency and marker-failure paths lack automated test coverage** (W1); verified by construction and full-suite runtime only.
3. **Bootstrap assumes the target database exists** (W3a). A wholly absent database would surface an Npgsql error instead of being auto-created by the helper; CI's explicit `CREATE DATABASE` masks this.
4. **Theoretical frontend OOM in CI** remains possible, though the current local run is 178/178 with 0 failures.

## Mechanical Copy Verification

- **Step 2 (spec sync)**: All three delta specs copied via `cp`; SHA-256 hash comparison confirmed byte-identity for all three domains.
- **Step 3 (archive move)**: `git mv` failed (source directory not tracked); `Move-Item` fallback succeeded. Robocopy comparison and SHA-256 hash verification across all 9 files confirmed byte-identity between source snapshot and archive destination. Source directory confirmed absent.

## Decision Log

- No destructive deltas (0 REMOVED requirements). All three delta specs were new capabilities (ADDED only).
- Delta specs were full specs (main specs did not exist prior to archive) — copied as-is.
- Archive performed without user override needed.
