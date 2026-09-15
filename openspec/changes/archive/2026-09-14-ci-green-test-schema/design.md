# Design: ci-green-test-schema

## Technical Approach

Test-tooling and CI only. A process-wide bootstrap materializes both EF models on `pos_test` with
model parity (no `Migrate`); a per-row idempotent seeder runs under `pg_advisory_xact_lock`; CI on
`windows-2025` drops/recreates `pos_test`; frontend on Node 24.

## Architecture Decisions

### Bootstrap component shape

New `CommandCenter.Tests/Builders/TestSchemaBootstrap.cs` (`public static class`) is the single choke
point. API: `Task EnsureSharedSchemaAsync(string, CancellationToken=default)` plus sync
`void EnsureSharedSchema(string)` for the factory.

| Aspect | Choice |
|---|---|
| Key | `NpgsqlConnectionStringBuilder` → `"{Host}:{Port}/{Database}"`. |
| Locking | `ConcurrentDictionary<string, Lazy<Task>>` (single-flight) + `SemaphoreSlim(1,1)` pass guard. Process-local suffices (one testhost per assembly). |
| Database | `IRelationalDatabaseCreator.Exists()` → `Create()` when absent. |
| Marker | `SqlQueryRaw<int>` counting `information_schema.tables`. Sales → `Users`, Inventory → `Products`. |
| Create | Marker absent → `creator.CreateTables()` (model DDL + `HasData` + sequences). Models disjoint. |
| Failure | Marker still absent → `InvalidOperationException` naming the missing table and database. |
| Bypass | InMemory/Sqlite never call it; only the Npgsql factory methods and `HistoryImmutabilityTests` do. |

### Seeding algorithm

Canonical rows aligned to the model: Id 1 `"Cash"`, Id 2 `"Card"` (model wins), Id 3-5
`"Punto de Venta"` / `"Pago Móvil"` / `"Zelle"`. Per row, plus Customer Id 1:

- name present (any Id, `IsDeleted=false`) → skip;
- name absent, Id free → insert at that Id;
- name absent, Id taken → insert at next free Id (name is the stable identity).

Npgsql: wrap in `Database.CreateExecutionStrategy().ExecuteAsync(...)` (the retry context's strategy
rejects bare user transactions), `BeginTransactionAsync`,
`ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", SeedLockKey)`, check/insert, commit. The
`xact` lock releases at commit. InMemory/Sqlite unchanged.

Reconciliation: (a) name under another Id → skip, never duplicate; (b) Id taken → insert under a free
Id (only a fresh schema guarantees Id 3; consumers resolve by name); (c) Inventory-only → bootstrap
adds Sales, then 3-5 seed; (d) partial Sales schema (markers pass, tables missing) is unrepaired —
first absent relation fails `42P01`, remedied by CI reset.

### Call sites

| File | Change |
|---|---|
| `TestDatabaseFactory.cs` | Replace both `EnsureCreated()` (L44, L63) with `EnsureSharedSchema(connStr)`. |
| `HistoryImmutabilityTests.cs` | Call `EnsureSharedSchemaAsync` once; delete both `EnsureCreatedAsync()` and the raw `ExchangeRateHistory` DDL. |
| `SalesServiceUnitTests.cs` | Call `SeedStandardSalesDataAsync(seedContext!)`; resolve `pm` by name; route L648 through `CreatePostgreSqlSalesDbContext()!`. |
| `DailyClosureRetryIntegrationTests.cs` | Resolve `puntoDeVentaId` by name after seeding; use it in `SalePayment` and `ClosureDetail`. |

### CI workflow (`timeout-minutes: 45`)

Order: checkout → setup .NET 10 → NuGet cache → `dotnet restore` → `dotnet build -c Release` → Start
PostgreSQL (pwsh) → tests → coverage gate → vuln audit → upload artifact (`if: always()` on the last
three).

Start step (pwsh):

1. `$service = Get-Service -Name 'postgresql*' | Select-Object -First 1`; throw if null.
2. `Set-Service -Name $service.Name -StartupType Manual` then `Start-Service` (image ships `Disabled`;
   `$ErrorActionPreference='Stop'` aborts otherwise).
3. `$pgRoot` = newest numeric dir under `C:\Program Files\PostgreSQL`; `$psql = <dir>\bin\psql.exe`;
   `$env:PGPASSWORD = 'root'`.
4. Poll `& $psql -U postgres -h localhost -tAc 'SELECT 1'` 30x/2s.
5. `DROP DATABASE IF EXISTS pos_test WITH (FORCE)`; `CREATE DATABASE pos_test`; throw on non-zero.

Test env: `TEST_POSTGRES_CONNECTION` (localhost/pos_test/postgres/root) and
`SMOKE_DB_SUFFIX: ${{ github.run_id }}`.

### Frontend engine

Add `"engines": { "node": ">=24" }` to `Web.Frontend/package.json`. No `.npmrc`/`engine-strict`, so
Node 22 warns only; CI pins `24.x`.

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Integration | Both markers; `AccessFailedCount`; `admin` `HasData`; Ids 1-2 `"Cash"`/`"Card"` | Guard test (`RequiresDocker`) over `information_schema` |
| Integration | Bootstrap idempotency; two concurrent callers, no `23505` | Same guard test |
| Integration | Seeder twice → Ids 1-5 once, `"Punto de Venta"`=3, Customer Id 1 `IsDefault` | `pos_test` via factory |
| Integration | Edge cases (a)-(d) | Scratch DB `pos_seed_<guid>`; (d) expects fail-loud |
| Regression | Existing suite on fresh `pos_test` | Full `dotnet test` in CI |
| CI YAML | Syntax/order locally (`actionlint`); service start, readiness, reset only on the runner | CI authoritative |

## Threat Matrix

N/A — no routing, classification, or VCS/PR automation; CI steps use fixed tools with pinned
arguments.

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Service `Disabled` blocks start | High | `Set-Service -StartupType Manual` first |
| Legacy half-initialized `pos_test` | Med | Marker repair + CI drop/recreate |
| Accidental `Migrate` regresses `42703` | Med | Keep `Migrate` out |
| Partial schema not repaired | Low | Fail-loud `42P01`; CI reset |
| Windows job timeout | Med | `timeout-minutes: 45` |
| Node/npm patch drift | Low | `engines.node >= 24` + pinned CI |

## Rollout / Rollback

No product data migration. Merge; CI exercises the fresh-DB path. Rollback: `git revert`;
delete `TestSchemaBootstrap.cs`; `pos_test` is disposable.

## Open Questions

- [ ] Detect partial per-model schemas (markers pass, tables missing)? Deferred; CI reset masks it.
- [ ] When does the `AccessFailedCount` migration get its own change?
