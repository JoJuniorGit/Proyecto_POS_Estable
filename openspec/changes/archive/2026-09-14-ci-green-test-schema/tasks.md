# Tasks: ci-green-test-schema

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | 280-380 |
| 400-line budget risk | Medium |
| Chained PRs recommended | No |
| Suggested split | Single PR |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending |

Decision needed before apply: Yes
Chained PRs recommended: No
Chain strategy: pending
400-line budget risk: Medium

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Shared-schema bootstrap and seeding fix | PR 1 | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~HistoryImmutability\|FullyQualifiedName~SalesServiceUnit\|FullyQualifiedName~DailyClosureRetry"` | Fresh `pos_test` via psql drop/create, then full suite | `TestSchemaBootstrap.cs` deletable; `TestDatabaseFactory.cs` revertable; no product code touched |
| 2 | CI workflow and frontend engine | PR 1 | `npm ci && npm test` under Node 24; CI syntax via `actionlint` if available | CI run on push | `.github/workflows/ci.yml` deletable; `package.json` revertable |

## Do-Not-Touch Guard

The following files MUST NOT be modified by this change:

- `.opencode/agent/reviewer.md`
- `AGENTS.md`
- `docs/INSTALLATION.md`
- `Web.Frontend/README.md`
- `docs/reporte.txt`
- `ARCHITECTURE.md`
- `architecture-*.md`
- `docs/INSTALLATION-advanced.md`
- Any EF migration file
- `Backend.API/Startup/DatabaseInitializer.cs`
- `CommandCenter.Tests/coverage.runsettings`
- `scripts/check-coverage.py`
- `CommandCenter.Tests/CommandCenter.Tests.csproj`
- `Directory.Build.props`
- `CommandCenter.Tests/Integration/ConcurrencyCapacityTests.cs`
- `CommandCenter.Tests/Integration/FaultToleranceTests.cs`
- `CommandCenter.Tests/Integration/WebApplicationFactorySmokeTests.cs`

## Phase 1: Test-Schema Bootstrap

- [x] 1.1 Create `CommandCenter.Tests/Builders/TestSchemaBootstrap.cs` as a `public static class` with `EnsureSharedSchemaAsync(string connStr, CancellationToken)` and sync `EnsureSharedSchema(string)` wrapper (uses `GetAwaiter().GetResult()` per binding clarification 4). Key by normalized `"{Host}:{Port}/{Database}"` via `NpgsqlConnectionStringBuilder`. Use `ConcurrentDictionary<string, Lazy<Task>>` for single-flight and `SemaphoreSlim(1,1)` as pass guard. Define a fixed `SeedLockKey` advisory-lock constant.
- [x] 1.2 Inside the bootstrap: open a `SalesDbContext` connection, check marker `Users` via `SqlQueryRaw<int>` on `information_schema.tables`; if absent, call `IRelationalDatabaseCreator.CreateTables()` on a Sales context. Then open an `InventoryDbContext` connection, check marker `Products`; if absent, call `CreateTables()` on an Inventory context. Assert both markers exist post-creation; throw `InvalidOperationException` naming the missing table and database if either is absent.
- [x] 1.3 Handle the half-initialized repair case: if one schema already exists (marker present), only create the missing one. Models are disjoint so no collision occurs. Skip `Migrate` entirely.
- [x] 1.4 Bypass for non-PostgreSQL: document that InMemory/Sqlite contexts must never call this helper. Only Npgsql factory methods and `HistoryImmutabilityTests` invoke it.

## Phase 2: Seeding Fix

- [x] 2.1 Modify `CommandCenter.Tests/Builders/TestDatabaseFactory.cs`: replace both `EnsureCreated()` calls (L44, L63) with `TestSchemaBootstrap.EnsureSharedSchema(connStr)`.
- [x] 2.2 Rewrite `SeedStandardSalesDataAsync` for Npgsql provider: open a transaction, execute `SELECT pg_advisory_xact_lock({SeedLockKey})`, then seed per-row idempotently. For each PaymentMethod (Id 1 "Cash", Id 2 "Card", Id 3 "Punto de Venta", Id 4 "Pago Movil", Id 5 "Zelle"): skip if name exists (any Id, `IsDeleted=false`); insert at canonical Id if free; insert at next free Id if canonical is taken. Seed Customer Id 1 `IsDefault=true` when absent. Commit transaction (releases xact lock). InMemory/Sqlite keep the current "seed when empty" path without advisory locks.
- [x] 2.3 Modify `CommandCenter.Tests/Unit/SalesServiceUnitTests.cs`: in `GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsSeededInvoices`, replace the ad-hoc `PaymentMethod { Name = "Punto de Venta" }` insert with a call to `SeedStandardSalesDataAsync(seedContext!)`. Resolve `pm` by name (`"Punto de Venta"`) from the context instead of hardcoding an Id. Route L648 through `CreatePostgreSqlSalesDbContext()!`.
- [x] 2.4 Modify `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs`: resolve `puntoDeVentaId` by name from the seeded `PaymentMethods` table after calling the seeder, instead of hardcoding Id 3.

