# Proposal: ci-green-test-schema

## Intent

Backend CI is red since 2026-09-07: a `net10.0-windows` target runs on Linux, the testhost aborts, and the coverage gate cascades to failure. Frontend `npm ci` fails under npm 10. On a fresh `pos_test`, parallel classes race to create the schema (database-wide, non-atomic `EnsureCreated`) and to insert `"Punto de Venta"`, making the suite non-deterministic. Goal: green CI and a deterministic fresh-DB suite with no product-behavior change.

## Scope

### In Scope
- Backend job on `windows-2025`: `Set-Service -StartupType Manual` before `Start-Service`; readiness wait; `DROP DATABASE IF EXISTS pos_test WITH (FORCE)` then `CREATE DATABASE pos_test`; PowerShell 7 coverage gate and vuln audit; `timeout-minutes: 45`; `SMOKE_DB_SUFFIX`.
- Frontend job on Node `24.x` (npm 11); `engines.node >= 24` in `Web.Frontend/package.json`.
- Shared-schema bootstrap (one-time, process-wide, per-connection-string) creating both models via `RelationalDatabaseCreator.CreateTables()` (markers `Users`/`Products`), through `TestDatabaseFactory` and `HistoryImmutabilityTests`.
- Race-free `SeedStandardSalesDataAsync` (per-row idempotent, deterministic PaymentMethod Ids 1-5, `pg_advisory_xact_lock`); remove ad-hoc seed writers.

### Out of Scope
- Product behavior, EF migrations, `DatabaseInitializer.cs`, production schema; the `AccessFailedCount` gap is a follow-up.
- `Migrate` on the shared test DB; per-class isolation; cross-class pollution; CI plan changes.

## Capabilities

### New Capabilities
- `ci-pipeline`: green backend and frontend jobs on push/PR to `main`, `develop`, `V0.15`; fresh `pos_test` per backend run; Node 24 frontend.
- `test-schema-bootstrap`: both schemas guaranteed on the shared test DB regardless of scheduling order.
- `test-data-seeding`: idempotent, concurrency-safe seed data with no bypassing writer.

### Modified Capabilities
- None.

## Approach

One process-wide helper guards each shared DB once, creating both models by marker table and asserting both markers exist. Keep `Migrate` out. Seed idempotently per row under `pg_advisory_xact_lock` on Npgsql; keep InMemory/Sqlite behavior. Fix the disabled service start and reset `pos_test` per run.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `.github/workflows/ci.yml` | Modified | Runner, service start, DB reset, gate/audit |
| `CommandCenter.Tests/Builders/TestSchemaBootstrap.cs` | New | Shared-schema bootstrap |
| `CommandCenter.Tests/Builders/TestDatabaseFactory.cs` | Modified | Route factory and seeding |
| `CommandCenter.Tests/Integration/HistoryImmutabilityTests.cs` | Modified | Drop inline `EnsureCreated` and DDL |
| `CommandCenter.Tests/Unit/SalesServiceUnitTests.cs` | Modified | Use seeder; resolve by name |
| `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs` | Modified | Resolve `PaymentMethodId` by name |
| `Web.Frontend/package.json` | Modified | `engines.node >= 24` |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Runner PostgreSQL service `Disabled` | High | `Set-Service -StartupType Manual` first |
| Legacy half-initialized `pos_test` | Med | Marker repair; CI drop/recreate |
| Accidental `Migrate` regresses `42703` | Med | Keep `Migrate` out |
| Job timeout or image drift | Med | `timeout-minutes: 45`; version discovery |

## Rollback Plan

Test tooling and CI configuration only; no product code, migrations, or persisted data change. Revert the change commits with `git revert`; delete `TestSchemaBootstrap.cs` if extracted. `pos_test` is disposable.

## Dependencies

- `windows-2025` runner with preinstalled PostgreSQL 17 and .NET 10 SDK.
- Node 24 (npm 11); npm 10 rejects the lockfile's `@emnapi/*` entries.

## Success Criteria

- [ ] Both CI jobs green on push/PR to `main`, `develop`, `V0.15`.
- [ ] Backend job passes from a freshly created `pos_test`.
- [ ] `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` and `npm test` green on a fresh DB.
- [ ] `dotnet build CommandCenter.slnx -c Release`: 0 errors, 0 warnings; no product, migration, or production schema change.
