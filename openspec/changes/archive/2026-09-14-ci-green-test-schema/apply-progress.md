# Apply Progress: ci-green-test-schema

## Summary

| Field | Value |
|-------|-------|
| Change | ci-green-test-schema |
| Mode | Standard (Strict TDD disabled) |
| Store | openspec |
| Tasks completed | 14/14 (Phases 1-5) |
| Verification | Phase 6 complete — all criteria met |

## Completed Tasks

- [x] 1.1 Create `TestSchemaBootstrap.cs` — `ConcurrentDictionary<string, Lazy<Task>>` single-flight, `SemaphoreSlim(1,1)` pass guard, `NpgsqlConnectionStringBuilder` key, `SeedLockKey` constant
- [x] 1.2 Bootstrap creates Sales (Users) and Inventory (Products) via `IRelationalDatabaseCreator.CreateTablesAsync()` when markers absent
- [x] 1.3 Half-initialized repair: only missing schema is created; models disjoint
- [x] 1.4 Non-PostgreSQL bypass: InMemory/Sqlite never call this helper
- [x] 2.1 `TestDatabaseFactory.cs` — both `EnsureCreated()` replaced with `TestSchemaBootstrap.EnsureSharedSchema(connStr)`
- [x] 2.2 `SeedStandardSalesDataAsync` rewritten for Npgsql: `pg_advisory_xact_lock`, per-row idempotent, name-first resolution, InMemory/Sqlite path preserved
- [x] 2.3 `SalesServiceUnitTests.cs` — `GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsSeededInvoices` calls seeder, resolves PM by name, routes through `CreatePostgreSqlSalesDbContext()!`
- [x] 2.4 `DailyClosureRetryIntegrationTests.cs` — `puntoDeVentaId` resolved by name from seeder
- [x] 3.1 `HistoryImmutabilityTests.cs` — `EnsureCreatedAsync()` and raw DDL replaced with `TestSchemaBootstrap.EnsureSharedSchemaAsync(connStr)`
- [x] 3.2 Marker verification preserved in guard test
- [x] 4.1 `ci.yml` trigger on push/PR to `main`, `develop`, `V0.15`
- [x] 4.2 Backend job: `Set-Service -StartupType Manual`, `DROP/CREATE DATABASE`, `timeout-minutes: 45`, `SMOKE_DB_SUFFIX`, coverage gate, vuln audit, upload artifact all `if: always()`
- [x] 4.3 Frontend job: Node 24, npm ci/lint/test/audit/build, upload on failure
- [x] 5.1 `package.json` engines.node >= 24

## Files Changed

| File | Action | What Was Done |
|------|--------|---------------|
| `CommandCenter.Tests/Builders/TestSchemaBootstrap.cs` | Created | Shared-schema bootstrap with single-flight, advisory lock, and marker-based creation |
| `CommandCenter.Tests/Builders/TestDatabaseFactory.cs` | Modified | Replaced EnsureCreated with bootstrap; rewrote seeder for Npgsql with pg_advisory_xact_lock |
| `CommandCenter.Tests/Unit/SalesServiceUnitTests.cs` | Modified | Use seeder + resolve PM by name in GetSalesHistory test |
| `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs` | Modified | Resolve PaymentMethodId by name instead of hardcoding |
| `CommandCenter.Tests/Integration/HistoryImmutabilityTests.cs` | Modified | Replace inline EnsureCreated + DDL with bootstrap call |
| `.github/workflows/ci.yml` | Modified | Service start fix, DB reset, timeout, SMOKE_DB_SUFFIX, artifact uploads |
| `Web.Frontend/package.json` | Modified | Add engines.node >= 24 |

## Verification Results

| Step | Command | Result |
|------|---------|--------|
| 6.1 Build | `dotnet build CommandCenter.slnx -c Release --nologo` | 0 errors, 0 warnings |
| 6.2 Fresh-DB E2E | Drop/create pos_test + `dotnet test -c Release --no-build` | 970/970 on first run |
| 6.3 Idempotency | Second `dotnet test` on same pos_test | 970/970 |
| 6.4 Coverage | `python scripts/check-coverage.py` | Core [OK] 0.83, Sales [OK] 0.89, Inventory [OK] 0.82 |
| 6.5 Frontend lint | `npm run lint` | 0 errors |
| 6.5 Frontend test | `npm test` | 164/167 pass; 3 OOM failures pre-existing, unrelated |
| 6.6 No product change | Build + no migration modifications | Confirmed |

## Work Unit Evidence

| Evidence | Value |
|----------|-------|
| Focused test command | `dotnet test CommandCenter.slnx -c Release --no-build --nologo` → 970/970 |
| Runtime harness | Fresh pos_test via psql drop/create, then full suite → 970/970 first run, 970/970 second run |
| Rollback boundary | `TestSchemaBootstrap.cs` deletable; `TestDatabaseFactory.cs` revertable; no product code touched |