## Phase 3: HistoryImmutabilityTests Cleanup

- [x] 3.1 Modify `CommandCenter.Tests/Integration/HistoryImmutabilityTests.cs`: delete both `EnsureCreatedAsync()` calls and the raw `CREATE TABLE IF NOT EXISTS "ExchangeRateHistory"` DDL. Replace with a single call to `TestSchemaBootstrap.EnsureSharedSchemaAsync(connStr)`. The bootstrap now guarantees both schemas including `ExchangeRateHistory` via the Inventory model.
- [x] 3.2 Verify that the `ExchangeRateHistory` table is present after bootstrap by inspecting `information_schema.tables` in the guard test.

## Phase 4: CI Workflow

- [x] 4.1 Create `.github/workflows/ci.yml` with two jobs: `backend-build-and-test` on `windows-2025` and `frontend-lint-and-test` on `ubuntu-latest` with Node `24.x`. Trigger on push/PR to `main`, `develop`, `V0.15`.
- [x] 4.2 Backend job steps: checkout, setup .NET 10, NuGet cache, `dotnet restore`, `dotnet build CommandCenter.slnx -c Release`, Start PostgreSQL (pwsh with `Set-Service -StartupType Manual` then `Start-Service`; poll readiness 30x/2s; `DROP DATABASE IF EXISTS pos_test WITH (FORCE)`; `CREATE DATABASE pos_test`; throw on non-zero), run tests (`TEST_POSTGRES_CONNECTION` set; `SMOKE_DB_SUFFIX: ${{ github.run_id }}`), coverage gate (pwsh; fail actionable if `coverage.cobertura.xml` missing), vulnerable-package audit (pwsh; fail if dotnet output matches markers), upload artifact (`if: always()` on last three). Set `timeout-minutes: 45`.
- [x] 4.3 Frontend job steps: checkout, setup Node `24.x`, `npm ci`, `npm run lint`, `npm test`, `npm audit`, `npm run build`. Upload artifact on failure.

## Phase 5: Frontend Engine Declaration

- [x] 5.1 Add `"engines": { "node": ">=24" }` to `Web.Frontend/package.json`. No `.npmrc` or `engine-strict`; Node 22 warns only; CI pins `24.x`.

## Phase 6: Verification

- [x] 6.1 Build: `dotnet build CommandCenter.slnx -c Release --nologo` → 0 errors, 0 warnings.
- [x] 6.2 Fresh-DB end-to-end: DROP/CREATE pos_test → `dotnet test` → 970/970 on FIRST run.
- [x] 6.3 Idempotency: second `dotnet test` on same pos_test → 970/970.
- [x] 6.4 Coverage gate: `python scripts/check-coverage.py` → Core [OK], Sales.Module [OK], Inventory.Module [OK], 0 failures.
- [x] 6.5 Frontend: `npm ci` → `npm run lint` (0 errors) → `npm test` (164/167 pass; 3 OOM failures are pre-existing, unrelated to this change).
- [x] 6.6 No product code changed: build still 0 errors/0 warnings; no migration files modified.

## Phase 7: Rollback Notes

- **Phase 1-3 rollback**: delete `TestSchemaBootstrap.cs`; revert `TestDatabaseFactory.cs`, `SalesServiceUnitTests.cs`, `DailyClosureRetryIntegrationTests.cs`, `HistoryImmutabilityTests.cs` to their pre-change state. `pos_test` is disposable.
- **Phase 4 rollback**: delete `.github/workflows/ci.yml` entirely.
- **Phase 5 rollback**: remove `"engines"` field from `Web.Frontend/package.json`.
- **No product code, migrations, or production schema is touched by any phase.**
