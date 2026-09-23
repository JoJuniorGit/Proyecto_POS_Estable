# Exploration: ci-green-test-schema

Change: `ci-green-test-schema`
Artifact store: `openspec`
Mode: read-only over source; this file is the only write.
Date: 2026-09-14

## Objective

Map how to make the repository CI fully green and its .NET test suite deterministic against a
**freshly created** PostgreSQL database, including removing shared-test-database data races.
Infrastructure/test-tooling only: no product feature behavior may change.

---

## 1. Current State

### 1.1 CI topology

`.github/workflows/ci.yml` defines two jobs, both triggered on push/PR to `main`, `develop`, `V0.15`:

| Job | Runner (committed) | Runner (working tree draft) | Purpose |
|---|---|---|---|
| `backend-build-and-test` | `ubuntu-latest` + `services: postgres:16` | `windows-2025` (no service container) | `dotnet restore` → `dotnet build -c Release` → `dotnet test` + coverage gate + vuln audit |
| `frontend-lint-and-test` | `ubuntu-latest`, Node `22.x` | `ubuntu-latest`, Node `24.x` | `npm ci` → lint → test → audit → build |

The draft (`git diff .github/workflows/ci.yml`, uncommitted, `M`) already:
- moves the backend job to `windows-2025`, drops the `postgres:16` service, adds a
  `Start PostgreSQL and Create Test Database` step;
- rewrites the coverage gate and the vulnerable-package audit in PowerShell 7;
- switches `TEST_POSTGRES_CONNECTION` password from `testpass` to `root`;
- moves the frontend job to `node-version: '24.x'`.

### 1.2 Why the backend job is structurally red on Linux

`CommandCenter.Tests/CommandCenter.Tests.csproj` targets `net10.0-windows` with
`TargetPlatformIdentifier=Windows` / `TargetPlatformVersion=10.0.19041.0`, and `ProjectReference`s
`Desktop.Client.csproj` (WPF) and `Desktop.Client.Core`. `EnableWindowsTargeting=true` in
`Directory.Build.props` lets the project **compile** on Linux, but the testhost requires the
`Microsoft.WindowsDesktop.App` shared framework, which does not exist on Linux, so the run aborts
before any test executes. No `coverage.cobertura.xml` is produced, so the `if: always()` coverage
step exits 1 and the job fails in cascade. This matches the reported red-since-2026-09-07 state.

### 1.3 Frontend job

`Web.Frontend/package.json` declares no `engines` field. `Web.Frontend/package-lock.json` contains
nested optional entries `node_modules/@rolldown/binding-wasm32-wasi/node_modules/@emnapi/core`
and `.../@emnapi/runtime` (both `1.11.1`). npm 10 (Node 22) rejects that resolution with
`Missing: ... from lock file`; npm 11 (Node 24) accepts it. Local environment is Node `24.11.1`
/ npm `11.6.2`, where `npm ci`, `npm run lint` and `npm test` (178 tests) pass.

### 1.4 Shared-database test map (`TEST_POSTGRES_CONNECTION`)

Database `pos_test` is the single shared database. Verified writers/readers:

| File | Class | Contexts against `pos_test` | Schema required |
|---|---|---|---|
| `CommandCenter.Tests/Builders/TestDatabaseFactory.cs` | `TestDatabaseFactory` | creates `SalesDbContext` (2 variants) | Sales |
| `CommandCenter.Tests/Integration/PostgresRealIntegrationTests.cs` | `PostgresRealIntegrationTests` | Sales (factory + own `UseNpgsql`) | Sales |
| `CommandCenter.Tests/Integration/PostgresRealSharedTransactionTests.cs` | `PostgresRealSharedTransactionTests` | Sales (factory) + Sales/Inventory on one shared `NpgsqlConnection` | Sales |
| `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs` | `DailyClosureRetryIntegrationTests` | Sales (factory, retry variant) | Sales |
| `CommandCenter.Tests/Integration/HistoryImmutabilityTests.cs` | `HistoryImmutabilityTests` | Sales + Inventory (manual, both `EnsureCreatedAsync`) | Sales + Inventory (`ExchangeRateHistory` only) |
| `CommandCenter.Tests/Unit/SalesServiceUnitTests.cs` | `SalesServiceUnitTests` | Sales (factory, line 538; own context line 648) | Sales |
| `CommandCenter.Tests/HoldOrderClaimTests.Postgres.cs` | `HoldOrderClaimTestsPostgres` | Sales (factory ×3) | Sales |

Isolated-database classes (NOT on `pos_test`, no shared-schema risk):
- `Integration/ConcurrencyCapacityTests.cs` → `pos_conc_<guid>` per test, `InventoryDbContext` + `EnsureCreatedAsync`.
- `Integration/FaultToleranceTests.cs` → `pos_fault_<guid>` per test, `SalesDbContext` + `EnsureCreatedAsync`.
- `Integration/WebApplicationFactorySmokeTests.cs` → `pos_smoke_<suffix>` and `pos_zero_<suffix>` (suffix from `SMOKE_DB_SUFFIX`), via `SalesDbContext`/`InventoryDbContext`.`MigrateAsync()`, sequential inside the class.

There is **no** `xunit.runner.json`, no `[assembly: CollectionBehavior]`, no `[CollectionDefinition]`
and no `[Collection]` attribute anywhere in `CommandCenter.Tests`. xUnit therefore parallelizes test
**classes** (collections) by default. All `pos_test` classes above run concurrently.

### 1.5 Schema bootstrap today

`TestDatabaseFactory.CreatePostgreSqlSalesDbContext()` and `...WithRetry()` call
`ctx.Database.EnsureCreated()`. `HistoryImmutabilityTests` calls `EnsureCreatedAsync()` on a Sales
context and then on an Inventory context, then issues a raw
`CREATE TABLE IF NOT EXISTS "ExchangeRateHistory"`.

Verified from the EF Core source (`RelationalDatabaseCreator.EnsureCreated`):

```
if (!Exists())            { Create(); CreateTables(); operationsPerformed = true; }
else if (!HasTables())    { CreateTables(); operationsPerformed = true; }
// else: silent no-op
```

`HasTables()` is **database-wide** ("no attempt is made to determine if tables belong to the current
model"). Therefore on a fresh database exactly one context wins and creates **only its own model's**
tables; every other context's `EnsureCreated` is a silent no-op. On an already-initialized `pos_test`
all 970 tests are green.

Sales and Inventory models are **disjoint** (Sales: `Users`, `Customers`, `Sales`, `SaleItems`,
`SalePayments`, `PaymentMethods`, `CashDrawerSessions`, `CashTransactions`, `DailyClosures`,
`ClosureDetails`, `OutboxMessages`, `IdempotentRequests`; Inventory: `Products`, `StockMovements`,
`StockMovements_Archive`, `StockReservations`, `SystemSettings`, `ExchangeRateHistory`). There is no
table-name overlap, so both schemas can coexist safely.

### 1.6 Data seeding today

`TestDatabaseFactory.SeedStandardSalesDataAsync` inserts `Customer` Id 1 and, **only when
`PaymentMethods` is completely empty**, `PaymentMethod` Ids 1–5 including `"Punto de Venta"` (Id 3).

Postgres callers of this seeder: `Integration/DailyClosureRetryIntegrationTests.cs:41` only. The
other 15 call sites use InMemory/Sqlite contexts.

`Unit/SalesServiceUnitTests.cs:549-556` (`GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsSeededInvoices`,
`[Trait("Category","RequiresDocker")]`) inserts an ad-hoc
`PaymentMethod { Name = "Punto de Venta" }` against `pos_test` with **auto-generated Id**,
unconditionally, and then seeds invoices 216–220 and asserts `detail218.Payments[0].MethodName == "Punto de Venta"`.

`PaymentMethod` carries a filtered unique index `IX_PaymentMethods_Name_Unique_NotDeleted`
(migration `20260905140000_AddIsDeletedAndUniqueNameToPaymentMethod`) and `SalePayment.PaymentMethodId`
is an FK to it.

### 1.7 Production schema path and the model/migration parity gap

Production startup is `Backend.API/Startup/DatabaseInitializer.cs`, invoked from `Program.cs`:
`invDb.Database.MigrateAsync()` then `salesDb.Database.MigrateAsync()`, followed by a defensive raw-SQL
convergence block that adds `Users.AccessFailedCount` (`DatabaseInitializer.cs:202-203`),
`LockoutEndUtc` (206-208), `LastLoginUtc` (210-212) and `SecurityStamp` (197-199) when missing.

Independent verification of the reverted `Migrate` attempt:
- `AccessFailedCount`, `LockoutEndUtc`, `LastLoginUtc` appear in `SalesDbContextModelSnapshot.cs` and
  in every `*.Designer.cs`, but **no migration `Up()` creates the `AccessFailedCount` column** — the
  only occurrences are `Sales.Module/Migrations/*.Designer.cs` and `SalesDbContextModelSnapshot.cs`.
  The `Users` table is created by `20260729051255_AddUserAndSaleCashierId`.
- So `Migrate` produces a schema that lacks `AccessFailedCount`, while the EF **model** (what
  `EnsureCreated`/`CreateTables` materialize) has it. Any test materializing `User` then fails with
  `42703`, and hold-claim concurrency resolves 0 winners instead of 1.
- This is a genuine production-vs-test schema drift, but it is currently masked in production by the
  raw-SQL patch in `DatabaseInitializer`.

### 1.8 CI support tooling

- `CommandCenter.Tests/coverage.runsettings`: coverlet `cobertura`, `Include` =
  `[Sales.Module]*,[Inventory.Module]*,[Core]*`. Assembly-name patterns, OS-agnostic.
- `scripts/check-coverage.py`: Python 3, `xml.etree`, thresholds Core `0.70`, `Sales.Module` `0.80`,
  `Inventory.Module` `0.72`, excludes `*.Migrations.*`, uses GitHub `::error::` annotations.
- `GITHUB_ACTIONS`-conditional behavior: `PostgresRealIntegrationTests`, `PostgresRealSharedTransactionTests`
  (implicitly), `HistoryImmutabilityTests`, `ConcurrencyCapacityTests`, `FaultToleranceTests`,
  `DailyClosureRetryIntegrationTests`, `SalesServiceUnitTests` all **throw** when
  `TEST_POSTGRES_CONNECTION` is unset under `GITHUB_ACTIONS`. So the variable is a hard CI contract.

---

## 2. Problem Statement

- **P1 — CI red (structural).** Backend job runs a Windows-only test target on Linux; the testhost
  aborts, no coverage artifact is produced, and the gate fails in cascade. Frontend `npm ci` fails on
  npm 10 with the versioned lockfile's optional `@emnapi/*` entries.
- **P2 — Shared-DB schema race.** On a fresh `pos_test`, parallel test classes call
  `EnsureCreated` concurrently. `HasTables()` and `CreateTables()` are separate round-trips (TOCTOU),
  so two contexts can both observe "empty" and both issue `CREATE TABLE`, producing `23505` on
  `pg_class_relname_nsp_index`; or one observes a partially created schema and queries a not-yet-created
  relation → `42P01`. Because `HasTables()` is database-wide and the winning model decides the schema,
  losing the race to `InventoryDbContext` leaves every Sales-dependent test permanently broken
  (`42P01`), which is the deterministic failure mode, not just a duplicate-key annoyance.
- **P3 — Shared-DB data race.** `SalesServiceUnitTests:549` inserts `"Punto de Venta"` concurrently
  with `DailyClosureRetryIntegrationTests` → `SeedStandardSalesDataAsync`, which seeds the same name
  only when the table is empty. Outcomes: `23505` on `IX_PaymentMethods_Name_Unique_NotDeleted`, or
  `23503` on `SalePayments` because `PaymentMethodId = 3` was never seeded (the emptiness check saw
  the other test's row).
- **P4 — Schema-source drift.** `EnsureCreated` (model) and `Migrate` (migrations) disagree; production
  closes the gap with raw SQL at startup. Tests must not adopt `Migrate` while that gap exists.

---

## 3. Independently Verified Facts

1. EF Core `EnsureCreated` creates tables only when `!HasTables()` (database-wide), and
   `HasTables()`/`CreateTables()` are not atomic — confirming P2's mechanism.
2. `RelationalDatabaseCreator.CreateTables()` generates DDL via
   `MigrationsSqlGenerator.Generate(ModelDiffer.GetDifferences(null, model.GetRelationalModel()))`,
   i.e. the **model**, so it preserves model parity (`AccessFailedCount` present) and applies `HasData`
   seed rows through the same `GetCreateTablesCommands()` path `EnsureCreated` uses.
3. No migration `Up()` creates `Users.AccessFailedCount` — the reverted `Migrate` regression is real
   and not a test artifact.
4. `windows-2025` runner image (image version `20260907.255.1`, read from
   `actions/runner-images/main/images/windows/Windows2025-Readme.md`):
   - PostgreSQL `17.11`, path `C:\Program Files\PostgreSQL\17`, env `PGROOT`/`PGDATA`/`PGBIN`,
     service `postgresql-x64-17`, user `postgres`, password `root`,
     **`ServiceStatus: Stopped` and `ServiceStartType: Disabled`**.
   - .NET SDKs including `10.0.400`; `Microsoft.WindowsDesktop.App` `10.0.8` / `10.0.11` present.
   - Python `3.12.10` (default `python`), PowerShell `7.6.5`.
5. `Directory.Build.props` sets `TreatWarningsAsErrors=true`, `AnalysisLevel=latest`,
   `EnforceCodeStyleInBuild=true`, `EnableWindowsTargeting=true`.

---

## 4. Options Evaluated

### 4a. Deterministic shared-schema bootstrap

| # | Approach | Pros | Cons | Effort |
|---|---|---|---|---|
| A | **Serialize `EnsureCreated` with an explicit first initializer** (process semaphore; first context to run creates the schema, all others no-op) | Tiny diff; keeps model parity | Still nondeterministic: whichever model wins defines the schema. If `InventoryDbContext` wins, Sales tables never exist → `42P01` everywhere. Does not guarantee "needed schemas regardless of scheduling order". | Low |
| B | **Per-context `IRelationalDatabaseCreator.CreateTables()` sequencing** inside a one-time, process-wide bootstrap for both `SalesDbContext` and `InventoryDbContext` | Deterministic: creates **both** schemas whoever calls first; model parity + `HasData`; idempotent via per-context marker tables; small diff; provider-agnostic | Requires a new helper and routing existing manual `EnsureCreated` call sites through it; must handle the "DB already has one schema only" repair case | Low–Medium |
| C | **One-time bootstrap helper invoked by every relevant context factory** (B packaged as `TestSchemaBootstrap.EnsureSharedSchema(connStr)`) | Same as B, plus a single choke point that future tests must use; easy to add a guard test asserting both schemas exist | Same as B; slightly larger surface (new file + call sites) | Medium |
| D | **`Migrate` + add a real `AccessFailedCount`/`LockoutEndUtc`/`LastLoginUtc`/`SecurityStamp` migration** | Removes drift permanently; tests then run the production schema path | Touches product migrations and the production schema path (out of stated scope); requires regenerating the snapshot under `PendingModelChangesWarning = Throw`; re-validating `WebApplicationFactorySmokeTests` assertions and the `pg_trgm` extension path; high regression surface | High |
| E | **Per-class database isolation** for every `pos_test` class (`pos_<class>_<guid>`, pattern already exists in `ConcurrencyCapacityTests`/`FaultToleranceTests`) | Removes the schema race **and** the data race with no shared state; no new schema machinery | Largest diff (6 classes + duplicated create/drop helper); each DB costs a CREATE/DROP round-trip, slowing CI; still needs the schema-creation sequencing within a class | Medium–High |

`Migrate` for the shared DB is rejected unless D is also done (stated constraint and confirmed P4).

### 4b. Removing the data race

| # | Approach | Pros | Cons | Effort |
|---|---|---|---|---|
| A | **Idempotent per-row upsert in `SeedStandardSalesDataAsync`** (seed only missing names, deterministic Ids) + have `SalesServiceUnitTests` call the seeder instead of inserting its own `"Punto de Venta"` | Smallest change; removes both the duplicate-key and the FK-23503 causes; provider-agnostic | Check-then-insert is still racy across concurrent callers unless serialized | Low |
| B | **A + Postgres advisory lock** (`pg_advisory_xact_lock`) around the seed on Npgsql | Race-free across connections/processes; keeps everything else | Adds provider-conditioned code; needs an explicit transaction | Low–Medium |
| C | **`INSERT ... ON CONFLICT DO NOTHING`** per payment method | Truly atomic | Interacts awkwardly with EF and the filtered unique index (`"Name" WHERE "IsDeleted" = false`); needs raw SQL per row | Low–Medium |
| D | **Per-class database isolation** (4a-E) | Removes the race class entirely | Same cost/size drawbacks as 4a-E | Medium–High |
| E | Put all `pos_test` classes in one xUnit `[Collection]` to serialize them | Tiny diff; removes concurrent `CREATE TABLE` | Does **not** fix the FK-23503 path (a stray `"Punto de Venta"` still suppresses the whole seed), and does not fix the "wrong model wins the schema" determinism problem | Low (insufficient alone) |

---

## 5. Recommended Approach

**Bootstrap (4a): Option B/C — a one-time, process-wide, per-connection-string shared-schema bootstrap
that materializes BOTH models, replacing bare `EnsureCreated()` on `pos_test`.**

Shape:

1. New helper (e.g. `CommandCenter.Tests/Builders/TestSchemaBootstrap.cs`, or a private nested type in
   `TestDatabaseFactory`) exposing `EnsureSharedSchema(string connString)`:
   - keyed by normalized database name in a `ConcurrentDictionary<string, Lazy<...>>` so it runs at
     most once per DB per process;
   - guarded by a `static readonly SemaphoreSlim(1, 1)` (VSTest runs an assembly's xUnit tests in a
     single testhost process, so process-local serialization is sufficient; an optional
     `pg_advisory_lock` can harden cross-process/self-hosted scenarios);
   - per context, decide with a **marker table** rather than `HasTables()`:
     `SalesDbContext` → `Users`; `InventoryDbContext` → `Products`.
     For each context, if its marker table is missing, call
     `ctx.Database.GetService<IRelationalDatabaseCreator>().CreateTables()`.
     The two models are disjoint, so creating the missing one never collides with the existing one;
   - after creation, assert both markers exist and throw a descriptive failure otherwise.
2. Route the shared-DB call sites through it:
   - `TestDatabaseFactory.CreatePostgreSqlSalesDbContext()` / `...WithRetry()`: replace
     `ctx.Database.EnsureCreated()` with the bootstrap call.
   - `HistoryImmutabilityTests`: replace both `EnsureCreatedAsync()` calls and the raw
     `CREATE TABLE IF NOT EXISTS "ExchangeRateHistory"` with the bootstrap (the Inventory schema now
     guarantees `ExchangeRateHistory`). This removes the last raw-DDL schema workaround in tests.
   - `SalesServiceUnitTests:648`, `PostgresRealSharedTransactionTests`, `PostgresRealIntegrationTests`,
     `DailyClosureRetryIntegrationTests`, `HoldOrderClaimTests.Postgres` are already covered because
     they obtain their context through the factory before querying.
3. Keep `Migrate` out of the shared DB (Option D deferred; see Open Questions).

Rationale: it is the smallest change that satisfies "guarantees the needed schemas regardless of test
scheduling order" while preserving model parity so `AccessFailedCount` exists. Option A is provably
insufficient (a lost race can leave the Sales schema absent forever); Option E is robust but a much
larger diff and slows CI.

**Data race (4b): Option B — idempotent per-row seeding under a Postgres advisory lock, plus removing
the ad-hoc insert in `SalesServiceUnitTests`.**

- `SeedStandardSalesDataAsync`: on Npgsql, open a transaction, take
  `pg_advisory_xact_lock(<fixed key>)`, then seed Customers and PaymentMethods **per row**
  (insert only names that are absent, with the deterministic Ids 1–5 that
  `DailyClosureRetryIntegrationTests` relies on). Non-Npgsql providers keep the current
  emptiness-check path (no advisory locks in InMemory/Sqlite).
- `SalesServiceUnitTests.GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsSeededInvoices`: call
  `SeedStandardSalesDataAsync(seedContext)` first and resolve the payment method by name
  (`"Punto de Venta"`, expected Id 3) instead of inserting a duplicate row. This removes the only
  writer that bypasses the seeder.
- Leave `ConcurrencyCapacityTests` / `FaultToleranceTests` / `WebApplicationFactorySmokeTests`
  untouched (already isolated by database).

**CI (already drafted, to be corrected):** keep the draft's direction, fix the PostgreSQL service
start (Section 6), and add an explicit database reset in CI.

---

## 6. CI Draft Validation (`d`)

Confirmed correct in the draft:

- `windows-2025` is the right host: the image has .NET 10 SDKs and `Microsoft.WindowsDesktop.App`
  10.0.x, which the `net10.0-windows` testhost requires.
- PostgreSQL is preinstalled with `postgres` / `root` at `C:\Program Files\PostgreSQL\17`; the draft's
  `PGPASSWORD='root'` and `Username=postgres;Password=root` connection string are consistent.
- `psql -tAc` readiness polling is a good replacement for the removed `pg_isready` health check.
- Rewritten PowerShell gate/audit steps are functionally equivalent to the bash versions:
  `scripts/check-coverage.py` uses `::error::` annotations (OS-agnostic) and Python 3.12 is on PATH;
  `$audit -match '...'` is case-insensitive and multi-line `-match` matches any line.
- `coverage.runsettings` and coverlet XPlat coverage are OS-agnostic; `TestResults/**/coverage.cobertura.xml`
  discovery with `Get-ChildItem -Recurse -Filter` is correct for a pwsh job rooted at the repo.
- The `GITHUB_ACTIONS` hard-fail contract is satisfied because the test step sets
  `TEST_POSTGRES_CONNECTION`.

**Defect found (blocking):** the runner image reports PostgreSQL `ServiceStartType: Disabled` as well
as `ServiceStatus: Stopped`. GitHub Actions runs `shell: pwsh` steps with `$ErrorActionPreference = 'stop'`,
so `Start-Service -Name $service.Name` on a **disabled** service throws and the step fails before any
test runs. The fix is to enable the start type first, e.g.
`Set-Service -Name $service.Name -StartupType Manual` (or `Automatic`) before `Start-Service`. Using
`pg_ctl start -D $env:PGDATA` as the current user is not a safe substitute because the data directory
ACLs belong to the PostgreSQL service account.

Minor robustness notes on the same step:
- `Sort-Object { [int]$_.Name }` throws if a non-numeric directory ever appears under
  `C:\Program Files\PostgreSQL`; filter with `Where-Object { $_.Name -match '^\d+$' }`. The static
  `PGROOT`/`PGBIN` env vars exposed by the image are a simpler alternative to directory discovery.
- `if ($exists.Trim() -ne '1')` assumes `$exists` is a scalar string; `-tAc` returns at most one row
  here, so this is acceptable but worth pinning with `[string]`.

**Recommended addition:** because the draft only creates `pos_test` when absent, guarantee
determinism for the schema-race fix by resetting it explicitly in CI
(`DROP DATABASE IF EXISTS pos_test WITH (FORCE)` then `CREATE DATABASE pos_test`) — hosted runners are
ephemeral, so this only makes the freshness contract explicit and exercises the fresh-DB path every run.
Locally, the bootstrap must repair a pre-existing half-initialized `pos_test` (the marker-table logic
above does).

Not required, low risk: set `SMOKE_DB_SUFFIX: ${{ github.run_id }}` on the test step to honor the
documented per-run isolation contract (hosted runners are ephemeral, so collisions only matter for
self-hosted runners or parallel jobs on one host).

---

## 7. Affected Files

Planned change surface (nothing written in this phase):

| File | Change |
|---|---|
| `.github/workflows/ci.yml` | enable PostgreSQL service start type before `Start-Service`; explicit `pos_test` drop/recreate; optionally `SMOKE_DB_SUFFIX`; optionally raise `timeout-minutes` |
| `CommandCenter.Tests/Builders/TestDatabaseFactory.cs` | new shared-schema bootstrap (or delegation to a new `TestSchemaBootstrap.cs`); per-row idempotent + advisory-lock seeding in `SeedStandardSalesDataAsync` |
| `CommandCenter.Tests/Builders/TestSchemaBootstrap.cs` | new (only if the bootstrap is extracted rather than nested) |
| `CommandCenter.Tests/Integration/HistoryImmutabilityTests.cs` | drop inline `EnsureCreatedAsync` + raw `ExchangeRateHistory` DDL; use the bootstrap |
| `CommandCenter.Tests/Unit/SalesServiceUnitTests.cs` | replace ad-hoc `"Punto de Venta"` insert with `SeedStandardSalesDataAsync`; resolve method id by name |
| `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs` | (optional) resolve `PaymentMethodId` by name instead of hardcoding 3 |
| `Web.Frontend/package.json` | (optional) declare `engines.node >= 24` to pin the npm 11 requirement |

Unchanged: `CommandCenter.Tests/coverage.runsettings`, `scripts/check-coverage.py`,
`CommandCenter.Tests/CommandCenter.Tests.csproj`, `Directory.Build.props`, all product code,
all EF migrations, `Backend.API/Startup/DatabaseInitializer.cs`.

---

## 8. Risks

1. **PostgreSQL service disabled on the runner image (High, blocking).** The draft's `Start-Service`
   fails on a `Disabled` service under `$ErrorActionPreference='stop'`. Must `Set-Service -StartupType`
   first. Verified against the current image README.
2. **Lost-race legacy state (Medium).** A local or cached `pos_test` created by a previous flaky run can
   have Inventory tables but no Sales tables. The bootstrap's marker-table repair must handle this, and
   CI should drop/recreate the DB.
3. **`Migrate`-based regressions (Medium).** Any future switch of the shared DB to `Migrate` reintroduces
   the `42703 AccessFailedCount` regression until Option D (a real migration) is done. This must be
   recorded as a constraint, not just a lesson.
4. **Windows job timeout (Medium).** `timeout-minutes: 30` for build + 970 tests + coverage
   instrumentation on a slower Windows runner is tight; the draft keeps 30. Consider 40–45.
5. **Runner image drift (Medium/Low).** `windows-2025` pins the OS but the image can bump PostgreSQL
   (17 today) and .NET patch levels. The service-name wildcard and version-dir discovery absorb the PG
   path change; a newer SDK can surface new analyzer diagnostics under `TreatWarningsAsErrors`.
6. **Node 24 patch drift (Low).** `node-version: '24.x'` installs the latest 24.x; the lockfile was
   produced with npm `11.6.2`. No `engines` constraint is declared, so a future npm behavior change
   could re-break `npm ci`. Declare `engines` + keep the lockfile regenerated by the same major.
7. **All-or-nothing `HasTables()` semantics (Medium).** Any test that later creates a raw
   `UseNpgsql(TEST_POSTGRES_CONNECTION)` context and queries without going through the factory
   reintroduces `42P01`. Mitigation: a guard test asserting both marker tables exist on the shared DB.
8. **CI cost/time (Low).** `windows-2025` bills at a higher multiplier and is slower than Linux; the
   frontend job stays on Linux to limit the increase.
9. **Cross-class DB data pollution, not a race (Medium).** `PostgresRealIntegrationTests` leaves
   `OutboxMessages` rows and `DailyClosureRetryIntegrationTests` leaves sales/closure rows on `pos_test`;
   `DailyClosureRetryIntegrationTests` also derives sale Ids from `Environment.TickCount`, so two runs
   in the same tick can collide. Out of scope for this change, but it caps how deterministic the shared
   DB can ever be and is an argument for the per-class isolation fallback long-term.
10. **`CreateTables` + `HasData` (Low).** Verified from the EF Core source that `CreateTables()` uses the
    same `GetCreateTablesCommands()` path as `EnsureCreated`, so the `HasData` seed (`User` Id 1
    `admin`) is emitted. The bootstrap should still assert it explicitly to catch future provider changes.

---

## 9. Open Questions

1. Should CI **drop and recreate** `pos_test` on every run (recommended), or keep "create if missing"
   and rely solely on the bootstrap's repair logic?
2. Is adding the missing `AccessFailedCount`/`LockoutEndUtc`/`LastLoginUtc`/`SecurityStamp` migration
   (Option D) in scope for a follow-up change? It is the only way to remove the production-vs-test
   schema drift permanently, but it touches the production schema path.
3. Is a process-local `SemaphoreSlim` sufficient, or should the bootstrap take a Postgres advisory lock
   to also cover self-hosted runners / multiple testhost processes? (Verified: one testhost process per
   assembly under VSTest, so process-local is sufficient for GitHub-hosted CI.)
4. Should the payment-method seed's deterministic Ids (1–5) be made load-bearing, or should every test
   resolve `PaymentMethodId` by name? Hardcoded Id 3 in `DailyClosureRetryIntegrationTests` is the last
   coupling to the seed order.
5. Should the shared-`pos_test` classes migrate to the per-class isolation pattern long-term
   (Option 4a-E) to also remove the data-pollution class of problems (Risk 9)?
6. Which ANEXO number should the change record in `docs/reporte.txt`? The draft cites `8.128`, but no
   `8.128` entry exists in `docs/reporte.txt` today.

---

## 10. Ready for Proposal

Yes. The problem is fully mapped, the deterministic bootstrap strategy is identified and justified
against the alternatives, the data-race fix is scoped to two files, and the CI draft has been validated
against the current runner image with one blocking defect identified (`ServiceStartType: Disabled`).
`next_recommended: sdd-propose`.
